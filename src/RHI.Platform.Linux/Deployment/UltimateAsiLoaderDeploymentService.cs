using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux-native Ultimate ASI Loader deployment for Wine/Proton targets.
///
/// The upstream project publishes dinput8.dll inside architecture-specific ZIPs.
/// RHI may deploy that binary under any loader-supported proxy name, then creates a
/// per-application Wine native,builtin override so Wine actually loads the game-local proxy.
///
/// Existing non-RHI target DLLs are preserved through Ultimate ASI Loader's documented
/// &lt;name&gt;Hooked.dll chaining convention. RHI-managed proxies are never chained over.
/// </summary>
public sealed class UltimateAsiLoaderDeploymentService
{
    public const string GitHubApiUrl = "https://api.github.com/repos/ThirteenAG/Ultimate-ASI-Loader/releases/latest";
    public const string ReleasesUrl = "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases";
    public const string DefaultInnerDllName = "dinput8.dll";

    /// <summary>
    /// F3 (review w12/mfg): the check's line (and the update's refusal) when the game's executable no longer has the
    /// architecture the loader was installed for; "x86 → x64" (installed for → now) follows.
    /// </summary>
    public const string ArchitectureChangedPrefix =
        "Not updated: the game's executable no longer has the architecture Ultimate ASI Loader was installed for, and an update never swaps it for the other one. Remove it, then Install it again. Installed for → now: ";

    private const string Asset32 = "Ultimate-ASI-Loader.zip";
    private const string Asset64 = "Ultimate-ASI-Loader_x64.zip";
    private const string MetaProxyName = "UAL.ProxyName";
    private const string MetaHookedPath = "UAL.HookedPath";
    private const string MetaHadOriginal = "UAL.HadOriginal";
    private const string MetaAssetSha256 = "UAL.AssetSha256";
    private const string MetaArchitecture = "UAL.Architecture";

    public static readonly IReadOnlyList<string> Win32Names =
    [
        "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll",
        "ddraw.dll", "dinput.dll", "dinput8.dll", "dsound.dll",
        "msacm32.dll", "msvfw32.dll",
        "version.dll", "wininet.dll", "winmm.dll", "winhttp.dll",
        "xlive.dll", "binkw32.dll", "bink2w32.dll", "vorbisFile.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll",
        "xinput9_1_0.dll", "xinputuap.dll",
    ];

    public static readonly IReadOnlyList<string> Win64Names =
    [
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll",
        "dinput8.dll", "dsound.dll",
        "version.dll", "wininet.dll", "winmm.dll", "winhttp.dll",
        "binkw64.dll", "bink2w64.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll",
        "xinput9_1_0.dll", "xinputuap.dll",
    ];

    public static readonly IReadOnlyList<string> RecommendedNames =
        ["version.dll", "winmm.dll", "winhttp.dll"];

    public static readonly IReadOnlyList<string> RhiConflictNames =
        ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll"];

    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly DeploymentJournal? _journal;
    private readonly string _cacheRoot;

    public UltimateAsiLoaderDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        string? cacheRoot = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _records = records;
        _overrides = overrides;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "ual");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.UltimateAsiLoader);

    public string? InstalledProxyName(DetectedGame game)
        => GetRecord(game)?.Metadata.GetValueOrDefault(MetaProxyName);

    public string? InstalledVersion(DetectedGame game)
        => GetRecord(game)?.Version;

    public static IReadOnlyList<string> SupportedNamesFor(DetectedGame game)
        => ResolveArchitecture(game) switch
        {
            PeArchitecture.X86 => Win32Names,
            PeArchitecture.X64 => Win64Names,
            _ => []
        };

    public static bool IsSupportedProxyName(DetectedGame game, string? dllName)
        => !string.IsNullOrWhiteSpace(dllName)
           && SupportedNamesFor(game).Contains(Path.GetFileName(dllName.Trim()), StringComparer.OrdinalIgnoreCase);

    public static string RecommendedDefaultFor(DetectedGame game)
    {
        var supported = SupportedNamesFor(game);
        foreach (var candidate in new[] { "version.dll", "winmm.dll", "winhttp.dll", "dinput8.dll" })
            if (supported.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return candidate;
        return supported.FirstOrDefault() ?? "";
    }

    public async Task<UalUpdateStatus> CheckLatestAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return new UalUpdateStatus(false, InstalledVersion(game), null, false, "ASI Loader requires a detected x86 or x64 Windows executable.");
        // w12 follow-up (re-review mfg N3): the Extras check reads as Update All's does once the game's executable is the
        // other bitness (could not check, Remove then Install), never "Update available" for an update that would refuse.
        if (GetRecord(game) is { } record && ArchitectureChanged(record, game) is { } changed)
            return new UalUpdateStatus(false, record.Version, null, false, changed);

        try
        {
            var release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
            var current = InstalledVersion(game);
            var hasUpdate = !string.IsNullOrWhiteSpace(current)
                            && !string.Equals(current, release.Version, StringComparison.OrdinalIgnoreCase);
            return new UalUpdateStatus(true, current, release.Version, hasUpdate,
                string.IsNullOrWhiteSpace(current)
                    ? $"Latest Ultimate ASI Loader: {release.Version}"
                    : hasUpdate
                        ? $"Update available: {current} → {release.Version}"
                        : $"Ultimate ASI Loader {current} is current.");
        }
        catch (Exception ex)
        {
            return new UalUpdateStatus(false, InstalledVersion(game), null, false, $"Could not check Ultimate ASI Loader: {ex.Message}");
        }
    }

    /// <summary>
    /// #238: the Update All check, for the game's own architecture (x86 and x64 each from their own asset). The proxy removed
    /// outside RHI is never put back (seen before any network call); the update is planned only while the proxy still holds
    /// RHI's bytes (hashed only then); a failed lookup or an unknown architecture could not check.
    /// </summary>
    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return new ManagedUpdateCheck(false, false, "Ultimate ASI Loader is not RHI-managed.");
        // F1 (review w12/mfg): a game held by its own earlier undo gets the hold's restart text before its files are read.
        if (await ManagedUpdateFileGuard.HeldAsync(_journal, game, ManagedComponentKind.UltimateAsiLoader, record.Version).ConfigureAwait(false) is { } held)
            return held;
        if (!record.Metadata.TryGetValue(MetaProxyName, out var proxyName) || string.IsNullOrWhiteSpace(proxyName))
            return new ManagedUpdateCheck(true, false, ManagedUpdateFileGuard.MissingProxyNameMessage,
                record.Version, CouldNotCheck: true, LocalReason: true);
        // F3 (review w12/mfg): never an unattended swap to the other bitness (seen before any network call).
        if (ArchitectureChanged(record, game) is { } changed)
            return new ManagedUpdateCheck(true, false, changed, record.Version, CouldNotCheck: true, LocalReason: true);
        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        if (ManagedUpdateFileGuard.Removed([targetPath], record.Version) is { } removed)
            return removed;
        var status = await CheckLatestAsync(game, cancellationToken).ConfigureAwait(false);
        var planned = new ManagedUpdateCheck(true, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: !status.Success);
        var fileRecord = record.Files.FirstOrDefault(f => Path.GetFullPath(f.Path).Equals(Path.GetFullPath(targetPath), StringComparison.Ordinal));
        return await ManagedUpdateFileGuard.BeforePlanningAsync(planned, [(targetPath, fileRecord?.Sha256)], cancellationToken).ConfigureAwait(false);
    }

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string dllName,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, dllName, staged: null, cancellationToken);

    /// <summary>
    /// Downloads and validates the payload <see cref="InstallAsync(DetectedGame, string, CancellationToken)"/> would
    /// deploy for <paramref name="game"/>, changing nothing in the game: a component that installs UAL as part of its
    /// own install (ShortFuse's auto-setup) stages it before its write gate, so no download runs after its first change.
    /// </summary>
    internal async Task<UalStagedPayload> StageAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var architecture = ResolveArchitecture(game);
        var release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
        var stagedDll = await EnsureCachedDllAsync(release, architecture, cancellationToken).ConfigureAwait(false);
        return new UalStagedPayload(release, stagedDll, architecture);
    }

    /// <param name="staged">A payload <see cref="StageAsync"/> prepared for this game: used instead of downloading.</param>
    internal async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        string dllName,
        UalStagedPayload? staged,
        CancellationToken cancellationToken)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("Ultimate ASI Loader is a Windows DLL proxy and is not applicable to a native Linux target.");

        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is already managed for this game. Use Update/Reinstall or Remove first.");

        var preflight = ValidateTarget(game, dllName);
        if (!preflight.Success)
            return ComponentOperationResult.Fail(preflight.Message);
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var architecture = ResolveArchitecture(game);
        var binaryDir = ResolveBinaryDirectory(game)!;
        var proxyName = Path.GetFileName(dllName.Trim());
        var targetPath = Path.Combine(binaryDir, proxyName);
        var hookedPath = Path.Combine(binaryDir, HookedName(proxyName));
        var hadOriginal = File.Exists(targetPath);

        if (IsRhiManagedPath(game, targetPath))
            return ComponentOperationResult.Fail(
                $"{proxyName} is already owned by another RHI-managed component. Choose a different ASI Loader proxy name; RHI will not reuse or chain over another managed proxy path.");

        if (hadOriginal && File.Exists(hookedPath))
            return ComponentOperationResult.Fail(
                $"Cannot chain {proxyName}: {Path.GetFileName(hookedPath)} already exists. RHI will not overwrite an existing Hooked backup.");

        UalReleaseInfo release;
        string stagedDll;
        if (staged is not null && staged.Architecture == architecture && File.Exists(staged.StagedDll))
        {
            (release, stagedDll) = (staged.Release, staged.StagedDll);
        }
        else
        {
            try
            {
                release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
                stagedDll = await EnsureCachedDllAsync(release, architecture, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"Ultimate ASI Loader download/validation failed: {ex.Message}");
            }
        }

        WineOverrideRecord? wineOverride = null;

        // Journaled (#23): the override, the chained original's move and the proxy are recorded before they happen, so a
        // crash before the record is saved is rolled back at the next start (original back at its name, no proxy).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "Ultimate ASI Loader install: " + game.Name, game, ManagedComponentKind.UltimateAsiLoader, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;

        // Downloaded and validated: from the first change on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        string targetHash;
        try
        {
            wineOverride = await _overrides.EnsureNativeBuiltinAsync(scope.Operation, game.PrefixPath!, proxyName, Path.GetFileName(game.ExecutablePath), cancellationToken).ConfigureAwait(false);

            await scope.TrackAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (hadOriginal)
            {
                await scope.TrackAsync(hookedPath, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(hookedPath, scope.Files.Find(targetPath)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                AtomicFile.Rename(targetPath, hookedPath);
            }

            // Verified durable copy; never replaces a file that appeared at the proxy name meanwhile.
            targetHash = await scope.PlaceCopyAsync(stagedDll, targetPath, cancellationToken, overwrite: false).ConfigureAwait(false);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaProxyName] = proxyName,
                [MetaHadOriginal] = hadOriginal ? "1" : "0",
                [MetaArchitecture] = architecture.ToString(),
                [MetaAssetSha256] = release.AssetSha256 ?? "",
            };
            // The cached DLL (the verified cache entry) is what was copied: its hash is the deployed file's.
            VerifiedDownload.Record(metadata, targetHash, HasPublishedDigest(release));
            if (hadOriginal)
                metadata[MetaHookedPath] = hookedPath;

            _records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.UltimateAsiLoader,
                BinaryDirectory = binaryDir,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                WineOverride = wineOverride,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = targetPath,
                        ExistedBeforeInstall = false,
                        Sha256 = targetHash,
                        AllowUserChanges = false
                    }
                ],
                Metadata = metadata
            }));
        }
        catch (Exception ex)
        {
            // Every file back to its pre-install bytes from the scope's copies (a copy that cannot be put back is kept
            // and named), then the override.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Ultimate ASI Loader");
            if (!rollback.Success)
                AppLog.Warn("ual", $"Install rollback could not put {proxyName} back: {string.Join("; ", rollback.Failures)}");
            if (rollback.RolledBack) _overrides.TryRestore(game.PrefixPath, wineOverride);

            return ComponentOperationResult.Fail($"Ultimate ASI Loader install rolled back: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record carries the operation id: it is the commit point.
        await scope.CommitAsync().ConfigureAwait(false);

        var chainText = hadOriginal
            ? $" Existing {proxyName} was preserved as {Path.GetFileName(hookedPath)} and will be restored on Remove."
            : "";
        return ComponentOperationResult.Ok(
            $"Ultimate ASI Loader {release.Version} installed as {proxyName}; Wine override {Path.GetFileName(game.ExecutablePath)}:{Path.GetFileNameWithoutExtension(proxyName)}=native,builtin applied.{chainText}");
    }

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is not managed by RHI Linux for this game.");

        // F1 (review w12/mfg; toolkit rule since w10 D15): the hold's restart text before the hash check below.
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.UltimateAsiLoader).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        if (!record.Metadata.TryGetValue(MetaProxyName, out var proxyName) || string.IsNullOrWhiteSpace(proxyName))
            return ComponentOperationResult.Fail(ManagedUpdateFileGuard.MissingProxyNameMessage);
        // F3 (review w12/mfg): the loader picked for the game's old bitness is never swapped for the other one behind the
        // user's back (Update All reaches this unattended); Remove, then Install, picks the right one.
        if (ArchitectureChanged(record, game) is { } changed)
            return ComponentOperationResult.Fail(changed);

        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(f => Path.GetFullPath(f.Path).Equals(Path.GetFullPath(targetPath), StringComparison.Ordinal));
        if (fileRecord is null || string.IsNullOrWhiteSpace(fileRecord.Sha256))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing the managed proxy hash.");

        if (!File.Exists(targetPath))
            return ComponentOperationResult.Fail($"Managed ASI Loader proxy is missing: {targetPath}. RHI will not recreate it without an explicit Remove/reinstall cycle.");

        var currentHash = await FileHash.Sha256HexAsync(targetPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"Managed ASI Loader proxy was modified outside RHI: {targetPath}. Update is blocked to protect the external change.");

        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail("Could not resolve x86/x64 architecture for the selected game.");

        UalReleaseInfo release;
        string stagedDll;
        try
        {
            release = await FetchLatestReleaseAsync(architecture, cancellationToken).ConfigureAwait(false);
            stagedDll = await EnsureCachedDllAsync(release, architecture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Ultimate ASI Loader update download/validation failed: {ex.Message}");
        }

        if (string.Equals(record.Version, release.Version, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Ok($"Ultimate ASI Loader {record.Version} is already current.");

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "Ultimate ASI Loader update: " + game.Name, game, ManagedComponentKind.UltimateAsiLoader, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            // Verified durable replace, tracked first: a failure or a crash before the record is saved puts the
            // previous proxy back.
            fileRecord.Sha256 = await scope.PlaceCopyAsync(stagedDll, targetPath, cancellationToken).ConfigureAwait(false);
            record.Version = release.Version;
            record.SourceUrl = release.AssetUrl;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Metadata[MetaAssetSha256] = release.AssetSha256 ?? "";
            VerifiedDownload.Record(record.Metadata, fileRecord.Sha256, HasPublishedDigest(release));
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Ultimate ASI Loader");
            return ComponentOperationResult.Fail($"Ultimate ASI Loader update failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok($"Ultimate ASI Loader updated to {release.Version} as {proxyName}.");
    }

    public async Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("Ultimate ASI Loader is not managed by RHI Linux. Removal is locked to protect external files.");

        // w12 final review M1: a record without its proxy's name is removed by its file list (the way out the check names).
        if (ProxylessRecordRemoval.Applies(record, MetaProxyName))
            return await RemoveWithoutProxyNameAsync(game, record, cancellationToken).ConfigureAwait(false);
        var proxyName = record.Metadata[MetaProxyName];
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var targetPath = Path.Combine(record.BinaryDirectory, proxyName);
        var fileRecord = record.Files.FirstOrDefault(f => Path.GetFullPath(f.Path).Equals(Path.GetFullPath(targetPath), StringComparison.Ordinal));
        if (fileRecord is null || string.IsNullOrWhiteSpace(fileRecord.Sha256))
            return ComponentOperationResult.Fail("Ultimate ASI Loader deployment record is missing the managed proxy hash.");

        var hadOriginal = record.Metadata.GetValueOrDefault(MetaHadOriginal) == "1";
        var hookedPath = record.Metadata.GetValueOrDefault(MetaHookedPath);
        if (hadOriginal && (string.IsNullOrWhiteSpace(hookedPath) || !File.Exists(hookedPath)))
            return ComponentOperationResult.Fail(
                $"Original chained DLL is missing ({hookedPath ?? HookedName(proxyName)}). Remove is blocked before touching the managed proxy.");

        if (File.Exists(targetPath))
        {
            var currentHash = await FileHash.Sha256HexAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, fileRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail(
                    $"Managed ASI Loader proxy was modified outside RHI: {targetPath}. Remove is blocked before teardown.");
        }

        // Journaled (#23): the record is stamped with the removal's id first; a crash before it is removed puts the
        // proxy, the chained original and the override back as the record still describes them.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "Ultimate ASI Loader removal: " + game.Name, game, ManagedComponentKind.UltimateAsiLoader, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;

        // Cancel is honoured only before the first change; from here the removal runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            await scope.TrackAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (hadOriginal)
            {
                // The original goes back over the proxy in one rename.
                await scope.TrackAsync(hookedPath!, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(targetPath, scope.Files.Find(hookedPath!)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                AtomicFile.Rename(hookedPath!, targetPath, overwrite: true);
            }
            else if (File.Exists(targetPath))
            {
                AtomicFile.Delete(targetPath);
            }

            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.UltimateAsiLoader);
        }
        catch (Exception ex)
        {
            // Back to the managed-installed state so a retry remains possible: files from the scope's copies, and the
            // override this removal restored (only where it did, #30) since the record stays.
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "ual").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Ultimate ASI Loader", removal: true);
            if (rollback.RolledBack && overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            if (!rollback.Success)
                AppLog.Warn("ual", $"Removal rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"Ultimate ASI Loader removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        // The record is gone: the commit point.
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(
            (hadOriginal
                ? $"Ultimate ASI Loader removed; original {proxyName} restored and Wine override rolled back."
                : $"Ultimate ASI Loader removed; Wine override for {proxyName} rolled back.")
            + WineDllOverrideService.KeptValueNotes(overrideRestore));
    }

    /// <summary>
    /// w12 final review M1: Remove for a record without its proxy's name (<see cref="ProxylessRecordRemoval"/>): every
    /// listed file that still holds RHI's bytes goes, the recorded override is restored, the record is dropped, and the
    /// files left behind are named. With a chained original (<c>UAL.HadOriginal</c>) the proxy is the listed file the
    /// original was renamed away from (<c>&lt;name&gt;</c> → <see cref="HookedName"/>): the original goes back over it while
    /// <c>UAL.HookedPath</c> exists and the proxy still holds RHI's bytes (or is gone); otherwise the proxy stays (the game
    /// would lose that DLL name altogether) and is named, and the record is still dropped.
    /// </summary>
    private async Task<ComponentOperationResult> RemoveWithoutProxyNameAsync(
        DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);
        if (await DeploymentScope.HoldRefusalAsync(_journal, game, ManagedComponentKind.UltimateAsiLoader).ConfigureAwait(false) is { } held)
            return ComponentOperationResult.Fail(held);
        var plan = await ProxylessRecordRemoval.PlanAsync(record, cancellationToken).ConfigureAwait(false);
        var delete = plan.Delete.ToList();
        var left = plan.Left.ToList();
        string? restoreOver = null, hooked = null;
        var notes = "";
        if (record.Metadata.GetValueOrDefault(MetaHadOriginal) == "1")
        {
            var hookedText = record.Metadata.GetValueOrDefault(MetaHookedPath);
            hooked = string.IsNullOrWhiteSpace(hookedText) ? null : Path.GetFullPath(hookedText);
            var proxy = hooked is null ? null : delete.Concat(left).FirstOrDefault(path => string.Equals(
                Path.Combine(Path.GetDirectoryName(path)!, HookedName(Path.GetFileName(path))), hooked, StringComparison.Ordinal));
            var hookedExists = hooked is not null && File.Exists(hooked);
            if (proxy is not null && hookedExists && (delete.Contains(proxy) || !File.Exists(proxy)))
            {
                restoreOver = proxy;
                delete.Remove(proxy);
                left.Remove(proxy);
                notes = ProxylessRecordRemoval.ChainedOriginalBackNote;
            }
            else
            {
                foreach (var path in delete.Where(path => proxy is null || path == proxy).ToList())
                {
                    delete.Remove(path);
                    left.Add(path);
                }
                if (hookedExists) left.Add(hooked!);
                notes = hookedExists ? ProxylessRecordRemoval.ChainedOriginalKeptNote : ProxylessRecordRemoval.ChainedOriginalMissingNote;
            }
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "Ultimate ASI Loader removal: " + game.Name, game, ManagedComponentKind.UltimateAsiLoader, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? overrideRestore = null;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var path in delete)
                await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            if (restoreOver is not null)
            {
                // The original goes back over the proxy in one rename, as in the named removal.
                await scope.TrackAsync(restoreOver, cancellationToken).ConfigureAwait(false);
                await scope.TrackAsync(hooked!, cancellationToken).ConfigureAwait(false);
                await scope.DeclareWriteAsync(restoreOver, scope.Files.Find(hooked!)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                AtomicFile.Rename(hooked!, restoreOver, overwrite: true);
            }
            overrideRestore = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath!, record.WineOverride, cancellationToken).ConfigureAwait(false);
            _records.Remove(game, ManagedComponentKind.UltimateAsiLoader);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "ual").ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("Ultimate ASI Loader", removal: true);
            if (rollback.RolledBack && overrideRestore?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryReapply(game.PrefixPath, record.WineOverride);
            if (!rollback.Success)
                AppLog.Warn("ual", $"Removal rollback did not complete: {string.Join("; ", rollback.Failures)}");
            return ComponentOperationResult.Fail($"Ultimate ASI Loader removal failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(ProxylessRecordRemoval.Message("Ultimate ASI Loader", left, notes + WineDllOverrideService.KeptValueNotes(overrideRestore)));
    }

    internal static PeArchitecture ResolveArchitecture(DetectedGame game) => GameArchitecture.Of(game);

    /// <summary>
    /// F3 (review w12/mfg): the refusal when the record names the architecture it was installed for and the game's
    /// executable now reads as the other one (a game patch, or Edit game pointing at another exe); null otherwise (no
    /// recorded architecture, the same one, or an architecture the update would refuse on its own).
    /// </summary>
    private static string? ArchitectureChanged(LinuxDeploymentRecord record, DetectedGame game)
    {
        if (!record.Metadata.TryGetValue(MetaArchitecture, out var recordedText)
            || !Enum.TryParse<PeArchitecture>(recordedText, ignoreCase: true, out var recorded)
            || recorded is not (PeArchitecture.X86 or PeArchitecture.X64))
            return null;
        var now = ResolveArchitecture(game);
        if (now is not (PeArchitecture.X86 or PeArchitecture.X64) || now == recorded) return null;
        return ArchitectureChangedPrefix + $"{recorded.ToString().ToLowerInvariant()} → {now.ToString().ToLowerInvariant()}";
    }

    internal static string? ResolveBinaryDirectory(DetectedGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.BinaryDirectory) && Directory.Exists(game.BinaryDirectory))
            return Path.GetFullPath(game.BinaryDirectory);
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            return Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath));
        return null;
    }

    public static string HookedName(string dllName)
        => $"{Path.GetFileNameWithoutExtension(dllName)}Hooked{Path.GetExtension(dllName)}";

    internal UalTargetValidation ValidateTarget(DetectedGame game, string? dllName)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return new(false, "Start the Windows game once so its Wine/Proton prefix exists before installing Ultimate ASI Loader.");
        if (!WineRegistryEditor.HasUserHive(game.PrefixPath))
            return new(false, "Wine/Proton user.reg is missing. Start the game once so the prefix is initialized.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return new(false, "A Windows game executable could not be resolved for Ultimate ASI Loader.");
        var architecture = ResolveArchitecture(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return new(false, "Ultimate ASI Loader requires a detected x86 or x64 Windows executable.");
        var binaryDir = ResolveBinaryDirectory(game);
        if (binaryDir is null)
            return new(false, "The game binary directory could not be resolved.");
        if (string.IsNullOrWhiteSpace(dllName) || !IsSupportedProxyName(game, dllName))
            return new(false, $"'{dllName}' is not a supported {architecture} Ultimate ASI Loader proxy name.");
        return new(true, "Ready");
    }

    private bool IsRhiManagedPath(DetectedGame game, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var record in _records.FindAll(game))
        {
            if (record.Component == ManagedComponentKind.UltimateAsiLoader) continue;
            if (record.Files.Any(file =>
                    Path.GetFullPath(file.Path).Equals(full, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private async Task<UalReleaseInfo> FetchLatestReleaseAsync(
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        var json = await TrustedDownload.GetStringAsync(_http, GitHubApiUrl, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        return ParseReleaseJson(json, architecture);
    }

    internal static UalReleaseInfo ParseReleaseJson(string json, PeArchitecture architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
                  ?? throw new InvalidDataException("Ultimate ASI Loader release has no tag_name.");
        var version = tag.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Ultimate ASI Loader release tag is empty.");

        var assetName = architecture == PeArchitecture.X86 ? Asset32 : Asset64;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidDataException($"{assetName} has no browser_download_url.");

            string? digest = null;
            if (asset.TryGetProperty("digest", out var digestElement))
            {
                var raw = digestElement.GetString();
                if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = raw["sha256:".Length..].Trim().ToLowerInvariant();
            }

            return new UalReleaseInfo(version, assetName, url, digest);
        }

        throw new InvalidDataException($"Latest Ultimate ASI Loader release does not contain {assetName}.");
    }

    internal async Task<string> EnsureCachedDllAsync(
        UalReleaseInfo release,
        PeArchitecture architecture,
        CancellationToken cancellationToken)
    {
        var archFolder = architecture == PeArchitecture.X86 ? "x86" : "x64";
        var versionDir = Path.Combine(_cacheRoot, archFolder, SanitizePathPart(release.Version));
        var cachedDll = Path.Combine(versionDir, DefaultInnerDllName);
        var metadataPath = Path.Combine(versionDir, "source.json");

        // Reuse only a DLL this service stamped for exactly this release asset (#142): a file that
        // reached the cache any other way is downloaded again, never deployed.
        if (CacheStamp.IsValid(cachedDll, release.AssetUrl, release.Version, release.AssetSha256) &&
            PeArchitectureDetector.Detect(cachedDll) == architecture)
            return cachedDll;

        Directory.CreateDirectory(versionDir);
        CacheStamp.Invalidate(cachedDll);
        // The zip is only a carrier: downloaded under the UAL host policy, checked against GitHub's digest when
        // published (#26), the DLL taken out through the checked extraction, then the zip is dropped.
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, release.AssetUrl, Path.Combine(versionDir, release.AssetName), DownloadSources.UltimateAsiLoader,
            release.AssetSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        var archiveHash = download.Sha256;

        using (var archive = ZipFile.OpenRead(download.Path))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                Path.GetFileName(e.FullName).Equals(DefaultInnerDllName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{DefaultInnerDllName} was not found in {release.AssetName}.");

            if (entry.Length < 32 * 1024)
                throw new InvalidDataException($"{DefaultInnerDllName} is unexpectedly small ({entry.Length} bytes).");

            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, cachedDll, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        }

        if (PeArchitectureDetector.Detect(cachedDll) != architecture)
        {
            FileCleanup.TryDelete(cachedDll);
            throw new InvalidDataException(
                $"Extracted {DefaultInnerDllName} has the wrong PE architecture for {architecture}.");
        }

        CacheStamp.Write(cachedDll, release.AssetUrl, release.Version, release.AssetSha256);
        if (string.IsNullOrWhiteSpace(release.AssetSha256))
            TrustedDownload.NoteAcceptedWithoutDigest(DownloadSources.UltimateAsiLoader, release.AssetUrl, archiveHash);
        AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            release.Version,
            release.AssetName,
            release.AssetUrl,
            release.AssetSha256,
            DownloadedSha256 = archiveHash,
            CachedAtUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));

        return cachedDll;
    }

    /// <summary>What the cached DLL's integrity rests on: GitHub's digest of the zip, or only HTTPS + host policy (#26).</summary>
    private static bool HasPublishedDigest(UalReleaseInfo release) => !string.IsNullOrWhiteSpace(release.AssetSha256);

    private static string SanitizePathPart(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
}

/// <summary>A downloaded, validated UAL payload ready to deploy (<see cref="UltimateAsiLoaderDeploymentService.StageAsync"/>).</summary>
internal sealed record UalStagedPayload(UalReleaseInfo Release, string StagedDll, PeArchitecture Architecture);

public sealed record UalReleaseInfo(
    string Version,
    string AssetName,
    string AssetUrl,
    string? AssetSha256);

public sealed record UalUpdateStatus(
    bool Success,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string Message);

internal sealed record UalTargetValidation(bool Success, string Message);
