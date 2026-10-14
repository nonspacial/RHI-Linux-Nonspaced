using System.IO.Compression;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record GitHubReShadeAddonDefinition(
    ManagedComponentKind Kind,
    string DisplayName,
    string ReleaseApiUrl,
    string? AssetName64,
    string? AssetName32,
    long MinimumSize,
    ManagedComponentKind? MutuallyExclusiveWith = null,
    string? BodyVersionPattern = null,
    string? AssetNamePattern64 = null,
    string? AssetNamePattern32 = null,
    string? DeployFileName64 = null,
    string? DeployFileName32 = null);

internal sealed record GitHubAddonRelease(string Version, string AssetUrl, string AssetName, string? Sha256);

/// <summary>
/// Safe deployment backend for ReShade add-ons published as .addon64/.addon32 assets on GitHub.
/// It supports both fixed asset names and dynamic release filenames, including release-list endpoints,
/// while keeping a stable deployment filename when requested. It respects ReShade [ADDON] AddonPath,
/// validates PE payloads and optional GitHub SHA-256 digests, protects foreign files, and only removes
/// files that still match the RHI-managed hash.
/// </summary>
public sealed class GitHubReShadeAddonDeploymentService
{
    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly GitHubReShadeAddonDefinition _definition;
    private readonly GitHubReleaseAssetResolver _releaseResolver;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;

    public GitHubReShadeAddonDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        GitHubReShadeAddonDefinition definition,
        string? cacheDir = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _definition = definition;
        _releaseResolver = new GitHubReleaseAssetResolver(http);
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "addons", definition.Kind.ToString().ToLowerInvariant());
    }

    public ManagedComponentKind Kind => _definition.Kind;

    public Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => CheckUpdateAsync(game, releaseTag: null, cancellationToken);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, _definition.Kind);
        if (record is null)
            return new ManagedUpdateCheck(false, false, $"{_definition.DisplayName} is not managed by RHI Linux.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return new ManagedUpdateCheck(true, false, "Game executable is unavailable; update check skipped.", record.Version, CouldNotCheck: true);
        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return new ManagedUpdateCheck(true, false, "Game architecture is unknown; update check skipped.", record.Version, CouldNotCheck: true);
        try
        {
            var latest = await ResolveReleaseAsync(architecture, releaseTag, cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase);
            var mode = string.IsNullOrWhiteSpace(releaseTag) ? "latest" : $"pinned {releaseTag}";
            return new ManagedUpdateCheck(true, update,
                update
                    ? $"{_definition.DisplayName} {mode}: {record.Version ?? "unknown"} → {latest.Version}."
                    : $"{_definition.DisplayName} {record.Version ?? latest.Version} is current ({mode}).",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"{_definition.DisplayName} update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => UpdateAsync(game, releaseTag: null, cancellationToken);

    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
        => _records.Find(game, _definition.Kind) is null
            ? Task.FromResult(ComponentOperationResult.Fail($"{_definition.DisplayName} is not managed by RHI Linux."))
            : InstallAsync(game, releaseTag, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallAsync(game, releaseTag: null, cancellationToken);

    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var preflight = ValidateTarget(game);
        if (preflight is not null) return preflight;

        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath!);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail($"Could not determine game bitness for {_definition.DisplayName}.");

        string deployFileName;
        try { deployFileName = GetDeployFileName(architecture); }
        catch (Exception ex) { return ComponentOperationResult.Fail(ex.Message); }

        var deployDir = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!);
        Directory.CreateDirectory(deployDir);

        if (_definition.MutuallyExclusiveWith is ManagedComponentKind otherKind)
        {
            if (_records.Find(game, otherKind) is not null || HasConflictingAddonOnDisk(deployDir, otherKind))
                return ComponentOperationResult.Fail($"{_definition.DisplayName} cannot be installed while {DisplayNameFor(otherKind)} is present. Remove it first.");
        }

        var target = Path.Combine(deployDir, deployFileName);
        var record = _records.Find(game, _definition.Kind);
        var existingManaged = record?.Files.FirstOrDefault();

        // Protect any same-family external/legacy add-on before network access.
        foreach (var candidate in FindComponentAddonFiles(deployDir, _definition.Kind))
        {
            if (existingManaged is not null &&
                Path.GetFullPath(candidate).Equals(Path.GetFullPath(existingManaged.Path), StringComparison.Ordinal))
                continue;
            return ComponentOperationResult.Fail($"External {_definition.DisplayName} installation detected at {candidate}. RHI Linux will not overwrite, remove or stack it automatically.");
        }

        // Foreign installation protection for the exact target is intentionally redundant with the family scan above.
        if (File.Exists(target) && existingManaged is null)
            return ComponentOperationResult.Fail($"External {_definition.DisplayName} installation detected at {target}. RHI Linux will not overwrite it.");

        // A managed file edited outside RHI is protected as well.
        if (existingManaged is not null && File.Exists(existingManaged.Path))
        {
            var currentHash = await FileHash.Sha256HexAsync(existingManaged.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, existingManaged.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed {_definition.DisplayName} file was modified outside RHI and will not be replaced: {existingManaged.Path}");
        }

        GitHubAddonRelease release;
        try
        {
            release = await ResolveReleaseAsync(architecture, releaseTag, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Could not resolve latest {_definition.DisplayName} release: {ex.Message}");
        }

        string cached;
        VerifiedDownload download;
        try
        {
            (cached, download) = await DownloadAsync(release, architecture, deployFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"{_definition.DisplayName} install failed: {ex.Message}");
        }

        // Journaled (#23): the add-on, the auxiliary config RHI creates and a previous build under another name are
        // tracked before they change; the record carrying the operation id is the commit point. Before this a failure
        // after the copy left the add-on with no record (refused as external afterwards).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, $"{_definition.DisplayName} install: {game.Name}", game, _definition.Kind, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            // Downloaded: from the copy on, Cancel is refused (a late cancel used to leave the copied add-on with no record).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var targetHash = await scope.PlaceCopyAsync(cached, target, cancellationToken).ConfigureAwait(false);
            if (AuxiliaryConfigPath(deployDir) is { } auxiliaryPath)
                await scope.TrackAsync(auxiliaryPath, cancellationToken).ConfigureAwait(false);
            var auxiliaryConfig = TryEnsureAuxiliaryConfigIfAbsent(deployDir);
            if (auxiliaryConfig.Created && auxiliaryConfig.Path is { } createdConfig)
                await scope.DeclareWrittenContentAsync(createdConfig, cancellationToken).ConfigureAwait(false);

            if (existingManaged is not null &&
                !Path.GetFullPath(existingManaged.Path).Equals(Path.GetFullPath(target), StringComparison.Ordinal) &&
                File.Exists(existingManaged.Path))
            {
                await scope.DeleteAsync(existingManaged.Path, cancellationToken).ConfigureAwait(false);
            }

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = _definition.Kind,
                BinaryDirectory = game.BinaryDirectory!,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = targetHash
                    }
                ],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GitHub.ReleaseApi"] = _definition.ReleaseApiUrl,
                    ["GitHub.AssetName"] = release.AssetName,
                    ["GitHub.DeployFileName"] = deployFileName,
                    ["GitHub.AssetSha256"] = release.Sha256 ?? "",
                    ["GitHub.ReleaseTagPin"] = releaseTag ?? "",
                    ["Auxiliary.ConfigPath"] = auxiliaryConfig.Path ?? "",
                    ["Auxiliary.ConfigCreatedByRhi"] = auxiliaryConfig.Created ? "true" : "false",
                    ["Auxiliary.ConfigError"] = auxiliaryConfig.Error ?? "",
                    [VerifiedDownload.MetadataSha256Key] = download.Sha256,
                    [VerifiedDownload.MetadataIntegrityKey] = download.Integrity
                }
            }));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail($"{_definition.DisplayName} install failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"{_definition.DisplayName} {release.Version} installed → {target}", target);
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, _definition.Kind);
        if (record is null)
            return ComponentOperationResult.Fail($"{_definition.DisplayName} is not managed by RHI Linux. Removal is locked to protect the external installation.");

        // Every file is checked before the first one is deleted: a modified second file used to stop the removal after
        // the first was already gone.
        foreach (var file in record.Files)
        {
            if (!File.Exists(file.Path)) continue;
            var currentHash = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
        }

        // Journaled (#23): a crash before the record is gone puts the files back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, $"{_definition.DisplayName} removal: {game.Name}", game, _definition.Kind, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            // The removal never stops half-way.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            foreach (var file in record.Files)
                await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, _definition.Kind);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "github-addon").ConfigureAwait(false);
            return ComponentOperationResult.Fail($"{_definition.DisplayName} removal failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"RHI-managed {_definition.DisplayName} removed.");
    }

    internal Task<GitHubAddonRelease> ResolveLatestAsync(PeArchitecture architecture, CancellationToken cancellationToken = default)
        => ResolveReleaseAsync(architecture, releaseTag: null, cancellationToken);

    internal async Task<GitHubAddonRelease> ResolveReleaseAsync(PeArchitecture architecture, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var exact = architecture == PeArchitecture.X86 ? _definition.AssetName32 : _definition.AssetName64;
        var pattern = architecture == PeArchitecture.X86 ? _definition.AssetNamePattern32 : _definition.AssetNamePattern64;
        var release = await _releaseResolver.ResolveAsync(
            new GitHubReleaseAssetQuery(
                _definition.ReleaseApiUrl,
                exact,
                pattern,
                _definition.BodyVersionPattern),
            releaseTag,
            cancellationToken).ConfigureAwait(false);
        return new GitHubAddonRelease(release.Version, release.AssetUrl, release.AssetName, release.Sha256);
    }

    private string GetDeployFileName(PeArchitecture architecture)
    {
        var explicitDeploy = architecture == PeArchitecture.X86 ? _definition.DeployFileName32 : _definition.DeployFileName64;
        if (!string.IsNullOrWhiteSpace(explicitDeploy)) return Path.GetFileName(explicitDeploy);

        var exact = architecture == PeArchitecture.X86 ? _definition.AssetName32 : _definition.AssetName64;
        if (!string.IsNullOrWhiteSpace(exact)) return Path.GetFileName(exact);

        throw new InvalidOperationException($"{_definition.DisplayName} uses a dynamic release filename but has no stable deployment filename configured for {ArchitectureLabel(architecture)}.");
    }

    private static string ArchitectureLabel(PeArchitecture architecture)
        => architecture == PeArchitecture.X86 ? "32-bit" : "64-bit";

    private ComponentOperationResult? ValidateTarget(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"{_definition.DisplayName} is a Windows ReShade add-on and is not deployed to native Linux games.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (!HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail($"Install ReShade with full add-on support before {_definition.DisplayName}.");
        return null;
    }

    /// <summary>
    /// Returns a validated payload for <paramref name="release"/> and the download it came from. The release
    /// asset goes through the stamped download cache under the add-on host policy (reused only while its stamp
    /// matches URL, version and GitHub digest; verified against that digest when published, #26). A zipped
    /// add-on is then extracted through the checked extraction into a derived entry bound to the zip's hash,
    /// reused only while that binding holds. Nothing unstamped is ever deployed.
    /// </summary>
    private async Task<(string Payload, VerifiedDownload Download)> DownloadAsync(
        GitHubAddonRelease release,
        PeArchitecture architecture,
        string deployFileName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var versionPart = Regex.Replace(release.Version, "[^A-Za-z0-9._-]", "_");
        var safeAssetName = Path.GetFileName(release.AssetName);
        var cache = Path.Combine(_cacheDir, $"{versionPart}-{safeAssetName}");
        var isArchive = safeAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, release.AssetUrl, cache, DownloadSources.GitHubAddonRelease, release.Version, release.Sha256,
            validate: (temp, _) =>
            {
                if (isArchive)
                {
                    if (new FileInfo(temp).Length < 512)
                        throw new InvalidOperationException("Downloaded add-on archive is unexpectedly small.");
                }
                else if (!BinarySignatureInspector.HasPeSignature(temp, _definition.MinimumSize))
                    throw new InvalidOperationException("Downloaded add-on is not a valid PE payload or is unexpectedly small.");
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!isArchive) return (cache, download);

        var payload = Path.Combine(_cacheDir, $"{versionPart}-{Path.GetFileName(deployFileName)}");
        if (CacheStamp.IsValidBoundTo(payload, release.AssetUrl, release.Version, download.Sha256))
            return (payload, download);
        CacheStamp.Invalidate(payload);

        using (var archive = ZipFile.OpenRead(cache))
        {
            var exact = archive.Entries.FirstOrDefault(entry =>
                !string.IsNullOrEmpty(entry.Name) &&
                Path.GetFileName(entry.FullName).Equals(deployFileName, StringComparison.OrdinalIgnoreCase));

            if (exact is null)
            {
                var extension = architecture == PeArchitecture.X86 ? ".addon32" : ".addon64";
                var candidates = archive.Entries
                    .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                                    Path.GetFileName(entry.FullName).EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                exact = candidates.Length == 1 ? candidates[0] : null;
            }

            if (exact is null)
                throw new InvalidDataException($"Archive does not contain the required {deployFileName} payload.");

            // Checked extraction (declared-size cap, fixed mode, length + CRC-32, atomic rename); never ExtractToFile (#42).
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(exact, payload, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }
        if (!BinarySignatureInspector.HasPeSignature(payload, _definition.MinimumSize))
        {
            FileCleanup.TryDelete(payload);
            throw new InvalidDataException("Extracted add-on is not a valid PE payload or is unexpectedly small.");
        }
        CacheStamp.WriteBoundTo(payload, release.AssetUrl, release.Version, download.Sha256);
        return (payload, download);
    }

    internal static bool HasReShade(string binaryDirectory)
    {
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll", "dxgi.dll", "d3d9.dll", "d3d11.dll", "opengl32.dll" })
        {
            var path = Path.Combine(binaryDirectory, name);
            if (File.Exists(path) && BinarySignatureInspector.LooksLikeReShade(path)) return true;
        }
        return false;
    }

    internal static bool HasConflictingAddonOnDisk(string deployDir, ManagedComponentKind kind)
        => FindComponentAddonFiles(deployDir, kind).Count > 0;

    internal static IReadOnlyList<string> FindComponentAddonFiles(string deployDir, ManagedComponentKind kind)
    {
        if (!Directory.Exists(deployDir)) return [];
        return Directory.EnumerateFiles(deployDir, "*.addon*", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return kind switch
                {
                    ManagedComponentKind.ReLimiter =>
                        name.Contains("relimiter", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("ultra_limiter", StringComparison.OrdinalIgnoreCase),
                    ManagedComponentKind.DisplayCommander =>
                        name.Contains("display_commander", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("displaycommander", StringComparison.OrdinalIgnoreCase),
                    ManagedComponentKind.MfgAdaUnlock =>
                        name.Contains("mfgunlock", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("mfg_unlock", StringComparison.OrdinalIgnoreCase),
                    ManagedComponentKind.Dlss5Feeder =>
                        name.Contains("dlss5-feed", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("dlss5_feeder", StringComparison.OrdinalIgnoreCase),
                    ManagedComponentKind.Dlss5Dx11Bridge =>
                        name.Contains("dlss5-bridge", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("dlss5-dx11-bridge", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            })
            .ToArray();
    }

    private (string FileName, string Content)? AuxiliaryTemplate => _definition.Kind switch
    {
        ManagedComponentKind.ReLimiter => ("relimiter.ini", "[UltraLimiter]\n"),
        ManagedComponentKind.DisplayCommander => ("DisplayCommander.ini", "[DisplayCommander]\n"),
        _ => null
    };

    // The path the auxiliary config would be created at (null when the add-on has none, or one is already there).
    private string? AuxiliaryConfigPath(string deployDir)
    {
        if (AuxiliaryTemplate is not { } template || !Directory.Exists(deployDir)) return null;
        var exists = Directory.EnumerateFiles(deployDir, "*", SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetFileName(path).Equals(template.FileName, StringComparison.OrdinalIgnoreCase));
        return exists ? null : Path.Combine(deployDir, template.FileName);
    }

    private (string? Path, bool Created, string? Error) TryEnsureAuxiliaryConfigIfAbsent(string deployDir)
    {
        var template = AuxiliaryTemplate;
        if (template is null) return (null, false, null);

        try
        {
            Directory.CreateDirectory(deployDir);
            var existing = Directory.EnumerateFiles(deployDir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(template.Value.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return (existing, false, null);

            var target = Path.Combine(deployDir, template.Value.FileName);
            // New file only: never replaces a config the user created meanwhile.
            AtomicFile.WriteNew(target, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(template.Value.Content));
            return (target, true, null);
        }
        catch (Exception ex)
        {
            // Matches upstream's best-effort Deploy*IniIfAbsent contract: config setup
            // must never turn a successful add-on deployment into a failed install.
            return (null, false, ex.Message);
        }
    }

    private static string DisplayNameFor(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReLimiter => "ReLimiter",
        ManagedComponentKind.DisplayCommander => "Display Commander",
        ManagedComponentKind.MfgAdaUnlock => "MFG Ada Unlock",
        ManagedComponentKind.Dlss5Feeder => "DLSS5 Feeder",
        ManagedComponentKind.Dlss5Dx11Bridge => "DLSS5 DX11 Bridge",
        _ => kind.ToString()
    };
}
