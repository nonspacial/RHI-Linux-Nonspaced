using System.IO.Compression;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux/Wine backend for the upstream RHI RE Framework flow.
/// Upstream deploys the monolithic praydog REFramework.zip dinput8.dll for RE Engine games.
/// The Linux port adds per-game Wine override ownership and hash-guarded teardown so external
/// or user-modified dinput8 proxies are never overwritten or removed silently.
/// </summary>
public sealed class REFrameworkDeploymentService
{
    public const string DllFileName = "dinput8.dll";
    public const string MonolithicZipName = "REFramework.zip";
    public const string ReleasesApiUrl = "https://api.github.com/repos/praydog/REFramework-nightly/releases/latest";
    private const long MinimumDllSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly GitHubReleaseAssetResolver _resolver;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;

    public REFrameworkDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        string? cacheDir = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _overrides = overrides;
        _resolver = new GitHubReleaseAssetResolver(http);
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "reframework");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.REFramework);

    public static bool IsGameEligible(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.ForcedArchitecture == PeArchitecture.X86) return false;
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath) &&
            game.ForcedArchitecture is null && PeArchitectureDetector.Detect(game.ExecutablePath) == PeArchitecture.X86)
            return false;
        return !string.IsNullOrWhiteSpace(game.EngineHint) &&
               game.EngineHint.Contains("RE Engine", StringComparison.OrdinalIgnoreCase);
    }

    public static string EligibilityReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "RE Framework is a Windows dinput8 injection component and is not used for native-Linux targets.";
        if (game.ForcedArchitecture == PeArchitecture.X86 ||
            (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath) &&
             game.ForcedArchitecture is null && PeArchitectureDetector.Detect(game.ExecutablePath) == PeArchitecture.X86))
            return "The current monolithic RE Framework nightly package is enabled only for 64-bit RE Engine games.";
        if (string.IsNullOrWhiteSpace(game.EngineHint) ||
            !game.EngineHint.Contains("RE Engine", StringComparison.OrdinalIgnoreCase))
            return "RE Framework está disponível para títulos RE Engine detectados.";
        return $"Eligible for RE Framework ({game.EngineHint}).";
    }

    public async Task<GitHubResolvedReleaseAsset> ResolveLatestAsync(CancellationToken cancellationToken = default)
        => await _resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            ReleasesApiUrl,
            ExactAssetName: MonolithicZipName), cancellationToken).ConfigureAwait(false);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (!IsGameEligible(game)) return ComponentOperationResult.Fail(EligibilityReason(game));
        if (!WineRegistryEditor.HasUserHive(game.PrefixPath))
            return ComponentOperationResult.Fail("Start the RE Engine game once so its Wine/Proton prefix exists before installing RE Framework.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var deployDir = ResolveDeployDirectory(game);
        if (string.IsNullOrWhiteSpace(deployDir) || !Directory.Exists(deployDir))
            return ComponentOperationResult.Fail("Game binary/install directory is not available for RE Framework deployment.");

        var target = FindCaseInsensitiveFile(deployDir, DllFileName) ?? Path.Combine(deployDir, DllFileName);
        var existingRecord = GetRecord(game);
        if (File.Exists(target) && existingRecord is null)
            return ComponentOperationResult.Fail("dinput8.dll already exists beside the game executable but is not owned by RHI Linux. The external proxy is protected from overwrite.");
        if (existingRecord is not null)
            return ComponentOperationResult.Fail("RE Framework is already managed by RHI Linux for this game. Remove it before reinstalling.");

        var release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
        var (cachedDll, download) = await DownloadExtractAndValidateAsync(release, cancellationToken).ConfigureAwait(false);
        var applicationName = Path.GetFileName(game.ExecutablePath ?? "");
        WineOverrideRecord? overrideRecord = null;
        var deployed = false;
        // The Wine override is journaled before it is written (#23): a crash before the record is saved
        // leaves an entry start-up recovery rolls back, never an override nothing owns.
        var (operation, journalRefusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(
            _journal, "RE Framework install: " + game.Name, game, ManagedComponentKind.REFramework, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var journaled = operation;
        // Downloaded and validated: from the override on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            overrideRecord = await _overrides.EnsureNativeBuiltinAsync(operation, game.PrefixPath!, DllFileName, applicationName, cancellationToken).ConfigureAwait(false);
            // Verified durable copy; never replaces a dinput8.dll that appeared since the check above.
            var deployedHash = await AtomicFile.CopyAsync(cachedDll, target, cancellationToken: cancellationToken, overwrite: false).ConfigureAwait(false);
            deployed = true;

            _records.Upsert(LinuxDeploymentRecovery.StampOperation(operation, new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.REFramework,
                BinaryDirectory = deployDir,
                SourceUrl = release.AssetUrl,
                Version = NormalizeVersion(release.Version),
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = overrideRecord,
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
                    ["REFramework.AssetName"] = release.AssetName,
                    ["REFramework.EngineHint"] = game.EngineHint ?? "",
                    ["REFramework.ApplicationName"] = applicationName,
                    [VerifiedDownload.MetadataSha256Key] = download.Sha256,
                    [VerifiedDownload.MetadataIntegrityKey] = download.Integrity
                }
            }));
            // The record carries the operation id: it is the commit point.
            if (operation is not null) await operation.CommitAsync().ConfigureAwait(false);
            return ComponentOperationResult.Ok($"RE Framework {NormalizeVersion(release.Version)} installed with per-game dinput8 Wine override.", target);
        }
        catch
        {
            if (deployed && !FileCleanup.TryDelete(target, out var deleteError))
                AppLog.Warn("reframework", $"Rollback could not remove {target}", deleteError);
            _overrides.TryRestore(game.PrefixPath, overrideRecord);
            throw;
        }
    }

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null) return new ManagedUpdateCheck(false, false, "RE Framework is not managed by RHI Linux.");
        try
        {
            var latest = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            var latestVersion = NormalizeVersion(latest.Version);
            var update = !string.Equals(record.Version, latestVersion, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.SourceUrl, latest.AssetUrl, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                update ? $"RE Framework update available: {record.Version ?? "unknown"} → {latestVersion}." : $"RE Framework {record.Version ?? latestVersion} is current.",
                record.Version, latestVersion);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"RE Framework update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public async Task<ComponentOperationResult> UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RE Framework is not managed by RHI Linux.");
        var owned = record.Files.SingleOrDefault(file =>
            Path.GetFileName(file.Path).Equals(DllFileName, StringComparison.OrdinalIgnoreCase));
        if (owned is null || string.IsNullOrWhiteSpace(owned.Sha256) || !File.Exists(owned.Path))
            return ComponentOperationResult.Fail("RE Framework managed dinput8.dll ownership is incomplete; update is blocked conservatively.");
        var currentHash = await FileHash.Sha256HexAsync(owned.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, owned.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RHI-managed RE Framework dinput8.dll was modified after installation. Update is blocked to preserve the changed file.");

        GitHubResolvedReleaseAsset release;
        string cachedDll;
        VerifiedDownload download;
        try
        {
            release = await ResolveLatestAsync(cancellationToken).ConfigureAwait(false);
            (cachedDll, download) = await DownloadExtractAndValidateAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"RE Framework update preparation failed: {ex.Message}");
        }

        var backup = owned.Path + $".rhi-update-backup-{Guid.NewGuid():N}";
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            AtomicFile.Copy(owned.Path, backup, currentHash);
            owned.Sha256 = await AtomicFile.CopyAsync(cachedDll, owned.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
            record.SourceUrl = release.AssetUrl;
            record.Version = NormalizeVersion(release.Version);
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata["REFramework.AssetName"] = release.AssetName;
            download.RecordIn(record.Metadata);
            _records.Upsert(record);
            FileCleanup.TryDelete(backup);
            return ComponentOperationResult.Ok($"RE Framework updated to {record.Version}; existing Wine override ownership was preserved.", owned.Path);
        }
        catch (Exception ex)
        {
            if (!File.Exists(backup))
                return ComponentOperationResult.Fail($"RE Framework update rolled back: {ex.Message}");
            try
            {
                // The backup is the previous dinput8.dll; it is deleted only once it is back in place.
                AtomicFile.Copy(backup, owned.Path, currentHash);
                FileCleanup.TryDelete(backup);
                return ComponentOperationResult.Fail($"RE Framework update rolled back: {ex.Message}");
            }
            catch (Exception restoreError)
            {
                AppLog.Warn("reframework", $"Update rollback could not restore {owned.Path}; the previous file is kept at {backup}", restoreError);
                return ComponentOperationResult.Fail($"RE Framework update failed: {ex.Message}{DeploymentMessages.PreviousFileKeptFragment}{backup}");
            }
        }
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("RE Framework is not managed by RHI Linux. External dinput8.dll files are protected from removal.");

        var owned = record.Files.SingleOrDefault(file =>
            Path.GetFileName(file.Path).Equals(DllFileName, StringComparison.OrdinalIgnoreCase));
        if (owned is null || string.IsNullOrWhiteSpace(owned.Sha256))
            return ComponentOperationResult.Fail("RE Framework ownership hash is missing; removal is blocked conservatively.");

        if (File.Exists(owned.Path))
        {
            var current = await FileHash.Sha256HexAsync(owned.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("RHI-managed RE Framework dinput8.dll was modified after installation. Remove is blocked to preserve the changed file.");
        }

        if (string.IsNullOrWhiteSpace(game.PrefixPath))
            return ComponentOperationResult.Fail("The Wine/Proton prefix is unavailable, so the dinput8 override cannot be restored safely.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        WineOverrideRestoreResult overrideRestore;
        try
        {
            overrideRestore = _overrides.Restore(game.PrefixPath!, record.WineOverride);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RE Framework removal stopped before deleting dinput8.dll because the Wine override could not be restored: {ex.Message}");
        }

        try
        {
            if (File.Exists(owned.Path)) File.Delete(owned.Path);
        }
        catch (Exception ex)
        {
            // Keep the game usable if filesystem teardown failed after registry restoration: put RHI's value back
            // where this removal restored it (a value set since stays, #30).
            if (overrideRestore.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"RE Framework dinput8.dll could not be removed: {ex.Message}");
        }

        _records.Remove(game, ManagedComponentKind.REFramework);
        return ComponentOperationResult.Ok("RE Framework removed and the previous per-game dinput8 Wine override restored."
            + WineDllOverrideService.KeptValueNotes(overrideRestore), owned.Path);
    }

    internal static string ResolveDeployDirectory(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.BinaryDirectory! : game.InstallPath;

    internal static string? FindCaseInsensitiveFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        foreach (var part in version.Split('-', StringSplitOptions.RemoveEmptyEntries))
            if (part.All(char.IsDigit)) return part;
        return version;
    }

    /// <summary>
    /// The release zip from the stamped download cache under the REFramework host policy (reused while its stamp
    /// matches URL, version and GitHub digest; verified against the digest when published, #26, #35), and its
    /// dinput8.dll extracted through the checked extraction into an entry bound to the zip's hash.
    /// </summary>
    private async Task<(string Dll, VerifiedDownload Download)> DownloadExtractAndValidateAsync(
        GitHubResolvedReleaseAsset release,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var safeVersion = string.Concat(NormalizeVersion(release.Version).Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_'));
        var zipPath = Path.Combine(_cacheDir, $"{safeVersion}-{MonolithicZipName}");
        var dllPath = Path.Combine(_cacheDir, $"{safeVersion}-{DllFileName}");

        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, release.AssetUrl, zipPath, DownloadSources.REFramework, release.Version, release.Sha256,
            validate: (temp, _) =>
            {
                using var archive = ZipFile.OpenRead(temp);
                var count = archive.Entries.Count(entry => entry.Name.Equals(DllFileName, StringComparison.OrdinalIgnoreCase));
                if (count != 1)
                    throw new InvalidDataException($"REFramework.zip must contain exactly one {DllFileName}; found {count}.");
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (CacheStamp.IsValidBoundTo(dllPath, release.AssetUrl, release.Version, download.Sha256))
            return (dllPath, download);
        CacheStamp.Invalidate(dllPath);
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entries = archive.Entries
                .Where(entry => entry.Name.Equals(DllFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException($"REFramework.zip must contain exactly one {DllFileName}; found {entries.Length}.");
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entries[0], dllPath, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }

        string? refusal = null;
        if (!BinarySignatureInspector.HasPeSignature(dllPath, MinimumDllSize))
            refusal = "Extracted RE Framework dinput8.dll is not a valid PE payload or is unexpectedly small.";
        else if (PeArchitectureDetector.Detect(dllPath) != PeArchitecture.X64)
            refusal = "Extracted RE Framework dinput8.dll is not the expected 64-bit payload.";
        if (refusal is not null)
        {
            FileCleanup.TryDelete(dllPath);
            throw new InvalidDataException(refusal);
        }
        CacheStamp.WriteBoundTo(dllPath, release.AssetUrl, release.Version, download.Sha256);
        return (dllPath, download);
    }
}
