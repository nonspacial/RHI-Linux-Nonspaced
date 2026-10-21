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
        if (GameArchitecture.Of(game) == PeArchitecture.X86) return false;
        return !string.IsNullOrWhiteSpace(game.EngineHint) &&
               game.EngineHint.Contains("RE Engine", StringComparison.OrdinalIgnoreCase);
    }

    public static string EligibilityReason(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return "RE Framework is a Windows dinput8 injection component and is not used for native-Linux targets.";
        if (GameArchitecture.Of(game) == PeArchitecture.X86)
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
        // Journaled (#23): the override and the proxy are recorded before they are written, so a crash before the
        // record is saved is rolled back at the next start instead of leaving a dinput8.dll and override nothing owns.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RE Framework install: " + game.Name, game, ManagedComponentKind.REFramework, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Downloaded and validated: from the override on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            overrideRecord = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, DllFileName, applicationName, cancellationToken).ConfigureAwait(false);
            // Verified durable copy; never replaces a dinput8.dll that appeared since the check above.
            var deployedHash = await scope.PlaceCopyAsync(cachedDll, target, cancellationToken, overwrite: false).ConfigureAwait(false);

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
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
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RE Framework", changedFiles: target);
            if (rollback.RolledBack) _overrides.TryRestore(game.PrefixPath, overrideRecord);
            if (rollback.Success) throw;
            // The copies a failed rollback kept are named, never dropped (#31).
            throw new IOException($"{ex.Message}{DeploymentScope.RollbackNote(rollback)}", ex);
        }
        // The record carries the operation id: it is the commit point, so nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"RE Framework {NormalizeVersion(release.Version)} installed with per-game dinput8 Wine override.", target);
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

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RE Framework update: " + game.Name, game, ManagedComponentKind.REFramework, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            // Tracked with a verified copy first: a failure (or a crash before the record is saved) puts the previous
            // dinput8.dll back from it, and a copy that cannot be put back is kept and named.
            owned.Sha256 = await scope.PlaceCopyAsync(cachedDll, owned.Path, cancellationToken).ConfigureAwait(false);
            record.SourceUrl = release.AssetUrl;
            record.Version = NormalizeVersion(release.Version);
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata["REFramework.AssetName"] = release.AssetName;
            download.RecordIn(record.Metadata);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RE Framework", changedFiles: owned.Path);
            if (!rollback.Success)
                AppLog.Warn("reframework", $"Update rollback could not restore {owned.Path}: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"RE Framework update rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"RE Framework updated to {record.Version}; existing Wine override ownership was preserved.", owned.Path);
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

        // Journaled (#23): the record is stamped with the removal's id; a crash before it is removed puts the dll and
        // the override back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "RE Framework removal: " + game.Name, game, ManagedComponentKind.REFramework, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        WineOverrideRestoreResult? overrideRestore = null;
        try
        {
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            await scope.DeleteAsync(owned.Path, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.REFramework);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "reframework").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RE Framework", removal: true);
            // Keep the game usable: put RHI's value back where this removal restored it (a value set since stays, #30).
            if (rollback.RolledBack && overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            return ComponentOperationResult.Fail($"RE Framework removal failed and was rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point. Nothing after it rolls back.
        await scope.CommitAsync().ConfigureAwait(false);

        return ComponentOperationResult.Ok("RE Framework removed and the previous per-game dinput8 Wine override restored."
            + WineDllOverrideService.KeptValueNotes(overrideRestore), owned.Path);
    }

    internal static string ResolveDeployDirectory(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.BinaryDirectory! : game.InstallPath;

    /// <summary>The cache folder RE Engine games keep beside the executable (upstream 2.8.6 REF ⚙, PAR-D72).</summary>
    public const string StorageFolderName = "_storage_";

    /// <summary>
    /// Where the game's <c>_storage_</c> folder is: beside the executable (where REF's dinput8.dll lives), else in the
    /// game root (where upstream looks). Returns the first one that exists as a folder or a link, or null when neither
    /// does. Read only.
    /// </summary>
    public static string? FindStorageFolder(DetectedGame game)
    {
        foreach (var directory in new[] { ResolveDeployDirectory(game), game.InstallPath }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(directory, StorageFolderName);
            if (Directory.Exists(candidate) || new FileInfo(candidate).LinkTarget is not null) return candidate;
        }
        return null;
    }

    /// <summary>Fixed refusal texts of <see cref="DeleteStorageFolderAsync"/> (PT-BR rows under the journal anchor); a path follows.</summary>
    public const string StorageNotFoundPrefix = "No _storage_ folder was found in the game folder: ";
    public const string StorageLinkRefusedPrefix = "The _storage_ folder is a link to another place, so RHI did not delete it: ";
    public const string StorageOutsideGameRefusedPrefix = "The _storage_ folder is not inside the game folder, so RHI did not delete it: ";
    public const string StorageWineRunningPrefix = "The game is running in its Wine prefix; close it before deleting the _storage_ folder: ";
    public const string StorageDeletedPrefix = "Deleted the _storage_ folder (RE Framework rebuilds it at the next launch): ";
    public const string StoragePartlyDeletedPrefix = "The _storage_ folder could not be deleted completely; what is left is listed in rhi.log: ";
    public const string StorageUnreadablePrefix = "The _storage_ folder could not be read, so RHI deleted nothing (the reason is in rhi.log): ";

    /// <summary>
    /// Deletes the game's <c>_storage_</c> folder, RE Framework's cache that the game rebuilds at the next launch
    /// (upstream 2.8.6 REF ⚙ "Delete _storage_ folder"; a user action on a folder RHI does not own). Refuses, changing
    /// nothing, when the folder is missing, is a link (or its path leads outside the game root through one), or while
    /// the game's Wine prefix runs. Walks the folder without following links (a link inside is removed as a link, its
    /// target untouched) and deletes children before parents; once it has started it runs to the end (a cache folder half
    /// deleted is still a cache, and the user asked for all of it). The caller applies the patch-target guard.
    /// </summary>
    public Task<ComponentOperationResult> DeleteStorageFolderAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => Task.Run(() => DeleteStorageFolder(game, cancellationToken), cancellationToken);

    private ComponentOperationResult DeleteStorageFolder(DetectedGame game, CancellationToken cancellationToken)
    {
        var folder = FindStorageFolder(game);
        if (folder is null)
            return ComponentOperationResult.Fail(StorageNotFoundPrefix + Path.Combine(ResolveDeployDirectory(game), StorageFolderName));
        if (new DirectoryInfo(folder).LinkTarget is not null)
            return ComponentOperationResult.Fail(StorageLinkRefusedPrefix + folder);
        // The parent may itself be reached through a link: the folder's real place must stay inside the game root.
        if (string.IsNullOrWhiteSpace(game.InstallPath) || !PathIdentity.IsSameOrInside(folder, game.InstallPath))
            return ComponentOperationResult.Fail(StorageOutsideGameRefusedPrefix + folder);
        if (!string.IsNullOrWhiteSpace(game.PrefixPath) && _overrides.Registry.IsPrefixInUse(game.PrefixPath))
            return ComponentOperationResult.Fail(StorageWineRunningPrefix + game.PrefixPath);

        // The walk below is never cancelled part-way: Cancel counts only up to here.
        _ = OperationCancellation.BeginWrites(cancellationToken);
        var failures = new List<string>();
        // Links (to files or folders) are entries of their own: removed as links, never followed. The whole folder is
        // read before anything is deleted: a sub-folder that cannot be read refuses the delete, changing nothing, with a
        // line the dialog shows (review w3-journal #5; it used to throw past the dialog, which then showed nothing).
        List<FileSystemInfo> entries;
        try { entries = SafeFileEnumeration.Entries(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("reframework", $"_storage_ folder could not be read ({folder}); nothing was deleted", ex);
            return ComponentOperationResult.Fail(StorageUnreadablePrefix + folder);
        }
        foreach (var entry in entries.Where(entry => entry is FileInfo || entry.LinkTarget is not null))
        {
            try { File.Delete(entry.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add($"{entry.FullName} ({ex.Message})"); }
        }
        foreach (var directory in entries.OfType<DirectoryInfo>().Where(d => d.LinkTarget is null)
                     .Select(d => d.FullName).Append(folder).OrderByDescending(path => path.Length))
        {
            try { Directory.Delete(directory, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add($"{directory} ({ex.Message})"); }
        }
        if (failures.Count > 0)
        {
            AppLog.Warn("reframework", $"_storage_ folder only partly deleted ({folder}): {string.Join("; ", failures)}");
            return ComponentOperationResult.Fail(StoragePartlyDeletedPrefix + folder);
        }
        return ComponentOperationResult.Ok(StorageDeletedPrefix + folder, folder);
    }

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
