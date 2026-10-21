using System.IO.Compression;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Transactional Linux/Wine orchestration for upstream RHI's ShortFuse DLSS Frame Generation path.
/// The service keeps ShortFuse ownership separate from ReShade and Ultimate ASI Loader ownership:
/// ReShade remains a ReShade record, UAL remains a UAL record, and this record owns only the
/// temporary orchestration state plus ShortFuse/DLSS/Streamline files.
/// </summary>
public sealed class ShortFuseDeploymentService
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/RankFTW/rhi-repo/releases?per_page=100";
    public const string AddonFileName = "renodx-dlss.addon64";
    /// <summary>
    /// Upstream 2.7.9 "ZZZ load order" name. ReShade loads add-ons in directory-iteration order;
    /// Wine/Proton's NtQueryDirectoryFile sorts entries case-insensitively (ntdll init_cached_dir_data),
    /// so this name loads after the other add-ons exactly as it does on NTFS.
    /// </summary>
    public const string ZzzAddonFileName = "zzz_renodx-dlss.addon64";
    public const string ReleaseTagPrefix = "renodx-dlss-SF-";
    public const string ArchiveAssetPrefix = "renodx-dlss";
    public const string ReShadeAsiName = "ReShade64.asi";

    internal const string MetaReShadeOriginalPath = "ReShade.ShortFuseOriginalPayloadPath";
    internal const string MetaAutoConfigEnabled = "ShortFuse.AutoConfigEnabled";
    internal const string MetaUalInstalledByShortFuse = "ShortFuse.UalInstalledByShortFuse";
    internal const string MetaUalProxyName = "ShortFuse.UalProxyName";
    internal const string MetaReShadeOverrideSuspended = "ShortFuse.ReShadeOverrideSuspended";
    internal const string MetaIniPath = "ShortFuse.IniPath";
    internal const string MetaIniSectionExisted = "ShortFuse.IniSectionExisted";
    internal const string MetaHookStreamlineExisted = "ShortFuse.HookStreamlineExisted";
    internal const string MetaHookStreamlineValue = "ShortFuse.HookStreamlineValue";
    internal const string MetaHookDirectXExisted = "ShortFuse.HookDirectXExisted";
    internal const string MetaHookDirectXValue = "ShortFuse.HookDirectXValue";
    internal const string MetaAddonSha256 = "ShortFuse.AddonSha256";

    private static readonly string[] PreferredUalNames = ["winmm.dll", "version.dll", "dinput8.dll"];

    private readonly HttpClient _http;
    private readonly IFileDeploymentService _files;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly WineDllOverrideService _overrides;
    private readonly UltimateAsiLoaderDeploymentService _ual;
    private readonly DlssManagerService _dlss;
    private readonly ShortFuseSettingsStore _settings;

    /// <summary>The per-game store "Repair after move" carries over (Refs #38).</summary>
    internal Settings.IGameStateStore GameState => _settings;
    private readonly string _cacheRoot;
    private readonly DeploymentJournal? _journal;

    public ShortFuseDeploymentService(
        HttpClient http,
        IFileDeploymentService files,
        LinuxDeploymentRecordStore records,
        WineDllOverrideService overrides,
        UltimateAsiLoaderDeploymentService ual,
        DlssManagerService dlss,
        string? cacheRoot = null,
        ShortFuseSettingsStore? settings = null,
        DeploymentJournal? journal = null)
    {
        _journal = journal;
        _http = http;
        _files = files;
        _records = records;
        _overrides = overrides;
        _ual = ual;
        _dlss = dlss;
        _settings = settings ?? new ShortFuseSettingsStore();
        _cacheRoot = cacheRoot ?? Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.CacheHome, "rhi-linux", "shortfuse");
    }

    public LinuxDeploymentRecord? GetRecord(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ShortFuse);

    public bool IsManaged(DetectedGame game) => GetRecord(game) is not null;

    public bool GetAutoConfigEnabled(DetectedGame game) => _settings.IsAutoConfigEnabled(game);

    public void SetAutoConfigEnabled(DetectedGame game, bool enabled)
        => _settings.SetAutoConfigEnabled(game, enabled);

    public static bool IsAddonFileName(string fileName)
        => fileName.Equals(AddonFileName, StringComparison.OrdinalIgnoreCase)
           || fileName.Equals(ZzzAddonFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The saved per-game preference; it decides the name used by the next install.</summary>
    public bool GetZzzLoadOrderEnabled(DetectedGame game) => _settings.IsZzzLoadOrderEnabled(game);

    /// <summary>
    /// Whether ReShade will actually load ShortFuse under the ZZZ name. For a managed install this is
    /// read from the add-on on disk (the recorded path, or the other name if a rename was cut short),
    /// so the UI never claims a load order the files do not have; otherwise it is the saved preference.
    /// </summary>
    public bool GetZzzLoadOrderInEffect(DetectedGame game)
    {
        if (GetRecord(game)?.Files.FirstOrDefault(f => IsAddonFileName(Path.GetFileName(f.Path))) is not { } entry)
            return GetZzzLoadOrderEnabled(game);
        if (File.Exists(entry.Path))
            return Path.GetFileName(entry.Path).Equals(ZzzAddonFileName, StringComparison.OrdinalIgnoreCase);
        var other = OtherAddonPath(entry.Path);
        return File.Exists(other)
            ? Path.GetFileName(other).Equals(ZzzAddonFileName, StringComparison.OrdinalIgnoreCase)
            : GetZzzLoadOrderEnabled(game);
    }

    // Seam for the rename so tests can make the rollback rename fail; production is the toolkit's durable rename (#31):
    // rename(2), then the folder is fsynced so the new name survives a crash.
    internal Action<string, string> MoveAddonFile { get; set; } = static (from, to) => AtomicFile.Rename(from, to, overwrite: true);

    /// <summary>
    /// Persists the per-game ZZZ load-order preference. When ShortFuse is managed, the owned add-on is
    /// renamed in place (one atomic rename) and its ownership record follows; an unreadable ledger, or a
    /// modified, missing or externally occupied add-on, blocks the change and leaves preference, file and
    /// record untouched. If the record cannot be saved after the rename, the rename is undone, and if that
    /// also fails the result says exactly which name the file and the record each hold.
    /// </summary>
    public async Task<ComponentOperationResult> SetZzzLoadOrderAsync(DetectedGame game, bool enabled, CancellationToken cancellationToken = default)
    {
        var targetName = enabled ? ZzzAddonFileName : AddonFileName;
        // Checked before anything is touched: renaming first and then failing the ledger write is the
        // one path that can leave the file and the record disagreeing.
        if (_records.CorruptLedgerPath is { } quarantined)
            return ComponentOperationResult.Fail($"{LedgerUnreadableMessage} {quarantined}");
        // Same for the preference file: it is written last, after the rename.
        try { _settings.EnsureWritable(); }
        catch (JsonStoreUnavailableException ex) { return ComponentOperationResult.Fail(ex.Message); }

        var stored = GetRecord(game);
        if (stored is null)
        {
            // Only the preference changes; still a write, so a Cancel pressed now is refused (its outcome is real).
            _ = OperationCancellation.BeginWrites(cancellationToken);
            _settings.SetZzzLoadOrderEnabled(game, enabled);
            return ComponentOperationResult.Ok($"ShortFuse will be deployed as {targetName} on the next install.");
        }

        // Work on a private copy (the shared deep copy) so nothing this method edits can reach the
        // in-memory ledger before, or without, a successful write.
        var record = LinuxDeploymentRecordStore.Clone(stored);
        var entry = record.Files.FirstOrDefault(f => IsAddonFileName(Path.GetFileName(f.Path)));
        if (entry is null)
            return ComponentOperationResult.Fail(NoAddonEntryMessage);
        var recordedPath = entry.Path;
        var resolved = await ResolveOwnedAddonAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!resolved.Success) return resolved;

        // Every check is done: the rename, the ledger write and the rename back on failure run to the end, so Cancel
        // is refused from here (it used to be accepted and its "Operation cancelled." replaced a split-state report).
        _ = OperationCancellation.BeginWrites(cancellationToken);
        var current = entry.Path;
        var target = Path.Combine(Path.GetDirectoryName(current)!, targetName);
        if (Path.GetFileName(current).Equals(targetName, StringComparison.Ordinal))
        {
            // Already named as requested. If the record was one name behind (interrupted rename),
            // persist the repaired path now rather than leaving the ledger stale.
            if (!current.Equals(recordedPath, StringComparison.Ordinal))
            {
                try { _records.Upsert(record); }
                catch (Exception ex)
                {
                    return ComponentOperationResult.Fail(
                        $"{RepairSaveFailedPrefix}{targetName}{RepairSaveFailedMiddle}{ex.Message}{RepairSaveFailedSuffix}{Path.GetFileName(recordedPath)}.");
                }
            }
        }
        else
        {
            if (ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(target)!, targetName) is { } occupied)
                return ComponentOperationResult.Fail($"{occupied}{OccupiedSuffix}");
            // overwrite:true maps to rename(2), which is atomic. overwrite:false links then unlinks on
            // Unix, briefly leaving both names, and a crash there would make ReShade load the add-on twice.
            try { MoveAddonFile(current, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ComponentOperationResult.Fail($"{RenameFailedPrefix}{targetName}: {ex.Message}");
            }
            entry.Path = target;
            // Renaming back to the name the record still holds (interrupted earlier rename) needs no write.
            try { if (!target.Equals(recordedPath, StringComparison.Ordinal)) _records.Upsert(record); }
            catch (Exception saveError)
            {
                try { MoveAddonFile(target, current); }
                catch (Exception rollbackError)
                {
                    return ComponentOperationResult.Fail(
                        $"{SplitStatePrefix}{targetName}{SplitStateRecord}{Path.GetFileName(recordedPath)}{SplitStateSave}{saveError.Message}" +
                        $"{SplitStateRollback}{rollbackError.Message}{SplitStateSuffix}");
                }
                return ComponentOperationResult.Fail($"{SaveFailedRestoredPrefix}{Path.GetFileName(current)} ({saveError.Message}).");
            }
        }

        _settings.SetZzzLoadOrderEnabled(game, enabled);
        return ComponentOperationResult.Ok($"ShortFuse add-on is now {targetName}.", target);
    }

    // Failure texts are split around the variable parts so UiLocalizationService can pair each
    // fixed fragment with its PT-BR text; keep both in step.
    internal const string LedgerUnreadableMessage = "The RHI deployment ledger is unreadable, so the ShortFuse load-order change was refused and nothing was renamed. Quarantined copy:";
    internal const string NoAddonEntryMessage = "The ShortFuse ownership record has no add-on entry; the load-order rename is blocked.";
    internal const string OccupiedSuffix = " already exists and is not owned by this ShortFuse record; the load-order rename is blocked.";
    internal const string RenameFailedPrefix = "Could not rename the ShortFuse add-on to ";
    internal const string SaveFailedRestoredPrefix = "Could not save the ShortFuse ownership record, so the add-on was renamed back to ";
    internal const string SplitStatePrefix = "The ShortFuse add-on is now named ";
    internal const string SplitStateRecord = ", but its ownership record still says ";
    internal const string SplitStateSave = ": saving the record failed (";
    internal const string SplitStateRollback = ") and renaming the add-on back also failed (";
    internal const string SplitStateSuffix = "). Rename the file back by hand, or switch the toggle again once the ledger is writable.";
    internal const string RepairSaveFailedPrefix = "The ShortFuse add-on is already named ";
    internal const string RepairSaveFailedMiddle = ", but saving the repaired ownership record failed (";
    internal const string RepairSaveFailedSuffix = "); the record still says ";
    internal const string AddonMissingPrefix = "The ShortFuse add-on is missing from ";
    internal const string BothNamesPrefix = "Both " + AddonFileName + " and " + ZzzAddonFileName + " exist in ";
    internal const string BothNamesSuffix = "; ReShade would load ShortFuse twice. Remove the one RHI does not own first.";
    internal const string NoSha256Prefix = "ShortFuse ownership record has no SHA-256 for ";
    internal const string ModifiedOutsidePrefix = "ShortFuse-owned file was modified outside RHI: ";
    internal const string RemoveRepairSaveFailedPrefix = "Could not save the repaired ShortFuse ownership record (";
    internal const string RemoveRepairSaveFailedSuffix = "); Remove is blocked before anything was changed.";

    private static string OtherAddonPath(string addonPath)
        => Path.Combine(Path.GetDirectoryName(addonPath)!,
            Path.GetFileName(addonPath).Equals(ZzzAddonFileName, StringComparison.OrdinalIgnoreCase) ? AddonFileName : ZzzAddonFileName);

    /// <summary>
    /// Points the record's add-on entry at whichever ShortFuse name is on disk (an interrupted rename can
    /// leave the record one name behind) and applies the hash guard to that file. Mutates
    /// <paramref name="entry"/>; pass an entry from a cloned record.
    /// </summary>
    private async Task<ComponentOperationResult> ResolveOwnedAddonAsync(LinuxDeployedFile entry, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(entry.Path)!;
        var onDisk = new[] { entry.Path, Path.Combine(dir, AddonFileName), Path.Combine(dir, ZzzAddonFileName) }
            .Distinct(StringComparer.Ordinal).Where(File.Exists).ToArray();
        if (onDisk.Length == 0)
            return ComponentOperationResult.Fail($"{AddonMissingPrefix}{dir}.");
        if (onDisk.Length > 1)
            return ComponentOperationResult.Fail($"{BothNamesPrefix}{dir}{BothNamesSuffix}");
        if (string.IsNullOrWhiteSpace(entry.Sha256))
            return ComponentOperationResult.Fail($"{NoSha256Prefix}{entry.Path}.");
        if (!string.Equals(await FileHash.Sha256HexAsync(onDisk[0], ct).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail($"{ModifiedOutsidePrefix}{onDisk[0]}.");
        entry.Path = onDisk[0];
        return ComponentOperationResult.Ok("ShortFuse add-on ownership verified.");
    }

    public Task<ShortFuseUpdateStatus> CheckLatestAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => CheckLatestAsync(game, releaseTag: null, cancellationToken);

    public async Task<ShortFuseUpdateStatus> CheckLatestAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            var current = GetRecord(game)?.Version;
            var update = !string.IsNullOrWhiteSpace(current) && !string.Equals(current, release.Version, StringComparison.OrdinalIgnoreCase);
            return new(true, current, release.Version, update,
                string.IsNullOrWhiteSpace(current)
                    ? $"ShortFuse {(string.IsNullOrWhiteSpace(releaseTag) ? "Latest" : $"pinned {releaseTag}")}: {release.Version}"
                    : update ? $"ShortFuse target: {current} → {release.Version}" : $"ShortFuse {current} is current{(string.IsNullOrWhiteSpace(releaseTag) ? "" : $" (pinned {releaseTag})")}.");
        }
        catch (Exception ex)
        {
            return new(false, GetRecord(game)?.Version, null, false, $"Could not check ShortFuse: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces only the ShortFuse add-on owned by the record with the pinned (or latest) release.
    /// The file keeps its recorded name (renodx-dlss.addon64 or the ZZZ load-order name). ReShade,
    /// UAL, INI and the DLSS/Streamline stack stay as installed. Used by the in-place version swap
    /// and by Update All.
    /// </summary>
    public async Task<ComponentOperationResult> SwapAddonVersionAsync(
        DetectedGame game,
        string? releaseTag,
        CancellationToken cancellationToken = default)
    {
        var record = GetRecord(game);
        if (record is null)
            return ComponentOperationResult.Fail("ShortFuse is not managed by RHI Linux.");
        var addon = FindRecordedAddon(record);
        if (addon is null)
            return ComponentOperationResult.Fail("The ShortFuse record does not own a ShortFuse add-on. Remove and reinstall ShortFuse.");

        ShortFuseReleaseInfo release;
        string staged;
        try
        {
            release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            staged = await EnsureCachedAddonAsync(release, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail($"ShortFuse download failed; nothing was changed: {ex.Message}");
        }

        try
        {
            var changed = await ManagedFileSwap.ReplaceAsync(record, [(addon.Path, staged)], updated =>
            {
                updated.Version = release.Version;
                updated.SourceUrl = release.AssetUrl;
                updated.Metadata[MetaAddonSha256] = updated.Files.First(f => f.Path == addon.Path).Sha256 ?? "";
                updated.Metadata["GitHub.ReleaseTagPin"] = releaseTag ?? "";
                VerifiedDownload.Record(updated.Metadata, updated.Metadata[MetaAddonSha256], HasPublishedDigest(release));
                _records.Upsert(updated);
            }, cancellationToken, _journal, game).ConfigureAwait(false);
            return ComponentOperationResult.Ok(changed == 0
                ? $"ShortFuse {release.Version} is already installed."
                : $"ShortFuse add-on swapped in place to {release.Version}. ReShade, UAL and the DLSS stack were not changed.", addon.Path);
        }
        catch (ManagedFileRestoreException ex)
        {
            return ComponentOperationResult.Fail($"ShortFuse in-place swap failed and the previous add-on could not be restored: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ShortFuse in-place swap failed; the previous add-on was kept: {ex.Message}");
        }
    }

    /// <summary>
    /// The add-on entry of a ShortFuse record. The record is the authority: the entry whose hash is
    /// the recorded add-on hash, else the single entry named *renodx-dlss.addon64 (the plain or the
    /// ZZZ load-order name). Ambiguity returns null rather than guessing.
    /// </summary>
    internal static LinuxDeployedFile? FindRecordedAddon(LinuxDeploymentRecord record)
    {
        var addons = record.Files
            .Where(f => Path.GetFileName(f.Path).EndsWith(AddonFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (record.Metadata.TryGetValue(MetaAddonSha256, out var hash) && !string.IsNullOrWhiteSpace(hash))
        {
            var byHash = addons.Where(f => string.Equals(f.Sha256, hash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byHash.Length == 1) return byHash[0];
        }
        return addons.Length == 1 ? addons[0] : null;
    }

    /// <summary>Swaps the ShortFuse-owned nvngx_dlssnr.dll to a manifest version in place (upstream 2.7.6).</summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(DetectedGame game, string? version, CancellationToken cancellationToken = default)
        => NeuralRenderingDllSwap.SwapAsync(_records, _dlss, game, ManagedComponentKind.ShortFuse, "ShortFuse", version, cancellationToken, _journal);

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, string? releaseTag, CancellationToken cancellationToken = default)
    {
        var status = await CheckLatestAsync(game, releaseTag, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(GetRecord(game) is not null, status.Success && status.HasUpdate, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: !status.Success);
    }

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled = true,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, autoConfigEnabled, releaseTag: null, cancellationToken);

    public Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled,
        string? releaseTag,
        CancellationToken cancellationToken = default)
        => InstallAsync(game, autoConfigEnabled, releaseTag, neuralRenderingDllVersion: null, cancellationToken);

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        bool autoConfigEnabled,
        string? releaseTag,
        string? neuralRenderingDllVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ShortFuse is a Windows/ReShade injection workflow and is not applicable to a native Linux target.");
        if (game.EffectiveGraphicsApi == GraphicsApi.OpenGL)
            return ComponentOperationResult.Fail("ShortFuse is published for 64-bit Windows games except OpenGL targets; OpenGL is not a supported ShortFuse path.");
        if (GetRecord(game) is not null)
            return ComponentOperationResult.Fail("ShortFuse is already managed for this game. Remove it before reinstalling.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath) ||
            GameArchitecture.Of(game) != PeArchitecture.X64)
            return ComponentOperationResult.Fail("ShortFuse auto-setup currently requires a detected 64-bit Windows executable.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not available for ShortFuse.");
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return ComponentOperationResult.Fail("Start the game once so its Wine/Proton prefix exists before installing ShortFuse.");
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        var reshadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        if (reshadeRecord is null)
            return ComponentOperationResult.Fail("ShortFuse requires RHI-managed ReShade. External ReShade installations are not adopted automatically.");
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("ShortFuse auto-setup will not rewrite an active RHI-managed OptiScaler/ReShade chain. Remove OptiScaler first or keep that stack unchanged.");
        if (_records.Find(game, ManagedComponentKind.Dlss5Feeder) is not null)
            return ComponentOperationResult.Fail("ShortFuse must not be stacked with RHI-managed DLSS5 Feeder Full. Remove Feeder first.");
        if (_records.Find(game, ManagedComponentKind.Dlss5Dx11Bridge) is not null)
            return ComponentOperationResult.Fail("ShortFuse must not be stacked with RHI-managed DLSS5 DX11 Bridge. Remove the bridge first.");

        var reshadePayload = reshadeRecord.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
        if (reshadePayload is null || !File.Exists(reshadePayload.Path))
            return ComponentOperationResult.Fail("The RHI-managed ReShade payload is missing; ShortFuse will not repair or adopt ambiguous state.");
        if (!string.IsNullOrWhiteSpace(reshadePayload.Sha256))
        {
            var hash = await FileHash.Sha256HexAsync(reshadePayload.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, reshadePayload.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail("The managed ReShade DLL was modified outside RHI. ShortFuse install is blocked to preserve that change.");
        }
        if (Path.GetFileName(reshadePayload.Path).Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("ReShade is currently in OptiScaler coexistence mode (ReShade64.dll). ShortFuse will not rewrite that chain automatically.");

        var originalReShadePath = reshadePayload.Path;
        var asiPath = Path.Combine(game.BinaryDirectory, ReShadeAsiName);
        if (!Path.GetFullPath(originalReShadePath).Equals(Path.GetFullPath(asiPath), StringComparison.Ordinal) && File.Exists(asiPath))
            return ComponentOperationResult.Fail($"{ReShadeAsiName} already exists but is not owned by this ShortFuse transaction. External/stale files are protected from overwrite.");

        var addonDir = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory);
        var addonTarget = Path.Combine(addonDir, _settings.IsZzzLoadOrderEnabled(game) ? ZzzAddonFileName : AddonFileName);
        // Either name loads under Wine (case-insensitive lookup), so both block an unowned install.
        foreach (var name in new[] { AddonFileName, ZzzAddonFileName })
            if (ReShadeDeploymentService.FindFileCaseInsensitive(addonDir, name) is { } existingAddon)
                return ComponentOperationResult.Fail($"{Path.GetFileName(existingAddon)} already exists without an RHI ShortFuse deployment record. It may be an external or stale ShortFuse install; RHI will not overwrite it implicitly.");

        // Refuse to stack over direct RHI DLL swaps. Their .original files represent a separate ownership cycle.
        var detection = _dlss.Detect(game, indexedFiles: null, protectedFiles: null, cancellationToken); // manifest DLSS folder too (#155)
        var existingTargets = detection.DlssPaths.Concat(detection.DlssdPaths).Concat(detection.DlssgPaths)
            .Concat(detection.DlssnrPaths)
            .Concat(detection.StreamlineFolders.SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name))))
            .Where(File.Exists).Distinct(StringComparer.Ordinal).ToArray();
        var stackedBackup = existingTargets.FirstOrDefault(path => File.Exists(path + TransactionalFileDeploymentService.BackupExtension));
        if (stackedBackup is not null)
            return ComponentOperationResult.Fail($"ShortFuse found an existing RHI .original ownership cycle at {stackedBackup}. Restore direct DLSS/Streamline swaps before enabling ShortFuse.");

        ShortFuseReleaseInfo release;
        string stagedAddon;
        Dictionary<DlssComponentKind, string?> cachedDlss;
        string? cachedStreamline;
        var ualWasPresent = _ual.GetRecord(game) is not null;
        UalStagedPayload? stagedUal = null;
        try
        {
            // The automatic UAL install downloads its release too: staged here, with the rest, so nothing is
            // downloaded once game files change (w2 merge review, minor 1).
            if (!ualWasPresent && autoConfigEnabled)
                stagedUal = await _ual.StageAsync(game, cancellationToken).ConfigureAwait(false);
            release = await FetchReleaseAsync(releaseTag, cancellationToken).ConfigureAwait(false);
            stagedAddon = await EnsureCachedAddonAsync(release, cancellationToken).ConfigureAwait(false);
            cachedDlss = new()
            {
                [DlssComponentKind.SuperResolution] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.SuperResolution, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.RayReconstruction] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.RayReconstruction, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.FrameGeneration] = await _dlss.EnsureNewestCachedAsync(DlssComponentKind.FrameGeneration, cancellationToken).ConfigureAwait(false),
                [DlssComponentKind.NeuralRendering] = await _dlss.EnsureCachedAsync(DlssComponentKind.NeuralRendering, neuralRenderingDllVersion, cancellationToken).ConfigureAwait(false),
            };
            cachedStreamline = await _dlss.EnsureNewestStreamlineCachedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"ShortFuse download/staging failed before any game files were changed: {ex.Message}");
        }

        var ualInstalledByUs = false;
        string? ualProxy = _ual.InstalledProxyName(game);
        var overrideSuspended = false;
        IniSnapshot? iniSnapshot = null;
        var deployed = new List<LinuxDeployedFile>();
        var changedFiles = new List<string>();

        // Journaled (#23): every game file this install changes is tracked (with a verified copy) and every write declared
        // before it happens, the ReShade override's suspension journals its put-back, and the ShortFuse record (with the
        // renamed ReShade record, in the same ledger write) is the commit point. A crash before it is saved is rolled back
        // at the next start; an in-process failure rolls back from the copies (#31).
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "ShortFuse install: " + game.Name, game, ManagedComponentKind.ShortFuse, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;

        // Downloads and staging are done: from the first game file on, the install runs to completion or full rollback.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            if (!ualWasPresent && autoConfigEnabled)
            {
                ComponentOperationResult? last = null;
                foreach (var candidate in PreferredUalNames)
                {
                    if (!UltimateAsiLoaderDeploymentService.IsSupportedProxyName(game, candidate)) continue;
                    last = await _ual.InstallAsync(game, candidate, stagedUal, cancellationToken).ConfigureAwait(false);
                    if (last.Success)
                    {
                        ualInstalledByUs = true;
                        ualProxy = candidate;
                        break;
                    }
                }
                if (!ualInstalledByUs)
                    throw new InvalidOperationException(last?.Message ?? "No safe UAL proxy from winmm → version → dinput8 was available.");
            }

            if (autoConfigEnabled)
            {
                if (reshadeRecord.WineOverride is not null)
                {
                    // Suspended only when RHI's value was really taken out: a value the user set is theirs (#30),
                    // and Remove must not put RHI's value back over it.
                    var suspension = await _overrides.RestoreAsync(scope.Operation, game.PrefixPath, reshadeRecord.WineOverride, cancellationToken).ConfigureAwait(false);
                    overrideSuspended = suspension.Outcome == WineOverrideRestoreOutcome.Restored;
                }

                if (!Path.GetFullPath(originalReShadePath).Equals(Path.GetFullPath(asiPath), StringComparison.Ordinal))
                {
                    await scope.TrackAsync(originalReShadePath, cancellationToken).ConfigureAwait(false);
                    await scope.TrackAsync(asiPath, cancellationToken).ConfigureAwait(false);
                    await scope.DeclareWriteAsync(asiPath, scope.Files.Find(originalReShadePath)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                    AtomicFile.Rename(originalReShadePath, asiPath);
                }

                // Saved with the ShortFuse record below (one ledger write).
                reshadePayload.Path = asiPath;
                reshadeRecord.Metadata[MetaReShadeOriginalPath] = originalReShadePath;
                reshadeRecord.Metadata[MetaReShadeOverrideSuspended] = overrideSuspended ? "1" : "0";
                changedFiles.Add(asiPath);

                var iniPath = FindReShadeIni(game.BinaryDirectory);
                if (iniPath is not null)
                {
                    await scope.TrackAsync(iniPath, cancellationToken).ConfigureAwait(false);
                    iniSnapshot = CaptureAndApplyIni(iniPath);
                    await scope.DeclareWrittenContentAsync(iniPath, cancellationToken).ConfigureAwait(false);
                    changedFiles.Add(iniPath);
                }
            }

            Directory.CreateDirectory(addonDir);
            var addonHash = await scope.PlaceCopyAsync(stagedAddon, addonTarget, cancellationToken, overwrite: false).ConfigureAwait(false);
            deployed.Add(new LinuxDeployedFile { Path = addonTarget, ExistedBeforeInstall = false, Sha256 = addonHash });
            changedFiles.Add(addonTarget);

            await DeployDlssStackAsync(game, detection, cachedDlss, cachedStreamline, deployed, changedFiles, scope, cancellationToken).ConfigureAwait(false);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetaAutoConfigEnabled] = autoConfigEnabled ? "1" : "0",
                [MetaUalInstalledByShortFuse] = ualInstalledByUs ? "1" : "0",
                [MetaUalProxyName] = ualProxy ?? "",
                [MetaReShadeOverrideSuspended] = overrideSuspended ? "1" : "0",
                [MetaAddonSha256] = addonHash,
                ["GitHub.ReleaseTagPin"] = releaseTag ?? "",
            };
            if (autoConfigEnabled) metadata[MetaReShadeOriginalPath] = originalReShadePath;
            if (iniSnapshot is not null) WriteIniSnapshot(metadata, iniSnapshot);
            // The cached add-on (verified cache entry) is what was copied: its hash is the deployed file's (#26).
            VerifiedDownload.Record(metadata, addonHash, HasPublishedDigest(release));

            var shortFuseRecord = scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.ShortFuse,
                BinaryDirectory = game.BinaryDirectory,
                SourceUrl = release.AssetUrl,
                Version = release.Version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = deployed,
                Metadata = metadata
            });
            // The commit point: the ShortFuse record and, with auto-config, ReShade's renamed record in one write.
            _records.Commit(autoConfigEnabled ? [shortFuseRecord, reshadeRecord] : [shortFuseRecord]);
        }
        catch (Exception ex)
        {
            // Every tracked file (add-on, DLSS/Streamline DLLs and their .original, ReShade's name, ReShade.ini) back to its
            // pre-install bytes from the scope's verified copies; ReShade's record was never saved. A copy that cannot be
            // put back is kept and named.
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("ShortFuse");
            if (!rollback.Success)
                AppLog.Warn("shortfuse", $"Install rollback did not complete: {string.Join("; ", rollback.Failures)}");
            if (rollback.RolledBack && overrideSuspended)
                _overrides.TryReapply(game.PrefixPath, reshadeRecord.WineOverride);
            if (rollback.RolledBack && ualInstalledByUs)
            {
                try { await _ual.RemoveAsync(game, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ualError) { AppLog.Warn("shortfuse", "Install rollback could not remove the ASI Loader it installed", ualError); }
            }
            return ComponentOperationResult.Fail($"ShortFuse install failed and was rolled back as far as possible: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);

        return ComponentOperationResult.Ok(
            $"ShortFuse {release.Version} installed. ReShade {(autoConfigEnabled ? "is loaded as ReShade64.asi" : "auto-config is OFF")}; " +
            $"UAL {(ualWasPresent ? "pre-existing/preserved" : ualInstalledByUs ? $"installed as {ualProxy}" : "not changed")}; HookStreamline/HookDirectX {(iniSnapshot is null ? "not changed (no ReShade.ini found)" : "enabled")}. " +
            "DLSS/Streamline ownership is transactional and direct swaps are locked until ShortFuse is removed.", changedFiles.ToArray());
    }

    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var stored = GetRecord(game);
        if (stored is null)
            return ComponentOperationResult.Fail("ShortFuse is not managed by RHI Linux. External/stale ShortFuse files are protected from implicit removal.");
        // Copy: the store's cached instance must not change unless a write succeeds.
        var record = LinuxDeploymentRecordStore.Clone(stored);
        if (_overrides.EditRefusal(game.PrefixPath) is { } wineRunning)
            return ComponentOperationResult.Fail(wineRunning);

        // An interrupted ZZZ load-order rename can leave the record one name behind; follow the
        // owned add-on to its other name so the hash guard and teardown below still apply to it.
        var addonFollowed = false;
        if (record.Files.FirstOrDefault(f => IsAddonFileName(Path.GetFileName(f.Path))) is { } addonEntry && !File.Exists(addonEntry.Path))
        {
            var other = OtherAddonPath(addonEntry.Path);
            if (File.Exists(other))
            {
                addonEntry.Path = other;
                addonFollowed = true;
            }
        }

        // Hash guard every non-user-editable ShortFuse-owned file before destructive teardown.
        foreach (var file in record.Files.Where(f => !f.AllowUserChanges))
        {
            if (!File.Exists(file.Path)) continue;
            if (string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"ShortFuse ownership record has no SHA-256 for {file.Path}; Remove is blocked.");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"ShortFuse-owned file was modified outside RHI: {file.Path}. Remove is blocked to protect the external change.");
        }

        // The followed add-on passed the hash guard, so it is ours: persist the repaired path. Doing it
        // here also proves the ledger is writable before any teardown starts.
        if (addonFollowed)
        {
            try { _records.Upsert(record); }
            catch (Exception ex)
            {
                return ComponentOperationResult.Fail($"{RemoveRepairSaveFailedPrefix}{ex.Message}{RemoveRepairSaveFailedSuffix}");
            }
        }

        var reshadeRecord = _records.Find(game, ManagedComponentKind.ReShade);
        var ualByUs = record.Metadata.GetValueOrDefault(MetaUalInstalledByShortFuse) == "1";

        if (record.Metadata.TryGetValue(MetaReShadeOriginalPath, out var preflightOriginalPath) && !string.IsNullOrWhiteSpace(preflightOriginalPath))
        {
            if (reshadeRecord is null)
                return ComponentOperationResult.Fail("ShortFuse owns a ReShade transformation but the ReShade deployment record is missing. Remove is blocked rather than guessing ownership.");
            var payload = reshadeRecord.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
            if (payload is null || !File.Exists(payload.Path))
                return ComponentOperationResult.Fail("ShortFuse-managed ReShade64.asi is missing. Remove is blocked to avoid re-enabling a proxy path without a recoverable ReShade payload.");
            if (!string.IsNullOrWhiteSpace(payload.Sha256))
            {
                var current = await FileHash.Sha256HexAsync(payload.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed ReShade payload was modified outside RHI: {payload.Path}. ShortFuse Remove is blocked.");
            }
            if (!Path.GetFullPath(preflightOriginalPath).Equals(Path.GetFullPath(payload.Path), StringComparison.Ordinal) && File.Exists(preflightOriginalPath))
                return ComponentOperationResult.Fail($"The pre-ShortFuse ReShade path is occupied by another file: {preflightOriginalPath}. Remove is blocked to protect it.");
        }

        if (ualByUs)
        {
            var ualRecord = _ual.GetRecord(game);
            if (ualRecord is null)
                return ComponentOperationResult.Fail("ShortFuse installed UAL, but its deployment record is missing. Remove is blocked to avoid guessing loader ownership.");
            foreach (var file in ualRecord.Files.Where(f => !f.AllowUserChanges))
            {
                if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                    return ComponentOperationResult.Fail($"ShortFuse-owned UAL state is incomplete: {file.Path}. Remove is blocked.");
                var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"ShortFuse-owned UAL proxy was modified outside RHI: {file.Path}. Remove is blocked.");
            }
        }

        // Journaled (#23): the ShortFuse record is stamped with the removal's id first; every file the teardown touches is
        // tracked with a verified copy (and every placed content declared) before it changes, and ReShade's override
        // re-enable journals its undo. The commit point is one ledger write that removes the ShortFuse record and saves
        // ReShade's renamed-back record. A crash before it is rolled back at the next start; an in-process failure rolls
        // back from the copies (#31: no raw byte rewrites, no swallowed restore errors). The registry is never
        // snapshotted and written back whole (#24): only the overrides this removal changed are undone, key by key.
        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "ShortFuse removal: " + game.Name, game, ManagedComponentKind.ShortFuse, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        WineOverrideRestoreResult? reshadeOverrideBack = null;
        LinuxDeploymentRecord? reshadeAfter = null;

        // Cancel is honoured only up to here: a teardown that stopped half-way would be refused on retry by the hash
        // guard (restored originals no longer match RHI's hash), so from here on it always runs to the end or rolls back.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        try
        {
            foreach (var file in record.Files.AsEnumerable().Reverse())
            {
                await scope.TrackAsync(file.Path, cancellationToken).ConfigureAwait(false);
                var original = file.Path + TransactionalFileDeploymentService.BackupExtension;
                if (file.ExistedBeforeInstall && File.Exists(original))
                {
                    await scope.DeclareWriteAsync(file.Path, scope.Files.Find(file.Path)!.BackupPreSha256
                        ?? await FileHash.Sha256HexAsync(original, CancellationToken.None).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    var restored = await _files.RestoreAsync(file.Path, cancellationToken).ConfigureAwait(false);
                    if (!restored.Success) throw new IOException(restored.Error ?? $"Could not restore {file.Path}");
                }
                else if (File.Exists(file.Path))
                {
                    AtomicFile.Delete(file.Path);
                }
            }

            if (record.Metadata.ContainsKey(MetaIniPath))
            {
                var ini = ReadIniSnapshot(record.Metadata);
                if (!string.IsNullOrWhiteSpace(ini.Path))
                {
                    await scope.TrackAsync(ini.Path, cancellationToken).ConfigureAwait(false);
                    RestoreIni(ini);
                    await scope.DeclareWrittenContentAsync(ini.Path, cancellationToken).ConfigureAwait(false);
                }
            }

            if (reshadeRecord is not null && record.Metadata.TryGetValue(MetaReShadeOriginalPath, out var originalPath) && !string.IsNullOrWhiteSpace(originalPath))
            {
                reshadeAfter = LinuxDeploymentRecordStore.Clone(reshadeRecord);
                var payload = reshadeAfter.Files.FirstOrDefault(f => IsReShadePayload(f.Path));
                var asiPath = payload?.Path ?? Path.Combine(game.BinaryDirectory!, ReShadeAsiName);
                if (File.Exists(originalPath))
                    throw new IOException($"Cannot restore ReShade name because the original path is occupied: {originalPath}");
                if (File.Exists(asiPath))
                {
                    await scope.TrackAsync(asiPath, cancellationToken).ConfigureAwait(false);
                    await scope.TrackAsync(originalPath, cancellationToken).ConfigureAwait(false);
                    await scope.DeclareWriteAsync(originalPath, scope.Files.Find(asiPath)!.PreSha256!, cancellationToken).ConfigureAwait(false);
                    AtomicFile.Rename(asiPath, originalPath);
                }
                if (payload is not null) payload.Path = originalPath;
                reshadeAfter.Metadata.Remove(MetaReShadeOriginalPath);
                reshadeAfter.Metadata.Remove(MetaReShadeOverrideSuspended);
            }

            // Re-enable ReShade's proxy override only where it is still at the value the install's suspension left.
            if (reshadeRecord?.WineOverride is not null && record.Metadata.GetValueOrDefault(MetaReShadeOverrideSuspended) == "1")
                reshadeOverrideBack = await _overrides.ReapplyAsync(scope.Operation, game.PrefixPath!, reshadeRecord.WineOverride, cancellationToken).ConfigureAwait(false);

            // The commit point: ShortFuse's record goes and ReShade's (renamed back) is saved in the same write.
            _records.Commit(reshadeAfter is null ? [] : [reshadeAfter], [(game, ManagedComponentKind.ShortFuse)]);
        }
        catch (Exception ex)
        {
            // ReShade's record goes back as it was when the commit write had renamed it (one write with ShortFuse's).
            var rollback = await scope.RollbackRemovalAsync(
                _records, game, record, "shortfuse", alongside: reshadeAfter is null ? null : [reshadeRecord!]).ConfigureAwait(false);
            // Landed: ShortFuse is removed, so the ASI Loader it installed goes too, as below (review w8 finding 4).
            if (rollback.Committed)
                return ComponentOperationResult.Ok(DeploymentScope.Landed("ShortFuse", removal: true).Message
                    + WineDllOverrideService.KeptValueNotes(reshadeOverrideBack) + await RemoveUalInstalledByShortFuseAsync(game, ualByUs).ConfigureAwait(false));
            if (!rollback.Success)
                AppLog.Warn("shortfuse", $"Removal rollback did not complete: {string.Join("; ", rollback.Failures)}");
            // Registry: undo exactly what this removal changed, per key (atomic, locked, refused while Wine runs).
            if (rollback.RolledBack && reshadeOverrideBack?.Outcome == WineOverrideRestoreOutcome.Restored)
                _overrides.TryRestore(game.PrefixPath, reshadeRecord!.WineOverride);
            return ComponentOperationResult.Fail($"{RemovalFailedPrefix}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);

        // When the loader stays, the success line does not claim its state was restored (review w3-journal #8).
        var ualNote = await RemoveUalInstalledByShortFuseAsync(game, ualByUs).ConfigureAwait(false);
        return ComponentOperationResult.Ok((ualNote.Length == 0 ? RemovedMessage : RemovedUalKeptMessage)
            + WineDllOverrideService.KeptValueNotes(reshadeOverrideBack) + ualNote);
    }

    /// <summary>
    /// After ShortFuse's removal committed: removes the ASI Loader ShortFuse installed, last and as its own operation. If
    /// that fails, ShortFuse is gone and the loader stays installed with its own record (removable on its own), never
    /// half-removed; returns the note that says so (<see cref="UalKeptPrefix"/>, the loader's reason), else "".
    /// </summary>
    private async Task<string> RemoveUalInstalledByShortFuseAsync(DetectedGame game, bool ualByUs)
    {
        if (!ualByUs || _ual.GetRecord(game) is null) return "";
        var ualResult = await _ual.RemoveAsync(game, CancellationToken.None).ConfigureAwait(false);
        return ualResult.Success ? "" : $" {UalKeptPrefix}{ualResult.Message}";
    }

    /// <summary>Success line of a removal (PT-BR rows under the journal anchor).</summary>
    public const string RemovedMessage = "ShortFuse removed. RHI restored the previous DLSS/Streamline files, ReShade filename, reshade.ini values, UAL state and ReShade Wine override ownership.";

    /// <summary>Success line of a removal whose ASI Loader stays installed (<see cref="UalKeptPrefix"/> follows).</summary>
    public const string RemovedUalKeptMessage = "ShortFuse removed. RHI restored the previous DLSS/Streamline files, ReShade filename, reshade.ini values and ReShade Wine override ownership.";

    /// <summary>Fixed text of a failed, rolled-back removal; the reason follows.</summary>
    public const string RemovalFailedPrefix = "ShortFuse removal failed; the installed ShortFuse/ReShade/INI/registry state was restored from transaction snapshots where possible: ";

    /// <summary>Appended when ShortFuse was removed but the ASI Loader it installed could not be; the loader's reason follows.</summary>
    public const string UalKeptPrefix = "The Ultimate ASI Loader ShortFuse installed stays installed (remove it on its own): ";

    private async Task DeployDlssStackAsync(
        DetectedGame game,
        DlssDetectionResult detection,
        IReadOnlyDictionary<DlssComponentKind, string?> cached,
        string? cachedStreamline,
        List<LinuxDeployedFile> deployed,
        List<string> changedFiles,
        DeploymentScope scope,
        CancellationToken cancellationToken)
    {
        var binaryDir = game.BinaryDirectory!;
        var mappings = new[]
        {
            (DlssComponentKind.SuperResolution, "nvngx_dlss.dll", detection.DlssPaths.FirstOrDefault()),
            (DlssComponentKind.RayReconstruction, "nvngx_dlssd.dll", detection.DlssdPaths.FirstOrDefault()),
            (DlssComponentKind.FrameGeneration, "nvngx_dlssg.dll", detection.DlssgPaths.FirstOrDefault()),
            (DlssComponentKind.NeuralRendering, "nvngx_dlssnr.dll", detection.DlssnrPaths.FirstOrDefault()),
        };

        foreach (var (kind, name, detected) in mappings)
        {
            if (!cached.TryGetValue(kind, out var source) || string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
            var target = detected ?? Path.Combine(binaryDir, name);
            await DeployOneAsync(source, target, deployed, changedFiles, scope, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(cachedStreamline) && Directory.Exists(cachedStreamline))
        {
            var targetFolder = detection.StreamlineFolders.FirstOrDefault() ?? binaryDir;
            foreach (var name in DlssManagerService.KnownStreamlineDlls)
            {
                var source = Path.Combine(cachedStreamline, name);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(targetFolder, name);
                await DeployOneAsync(source, target, deployed, changedFiles, scope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DeployOneAsync(string source, string target, List<LinuxDeployedFile> deployed, List<string> changedFiles, DeploymentScope scope, CancellationToken ct)
    {
        var existed = File.Exists(target);
        // Tracked (with its .original) and the new bytes declared before the file service places them: the install's
        // own rollback, and the next start after a crash, put the game's DLL and its .original back.
        await scope.TrackAsync(target, ct).ConfigureAwait(false);
        await scope.DeclareWriteAsync(target, await FileHash.Sha256HexAsync(source, CancellationToken.None).ConfigureAwait(false), ct).ConfigureAwait(false);
        var result = await _files.ReplaceAsync(source, target, ct).ConfigureAwait(false);
        if (!result.Success) throw new IOException(result.Error ?? $"Could not deploy {target}");
        if (!result.Changed) return;
        deployed.Add(new LinuxDeployedFile
        {
            Path = target,
            ExistedBeforeInstall = existed,
            Sha256 = result.Sha256 ?? await FileHash.Sha256HexAsync(target, CancellationToken.None).ConfigureAwait(false),
            AllowUserChanges = false
        });
        changedFiles.Add(target);
    }

    internal Task<ShortFuseReleaseInfo> FetchLatestReleaseAsync(CancellationToken cancellationToken = default)
        => FetchReleaseAsync(releaseTag: null, cancellationToken);

    internal async Task<ShortFuseReleaseInfo> FetchReleaseAsync(string? releaseTag, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(releaseTag)
            ? ReleasesApiUrl
            : GitHubReleaseAssetResolver.BuildReleaseTagApiUrl(ReleasesApiUrl, releaseTag);
        var json = await TrustedDownload.GetStringAsync(_http, url, DownloadSources.GitHubApi, "application/vnd.github+json", cancellationToken).ConfigureAwait(false);
        return ParseReleaseJson(json, releaseTag);
    }

    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await new GitHubReleaseAssetResolver(_http)
            .ListVersionsAsync(ReleasesApiUrl, cancellationToken)
            .ConfigureAwait(false);
        return all.Where(v => v.Tag.StartsWith(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static ShortFuseReleaseInfo ParseReleaseJson(string json, string? requiredTag = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new InvalidDataException("ShortFuse release response was not an object or array.");

        var candidates = new List<(ShortFuseReleaseInfo Info, (Version Base, int PreRelease) Parsed)>();
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray()
            : new[] { doc.RootElement.Clone() };
        foreach (var release in releases)
        {
            var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(requiredTag) && !tag.Equals(requiredTag, StringComparison.OrdinalIgnoreCase)) continue;
            if (release.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            JsonElement? archive = null;
            JsonElement? direct = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(name, AddonFileName, StringComparison.OrdinalIgnoreCase))
                {
                    direct = asset.Clone();
                    break;
                }
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith(ArchiveAssetPrefix, StringComparison.OrdinalIgnoreCase))
                    archive ??= asset.Clone();
            }

            var chosen = direct ?? archive;
            if (chosen is null) continue;
            var chosenValue = chosen.Value;
            var assetName = chosenValue.TryGetProperty("name", out var chosenNameEl) ? chosenNameEl.GetString() ?? "" : "";
            var url = chosenValue.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var digest = chosenValue.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest[7..];

            var versionText = tag[ReleaseTagPrefix.Length..];
            var parsed = ReleaseVersionOrdering.Key(versionText);
            candidates.Add((new ShortFuseReleaseInfo(
                versionText, tag, url, string.IsNullOrWhiteSpace(digest) ? null : digest,
                assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), assetName), parsed));
        }

        if (candidates.Count == 0)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(requiredTag)
                ? $"No {ReleaseTagPrefix} release containing {AddonFileName} or a {ArchiveAssetPrefix}*.zip payload was found."
                : $"Pinned ShortFuse release {requiredTag} was not found or does not contain a supported payload.");
        // Version.TryParse rejected every "-rcN" tag, so rc builds fell back to API order (rc5 could beat rc10).
        return candidates
            .OrderByDescending(c => c.Parsed.Base)
            .ThenByDescending(c => c.Parsed.PreRelease)
            .First().Info;
    }

    // Direct and archive assets alike are reused only through a CacheStamp written after RHI itself
    // downloaded (and, for archives, extracted) the add-on: a foreign, truncated or older-build file at
    // <cache>/<version>/renodx-dlss.addon64 is deleted and re-downloaded, never deployed (#142).
    private static bool HasPublishedDigest(ShortFuseReleaseInfo release) => !string.IsNullOrWhiteSpace(release.AssetSha256);

    internal async Task<string> EnsureCachedAddonAsync(ShortFuseReleaseInfo release, CancellationToken ct)
    {
        var dir = Path.Combine(_cacheRoot, Sanitize(release.Version));
        var target = Path.Combine(dir, AddonFileName);
        Directory.CreateDirectory(dir);
        // Legacy archive provenance sidecar; superseded by the CacheStamp.
        FileCleanup.TryDelete(target + ".source.json");

        if (CacheStamp.IsValid(target, release.AssetUrl, release.Version, release.AssetSha256))
            return target;

        CacheStamp.Invalidate(target);
        FileCleanup.TryDelete(target);

        // Downloaded under the RHI-repository host policy and checked against GitHub's digest when published
        // (fail-open without one, as upstream: the SHA-256 is recorded in the stamp and rhi.log, #26).
        using var download = await TrustedDownload.DownloadToTempAsync(
            _http, release.AssetUrl, Path.Combine(dir, release.IsArchive ? Path.GetFileName(release.AssetName) : AddonFileName),
            DownloadSources.RhiRepository, release.AssetSha256, cancellationToken: ct).ConfigureAwait(false);

        // Do not reject a ZIP based on its compressed byte size. A valid archive can be
        // very small when its contents compress well; archive safety is established below
        // by the release SHA-256 (when provided), ZIP parsing, exact payload cardinality,
        // and the uncompressed add-on size check. Direct .addon64 assets still keep the
        // conservative minimum-size guard.
        if (!release.IsArchive && download.Length < 32 * 1024)
            throw new InvalidDataException("ShortFuse release asset is unexpectedly small.");

        if (release.IsArchive)
        {
            using var zip = ZipFile.OpenRead(download.Path);
            var selected = SelectArchiveAddonEntry(zip, release.AssetName);
            if (selected.Length < 32 * 1024)
                throw new InvalidDataException("ShortFuse add-on inside the archive is unexpectedly small.");
            // Checked extraction (declared-size cap, fixed mode, length + CRC-32, atomic rename); never a raw copy (#42).
            await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(selected, target, TrustedDownloadPolicy.AddonMaxBytes, ct).ConfigureAwait(false);
        }
        else
        {
            download.MoveTo(target);
        }

        CacheStamp.Write(target, release.AssetUrl, release.Version, release.AssetSha256);
        if (release.AssetSha256 is null)
            TrustedDownload.NoteAcceptedWithoutDigest(DownloadSources.RhiRepository, release.AssetUrl, download.Sha256);
        return target;
    }

    internal static ZipArchiveEntry SelectArchiveAddonEntry(ZipArchive zip, string assetName)
    {
        ArgumentNullException.ThrowIfNull(zip);

        var exact = zip.Entries
            .Where(entry => string.Equals(entry.Name, AddonFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1)
            throw new InvalidDataException($"ShortFuse archive '{assetName}' contains multiple ambiguous {AddonFileName} entries.");

        // Upstream relaxed archive extraction because ShortFuse occasionally ships the
        // correct payload under a different .addon64 filename. On Linux we keep that tolerance
        // fail-closed: exactly one fallback candidate is accepted; multiple candidates remain
        // ambiguous and no file is deployed.
        var fallback = zip.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                            && entry.Name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fallback.Length == 1) return fallback[0];
        if (fallback.Length == 0)
            throw new InvalidDataException($"ShortFuse archive '{assetName}' contains no .addon64 payload.");
        throw new InvalidDataException($"ShortFuse archive '{assetName}' contains multiple ambiguous .addon64 payloads.");
    }

    private static IniSnapshot CaptureAndApplyIni(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var section = FindSection(lines, "INSTALL");
        var sectionExisted = section.Start >= 0;
        var stream = ReadIniValue(lines, section, "HookStreamline");
        var direct = ReadIniValue(lines, section, "HookDirectX");
        SetIniValue(lines, "INSTALL", "HookStreamline", "1");
        SetIniValue(lines, "INSTALL", "HookDirectX", "1");
        AtomicWriteLines(path, lines);
        return new(path, sectionExisted, stream.Exists, stream.Value, direct.Exists, direct.Value);
    }

    private static void RestoreIni(IniSnapshot snapshot)
    {
        if (!File.Exists(snapshot.Path)) return;
        var lines = File.ReadAllLines(snapshot.Path).ToList();
        RestoreIniValue(lines, "INSTALL", "HookStreamline", snapshot.HookStreamlineExisted, snapshot.HookStreamlineValue);
        RestoreIniValue(lines, "INSTALL", "HookDirectX", snapshot.HookDirectXExisted, snapshot.HookDirectXValue);
        if (!snapshot.SectionExisted) RemoveSectionIfEmpty(lines, "INSTALL");
        AtomicWriteLines(snapshot.Path, lines);
    }

    private static void WriteIniSnapshot(Dictionary<string, string> metadata, IniSnapshot s)
    {
        metadata[MetaIniPath] = s.Path;
        metadata[MetaIniSectionExisted] = s.SectionExisted ? "1" : "0";
        metadata[MetaHookStreamlineExisted] = s.HookStreamlineExisted ? "1" : "0";
        metadata[MetaHookStreamlineValue] = s.HookStreamlineValue ?? "";
        metadata[MetaHookDirectXExisted] = s.HookDirectXExisted ? "1" : "0";
        metadata[MetaHookDirectXValue] = s.HookDirectXValue ?? "";
    }

    private static IniSnapshot ReadIniSnapshot(IReadOnlyDictionary<string, string> m) => new(
        m.GetValueOrDefault(MetaIniPath) ?? "",
        m.GetValueOrDefault(MetaIniSectionExisted) == "1",
        m.GetValueOrDefault(MetaHookStreamlineExisted) == "1",
        m.GetValueOrDefault(MetaHookStreamlineValue),
        m.GetValueOrDefault(MetaHookDirectXExisted) == "1",
        m.GetValueOrDefault(MetaHookDirectXValue));

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string sectionName)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (start < 0) { if (line.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase)) start = i; continue; }
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) return (start, i);
        }
        return start < 0 ? (-1, -1) : (start, lines.Count);
    }

    private static (bool Exists, string? Value) ReadIniValue(IReadOnlyList<string> lines, (int Start, int End) section, string key)
    {
        if (section.Start < 0) return (false, null);
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return (true, line[(eq + 1)..].Trim());
        }
        return (false, null);
    }

    private static void SetIniValue(List<string> lines, string sectionName, string key, string value)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add($"[{sectionName}]");
            lines.Add($"{key}={value}");
            return;
        }
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim(); var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { lines[i] = $"{key}={value}"; return; }
        }
        lines.Insert(section.End, $"{key}={value}");
    }

    private static void RestoreIniValue(List<string> lines, string sectionName, string key, bool existed, string? value)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0) { if (existed) SetIniValue(lines, sectionName, key, value ?? ""); return; }
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var line = lines[i].Trim(); var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (existed) lines[i] = $"{key}={value ?? ""}"; else lines.RemoveAt(i);
            return;
        }
        if (existed) SetIniValue(lines, sectionName, key, value ?? "");
    }

    private static void RemoveSectionIfEmpty(List<string> lines, string sectionName)
    {
        var section = FindSection(lines, sectionName);
        if (section.Start < 0) return;
        for (var i = section.Start + 1; i < section.End; i++)
        {
            var text = lines[i].Trim();
            if (text.Length > 0 && !text.StartsWith(";", StringComparison.Ordinal) && !text.StartsWith("#", StringComparison.Ordinal)) return;
        }
        lines.RemoveRange(section.Start, section.End - section.Start);
    }

    /// <summary>ReShade.ini is the user's file: the one user-file write path (mode kept, read-only and links refused, verified).</summary>
    private static void AtomicWriteLines(string path, IReadOnlyList<string> lines)
    {
        var text = new System.Text.StringBuilder();
        foreach (var line in lines) text.Append(line).Append(Environment.NewLine);
        AtomicFile.ReplaceUserFile(path, new System.Text.UTF8Encoding(false).GetBytes(text.ToString()));
    }

    private static string? FindReShadeIni(string binaryDir)
        => new[] { "ReShade.ini", "reshade.ini" }.Select(name => Path.Combine(binaryDir, name)).FirstOrDefault(File.Exists);

    private static bool IsReShadePayload(string path) => ProxyPayloadLocator.IsReShadePayloadPath(path); // w3/dllnames hook (#120)

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));

    internal sealed record IniSnapshot(string Path, bool SectionExisted, bool HookStreamlineExisted, string? HookStreamlineValue, bool HookDirectXExisted, string? HookDirectXValue);
}

public sealed record ShortFuseReleaseInfo(string Version, string Tag, string AssetUrl, string? AssetSha256, bool IsArchive = false, string AssetName = "");
public sealed record ShortFuseUpdateStatus(bool Success, string? InstalledVersion, string? LatestVersion, bool HasUpdate, string Message);
