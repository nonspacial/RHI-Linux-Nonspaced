using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed record DofFixReleaseInfo(
    string Version,
    string AssetUrl,
    string AssetName,
    string? Sha256);

/// <summary>
/// Linux port of the upstream RHI Universal UE DOF Fix deployment. Eligibility and release
/// selection intentionally mirror the pinned Windows source; file ownership/removal is
/// stricter on Linux so an external or user-modified add-on is never destroyed silently.
/// </summary>
public sealed class DofFixDeploymentService
{
    public const string AddonFileName = "renodx-universal_ue-dof-fix.addon64";
    public const string DisplayName = "DOF Fix";

    /// <summary>A DOF Fix add-on RHI's record does not list is in the way (PT-BR presentation pair).</summary>
    public const string NotOwnedMessage =
        "Universal UE DOF Fix already exists on disk but is not owned by RHI Linux. The external add-on is protected from overwrite.";
    public const string TagPrefix = "ue-dof-fix-";
    public const string GitHubApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string DefaultDownloadBaseUrl = "https://github.com/RankFTW/rhi-repo/releases/download";
    private const long MinimumPayloadSize = 128 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly UpstreamRhiManifestService _manifest;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;

    public DofFixDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        UpstreamRhiManifestService manifest,
        string? cacheDir = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _manifest = manifest;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "ue-dof-fix");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.DofFix);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "DOF Fix is not managed by RHI Linux.");
        try
        {
            var latest = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            var update = !string.Equals(record.Version, latest.Version, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase);
            // w13 final review m1: AddonPath set or changed after the install: the move is the update, same version or not.
            if (!update && AddonMoveDue(game, record))
                return new ManagedUpdateCheck(true, true, DisplayName + AddonPathFolder.MoveDueSuffix, record.Version, latest.Version);
            return new ManagedUpdateCheck(true, update,
                update ? $"DOF Fix update available: {record.Version ?? "unknown"} → {latest.Version}." : $"DOF Fix {record.Version ?? latest.Version} is current.",
                record.Version, latest.Version);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"DOF Fix update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    /// <summary>
    /// w13 final review m1: RHI's add-on is recorded outside ReShade's add-on folder (reshade.ini's AddonPath, mapped
    /// through the prefix), so the next Update moves it. False when that folder cannot be resolved (Update refuses then).
    /// </summary>
    internal static bool AddonMoveDue(DetectedGame game, LinuxDeploymentRecord record)
    {
        var gameDir = ResolveDeployDirectory(game);
        if (string.IsNullOrWhiteSpace(gameDir)) return false;
        var folder = AddonPathFolder.Resolve(gameDir, game.PrefixPath);
        if (folder.Refusal is not null) return false;
        var target = Path.Combine(folder.Folder, AddonFileName);
        return record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && !AddonPathFolder.SameFile(file.Path, target));
    }

    public Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => GetRecord(game) is null
            ? Task.FromResult(ComponentOperationResult.Fail("DOF Fix is not managed by RHI Linux."))
            : InstallAsync(game, cancellationToken);

    /// <summary>
    /// Mirrors upstream RHI DofFixService.IsGameEligible:
    /// explicit x86 is rejected; manifest force wins immediately; otherwise the engine must
    /// identify Unreal Engine 5, then the skip list is applied, and UE 5.0-5.6 is accepted.
    /// Unknown minor versions on an otherwise UE5 hint are treated as eligible upstream.
    /// </summary>
    public static bool IsGameEligible(DetectedGame game)
    {
        if (IsExplicitly32Bit(game)) return false;
        if (game.DofFixForcedByManifest) return true;

        var engineHint = game.EngineHint;
        if (string.IsNullOrEmpty(engineHint)) return false;
        if (!engineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.DofFixSkippedByManifest) return false;

        var idx = engineHint.IndexOf("5.", StringComparison.Ordinal);
        if (idx < 0) return true;
        var afterFive = engineHint[(idx + 2)..];
        var dotIdx = afterFive.IndexOf('.');
        var minorText = dotIdx >= 0 ? afterFive[..dotIdx] : afterFive.TrimEnd(' ', ')');
        return !int.TryParse(minorText, out var minor) || minor is >= 0 and <= 6;
    }

    public static string EligibilityReason(DetectedGame game)
    {
        if (IsExplicitly32Bit(game)) return "Universal UE DOF Fix is 64-bit only.";
        if (game.DofFixForcedByManifest)
            return "Ativado pela regra de força (force) do manifesto do RHI.";
        if (string.IsNullOrWhiteSpace(game.EngineHint))
            return "O DOF Fix é habilitado apenas para títulos explicitamente listados ou jogos Unreal Engine 5.0–5.6 detectados.";
        if (!game.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase))
            return $"DOF Fix targets Unreal Engine 5.0-5.6; detected engine hint is {game.EngineHint}.";
        if (game.DofFixSkippedByManifest)
            return "Desativado pela regra do manifesto do RHI para jogos ignorados.";
        return IsGameEligible(game)
            ? $"Eligible for Universal UE DOF Fix ({game.EngineHint})."
            : $"DOF Fix targets Unreal Engine 5.0-5.6; detected engine hint is {game.EngineHint}.";
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!IsGameEligible(game))
            return ComponentOperationResult.Fail(EligibilityReason(game));

        var gameDir = ResolveDeployDirectory(game);
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return ComponentOperationResult.Fail("Game binary/install directory is not available for DOF Fix deployment.");
        // #255 (upstream 2.8.8 54ec8f2): ReShade loads add-ons from reshade.ini's AddonPath when it is set.
        // w13 final review m2: an AddonPath on a drive the game's Wine prefix does not map is refused, never joined on.
        var addonFolder = AddonPathFolder.Resolve(gameDir, game.PrefixPath);
        if (addonFolder.Refusal is not null) return ComponentOperationResult.Fail(addonFolder.Refusal);
        var deployDir = addonFolder.Folder;

        var target = Path.Combine(deployDir, AddonFileName);
        var existingRecord = GetRecord(game);
        var existingPath = FindExistingAddonPath(deployDir);
        if (existingPath is not null && existingRecord is null)
            return ComponentOperationResult.Fail(NotOwnedMessage);
        if (existingRecord is not null && existingPath is not null && !AddonPathFolder.SameFile(existingPath, target))
            return ComponentOperationResult.Fail("The RHI-managed DOF Fix path changed outside RHI. Update is blocked to avoid creating a case-variant duplicate.");

        if (existingRecord is not null && File.Exists(target))
        {
            var owned = existingRecord.Files.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.Path) && AddonPathFolder.SameFile(file.Path, target));
            // w13 final review nit: a file at the target the record does not list (AddonPath set since) is not RHI's.
            if (owned is null) return ComponentOperationResult.Fail(NotOwnedMessage);
            if (owned.Sha256 is not { Length: > 0 })
                return ComponentOperationResult.Fail("Existing RHI DOF Fix record has no ownership hash. Update is blocked conservatively.");
            var currentHash = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed DOF Fix was modified after installation. Update is blocked to preserve the changed file.");
        }

        // #255: RHI's copy at another path (the game folder, before AddonPath was set) moves to the add-on folder; it is
        // deleted only while it still holds what RHI placed, and never when it is the target itself reached another way
        // (w13 final review m3: AddonPath linking to the game folder; the copy overwrote it, the delete would remove it).
        var movedFrom = existingRecord?.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)
                           && !AddonPathFolder.SameFile(file.Path, target))
            .ToArray() ?? [];
        foreach (var old in movedFrom)
        {
            if (old.Sha256 is not { Length: > 0 }
                || !string.Equals(await FileHash.Sha256HexAsync(old.Path, cancellationToken).ConfigureAwait(false), old.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed DOF Fix was modified after installation. Update is blocked to preserve the changed file.");
        }

        var release = await ResolveLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var cached = await DownloadAndValidateAsync(release, cancellationToken).ConfigureAwait(false);

        // Journaled (#23): the add-on is tracked before it changes; the record carrying the operation id is the commit
        // point. A failure after the copy no longer leaves the add-on without a record (#31).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "DOF Fix install: " + game.Name, game, ManagedComponentKind.DofFix, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Downloaded: from the copy on, Cancel is refused (a late cancel used to leave the add-on with no record).
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var createdFolders = new List<string>();
        try
        {
            // w13 final review nit: a missing AddonPath folder is created inside the operation, so a rollback removes it.
            await AddonPathFolder.CreateJournaledAsync(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("DOF Fix target directory is invalid."),
                scope.Operation, createdFolders, cancellationToken).ConfigureAwait(false);
            var deployedHash = await scope.PlaceCopyAsync(cached.Path, target, cancellationToken).ConfigureAwait(false);
            foreach (var old in movedFrom)
                await scope.DeleteAsync(old.Path, cancellationToken).ConfigureAwait(false);

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.DofFix,
                BinaryDirectory = gameDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = deployedHash,
                        AllowUserChanges = false
                    }
                ],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DofFix.TagPrefix"] = TagPrefix,
                    ["DofFix.AssetName"] = release.AssetName,
                    ["DofFix.ManifestForced"] = game.DofFixForcedByManifest ? "1" : "0",
                    ["DofFix.EngineHint"] = game.EngineHint ?? "",
                    [VerifiedDownload.MetadataSha256Key] = cached.Sha256,
                    [VerifiedDownload.MetadataIntegrityKey] = cached.Integrity
                }
            }));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Universal UE DOF Fix", changedFiles: target);
            if (rollback.RolledBack) AddonPathFolder.RemoveCreated(createdFolders);
            if (rollback.Success) throw;
            throw new IOException($"{ex.Message}{DeploymentScope.RollbackNote(rollback)}", ex);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"Universal UE DOF Fix {release.Version} installed."
                                           + AddonPathFolder.LeftInGameFolderNote(gameDir, deployDir, [AddonFileName]), target);
    }

    /// <summary>Fixed text of a failed, rolled-back removal (PT-BR row under the journal anchor); the reason follows.</summary>
    public const string RemovalRolledBackPrefix = "DOF Fix removal failed and was rolled back: ";

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Universal UE DOF Fix is not managed by RHI Linux. External files are protected from removal.");

        var paths = record.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .ToArray();
        foreach (var file in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail("DOF Fix ownership hash is missing; removal is blocked conservatively.");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed DOF Fix was modified after installation. Remove is blocked to preserve the changed file.");
        }

        // Journaled (#23): a crash before the record is gone puts the add-on back as the record still describes it.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "DOF Fix removal: " + game.Name, game, ManagedComponentKind.DofFix, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Cancel is honoured only up to here: the removal never stops half-way.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var changed = new List<string>();
        try
        {
            foreach (var file in paths)
            {
                if (!File.Exists(file.Path)) continue;
                await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);
                changed.Add(file.Path);
            }
            _records.Remove(game, ManagedComponentKind.DofFix);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "dof-fix").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Universal UE DOF Fix", removal: true);
            return ComponentOperationResult.Fail($"{RemovalRolledBackPrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok("Universal UE DOF Fix removed. User/external files were left untouched.", changed.ToArray());
    }

    public async Task<DofFixReleaseInfo> ResolveLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        var json = await TrustedDownload.GetStringAsync(_http, GitHubApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RHI DOF Fix release API returned an unexpected JSON shape.");

        var candidates = new List<(string Version, Version Parsed, string? AssetUrl, string? Sha256)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var version = tag[TagPrefix.Length..];
            string? assetUrl = null;
            string? sha256 = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.Equals(name, AddonFileName, StringComparison.Ordinal)) continue;
                    assetUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha256 = digest["sha256:".Length..].Trim();
                    break;
                }
            }

            Version.TryParse(version, out var parsed);
            candidates.Add((version, parsed ?? new Version(0, 0), assetUrl, sha256));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("No ue-dof-fix-* release exists in the pinned RHI release repository response.");

        var best = candidates.OrderByDescending(candidate => candidate.Parsed).First();
        string? overrideUrl = null;
        try
        {
            overrideUrl = await _manifest.FetchComponentUrlAsync("ueDofFix", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Manifest enrichment is fail-open upstream: the GitHub release asset remains authoritative
            // when the pinned manifest cannot be reached.
            AppLog.Warn("dof-fix", "The upstream manifest's ueDofFix link could not be read; using the GitHub release asset", ex);
        }
        var usingOverride = !string.IsNullOrWhiteSpace(overrideUrl);
        var finalUrl = usingOverride
            ? overrideUrl!.Replace("{version}", best.Version, StringComparison.Ordinal)
            : best.AssetUrl ?? $"{DefaultDownloadBaseUrl}/{TagPrefix}{best.Version}/{AddonFileName}";

        return new DofFixReleaseInfo(
            best.Version,
            finalUrl,
            AddonFileName,
            usingOverride ? null : best.Sha256);
    }

    internal static bool IsExplicitly32Bit(DetectedGame game)
    {
        return GameArchitecture.Of(game) == PeArchitecture.X86;
    }

    internal static string ResolveDeployDirectory(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.BinaryDirectory! : game.InstallPath;

    internal static string? FindExistingAddonPath(string deployDirectory)
    {
        if (!Directory.Exists(deployDirectory)) return null;
        return Directory.EnumerateFiles(deployDirectory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(AddonFileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The add-on from the stamped download cache under the GitHub policy (reused while its stamp matches URL, version
    /// and digest; #35). GitHub's digest is verified when published; a manifest override link names another file, so
    /// GitHub's digest does not apply to it and it is accepted on HTTPS + host policy, its SHA-256 recorded (#26).
    /// </summary>
    private Task<VerifiedDownload> DownloadAndValidateAsync(DofFixReleaseInfo release, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(release.Version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var cache = Path.Combine(_cacheDir, $"{safeVersion}-{AddonFileName}");

        // DEP-06: the release asset or the manifest's ueDofFix template must be HTTPS on GitHub (request and redirects).
        return TrustedDownload.EnsureVerifiedAsync(
            _http, release.AssetUrl, cache, TrustedDownloadPolicy.GitHub, release.Version, release.Sha256,
            validate: (temp, _) => BinarySignatureInspector.HasPeSignature(temp, MinimumPayloadSize)
                ? Task.CompletedTask
                : throw new InvalidDataException("Downloaded Universal UE DOF Fix is not a valid PE payload or is unexpectedly small."),
            cancellationToken: cancellationToken);
    }
}
