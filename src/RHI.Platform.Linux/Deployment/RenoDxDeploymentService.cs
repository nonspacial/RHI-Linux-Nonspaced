using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;


public sealed record RenoDxCompatibilityOption(string Value, string Name);

public sealed record RenoDxCompatibilitySetting(
    string Key,
    string Label,
    string CurrentValue,
    IReadOnlyList<RenoDxCompatibilityOption> Options,
    bool ManifestDriven);

public sealed record RenoDxCompatibilitySnapshot(
    string? IniPath,
    bool HasRenoDxSection,
    IReadOnlyList<RenoDxCompatibilitySetting> Settings,
    string? Warning);

public sealed class RenoDxDeploymentService
{
    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly RenoDxCatalogService _catalog;
    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly UpstreamEngineIniFileManager _engineIniFiles;
    private readonly RenoDxUnityDatabaseService? _unityDatabase;
    private readonly ControlUePostInstallService _controlUe;
    private readonly string _cacheDir;
    private readonly DeploymentJournal? _journal;

    /// <summary>
    /// #205: Control UE's DLL swaps inside RenoDX's operation: each swap is atomic on its own (in memory), and RenoDX's
    /// journaled operation, which tracked every file they touch first, is the one undo (a nested journaled swap would
    /// commit on its own and leave two mechanisms restoring the same file).
    /// </summary>
    private static readonly TransactionalFileDeploymentService InsideOperation = new();

    /// <param name="journal">#205: the app's deployment journal; install, update, local drop and removal each run as one
    /// journaled operation on RenoDX's record (null: the same operation, rolled back in process only).</param>
    public RenoDxDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        RenoDxCatalogService catalog,
        string? cacheDir = null,
        UpstreamRhiManifestService? upstreamManifest = null,
        RenoDxUnityDatabaseService? unityDatabase = null,
        ControlUePostInstallService? controlUe = null,
        DeploymentJournal? journal = null)
    {
        _http = http;
        _journal = journal;
        // Without a DLSS manager the Control UE step still restores on removal; it just cannot fetch DLLs.
        _controlUe = controlUe ?? new ControlUePostInstallService(new TransactionalFileDeploymentService(), newestDll: null);
        _records = records;
        _catalog = catalog;
        _upstreamManifest = upstreamManifest;
        _unityDatabase = unityDatabase;
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "renodx");
        _engineIniFiles = new UpstreamEngineIniFileManager(http, Path.Combine(_cacheDir, "engine-files"));
    }

    /// <summary>Hosts a RenoDX add-on may be downloaded from (see <see cref="TrustedDownloadPolicy.RenoDxAddons"/>).</summary>
    public static TrustedDownloadPolicy DownloadPolicy => TrustedDownloadPolicy.RenoDxAddons;

    /// <summary>#182: the global peak brightness written into RenoDX presets at install (null: none).</summary>
    public Func<PeakNitsSettings>? PeakNits { get; set; }

    /// <summary>#182: the RenoDX ⚙ UE-Extended choice, kept on the RenoDX record: "on" / "off" (absent = the catalogue's pick).</summary>
    public const string UeExtendedMetadataKey = "RenoDX.UeExtended";

    /// <summary>Upstream's default UE-Extended add-on (<c>MainViewModel.DefaultUeExtendedUrl</c>).</summary>
    public const string DefaultUeExtendedUrl = "https://marat569.github.io/renodx/renodx-ue-extended.addon64";

    /// <summary>
    /// #182: whether the RenoDX ⚙ offers the UE-Extended switch (upstream <c>UeExtendedToggleVisibility</c>): an Unreal
    /// Engine game (manifest hint, or a generic Unreal / UE-Extended add-on installed) the manifest does not bar from it.
    /// </summary>
    public static bool OffersUeExtendedSwitch(DetectedGame game, LinuxDeploymentRecord? record)
        => record is not null && !game.UeExtendedBlockedByManifest
           && (game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true
               || ModAuthorCredit.IsUeExtendedUrl(record.SourceUrl)
               || Path.GetFileName(record.SourceUrl ?? "").StartsWith("renodx-unrealengine.", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the installed RenoDX is the UE-Extended build.</summary>
    public static bool IsUeExtendedInstalled(LinuxDeploymentRecord? record) => ModAuthorCredit.IsUeExtendedUrl(record?.SourceUrl);

    /// <summary>
    /// #182: the user's UE-Extended choice over the catalogue's pick (upstream <c>ToggleUeExtended</c>): "on" swaps in the
    /// UE-Extended build; "off" swaps a UE-Extended pick for the generic Unreal add-on (upstream restores the card's earlier
    /// mod; the fork's catalogue picks UE-Extended only from a manifest override, where the generic add-on is that mod).
    /// </summary>
    internal static RenoDxModEntry? ApplyUeExtendedChoice(DetectedGame game, RenoDxModEntry? mod, string? choice)
    {
        if (game.UeExtendedBlockedByManifest) return mod;
        var isUeExtended = mod is not null && ModAuthorCredit.IsUeExtendedUrl(mod.SnapshotUrl64 ?? mod.SnapshotUrl32);
        if (string.Equals(choice, "on", StringComparison.Ordinal) && !isUeExtended)
            return new RenoDxModEntry("Unreal Engine (UE-Extended)", DefaultUeExtendedUrl, null, "UE-Extended (chosen in the RenoDX settings)",
                IsGeneric: true, Author: ModAuthorCredit.UeExtendedAuthor);
        if (string.Equals(choice, "off", StringComparison.Ordinal) && isUeExtended)
            return new RenoDxModEntry("Generic Unreal Engine", RenoDxCatalogService.GenericUnrealUrl, null, "Generic Unreal Engine (chosen in the RenoDX settings)",
                IsGeneric: true, Author: ModAuthorCredit.GenericUnrealAuthor);
        return mod;
    }

    // choice: the RenoDX ⚙ switch being applied (review 9); null = the one kept on the record.
    private async Task<RenoDxModEntry?> ResolveModAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken, string? choice = null)
        => ApplyUeExtendedChoice(game, await _catalog.ResolveAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false),
            choice ?? _records.Find(game, ManagedComponentKind.RenoDX)?.Metadata.GetValueOrDefault(UeExtendedMetadataKey));

    public bool IsManaged(DetectedGame game) => _records.Find(game, ManagedComponentKind.RenoDX) is not null;

    public Task<RenoDxModEntry?> PrepareCompatibilityAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
        => _catalog.PrimeResolvedMatchAsync(game, indexedFiles, cancellationToken);

    public bool TryGetPreparedCompatibility(DetectedGame game, out RenoDxModEntry? match)
        => _catalog.TryGetResolvedMatch(game, out match);

    public async Task<ManagedUpdateCheck> CheckUpdateAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.RenoDX);
        if (record is null) return new ManagedUpdateCheck(false, false, "RenoDX is not managed by RHI Linux.");
        // #195: an add-on the user dropped (or the watch folder found) has no source to compare with: never "current".
        if (IsLocalPackage(record)) return new ManagedUpdateCheck(true, false, LocalPackageCheckMessage, record.Version, CouldNotCheck: true);
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return new ManagedUpdateCheck(true, false, "Game executable is unavailable; RenoDX update check skipped.", record.Version, CouldNotCheck: true);
        var architecture = GameArchitecture.Of(game);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return new ManagedUpdateCheck(true, false, "Game architecture is unknown; RenoDX update check skipped.", record.Version, CouldNotCheck: true);
        try
        {
            var mod = await ResolveModAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false); // #182 UE-Extended choice
            if (mod is null) return new ManagedUpdateCheck(true, false, "No current RenoDX catalog match was found; installed payload was preserved.", record.Version, CouldNotCheck: true);
            var latestUrl = mod.UrlFor(architecture);
            if (string.IsNullOrWhiteSpace(latestUrl))
                return new ManagedUpdateCheck(true, false, "Current RenoDX catalog match has no compatible payload for this architecture.", record.Version, CouldNotCheck: true);
            if (!DownloadPolicy.IsTrusted(latestUrl))
                return new ManagedUpdateCheck(true, false, new UntrustedDownloadException(latestUrl, DownloadPolicy).Message, record.Version, CouldNotCheck: true);
            if (!string.Equals(record.SourceUrl, latestUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.Version, mod.Name, StringComparison.OrdinalIgnoreCase))
                return new ManagedUpdateCheck(true, true,
                    $"RenoDX catalog update available: {record.Version ?? "unknown"} → {mod.Name}.", record.Version, mod.Name);

            // Snapshots are rebuilt in place under the same URL and name (upstream 2.8.0 flags them
            // from the remote file), so compare what the server serves now with what was installed.
            var (remote, error) = await HeadAsync(latestUrl, cancellationToken).ConfigureAwait(false);
            if (remote is null)
                return new ManagedUpdateCheck(true, false, $"RenoDX update check could not reach the snapshot: {error}", record.Version, CouldNotCheck: true);
            var recorded = InstalledRemoteStamp(record);
            var comparison = recorded is not null
                ? RenoDxRemoteStamp.Compare(recorded, remote)
                : CompareLegacyRecord(record, remote);
            return comparison switch
            {
                RemoteStampComparison.Changed => new ManagedUpdateCheck(true, true,
                    $"RenoDX snapshot was rebuilt upstream: {mod.Name}.", record.Version, mod.Name),
                RemoteStampComparison.Same => new ManagedUpdateCheck(true, false,
                    $"RenoDX snapshot is current: {mod.Name}.", record.Version, mod.Name),
                _ => new ManagedUpdateCheck(true, false,
                    "RenoDX update check could not compare the snapshot: the server sent no ETag, Last-Modified or size.", record.Version, CouldNotCheck: true)
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"RenoDX update check failed: {ex.Message}", record.Version, CouldNotCheck: true);
        }
    }

    public const string RemoteStampMetadataKey = "RenoDX.RemoteStamp";
    public const string AddonVersionMetadataKey = "RenoDX.AddonVersion";

    /// <summary>
    /// Version shown in the update history (upstream 2.8.0): the add-on's own version recorded at
    /// install, else read from the installed file (records written before it was recorded).
    /// </summary>
    public static string? InstalledAddonVersion(LinuxDeploymentRecord record)
    {
        if (record.Metadata.TryGetValue(AddonVersionMetadataKey, out var recorded) && !string.IsNullOrWhiteSpace(recorded))
            return recorded;
        var installed = record.Files.FirstOrDefault()?.Path;
        return installed is not null && File.Exists(installed) ? ReadAddonVersion(installed) : null;
    }

    /// <summary>
    /// Upstream AuxInstallService.ReadInstalledVersion for add-ons: PE ProductVersion, else FileVersion,
    /// with RenoDX's "0.YYYY.MMDD.HHMM" shown as "YY.MMDD.HHMM"; no version resource → file time in
    /// the same shape. Linux also accepts the fixed VS_FIXEDFILEINFO number before the file time.
    /// </summary>
    public static string ReadAddonVersion(string path)
    {
        var info = PeVersionReader.Read(path);
        var raw = !string.IsNullOrWhiteSpace(info?.ProductVersion) ? info.ProductVersion
            : !string.IsNullOrWhiteSpace(info?.FileVersion) ? info.FileVersion
            : info?.FixedFileVersion?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            var written = File.GetLastWriteTimeUtc(path);
            return written.ToString("yy'.'MMdd'.'HHmm", System.Globalization.CultureInfo.InvariantCulture);
        }
        return FormatAddonVersion(raw);
    }

    internal static string FormatAddonVersion(string raw)
    {
        var parts = raw.Trim().Split('.');
        if (parts.Length < 2 || parts[0] != "0") return raw.Trim();
        var remaining = parts[1..];
        if (remaining[0].Length == 4) remaining[0] = remaining[0][2..];
        return string.Join('.', remaining);
    }

    internal static RenoDxRemoteStamp? InstalledRemoteStamp(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(RemoteStampMetadataKey, out var json) ? RenoDxRemoteStamp.Deserialize(json) : null;

    // Records written before stamps were kept: a remote modified after the install is newer
    // (catches same-size rebuilds); otherwise fall back to upstream's size comparison. Residual gap:
    // a dev build with the size-only cache could install a stale same-size file after a rebuild;
    // that record reads as current until the snapshot is rebuilt again or the mod is reinstalled.
    private static RemoteStampComparison CompareLegacyRecord(LinuxDeploymentRecord record, RenoDxRemoteStamp remote)
    {
        var installedAt = record.InstalledAtUtc.Kind == DateTimeKind.Local
            ? record.InstalledAtUtc.ToUniversalTime()
            : DateTime.SpecifyKind(record.InstalledAtUtc, DateTimeKind.Utc);
        if (remote.LastModified is { } modified && modified.UtcDateTime > installedAt)
            return RemoteStampComparison.Changed;
        var installedFile = record.Files.FirstOrDefault()?.Path;
        if (remote.Length is > 0 && installedFile is not null && File.Exists(installedFile))
            return new FileInfo(installedFile).Length == remote.Length ? RemoteStampComparison.Same : RemoteStampComparison.Changed;
        return remote.LastModified is not null ? RemoteStampComparison.Same : RemoteStampComparison.Unknown;
    }

    /// <param name="ueExtendedChoice">#182 review 9: the RenoDX ⚙ UE-Extended switch ("on"/"off") this update applies; it is
    /// saved only with the update's own record (nothing changes when the update fails). Null = the choice on the record.</param>
    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default,
        string? ueExtendedChoice = null)
    {
        var record = _records.Find(game, ManagedComponentKind.RenoDX);
        if (record is null) return ComponentOperationResult.Fail("RenoDX is not managed by RHI Linux.");
        foreach (var file in record.Files)
        {
            if (!File.Exists(file.Path) || string.IsNullOrWhiteSpace(file.Sha256))
                return ComponentOperationResult.Fail($"RenoDX managed file is missing or has no ownership hash: {file.Path}");
            var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed RenoDX file was modified outside RHI and will not be updated: {file.Path}");
        }
        return await InstallAsync(game, indexedFiles, cancellationToken, ueExtendedChoice).ConfigureAwait(false);
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default,
        string? ueExtendedChoice = null)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RenoDX add-ons require the Windows ReShade add-on API and are not deployed to native Linux games.");
        // #141: an unlinked title is still installable from a manifest snapshot override, and only from that.
        if (game.WikiUnlinkedByManifest && !await HasSnapshotOverrideAsync(game, cancellationToken).ConfigureAwait(false))
            return ComponentOperationResult.Fail("Upstream wikiUnlinks explicitly removes this title from RenoDX deployment.");
        if (!string.IsNullOrWhiteSpace(game.ExternalModUrl))
            return ComponentOperationResult.Fail($"Upstream forceExternalOnly requires external installation ({game.ExternalModLabel ?? "Open external mod"}): {game.ExternalModUrl}");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("O diretório de binários do jogo ainda não é conhecido.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (!HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install ReShade with full add-on support first. RenoDX is a ReShade add-on.");

        var architecture = GameArchitecture.Of(game);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("Could not determine game bitness for the RenoDX add-on.");

        RenoDxModEntry? mod;
        try { mod = await ResolveModAsync(game, indexedFiles, cancellationToken, ueExtendedChoice).ConfigureAwait(false); } // #182 UE-Extended choice
        catch (Exception ex) { return ComponentOperationResult.Fail($"Could not fetch/parse the RenoDX catalog: {ex.Message}"); }
        if (mod is null)
            return ComponentOperationResult.Fail("Nenhum perfil RenoDX específico ou fallback genérico seguro foi encontrado para este jogo.");

        var url = mod.UrlFor(architecture);
        if (string.IsNullOrWhiteSpace(url))
            return ComponentOperationResult.Fail($"{mod.Name} does not publish a compatible {(architecture == PeArchitecture.X86 ? "32-bit" : "64-bit")} add-on.");
        // DEP-06: wiki, database, manifest and Unity links alike must be HTTPS on a trusted host; checked before any download.
        if (!DownloadPolicy.IsTrusted(url))
            return ComponentOperationResult.Fail(new UntrustedDownloadException(url, DownloadPolicy).Message);
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        var extension = Path.GetExtension(fileName);
        if ((!extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase))
            || fileName.IndexOfAny(['*', '?', '\\', ':']) >= 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ComponentOperationResult.Fail("RenoDX catalog returned an unsupported file type.");

        IReadOnlyDictionary<string, string> manifestIniOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_upstreamManifest is not null)
        {
            try
            {
                var gameOverride = await _upstreamManifest.ResolveGameOverrideAsync(game, cancellationToken).ConfigureAwait(false);
                if (gameOverride is not null && gameOverride.RenoDxIniOverrides.Count > 0)
                    manifestIniOverrides = gameOverride.RenoDxIniOverrides;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Manifest INI values are an enrichment layer. A temporary manifest failure must not
                // block the already-resolved RenoDX payload or mutate local INI state.
            }
        }

        var unityUpgrades = await ResolveUnityDatabaseUpgradesAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
        var requestedIniOverrides = MergeIniOverrides(unityUpgrades, manifestIniOverrides);

        var deployDir = ResolveAddonDeployPath(game.BinaryDirectory);
        Directory.CreateDirectory(deployDir);
        var target = Path.Combine(deployDir, fileName);
        var existingRecord = _records.Find(game, ManagedComponentKind.RenoDX);
        var managedOldPath = existingRecord?.Files.FirstOrDefault()?.Path;

        foreach (var existing in Directory.EnumerateFiles(deployDir, "renodx*.addon*", CaseInsensitiveTopLevel))
        {
            var existingName = Path.GetFileName(existing);
            if (IsAuxiliaryRenoDxAddon(existingName)) continue;
            if (managedOldPath is not null && Path.GetFullPath(existing) == Path.GetFullPath(managedOldPath)) continue;
            if (Path.GetFullPath(existing) == Path.GetFullPath(target) && existingRecord is not null) continue;
            return ComponentOperationResult.Fail($"Foreign RenoDX protection: another RenoDX add-on already exists at {existing}. RHI Linux will not overwrite or stack it automatically.");
        }

        // DEP-10: whatever the name (manifest overrides need not start with "renodx"), a file at the target that the
        // RenoDX record does not own, or that changed since RHI wrote it, is never overwritten — and so never deleted on
        // remove. Matched case-insensitively: Wine and ReShade would load a case variant alongside it.
        foreach (var sameName in Directory.EnumerateFiles(deployDir, "*", CaseInsensitiveTopLevel)
                     .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            var owned = existingRecord?.Files.FirstOrDefault(file => PathsEqual(file.Path, sameName));
            if (owned is null)
                return ComponentOperationResult.Fail($"RenoDX install refused: a file RHI Linux does not own already has this add-on's name. Move or remove it, then install again: {sameName}");
            if (string.IsNullOrWhiteSpace(owned.Sha256)
                || !string.Equals(await FileHash.Sha256HexAsync(sameName, cancellationToken).ConfigureAwait(false), owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed RenoDX file was modified outside RHI and will not be replaced: {sameName}");
        }

        // Downloads and reads first (Cancel still honoured), every game file change after the write gate.
        string cached;
        RenoDxRemoteStamp? remoteStamp;
        ControlUePostInstallState? previousControlState;
        ControlUeSources? controlSources;
        UpstreamEngineIniTemplate? engineIniTemplate;
        LinuxDeployedFile? oldManagedFile = null;
        // #218: a UE-Extended install reads the game's UE-Extended database entry (upstream's priority over the manifest).
        var isUeExtended = ModAuthorCredit.IsUeExtendedUrl(url);
        var ueExtendedEntry = isUeExtended ? await FindUnrealEntryAsync(game, cancellationToken).ConfigureAwait(false) : null;
        try
        {
            (cached, remoteStamp) = await DownloadAsync(url, fileName, cancellationToken).ConfigureAwait(false);
            // Control UE (upstream 2.7.9): runs on a fresh install, which the UI confirms first, or on a reinstall/update of
            // an install that already carries the step. Its DLLs are fetched here, before any game file changes.
            previousControlState = ControlUePostInstallService.Deserialize(existingRecord);
            controlSources = ControlUePostInstallService.Applies(game, fileName) &&
                             (existingRecord is null || previousControlState is not null)
                ? await _controlUe.PrepareAsync(cancellationToken).ConfigureAwait(false)
                : null;
            // The upstream Engine.ini template is fetched here, with the other downloads, and applied after the gate
            // (w2 merge review, minor 1).
            engineIniTemplate = string.IsNullOrWhiteSpace(game.EngineIniFileOverride)
                ? null
                : await _engineIniFiles.FetchAsync(game.EngineIniFileOverride!, cancellationToken).ConfigureAwait(false);
            if (existingRecord is not null && managedOldPath is not null && !PathsEqual(managedOldPath, target))
            {
                oldManagedFile = existingRecord.Files.First();
                if (File.Exists(oldManagedFile.Path)
                    && !string.Equals(await FileHash.Sha256HexAsync(oldManagedFile.Path, cancellationToken).ConfigureAwait(false), oldManagedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Managed RenoDX file was modified outside RHI and will not be replaced: {oldManagedFile.Path}");
            }
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RenoDX install failed: {ex.Message}");
        }

        var previousIniState = existingRecord is not null &&
            existingRecord.Metadata.TryGetValue(RenoDxIniOverrideManager.MetadataKey, out var previousIniJson)
            ? RenoDxIniOverrideManager.Deserialize(previousIniJson)
            : null;
        var previousEngineIniState = existingRecord is not null &&
            existingRecord.Metadata.TryGetValue(UpstreamEngineIniFileManager.MetadataKey, out var previousEngineIniJson)
                ? UpstreamEngineIniFileManager.Deserialize(previousEngineIniJson)
                : null;
        var peak = PeakNits?.Invoke() is { Applies: true } global ? global : null; // #182
        // #217/#218: RenoDX's own Engine.ini keys (r.LUT.UpdateEveryFrame on Unreal games; UE-Extended's HDR keys), as the
        // user's ⚙ choices, the database and the manifest allow. A RenoDX that is no longer UE-Extended takes its HDR keys out.
        var hdrApplies = isUeExtended && RenoDxEngineIni.HdrApplies(game, ueExtendedEntry);
        var engineKeysOn = EngineIniKeysFor(game, existingRecord, hdrApplies);
        var engineKeysOff = isUeExtended ? NoGroups : HdrOnly;
        var engineKeysState = RenoDxEngineIni.Read(existingRecord);
        var engineKeysIni = engineKeysOn.Count > 0 ? RenoDxEngineIni.ResolvePath(game)
            : engineKeysState?.Entries.Any(entry => engineKeysOff.Contains(entry.Group)) == true ? engineKeysState.IniPath : null;
        var engineKeysSkipped = EngineKeysRefusal(ref engineKeysIni, engineKeysState); // review 2: optional, never fails RenoDX
        // #218: UE-Extended's [renodx] native-HDR keys (missing ones only, as upstream; its database upgrade key forced).
        var previousNative = RenoDxIniOverrideManager.Deserialize(existingRecord?.Metadata.GetValueOrDefault(RenoDxEngineIni.NativeHdrMetadataKey));
        var nativeRequested = isUeExtended ? NativeHdrRequest(game, ueExtendedEntry, previousNative) : new Dictionary<string, string>();
        var nativeCleanup = !isUeExtended && previousNative is not null;

        // #205: one journaled operation on RenoDX's record — the add-on, its [renodx] keys, peak brightness, the Engine.ini
        // template, RHI's earlier add-on under another name and the Control UE steps; the stamped record is the commit point.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RenoDX install: " + game.Name, game, ManagedComponentKind.RenoDX, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var inis = new RenoDxIniTracking(scope);
        RenoDxIniOverrideApplyResult iniApply;
        Dictionary<string, RenoDxIniOverrideState> peakStates;
        UpstreamEngineIniApplyResult engineIniApply = new(previousEngineIniState, 0, 0, null);
        var controlNotes = new List<string>();
        try
        {
            // Downloads and checks are done: from here on, Cancel is refused (a late cancel used to leave the add-on and its
            // INI keys in place with no record).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            // The INI files first, at the files a write through them replaces (a refused ReShade.ini link changes nothing).
            if (RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory) is { } reshadeIni && File.Exists(reshadeIni)
                && (requestedIniOverrides.Count > 0 || peak is not null || nativeRequested.Count > 0))
                await inis.TrackReShadeIniAsync(reshadeIni, game.BinaryDirectory, game.InstallPath, cancellationToken).ConfigureAwait(false);
            if (nativeCleanup && previousNative!.IniPath is { Length: > 0 } nativeIni && File.Exists(nativeIni))
                await inis.TrackReShadeIniAsync(nativeIni, Path.GetDirectoryName(nativeIni) ?? nativeIni, game.InstallPath, cancellationToken).ConfigureAwait(false);
            foreach (var engineIni in EngineIniPathsTouched(game, engineIniTemplate, previousEngineIniState))
                await inis.TrackEngineIniAsync(engineIni, cancellationToken).ConfigureAwait(false);
            await TrackEngineKeysAsync(inis, engineKeysIni, engineKeysState, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("RenoDX target directory is invalid."));
            // The verified copy returns the hash of what it placed: no second read of the target.
            var newHash = await scope.PlaceCopyAsync(cached, target, cancellationToken).ConfigureAwait(false);
            // #218: the native-HDR keys first, then the manifest's [renodx] values (upstream's order: the manifest wins).
            var nativeState = previousNative;
            if (nativeRequested.Count > 0)
                nativeState = RenoDxIniOverrideManager.Apply(game.BinaryDirectory, nativeRequested, previousNative, gameRoot: game.InstallPath).State;
            else if (nativeCleanup)
            {
                RenoDxIniOverrideManager.Cleanup(previousNative, gameRoot: game.InstallPath);
                nativeState = null;
            }
            iniApply = RenoDxIniOverrideManager.Apply(game.BinaryDirectory, requestedIniOverrides, previousIniState, gameRoot: game.InstallPath);
            // #182: the global peak brightness into the chosen presets (kept where the user changed it since RHI's write).
            peakStates = RenoDxPeakNits.Read(existingRecord);
            if (peak is not null)
                peakStates = RenoDxPeakNits.Apply(game.BinaryDirectory, game.InstallPath, peak.Nits, RenoDxPeakNits.GlobalSections(peak.Presets), peakStates, force: false);
            if (engineIniTemplate is not null)
            {
                engineIniApply = UpstreamEngineIniFileManager.Apply(game, engineIniTemplate, previousEngineIniState);
            }
            else if (previousEngineIniState is not null)
            {
                UpstreamEngineIniFileManager.Cleanup(previousEngineIniState);
                engineIniApply = new UpstreamEngineIniApplyResult(null, 0, 0, null);
            }
            if (engineKeysIni is not null)
            {
                engineKeysState = RenoDxEngineIni.ApplyOptional(engineKeysIni, engineKeysOn, engineKeysOff, engineKeysState, out var keysFailure);
                engineKeysSkipped ??= keysFailure;
            }
            await inis.DeclareWrittenAsync(cancellationToken).ConfigureAwait(false);

            // RHI's earlier add-on under another name goes before the commit point, so a rollback brings it back.
            if (oldManagedFile is not null && File.Exists(oldManagedFile.Path))
                await scope.DeleteAsync(oldManagedFile.Path, cancellationToken).ConfigureAwait(false);

            // Put the previous Control UE changes back first, then apply afresh, so the first pre-RHI state stays the restore
            // point; a file the user changed since (DLSS version, HDR preset) is left as they set it and not applied again
            // (#124). Every file either step may change is tracked in this operation first (#205): one undo for all of it.
            var controlState = previousControlState;
            if (previousControlState is not null || controlSources is not null)
            {
                var controlPaths = await ControlUePostInstallService.TrackAsync(
                    scope, game, game.BinaryDirectory, previousControlState, controlSources, cancellationToken).ConfigureAwait(false);
                IReadOnlySet<string>? controlKeptUserChanges = null;
                if (previousControlState is not null)
                {
                    var restored = await _controlUe.RestoreAsync(previousControlState, InsideOperation).ConfigureAwait(false);
                    controlState = restored.Remaining;
                    controlKeptUserChanges = restored.KeptUserChanges;
                    if (restored.Remaining is not null) controlNotes.AddRange(restored.Notes);
                }
                if (controlSources is not null && controlState is null)
                {
                    var applied = await _controlUe.ApplyAsync(game, game.BinaryDirectory, controlSources, controlKeptUserChanges, InsideOperation).ConfigureAwait(false);
                    controlState = applied.State;
                    controlNotes.AddRange(applied.Notes);
                }
                await ControlUePostInstallService.DeclareWrittenAsync(scope, controlPaths, cancellationToken).ConfigureAwait(false);
            }

            var record = new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game),
                GameName = game.Name,
                Component = ManagedComponentKind.RenoDX,
                BinaryDirectory = game.BinaryDirectory,
                SourceUrl = url,
                Version = mod.Name,
                InstalledAtUtc = DateTime.UtcNow,
                Files =
                [
                    new LinuxDeployedFile
                    {
                        Path = target,
                        ExistedBeforeInstall = false,
                        Sha256 = newHash
                    }
                ]
            };
            if (remoteStamp is not null)
                record.Metadata[RemoteStampMetadataKey] = remoteStamp.Serialize();
            record.Metadata[AddonVersionMetadataKey] = ReadAddonVersion(cached);
            if (iniApply.State is not null)
                record.Metadata[RenoDxIniOverrideManager.MetadataKey] = RenoDxIniOverrideManager.Serialize(iniApply.State);
            if (engineIniApply.State is not null)
                record.Metadata[UpstreamEngineIniFileManager.MetadataKey] = UpstreamEngineIniFileManager.Serialize(engineIniApply.State);
            if (controlState is not null)
                record.Metadata[ControlUePostInstallService.MetadataKey] = ControlUePostInstallService.Serialize(controlState);
            // Control UE frame generation's FgSlHdr10 (#136) stays owned across updates, so removal can still put it back.
            if (existingRecord?.Metadata.GetValueOrDefault(ControlUeFrameGenerationService.HdrMetadataKey) is { Length: > 0 } fgHdrState)
                record.Metadata[ControlUeFrameGenerationService.HdrMetadataKey] = fgHdrState;
            if (peakStates.Count > 0) record.Metadata[RenoDxPeakNits.MetadataKey] = RenoDxPeakNits.Serialize(peakStates); // #182
            if ((ueExtendedChoice ?? existingRecord?.Metadata.GetValueOrDefault(UeExtendedMetadataKey)) is { Length: > 0 } ueChoice)
                record.Metadata[UeExtendedMetadataKey] = ueChoice; // #182: the UE-Extended choice stays across updates; a new one is saved here, in the update's own record
            CarryEngineIniKeys(existingRecord, record, engineKeysState); // #217: the ⚙ choices stay, the keys stay owned
            if (hdrApplies) record.Metadata[RenoDxEngineIni.HdrAppliesMetadataKey] = "1"; // #218: the ⚙ offers the HDR row
            if (nativeState is not null) record.Metadata[RenoDxEngineIni.NativeHdrMetadataKey] = RenoDxIniOverrideManager.Serialize(nativeState);
            // The commit point: nothing above survives without this record (the rollback, or the next start, undoes it all).
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            // #204 (w8 integration): the stamped record landed and then the save reported an error: finished as committed.
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX" + EngineKeysNote(engineKeysSkipped), false, target);
            if (rollback.RolledBack) inis.RestoreModesAfterRollback();
            return ComponentOperationResult.Fail($"RenoDX install failed: {ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);

        var iniSummary = iniApply.AppliedCount > 0
            ? unityUpgrades.Count > 0
                ? $" · {iniApply.AppliedCount} [renodx] override(s) applied (RHI Unity database / manifest)"
                : $" · {iniApply.AppliedCount} manifest [renodx] override(s) applied"
            : iniApply.PreservedUserEdits > 0
                ? $" · {iniApply.PreservedUserEdits} user-edited [renodx] override(s) preserved"
                : "";
        var engineIniSummary = engineIniApply.AppliedCount > 0
            ? $" · {engineIniApply.AppliedCount} upstream Engine.ini template key(s) applied from {game.EngineIniFileOverride}"
            : engineIniApply.PreservedUserEdits > 0
                ? $" · {engineIniApply.PreservedUserEdits} user-edited Engine.ini key(s) preserved"
                : "";
        var controlSummary = controlNotes.Count > 0 ? $" · Control UE: {string.Join(" ", controlNotes)}" : "";
        return ComponentOperationResult.Ok($"RenoDX installed: {mod.Name} → {target}{iniSummary}{engineIniSummary}{controlSummary}{EngineKeysNote(engineKeysSkipped)}", target);
    }

    // ── #217: RenoDX's own Engine.ini keys ─────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> NoGroups = new HashSet<string>();

    /// <summary>
    /// The Engine.ini key groups an install, update or local drop writes (upstream: the LUT key on an Unreal game unless the
    /// user turned it off in the ⚙, the manifest's <c>ueExtendedCompatibility</c> says <c>lut: false</c> or a custom Engine.ini
    /// file manages Engine.ini).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> EngineIniKeysFor(
        DetectedGame game, LinuxDeploymentRecord? existing, bool hdr)
    {
        var on = new Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>>(StringComparer.Ordinal);
        if (RenoDxEngineIni.LutApplies(game) && RenoDxEngineIni.LutChosen(existing)) on[RenoDxEngineIni.LutGroup] = RenoDxEngineIni.LutKeys;
        if (hdr && RenoDxEngineIni.HdrChosen(existing)) on[RenoDxEngineIni.HdrGroup] = RenoDxEngineIni.HdrKeys; // #218
        return on;
    }

    private static readonly IReadOnlySet<string> HdrOnly = new HashSet<string> { RenoDxEngineIni.HdrGroup };

    /// <summary>#218: upstream's UE-Extended database (<c>RenoDXdb-unreal.json</c>); null (tests) = the manifest / engine rule only.</summary>
    public RenoDxUnrealDatabaseService? UnrealDatabase { get; set; }

    // Enrichment only, as upstream: a database that cannot be read leaves the manifest / engine rule.
    private async Task<RenoDxUnrealDbEntry?> FindUnrealEntryAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (UnrealDatabase is null) return null;
        try { return await UnrealDatabase.FindAsync(game.Name).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("renodx", "The UE-Extended database could not be read; the manifest decides the HDR settings", ex);
            return null;
        }
    }

    /// <summary>
    /// #218 (upstream <c>ApplyRenoDxNativeHdrSettings</c>): the <c>[renodx]</c> keys UE-Extended needs that the section does not
    /// have yet (a value already there is never overwritten; keys RHI added before stay requested, so they stay owned), with
    /// <c>Set_Path</c> from the database (<c>Method</c>) or the engine (Unreal Engine 4 = the SDR path), plus the database's
    /// upgrade key, which is written whatever is there (upstream's one forced key).
    /// </summary>
    private static Dictionary<string, string> NativeHdrRequest(DetectedGame game, RenoDxUnrealDbEntry? entry, RenoDxIniOverrideState? previous)
    {
        var section = string.IsNullOrWhiteSpace(game.BinaryDirectory) ? null : RenoDxIniOverrideManager.ReadRenoDxSection(game.BinaryDirectory);
        var owned = (previous?.Entries ?? []).Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var request = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in RenoDxEngineIni.NativeHdrKeys(entry?.UsesSdrPath ?? RenoDxEngineIni.IsUnreal4(game)))
            if (section is null || !section.ContainsKey(key) || owned.Contains(key)) request[key] = value;
        if (entry is { UpgradeIniKey: { Length: > 0 } upgradeKey, UpgradeIniValue: { Length: > 0 } upgradeValue })
            request[upgradeKey] = upgradeValue;
        return request;
    }

    /// <summary>Tracks the Engine.ini the keys go into, and the one RHI's keys were in when the game's Engine.ini moved since.</summary>
    private static async Task TrackEngineKeysAsync(RenoDxIniTracking inis, string? next, RenoDxEngineIniState? previous, CancellationToken cancellationToken)
    {
        if (next is null) return;
        await inis.TrackEngineIniAsync(next, cancellationToken).ConfigureAwait(false);
        if (previous?.IniPath is { Length: > 0 } old && File.Exists(old)) await inis.TrackEngineIniAsync(old, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Review 2: why RenoDX's Engine.ini keys cannot go into <paramref name="ini"/> (or come out of the file RHI's keys were in
    /// before) on an install, update or drop — a link outside the prefix, an unreadable file — checked before anything is
    /// tracked; then the step is skipped (<paramref name="ini"/> = null, the owned keys stay as they are), logged, and the
    /// result says why. Null: the step runs.
    /// </summary>
    private static string? EngineKeysRefusal(ref string? ini, RenoDxEngineIniState? state)
    {
        if (ini is null) return null;
        var refusal = RenoDxEngineIni.CannotEdit(ini);
        if (refusal is null && state?.IniPath is { Length: > 0 } old && !PathsEqual(old, ini) && File.Exists(old)) refusal = RenoDxEngineIni.CannotEdit(old);
        if (refusal is null) return null;
        AppLog.Warn("renodx-engine-ini", $"RenoDX's Engine.ini line is not written to {ini}; RenoDX itself goes on: {refusal}");
        ini = null;
        return refusal;
    }

    /// <summary>The result's note when the Engine.ini keys were skipped (empty when they were not).</summary>
    private static string EngineKeysNote(string? skipped) => skipped is null ? "" : " · " + EngineIniKeysNotWrittenPrefix + skipped;

    /// <summary>Fixed text of the note when RenoDX installed but its Engine.ini keys could not be written (PT-BR pair); the reason follows.</summary>
    public const string EngineIniKeysNotWrittenPrefix = "RenoDX's Unreal colour-grading/HDR lines were not written to Engine.ini: ";

    /// <summary>Why the keys were not written when Engine.ini is not text RHI can edit byte for byte (PT-BR pair).</summary>
    public const string EngineIniNotTextMessage = "Engine.ini is not UTF-8 or UTF-16 text, so RHI does not edit it.";

    /// <summary>A fresh record keeps the ⚙'s Engine.ini choices of the one it replaces, and the keys RHI owns now.</summary>
    private static void CarryEngineIniKeys(LinuxDeploymentRecord? existing, LinuxDeploymentRecord record, RenoDxEngineIniState? state)
    {
        foreach (var choice in new[] { RenoDxEngineIni.LutChoiceMetadataKey, RenoDxEngineIni.HdrChoiceMetadataKey })
            if (existing?.Metadata.GetValueOrDefault(choice) is { Length: > 0 } value) record.Metadata[choice] = value;
        RenoDxEngineIni.Write(record, state);
    }

    /// <summary>Fixed text when the ⚙'s Engine.ini setting is asked of a game whose RenoDX RHI did not install (PT-BR pair); the game follows.</summary>
    public const string EngineIniNotManagedPrefix = "RenoDX's Engine.ini settings need RenoDX installed by RHI: ";

    /// <summary>Fixed text when the ⚙'s Engine.ini setting does not apply to the game (PT-BR pair); the game follows.</summary>
    public const string EngineIniNotOfferedPrefix = "This Engine.ini setting does not apply to this game: ";

    /// <summary>The LUT key as written (for the ⚙ row's tooltip: the INI key only there).</summary>
    public static string EngineIniLutLines => string.Join(", ", RenoDxEngineIni.LutKeys.Select(key => $"{key.Key}={key.Value}"));

    /// <summary>Whether the RenoDX ⚙ offers the LUT row for <paramref name="game"/> (RHI's RenoDX, an Unreal game it applies to).</summary>
    public bool OffersEngineIniLut(DetectedGame game)
        => IsManaged(game) && RenoDxEngineIni.LutApplies(game)
           && RenoDxEngineIni.RowUsable(game, _records.Find(game, ManagedComponentKind.RenoDX), RenoDxEngineIni.LutGroup);

    /// <summary>Whether the LUT key is in effect for the ⚙ (RHI's, or the user's own with the value 1).</summary>
    public bool EngineIniLutIsOn(DetectedGame game) => RenoDxEngineIni.LutIsOn(game, _records.Find(game, ManagedComponentKind.RenoDX));

    /// <summary>
    /// #217: the RenoDX ⚙ LUT switch (upstream's "LUT Update Every Frame"): On adds RHI's key where the file lacks it, Off
    /// takes RHI's key out (one the user had stays); the choice is kept on RenoDX's record, so updates follow it. One
    /// journaled operation on RenoDX's record (the record is the commit point).
    /// </summary>
    public Task<ComponentOperationResult> SetEngineIniLutAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
        => SetEngineIniKeysAsync(game, RenoDxEngineIni.LutGroup, RenoDxEngineIni.LutKeys, RenoDxEngineIni.LutChoiceMetadataKey,
            RenoDxEngineIni.LutApplies(game), on, cancellationToken);

    /// <summary>
    /// #218: whether the RenoDX ⚙ offers the HDR row (upstream's "Engine.ini HDR"): RHI's UE-Extended RenoDX on a game whose
    /// last install found the HDR keys apply (database <c>Method: ini</c>, or the manifest / engine rule), with no custom
    /// Engine.ini file and an Engine.ini location.
    /// </summary>
    public bool OffersEngineIniHdr(DetectedGame game) => HdrOffered(game, _records.Find(game, ManagedComponentKind.RenoDX));

    private static bool HdrOffered(DetectedGame game, LinuxDeploymentRecord? record)
        => record is not null && IsUeExtendedInstalled(record)
           && record.Metadata.GetValueOrDefault(RenoDxEngineIni.HdrAppliesMetadataKey) == "1"
           && string.IsNullOrWhiteSpace(game.EngineIniFileOverride) && RenoDxEngineIni.ResolvePath(game) is not null
           && RenoDxEngineIni.RowUsable(game, record, RenoDxEngineIni.HdrGroup);

    /// <summary>#218: whether UE-Extended's HDR keys are in effect for the ⚙.</summary>
    public bool EngineIniHdrIsOn(DetectedGame game) => RenoDxEngineIni.HdrIsOn(game, _records.Find(game, ManagedComponentKind.RenoDX));

    /// <summary>The HDR keys as written (the ⚙ row's tooltip).</summary>
    public static string EngineIniHdrLines => string.Join(", ", RenoDxEngineIni.HdrKeys.Select(key => $"{key.Key}={key.Value}"));

    /// <summary>
    /// #218: the RenoDX ⚙ HDR switch (upstream's "Engine.ini HDR" Off / On): On adds UE-Extended's HDR keys where the file
    /// lacks them, Off takes RHI's out (keys the user had stay). One journaled operation on RenoDX's record.
    /// </summary>
    public Task<ComponentOperationResult> SetEngineIniHdrAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
        => SetEngineIniKeysAsync(game, RenoDxEngineIni.HdrGroup, RenoDxEngineIni.HdrKeys, RenoDxEngineIni.HdrChoiceMetadataKey,
            OffersEngineIniHdr(game), on, cancellationToken);

    private async Task<ComponentOperationResult> SetEngineIniKeysAsync(DetectedGame game, string group,
        IReadOnlyList<(string Section, string Key, string Value)> keys, string choiceKey, bool applies, bool on, CancellationToken cancellationToken)
    {
        // Find hands out a copy; the ledger changes only through the stamped Upsert below.
        var record = _records.Find(game, ManagedComponentKind.RenoDX);
        if (record is null) return ComponentOperationResult.Fail(EngineIniNotManagedPrefix + game.Name);
        if (!applies || RenoDxEngineIni.ResolvePath(game) is not { } ini) return ComponentOperationResult.Fail(EngineIniNotOfferedPrefix + game.Name);
        var state = RenoDxEngineIni.Read(record);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RenoDX Engine.ini setting: " + game.Name, game, ManagedComponentKind.RenoDX, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var inis = new RenoDxIniTracking(scope);
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await TrackEngineKeysAsync(inis, ini, state, cancellationToken).ConfigureAwait(false);
            state = on
                ? RenoDxEngineIni.Apply(ini, new Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> { [group] = keys }, NoGroups, state)
                : RenoDxEngineIni.Apply(ini, new Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>>(), new HashSet<string> { group }, state);
            await inis.DeclareWrittenAsync(cancellationToken).ConfigureAwait(false);
            if (on) record.Metadata.Remove(choiceKey);
            else record.Metadata[choiceKey] = RenoDxEngineIni.Off;
            RenoDxEngineIni.Write(record, state);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX", false, ini); // #204: the switch's record landed
            if (rollback.RolledBack) inis.RestoreModesAfterRollback();
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(ini, ini);
    }

    /// <summary>#205: the Engine.ini files an install's template step writes or cleans (the new target and the previous one).</summary>
    private static IEnumerable<string> EngineIniPathsTouched(DetectedGame game, UpstreamEngineIniTemplate? template, UpstreamEngineIniFileState? previous)
    {
        if (template is not null && UpstreamEngineIniFileManager.ResolveTargetPath(game) is { } next) yield return next;
        if ((template is not null || previous is not null) && previous?.IniPath is { Length: > 0 } old) yield return old;
    }

    // ── #195: a dropped or watched RenoDX add-on ─────────────────────────────────────────────────────────────────

    /// <summary>Record metadata of a RenoDX installed from a local file (#195): Update All cannot check it.</summary>
    public const string LocalPackageMetadataKey = "RenoDX.LocalPackage";
    public const string LocalPackageCheckMessage = "RenoDX was installed from a local file; Update All cannot check it.";
    public const string LocalInstalledPrefix = "RenoDX installed from a local file: ";
    public const string LocalNotAnAddonPrefix = "Not a RenoDX add-on RHI can install (a ReShade add-on file: .addon64, .addon32 or .addon): ";
    public const string LocalLedgerUnreadablePrefix = "RenoDX install refused: RHI's deployment records cannot be read, so nothing was written. Copy of the records: ";
    public const string Local32BitOn64BitMessage = "This RenoDX add-on is 32-bit and the game is 64-bit; it needs the game's .addon64 file.";
    public const string Local64BitOn32BitMessage = "This RenoDX add-on is 64-bit and the game is 32-bit; it needs the game's .addon32 file.";
    public const string LocalControlUeMessage = "Control's RenoDX add-on needs RHI's extra steps: install it with the RenoDX row's Install button.";

    /// <summary>Whether the record is a RenoDX installed from a local file (#195).</summary>
    public static bool IsLocalPackage(LinuxDeploymentRecord? record)
        => record?.Metadata.TryGetValue(LocalPackageMetadataKey, out var value) == true && value == "1";

    /// <summary>
    /// #195: whether a dropped or watched file is a RenoDX game add-on (upstream <c>AddonFileWatcher</c>: a
    /// <c>renodx-*</c> name other than <c>renodx-universal_ue</c>, <c>-devkit</c>, <c>-dlssfix</c>, <c>-upgrade</c>,
    /// <c>-mfgunlock</c>, <c>-dlss5</c> and <c>-dlss.</c>; ending in <c>.addon64</c>, <c>.addon32</c> or, as upstream's drop,
    /// <c>.addon</c>) rather than another ReShade add-on or an auxiliary one other components own.
    /// </summary>
    public static bool IsDroppedRenoDxAddon(string? path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || !name.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = Path.GetExtension(name);
        return (extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) || extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".addon", StringComparison.OrdinalIgnoreCase))
               && !IsAuxiliaryRenoDxAddon(name)
               && !UpstreamWatcherSkips.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>renodx-</c> names upstream's watcher leaves alone (global or other components' add-ons).</summary>
    private static readonly string[] UpstreamWatcherSkips =
        ["renodx-universal_ue", "renodx-devkit", "renodx-dlssfix", "renodx-upgrade", "renodx-mfgunlock", "renodx-dlss5", "renodx-dlss."];

    /// <summary>
    /// #195 (upstream <c>DragDropHandler.ProcessDroppedAddon</c>): installs a local RenoDX add-on file as the game's RenoDX
    /// mod, recorded like a catalogue install (removal and Repair after move know it; <c>Version</c> = the file name; Update
    /// All reports it as "could not be checked"). The same refusals as an install from the catalogue (native Linux game,
    /// no ReShade, unknown bitness, a RenoDX add-on or a same-named file RHI does not own, a managed file the user changed),
    /// plus a 32-bit add-on for a 64-bit game (and the reverse) and Control's add-on (its extra steps run from the row).
    /// Only the add-on file is written, and on an Unreal game RenoDX's Engine.ini key (#217, as upstream's drop; no INI or
    /// template step): the source is copied verified and the RenoDX add-on RHI
    /// installed before (another name) removed in one journaled operation (#205) whose commit point is the saved record;
    /// any failure before it, or a crash, puts both back. The source file is never changed.
    /// </summary>
    public async Task<ComponentOperationResult> InstallLocalAddonAsync(DetectedGame game, string addonPath, CancellationToken cancellationToken = default)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("RenoDX add-ons require the Windows ReShade add-on API and are not deployed to native Linux games.");
        var fileName = Path.GetFileName(addonPath);
        if (!IsDroppedRenoDxAddon(addonPath) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !File.Exists(addonPath)
            || new FileInfo(addonPath).Length is 0 or > TrustedDownloadPolicy.AddonMaxBytes)
            return ComponentOperationResult.Fail(LocalNotAnAddonPrefix + fileName);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("O diretório de binários do jogo ainda não é conhecido.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");
        if (!HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install ReShade with full add-on support first. RenoDX is a ReShade add-on.");
        var architecture = GameArchitecture.Of(game);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("Could not determine game bitness for the RenoDX add-on.");
        // The add-on's own PE header decides its bitness; a file without a readable x86/x64 header (a saved web page,
        // a cut-off download) is not an add-on.
        var addonArchitecture = PeArchitectureDetector.Detect(addonPath);
        if (addonArchitecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail(LocalNotAnAddonPrefix + fileName);
        if (addonArchitecture != architecture)
            return ComponentOperationResult.Fail(addonArchitecture == PeArchitecture.X86 ? Local32BitOn64BitMessage : Local64BitOn32BitMessage);
        if (ControlUePostInstallService.Applies(game, fileName)) return ComponentOperationResult.Fail(LocalControlUeMessage);

        var deployDir = ResolveAddonDeployPath(game.BinaryDirectory);
        var target = Path.Combine(deployDir, fileName);
        var existingRecord = _records.Find(game, ManagedComponentKind.RenoDX);
        // The record is the commit point: refuse before the copy when it cannot be saved (an unreadable ledger) — and before
        // the foreign-file scan, which would take RHI's own add-on (its record unreadable) for someone else's (re-review 7).
        if (_records.CorruptLedgerPath is { } quarantined)
            return ComponentOperationResult.Fail(LocalLedgerUnreadablePrefix + quarantined);
        var managedOldPath = existingRecord?.Files.FirstOrDefault()?.Path;
        if (Directory.Exists(deployDir))
        {
            foreach (var existing in Directory.EnumerateFiles(deployDir, "renodx*.addon*", CaseInsensitiveTopLevel))
            {
                var existingName = Path.GetFileName(existing);
                if (IsAuxiliaryRenoDxAddon(existingName)) continue;
                if (managedOldPath is not null && PathsEqual(existing, managedOldPath)) continue;
                // A same-named file RHI does not own is caught here too: every accepted name is a renodx-* add-on.
                return ComponentOperationResult.Fail($"Foreign RenoDX protection: another RenoDX add-on already exists at {existing}. RHI Linux will not overwrite or stack it automatically.");
            }
        }
        if (managedOldPath is not null && File.Exists(managedOldPath))
        {
            var owned = existingRecord!.Files.First();
            if (string.IsNullOrWhiteSpace(owned.Sha256)
                || !string.Equals(await FileHash.Sha256HexAsync(managedOldPath, cancellationToken).ConfigureAwait(false), owned.Sha256, StringComparison.OrdinalIgnoreCase))
                return ComponentOperationResult.Fail($"Managed RenoDX file was modified outside RHI and will not be replaced: {managedOldPath}");
        }


        var sha = await FileHash.Sha256HexAsync(addonPath, cancellationToken).ConfigureAwait(false);
        // #217: a dropped add-on gets RenoDX's Engine.ini key on an Unreal game too (upstream DragDropHandler.ProcessDroppedAddon).
        var engineKeysOn = EngineIniKeysFor(game, existingRecord, hdr: false); // UE-Extended's HDR keys stay as they are
        var engineKeysState = RenoDxEngineIni.Read(existingRecord);
        var engineKeysIni = engineKeysOn.Count > 0 ? RenoDxEngineIni.ResolvePath(game) : null;
        var engineKeysSkipped = EngineKeysRefusal(ref engineKeysIni, engineKeysState); // review 2: optional, never fails RenoDX
        // #205: one journaled operation on RenoDX's record; the stamped record is the commit point.
        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(
            _journal, "RenoDX local add-on: " + game.Name, game, ManagedComponentKind.RenoDX, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var inis = new RenoDxIniTracking(scope);
        try
        {
            // Checks are done: from the copy on, Cancel is refused.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await TrackEngineKeysAsync(inis, engineKeysIni, engineKeysState, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(deployDir);
            await scope.PlaceCopyAsync(addonPath, target, cancellationToken, sha).ConfigureAwait(false);
            if (engineKeysIni is not null)
            {
                engineKeysState = RenoDxEngineIni.ApplyOptional(engineKeysIni, engineKeysOn, NoGroups, engineKeysState, out var keysFailure);
                engineKeysSkipped ??= keysFailure;
            }
            await inis.DeclareWrittenAsync(cancellationToken).ConfigureAwait(false);
            // RHI's earlier add-on under another name goes before the commit point, so a rollback brings it back.
            if (managedOldPath is not null && !PathsEqual(managedOldPath, target) && File.Exists(managedOldPath))
                await scope.DeleteAsync(managedOldPath, cancellationToken).ConfigureAwait(false);
            var record = existingRecord is null ? new LinuxDeploymentRecord() : LinuxDeploymentRecordStore.Clone(existingRecord);
            record.GameKey = LinuxDeploymentRecordStore.GameKey(game);
            record.GameName = game.Name;
            record.Component = ManagedComponentKind.RenoDX;
            record.BinaryDirectory = game.BinaryDirectory;
            record.SourceUrl = null;
            record.Version = fileName;
            record.InstalledAtUtc = DateTime.UtcNow;
            record.Files = [new LinuxDeployedFile { Path = target, ExistedBeforeInstall = false, Sha256 = sha }];
            // The INI / Engine.ini / Control UE / peak-brightness ownership of an earlier install stays, so removal still
            // puts those values back; what described the downloaded file goes.
            record.Metadata.Remove(RemoteStampMetadataKey);
            record.Metadata.Remove(UeExtendedMetadataKey);
            VerifiedDownload.Forget(record.Metadata);
            record.Metadata[LocalPackageMetadataKey] = "1";
            record.Metadata[AddonVersionMetadataKey] = ReadAddonVersion(target);
            RenoDxEngineIni.Write(record, engineKeysState);
            _records.Upsert(scope.Stamp(record));
        }
        catch (Exception failure) // any failure up to the saved record (the ledger refusing the save too) puts everything back
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX" + EngineKeysNote(engineKeysSkipped), false, target); // #204
            if (rollback.RolledBack) inis.RestoreModesAfterRollback();
            return ComponentOperationResult.Fail("RenoDX install failed: " + failure.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(LocalInstalledPrefix + target + EngineKeysNote(engineKeysSkipped), target);
    }

    private async Task<bool> HasSnapshotOverrideAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (_upstreamManifest is null) return false;
        try
        {
            var gameOverride = await _upstreamManifest.ResolveGameOverrideAsync(game, cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(gameOverride?.SnapshotUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TimeoutException or System.Text.Json.JsonException)
        {
            return false; // manifest unreachable: the unlink stands
        }
    }

    // Upstream 2.7.9+ (InstallModAsync / UpdateOrchestrationService): for a Unity-engine game, the
    // RHI Unity database's per-game [renodx] keys are written on install and update. Upstream keys
    // this on EngineHint, which it sets from UnityPlayer.dll; Linux checks both.
    private async Task<IReadOnlyList<KeyValuePair<string, string>>> ResolveUnityDatabaseUpgradesAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        if (_unityDatabase is null) return [];
        var isUnity = game.EngineHint?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true
                      || RenoDxCatalogService.LooksLikeUnity(indexedFiles);
        if (!isUnity) return [];
        try
        {
            var database = await _unityDatabase.GetAsync(cancellationToken).ConfigureAwait(false);
            return database?.Find(game.Name)?.ParsedUpgrades() ?? [];
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Enrichment only, as upstream: an unreadable database must not block the add-on install.
            return [];
        }
    }

    // Upstream writes the Unity database keys first and the manifest renodxIniOverrides after, so
    // the manifest wins on a shared key.
    internal static IReadOnlyDictionary<string, string> MergeIniOverrides(
        IReadOnlyList<KeyValuePair<string, string>> unityUpgrades,
        IReadOnlyDictionary<string, string> manifestOverrides)
    {
        if (unityUpgrades.Count == 0) return manifestOverrides;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in unityUpgrades) merged[key] = value;
        foreach (var (key, value) in manifestOverrides) merged[key] = value;
        return merged;
    }

    public async Task<RenoDxCompatibilitySnapshot> GetCompatibilitySettingsAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), "O diretório de binários do jogo ainda não é conhecido.");

        var iniPath = RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory);
        if (iniPath is null || !File.Exists(iniPath))
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), "Nenhum ReShade.ini foi encontrado no diretório de binários do jogo.");

        var section = RenoDxIniOverrideManager.ReadRenoDxSection(game.BinaryDirectory);
        if (section is null)
            return new(iniPath, false, Array.Empty<RenoDxCompatibilitySetting>(), "Execute o jogo uma vez com o RenoDX instalado para gerar a seção de configurações [renodx].");

        var rows = new List<RenoDxCompatibilitySetting>();
        foreach (var pair in section
                     .Where(x => IsBaseCompatibilityKey(x.Key))
                     .OrderBy(x => x.Key.Equals("DumpLUTShaders", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new RenoDxCompatibilitySetting(
                pair.Key,
                CompatibilityLabel(pair.Key),
                pair.Value,
                BaseCompatibilityOptions(pair.Key),
                false));
        }

        string? warning = null;
        if (rows.Count > 0 && _upstreamManifest is not null)
        {
            try
            {
                var extras = await _upstreamManifest.FetchRenoDxExtraSettingsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var setting in extras)
                {
                    var currentValue = section.TryGetValue(setting.Key, out var existing)
                        ? existing
                        : setting.DefaultValue;
                    rows.Add(new RenoDxCompatibilitySetting(
                        setting.Key,
                        CompatibilityLabel(setting.Key, setting.Label),
                        currentValue,
                        setting.Options.Select(x => new RenoDxCompatibilityOption(x.Value, FriendlyCompatibilityOptionName(x.Name))).ToArray(),
                        true));
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                warning = "O manifesto do RHI não pôde ser carregado; as configurações internas de compatibilidade do RenoDX continuam disponíveis.";
            }
        }

        return new(iniPath, true, rows, warning);
    }

    public ComponentOperationResult SetCompatibilitySetting(DetectedGame game, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("O diretório de binários do jogo ainda não é conhecido.");
        if (string.IsNullOrWhiteSpace(key))
            return ComponentOperationResult.Fail("A chave da configuração de compatibilidade do RenoDX está vazia.");
        var normalizedKey = key.Trim();
        var normalizedValue = value?.Trim() ?? "";
        if (!RenoDxIniGrammar.IsSafePair(normalizedKey, normalizedValue))
            return ComponentOperationResult.Fail($"RenoDX setting refused: not a plain INI key and value: {key}");

        try
        {
            if (!RenoDxIniOverrideManager.SetUserValue(game.BinaryDirectory, normalizedKey, normalizedValue, out var iniPath, game.InstallPath))
                return ComponentOperationResult.Fail("O ReShade.ini ainda não contém uma seção [renodx]. Execute o jogo uma vez com o RenoDX instalado.");
            if (string.IsNullOrWhiteSpace(iniPath))
                return ComponentOperationResult.Fail("O caminho do ReShade.ini não pôde ser resolvido após salvar a configuração do RenoDX.");
            return ComponentOperationResult.Ok($"Configuração do RenoDX salva: {normalizedKey}={normalizedValue} → {iniPath}", iniPath);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Não foi possível salvar a configuração de compatibilidade do RenoDX: {ex.Message}");
        }
    }

    private static bool IsBaseCompatibilityKey(string key)
        => (key.StartsWith("Upgrade_", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("Upgrade_UseSCRGB", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("Upgrade_CopyDestinations", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("Upgrade_SwapChainCompatibility", StringComparison.OrdinalIgnoreCase))
           || key.Equals("Set_Path", StringComparison.OrdinalIgnoreCase)
           || key.Equals("DumpLUTShaders", StringComparison.OrdinalIgnoreCase);

    private static string CompatibilityLabel(string key, string? fallback = null)
    {
        if (key.Equals("Upgrade_R10G10B10A2_TYPELESS", StringComparison.OrdinalIgnoreCase))
            return "RGB 10-bit + Alpha — Genérico";
        if (key.Equals("Upgrade_R10G10B10A2_UNORM", StringComparison.OrdinalIgnoreCase))
            return "RGB 10-bit + Alpha — Normalizado";
        if (key.Equals("Upgrade_R11G11B10_FLOAT", StringComparison.OrdinalIgnoreCase))
            return "RGB Float 11/11/10 — Alta faixa dinâmica";
        if (key.Equals("Upgrade_R16G16B16A16_TYPELESS", StringComparison.OrdinalIgnoreCase))
            return "RGBA 16-bit — Alta precisão";
        if (key.Equals("Upgrade_R8G8B8A8_TYPELESS", StringComparison.OrdinalIgnoreCase))
            return "RGBA 8-bit — Genérico";
        if (key.Equals("Upgrade_R8G8B8A8_UNORM", StringComparison.OrdinalIgnoreCase))
            return "RGBA 8-bit — Linear";
        if (key.Equals("Upgrade_R8G8B8A8_UNORM_SRGB", StringComparison.OrdinalIgnoreCase))
            return "RGBA 8-bit — sRGB";
        if (key.Equals("Upgrade_CopyDestinations", StringComparison.OrdinalIgnoreCase))
            return "Atualizar destinos de cópia";
        if (key.Equals("Upgrade_UseSCRGB", StringComparison.OrdinalIgnoreCase))
            return "Usar scRGB";
        if (key.Equals("Upgrade_SwapChainCompatibility", StringComparison.OrdinalIgnoreCase))
            return "Compatibilidade da swapchain";
        if (key.Equals("Set_Path", StringComparison.OrdinalIgnoreCase))
            return "Caminho de upgrade";
        if (key.Equals("DumpLUTShaders", StringComparison.OrdinalIgnoreCase))
            return "Exportar shaders LUT";
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;
        if (key.StartsWith("Upgrade_", StringComparison.OrdinalIgnoreCase))
            return key["Upgrade_".Length..].Replace('_', ' ');
        return key;
    }

    private static string FriendlyCompatibilityOptionName(string name)
    {
        if (name.Equals("Off", StringComparison.OrdinalIgnoreCase)) return "Desativado";
        if (name.Equals("On", StringComparison.OrdinalIgnoreCase)) return "Ativado";
        if (name.Equals("Output size", StringComparison.OrdinalIgnoreCase)) return "Tamanho da saída";
        if (name.Equals("Output ratio", StringComparison.OrdinalIgnoreCase)) return "Proporção da saída";
        if (name.Equals("Any size", StringComparison.OrdinalIgnoreCase)) return "Qualquer tamanho";
        if (name.Equals("All copies", StringComparison.OrdinalIgnoreCase)) return "Todas as cópias";
        return name;
    }

    private static IReadOnlyList<RenoDxCompatibilityOption> BaseCompatibilityOptions(string key)
    {
        if (key.Equals("Set_Path", StringComparison.OrdinalIgnoreCase))
            return [new("0", "HDR — Desativado"), new("1", "SDR — Ativado")];
        if (key.Equals("DumpLUTShaders", StringComparison.OrdinalIgnoreCase))
            return [new("0", "Desativado"), new("1", "Ativado")];
        return
        [
            new("0", "Desativado"),
            new("1", "Tamanho da saída"),
            new("2", "Proporção da saída"),
            new("3", "Qualquer tamanho")
        ];
    }

    /// <summary>Fixed text of a removal stopped by a Control UE file that could not be put back (PT-BR pair); the reason follows.</summary>
    public const string ControlUeRestoreIncompletePrefix = "Control UE restore incomplete; RenoDX was kept: ";

    /// <summary>
    /// Removes RHI's RenoDX: the Control UE files go back first, then RHI's [renodx], peak-brightness and Engine.ini keys
    /// come out (a value the user changed since stays), then the add-on. One journaled operation (#205) whose commit point is
    /// the record leaving the ledger: any failure, or a crash, puts every file back and keeps the record, so a retry can
    /// finish the job; a Control UE file that cannot be put back keeps RenoDX installed as a whole.
    /// </summary>
    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.RenoDX);
        if (record is null)
            return ComponentOperationResult.Fail("RenoDX was detected, but it is not managed by RHI Linux. Removal is locked to protect the external installation.");
        try
        {
            // Fail closed before any INI/file teardown if a managed binary changed outside RHI.
            foreach (var file in record.Files)
            {
                if (!File.Exists(file.Path)) continue;
                var current = await FileHash.Sha256HexAsync(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
            }
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RenoDX removal failed: {ex.Message}");
        }

        var (opened, journalRefusal) = await DeploymentScope.BeginRemovalAsync(
            _journal, _records, "RenoDX removal: " + game.Name, game, ManagedComponentKind.RenoDX, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        var inis = new RenoDxIniTracking(scope);
        // Cancel is honoured only up to here: the removal never stops half-way.
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var controlSummary = "";
        var inControlStep = false;
        try
        {
            // Control UE DLLs and renderer.ini go back first. If any of them cannot, the whole removal is undone: RenoDX
            // stays installed and its record keeps owning everything, so a retry can finish the job.
            if (ControlUePostInstallService.Deserialize(record) is { } controlState)
            {
                inControlStep = true;
                var controlPaths = await ControlUePostInstallService.TrackAsync(
                    scope, game, game.BinaryDirectory ?? record.BinaryDirectory, controlState, null, cancellationToken).ConfigureAwait(false);
                var restored = await _controlUe.RestoreAsync(controlState, InsideOperation).ConfigureAwait(false);
                if (restored.Remaining is not null) throw new IOException(string.Join(" ", restored.Notes));
                await ControlUePostInstallService.DeclareWrittenAsync(scope, controlPaths, cancellationToken).ConfigureAwait(false);
                controlSummary = $" Control UE: {string.Join(" ", restored.Notes)}";
                inControlStep = false;
            }

            var iniState = record.Metadata.TryGetValue(RenoDxIniOverrideManager.MetadataKey, out var iniJson)
                ? RenoDxIniOverrideManager.Deserialize(iniJson)
                : null;
            // Control UE frame generation's FgSlHdr10 in [renodx-preset1] (#136): owned the same way, put back the same way.
            var fgHdrState = record.Metadata.TryGetValue(ControlUeFrameGenerationService.HdrMetadataKey, out var fgHdrJson)
                ? RenoDxIniOverrideManager.Deserialize(fgHdrJson)
                : null;
            var peakStates = RenoDxPeakNits.Read(record); // #182: peak brightness owned per preset section
            var engineIniState = record.Metadata.TryGetValue(UpstreamEngineIniFileManager.MetadataKey, out var engineIniJson)
                ? UpstreamEngineIniFileManager.Deserialize(engineIniJson)
                : null;
            // Every INI the clean-up edits, at the file a write through it replaces (a shared ReShade.ini, not its link).
            var nativeState = RenoDxIniOverrideManager.Deserialize(record.Metadata.GetValueOrDefault(RenoDxEngineIni.NativeHdrMetadataKey)); // #218
            foreach (var ini in new[] { iniState?.IniPath, fgHdrState?.IniPath, nativeState?.IniPath }.Concat(peakStates.Values.Select(state => state.IniPath))
                         .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct(StringComparer.Ordinal))
                await inis.TrackReShadeIniAsync(ini!, Path.GetDirectoryName(ini!) ?? ini!, game.InstallPath, cancellationToken).ConfigureAwait(false);
            if (engineIniState?.IniPath is { Length: > 0 } engineIni && File.Exists(engineIni))
                await inis.TrackEngineIniAsync(engineIni, cancellationToken).ConfigureAwait(false);
            var engineKeysState = RenoDxEngineIni.Read(record); // #217
            if (engineKeysState?.IniPath is { Length: > 0 } keysIni && File.Exists(keysIni))
                await inis.TrackEngineIniAsync(keysIni, cancellationToken).ConfigureAwait(false);

            RenoDxIniOverrideManager.Cleanup(iniState, gameRoot: game.InstallPath);
            RenoDxIniOverrideManager.Cleanup(nativeState, gameRoot: game.InstallPath); // #218: after the manifest's values, as installed before them
            RenoDxIniOverrideManager.Cleanup(fgHdrState, gameRoot: game.InstallPath);
            RenoDxPeakNits.Cleanup(peakStates, game.InstallPath); // #182
            UpstreamEngineIniFileManager.Cleanup(engineIniState);
            RenoDxEngineIni.Cleanup(engineKeysState); // #217: only RHI's keys come out, the earlier lock state goes back
            await inis.DeclareWrittenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var file in record.Files)
                if (File.Exists(file.Path)) await scope.DeleteAsync(file.Path, cancellationToken).ConfigureAwait(false);

            _records.Remove(game, ManagedComponentKind.RenoDX);
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackRemovalAsync(_records, game, record, "renodx").ConfigureAwait(false);
            // #204: the record's removal landed and then the save reported an error: the removal stands (Control UE included).
            if (rollback.Committed) return DeploymentScope.Landed("RenoDX", removal: true);
            if (rollback.RolledBack) inis.RestoreModesAfterRollback();
            return ComponentOperationResult.Fail(
                $"{(inControlStep ? ControlUeRestoreIncompletePrefix : "RenoDX removal failed: ")}{ex.Message}{DeploymentScope.RollbackNote(rollback)}");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok("RHI-managed RenoDX add-on removed and manifest ReShade/Engine.ini ownership restored." + controlSummary);
    }

    public static string ResolveAddonDeployPath(string binaryDirectory)
    {
        try
        {
            var ini = RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory);
            if (ini is null || !File.Exists(ini)) return binaryDirectory;
            var inAddon = false;
            foreach (var raw in File.ReadLines(ini))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inAddon = line.Equals("[ADDON]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inAddon || !line.StartsWith("AddonPath", StringComparison.OrdinalIgnoreCase) || !line.Contains('=')) continue;
                var value = line[(line.IndexOf('=') + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(value)) return binaryDirectory;
                var first = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(first)) return binaryDirectory;
                var normalized = first.Replace('\\', Path.DirectorySeparatorChar);
                return Path.IsPathRooted(normalized)
                    ? normalized
                    : Path.GetFullPath(Path.Combine(binaryDirectory, normalized));
            }
        }
        catch { }
        return binaryDirectory;
    }


    private static readonly EnumerationOptions CaseInsensitiveTopLevel = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);

    private static bool IsAuxiliaryRenoDxAddon(string fileName)
        => fileName.Contains("dlss5", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("mfgunlock", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("mfg_unlock", StringComparison.OrdinalIgnoreCase);

    // RenoDxRemoteStamp (locals `remote`/`cachedRemote`) = the HTTP validators the file was served with;
    // CacheStamp = provenance proving the cached bytes are the ones RHI downloaded (#142).
    internal async Task<(string Path, RenoDxRemoteStamp? Stamp)> DownloadAsync(string url, string fileName, CancellationToken cancellationToken)
    {
        DownloadPolicy.Validate(url);
        Directory.CreateDirectory(_cacheDir);
        var cache = Path.Combine(_cacheDir, fileName);
        var source = cache + ".source";
        var (current, cachedRemote) = await IsCacheCurrentAsync(url, cache, source, cancellationToken).ConfigureAwait(false);
        if (current)
            return (cache, cachedRemote);
        CacheStamp.Invalidate(cache);
        RenoDxRemoteStamp? remote = null;
        // HTTPS + allow-list on the request and after redirects, size cap, fsynced verified temp (TrustedDownload).
        using (var download = await TrustedDownload.DownloadToTempAsync(
                   _http, url, cache, DownloadPolicy, cancellationToken: cancellationToken,
                   onResponse: response => remote = RenoDxRemoteStamp.FromResponse(response)).ConfigureAwait(false))
        {
            if (!BinarySignatureInspector.HasPeSignature(download.Path, 100 * 1024))
                throw new InvalidOperationException("Downloaded RenoDX file is not a valid PE add-on (possible HTML/error response).");
            // The snapshot's own time, so a version-less add-on keeps one version across re-downloads.
            if (remote!.LastModified is { } lastModified)
                File.SetLastWriteTimeUtc(download.Path, lastModified.UtcDateTime);
            AtomicFile.Delete(source);
            download.MoveTo(cache);
        }
        AtomicFile.WriteAllText(source, RenoDxRemoteStamp.SerializeSidecar(url, remote));
        // Written last: the stamp hashes the bytes now in place (it does not use mtime).
        CacheStamp.Write(cache, url);
        return (cache, remote.HasValidator ? remote : null);
    }

    // Upstream 2.7.6-2.7.9 reuses the cached add-on unless the remote size differs; a failed HEAD
    // keeps the cache. Linux (#107): the .source sidecar ties the file to its URL and to the ETag/
    // Last-Modified it was served with, so a same-size rebuild is fetched again. When the server
    // sends validators the cache must match them; an old URL-only sidecar is therefore refreshed.
    private async Task<(bool Current, RenoDxRemoteStamp? Stamp)> IsCacheCurrentAsync(string url, string cache, string source, CancellationToken cancellationToken)
    {
        if (!File.Exists(cache) || !File.Exists(source)) return (false, null);
        var (cachedUrl, cachedRemote) = RenoDxRemoteStamp.ReadSidecar(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(cachedUrl, url, StringComparison.Ordinal)) return (false, null);
        // The .source sidecar names the URL and HTTP validators; the stamp proves the bytes are the ones RHI downloaded (#142).
        if (!CacheStamp.IsValid(cache, url)) return (false, null);
        if (!BinarySignatureInspector.HasPeSignature(cache, 100 * 1024)) return (false, null);
        if (cachedRemote?.Length is > 0 && cachedRemote.Length != new FileInfo(cache).Length) return (false, null);

        var (remote, _) = await HeadAsync(url, cancellationToken).ConfigureAwait(false);
        if (remote is null || !remote.HasValidator) return (true, cachedRemote);
        return RenoDxRemoteStamp.Compare(cachedRemote, remote) == RemoteStampComparison.Same
            ? (true, remote)
            : (false, null);
    }

    // Null stamp = the remote could not be asked; the error text says why.
    private async Task<(RenoDxRemoteStamp? Stamp, string? Error)> HeadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await TrustedDownload.HeadAsync(_http, url, DownloadPolicy, headCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (RenoDxRemoteStamp.FromResponse(response), null)
                : (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Only network failures and the 10 s timeout mean "could not ask" (#108); anything else is a fault and propagates.
        catch (OperationCanceledException) { return (null, "the request timed out"); }
        catch (HttpRequestException ex) { return (null, ex.Message); }
    }

    // ReShade under any name it can be installed as (#120).
    private static bool HasReShade(string binaryDirectory) => ProxyPayloadLocator.HasReShadeOnDisk(binaryDirectory);

}
