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

    public RenoDxDeploymentService(
        HttpClient http,
        LinuxDeploymentRecordStore records,
        RenoDxCatalogService catalog,
        string? cacheDir = null,
        UpstreamRhiManifestService? upstreamManifest = null,
        RenoDxUnityDatabaseService? unityDatabase = null,
        ControlUePostInstallService? controlUe = null)
    {
        _http = http;
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
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return new ManagedUpdateCheck(true, false, "Game executable is unavailable; RenoDX update check skipped.", record.Version, CouldNotCheck: true);
        var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return new ManagedUpdateCheck(true, false, "Game architecture is unknown; RenoDX update check skipped.", record.Version, CouldNotCheck: true);
        try
        {
            var mod = await _catalog.ResolveAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
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

    public async Task<ComponentOperationResult> UpdateAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
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
        return await InstallAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
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

        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("Could not determine game bitness for the RenoDX add-on.");

        RenoDxModEntry? mod;
        try { mod = await _catalog.ResolveAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false); }
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

        try
        {
            var (cached, remoteStamp) = await DownloadAsync(url, fileName, cancellationToken).ConfigureAwait(false);
            // Control UE (upstream 2.7.9): runs on a fresh install, which the UI confirms first, or
            // on a reinstall/update of an install that already carries the step. Downloads happen
            // here, before any game file changes, so cancelling cannot leave the step half done.
            var previousControlState = ControlUePostInstallService.Deserialize(existingRecord);
            var controlSources = ControlUePostInstallService.Applies(game, fileName) &&
                                 (existingRecord is null || previousControlState is not null)
                ? await _controlUe.PrepareAsync(cancellationToken).ConfigureAwait(false)
                : null;
            LinuxDeployedFile? oldManagedFile = null;
            if (existingRecord is not null && managedOldPath is not null && !Path.GetFullPath(managedOldPath).Equals(Path.GetFullPath(target), StringComparison.Ordinal))
            {
                oldManagedFile = existingRecord.Files.First();
                if (File.Exists(oldManagedFile.Path))
                {
                    var oldHash = await FileHash.Sha256HexAsync(oldManagedFile.Path, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(oldHash, oldManagedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        return ComponentOperationResult.Fail($"Managed RenoDX file was modified outside RHI and will not be replaced: {oldManagedFile.Path}");
                }
            }

            var targetExistedBeforeCopy = File.Exists(target);
            var targetBeforeCopy = targetExistedBeforeCopy
                ? await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)
                : null;
            var iniPathBeforeApply = RenoDxIniOverrideManager.ResolveIniPath(game.BinaryDirectory);
            var iniBeforeApply = iniPathBeforeApply is not null && File.Exists(iniPathBeforeApply)
                ? await File.ReadAllBytesAsync(iniPathBeforeApply, cancellationToken).ConfigureAwait(false)
                : null;
            var previousEngineIniState = existingRecord is not null &&
                existingRecord.Metadata.TryGetValue(UpstreamEngineIniFileManager.MetadataKey, out var previousEngineIniJson)
                    ? UpstreamEngineIniFileManager.Deserialize(previousEngineIniJson)
                    : null;
            var engineIniSnapshotPath = previousEngineIniState?.IniPath
                ?? (!string.IsNullOrWhiteSpace(game.EngineIniFileOverride)
                    ? UpstreamEngineIniFileManager.ResolveTargetPath(game)
                    : null);
            var engineIniBeforeExisted = engineIniSnapshotPath is not null && File.Exists(engineIniSnapshotPath);
            var engineIniBeforeApply = engineIniBeforeExisted
                ? await File.ReadAllBytesAsync(engineIniSnapshotPath!, cancellationToken).ConfigureAwait(false)
                : null;

            // The upstream Engine.ini template is fetched here, with the other downloads, and applied after the gate
            // (w2 merge review, minor 1: it used to be fetched after the add-on was copied).
            var engineIniTemplate = string.IsNullOrWhiteSpace(game.EngineIniFileOverride)
                ? null
                : await _engineIniFiles.FetchAsync(game.EngineIniFileOverride!, cancellationToken).ConfigureAwait(false);

            RenoDxIniOverrideApplyResult iniApply;
            UpstreamEngineIniApplyResult engineIniApply = new(previousEngineIniState, 0, 0, null);
            // Downloads and snapshots are done: from the copy on, Cancel is refused (a late cancel used to leave the
            // add-on and its INI keys in place with no record).
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("RenoDX target directory is invalid."));
                await AtomicFile.CopyAsync(cached, target, cancellationToken: cancellationToken).ConfigureAwait(false);
                var previousIniState = existingRecord is not null &&
                    existingRecord.Metadata.TryGetValue(RenoDxIniOverrideManager.MetadataKey, out var previousIniJson)
                    ? RenoDxIniOverrideManager.Deserialize(previousIniJson)
                    : null;
                iniApply = RenoDxIniOverrideManager.Apply(game.BinaryDirectory, requestedIniOverrides, previousIniState, gameRoot: game.InstallPath);

                if (engineIniTemplate is not null)
                {
                    engineIniApply = UpstreamEngineIniFileManager.Apply(game, engineIniTemplate, previousEngineIniState);
                }
                else if (previousEngineIniState is not null)
                {
                    UpstreamEngineIniFileManager.Cleanup(previousEngineIniState);
                    engineIniApply = new UpstreamEngineIniApplyResult(null, 0, 0, null);
                }
            }
            catch (Exception failure)
            {
                // Put every file back as it was; one that cannot be restored keeps its snapshot and is reported (#121).
                var snapshots = new List<FileSnapshot> { new(target, targetBeforeCopy) };
                if (iniPathBeforeApply is not null && iniBeforeApply is not null)
                    snapshots.Add(new(iniPathBeforeApply, iniBeforeApply, RenoDxIniOverrideManager.LinkPolicy(game.BinaryDirectory, game.InstallPath)));
                if (engineIniSnapshotPath is not null)
                    snapshots.Add(FileSnapshot.EngineIni(engineIniSnapshotPath, engineIniBeforeApply));
                var notes = RestoreSnapshots(snapshots, SnapshotFallbackDirectory);
                if (notes.Count > 0)
                    throw new IOException($"{failure.Message} Rollback incomplete: {string.Join(" ", notes)}", failure);
                throw;
            }

            var newHash = await FileHash.Sha256HexAsync(target, cancellationToken).ConfigureAwait(false);

            // Only remove a previous RHI-managed filename after the replacement and INI application are safely in place.
            if (oldManagedFile is not null && File.Exists(oldManagedFile.Path))
                File.Delete(oldManagedFile.Path);

            // Put the previous Control UE changes back first, then apply afresh, so the first
            // pre-RHI state stays the restore point. A partial restore keeps what is left owned.
            var controlNotes = new List<string>();
            var controlState = previousControlState;
            ControlUePostInstallState? controlApplied = null;
            if (previousControlState is not null)
            {
                var restored = await _controlUe.RestoreAsync(previousControlState).ConfigureAwait(false);
                controlState = restored.Remaining;
                if (restored.Remaining is not null) controlNotes.AddRange(restored.Notes);
            }
            if (controlSources is not null && controlState is null)
            {
                var applied = await _controlUe.ApplyAsync(game, game.BinaryDirectory, controlSources).ConfigureAwait(false);
                controlState = controlApplied = applied.State;
                controlNotes.AddRange(applied.Notes);
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
            try
            {
                _records.Upsert(record);
            }
            catch (Exception saveFailure)
            {
                // Nothing would own the Control UE changes; undo them rather than orphan them, and say so if that fails.
                if (controlApplied is not null)
                {
                    try { await _controlUe.RestoreAsync(controlApplied).ConfigureAwait(false); }
                    catch (Exception restoreFailure)
                    {
                        throw new IOException($"{saveFailure.Message} Control UE changes could not be undone: {restoreFailure.Message}", saveFailure);
                    }
                }
                throw;
            }
            var iniSummary = iniApply.AppliedCount > 0
                ? unityUpgrades.Count > 0
                    ? $" · {iniApply.AppliedCount} [renodx] override(s) applied (RHI Unity database + manifest)"
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
            return ComponentOperationResult.Ok($"RenoDX installed: {mod.Name} → {target}{iniSummary}{engineIniSummary}{controlSummary}", target);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RenoDX install failed: {ex.Message}");
        }
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

            // Cancel is honoured only up to here: the removal never stops half-way.
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);

            // Control UE DLLs and renderer.ini go back first. If any of them cannot, RenoDX stays
            // installed and the record keeps owning what is left, so a retry can finish the job.
            var controlSummary = "";
            if (ControlUePostInstallService.Deserialize(record) is { } controlState)
            {
                var restored = await _controlUe.RestoreAsync(controlState).ConfigureAwait(false);
                if (restored.Remaining is not null)
                {
                    record.Metadata[ControlUePostInstallService.MetadataKey] = ControlUePostInstallService.Serialize(restored.Remaining);
                    _records.Upsert(record);
                    return ComponentOperationResult.Fail($"Control UE restore incomplete; RenoDX was kept: {string.Join(" ", restored.Notes)}");
                }
                record.Metadata.Remove(ControlUePostInstallService.MetadataKey);
                _records.Upsert(record);
                controlSummary = $" Control UE: {string.Join(" ", restored.Notes)}";
            }

            var iniState = record.Metadata.TryGetValue(RenoDxIniOverrideManager.MetadataKey, out var iniJson)
                ? RenoDxIniOverrideManager.Deserialize(iniJson)
                : null;
            var iniSnapshotPath = iniState?.IniPath;
            var iniSnapshot = iniSnapshotPath is not null && File.Exists(iniSnapshotPath)
                ? await File.ReadAllBytesAsync(iniSnapshotPath, cancellationToken).ConfigureAwait(false)
                : null;
            var engineIniState = record.Metadata.TryGetValue(UpstreamEngineIniFileManager.MetadataKey, out var engineIniJson)
                ? UpstreamEngineIniFileManager.Deserialize(engineIniJson)
                : null;
            var engineIniSnapshotPath = engineIniState?.IniPath;
            var engineIniSnapshot = engineIniSnapshotPath is not null && File.Exists(engineIniSnapshotPath)
                ? await File.ReadAllBytesAsync(engineIniSnapshotPath, cancellationToken).ConfigureAwait(false)
                : null;

            try
            {
                RenoDxIniOverrideManager.Cleanup(iniState, gameRoot: game.InstallPath);
                UpstreamEngineIniFileManager.Cleanup(engineIniState);
                foreach (var file in record.Files)
                    if (File.Exists(file.Path)) File.Delete(file.Path);
            }
            catch (Exception failure)
            {
                // Only files that existed are put back: a removal never recreates something that was absent.
                var snapshots = new List<FileSnapshot>();
                if (iniSnapshotPath is not null && iniSnapshot is not null)
                    snapshots.Add(new(iniSnapshotPath, iniSnapshot,
                        RenoDxIniOverrideManager.LinkPolicy(Path.GetDirectoryName(iniSnapshotPath) ?? iniSnapshotPath, game.InstallPath)));
                if (engineIniSnapshotPath is not null && engineIniSnapshot is not null) snapshots.Add(FileSnapshot.EngineIni(engineIniSnapshotPath, engineIniSnapshot));
                var notes = RestoreSnapshots(snapshots, SnapshotFallbackDirectory);
                if (notes.Count > 0)
                    throw new IOException($"{failure.Message} Restore incomplete: {string.Join(" ", notes)}", failure);
                throw;
            }

            _records.Remove(game, ManagedComponentKind.RenoDX);
            return ComponentOperationResult.Ok("RHI-managed RenoDX add-on removed and manifest ReShade/Engine.ini ownership restored." + controlSummary);
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"RenoDX removal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A file's bytes before an operation; null bytes = the file did not exist. <paramref name="LinkPolicy"/> says which
    /// symlink the restore may write through (null: none); <paramref name="ReadOnly"/> whether a read-only file is
    /// refused or written and kept read-only.
    /// </summary>
    internal sealed record FileSnapshot(string Path, byte[]? Bytes, UserFileLinkPolicy? LinkPolicy = null,
        ReadOnlyUserFile ReadOnly = ReadOnlyUserFile.Refuse)
    {
        /// <summary>An Engine.ini snapshot: restored like every Engine.ini write (a read-only file is written and stays locked).</summary>
        internal static FileSnapshot EngineIni(string path, byte[]? bytes)
            => new(path, bytes, LumaEngineIniOverrideManager.LinkPolicy(path), ReadOnlyUserFile.WriteKeepingLock);
    }

    private string SnapshotFallbackDirectory => System.IO.Path.Combine(_cacheDir, "restore-snapshots");

    /// <summary>
    /// Puts each snapshot back atomically (or deletes a file that did not exist). A file that cannot be restored
    /// keeps its previous bytes in a sidecar next to it, else under <paramref name="fallbackDirectory"/>, and the
    /// returned notes say where; nothing is dropped silently. A file that still holds its snapshot bytes is left alone.
    /// Runs to completion whatever the caller's token, and whatever one snapshot throws.
    /// </summary>
    internal static IReadOnlyList<string> RestoreSnapshots(IEnumerable<FileSnapshot> snapshots, string fallbackDirectory)
    {
        var notes = new List<string>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (snapshot.Bytes is not null)
                {
                    if (File.Exists(snapshot.Path) && File.ReadAllBytes(snapshot.Path).AsSpan().SequenceEqual(snapshot.Bytes))
                        continue;
                    var directory = System.IO.Path.GetDirectoryName(snapshot.Path);
                    if (string.IsNullOrEmpty(directory))
                        throw new IOException(AtomicFile.NoFolderPrefix + snapshot.Path);
                    Directory.CreateDirectory(directory);
                    AtomicFile.ReplaceUserFile(snapshot.Path, snapshot.Bytes, snapshot.LinkPolicy, readOnly: snapshot.ReadOnly);
                }
                else if (File.Exists(snapshot.Path))
                {
                    File.Delete(snapshot.Path);
                }
            }
            catch (Exception ex)
            {
                // Rollback must finish: any fault (bad recorded path, refused link, I/O) keeps the bytes and is reported,
                // in the result and in the app log.
                var note = KeepSnapshot(snapshot, ex, fallbackDirectory);
                AppLog.Warn("renodx-rollback", note, ex);
                notes.Add(note);
            }
        }
        return notes;
    }

    private static string KeepSnapshot(FileSnapshot snapshot, Exception failure, string fallbackDirectory)
    {
        if (snapshot.Bytes is null)
            return $"Could not remove {snapshot.Path}: {failure.Message}";
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var candidates = new List<string>();
        if (System.IO.Path.IsPathFullyQualified(snapshot.Path))
            candidates.Add($"{snapshot.Path}.rhi-snapshot-{stamp}");
        candidates.Add(System.IO.Path.Combine(fallbackDirectory, $"{stamp}-{SafeFileName(System.IO.Path.GetFileName(snapshot.Path))}"));
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(candidate)!);
                AtomicFile.ReplaceUserFile(candidate, snapshot.Bytes);
                return $"Could not restore {snapshot.Path} ({failure.Message}); its previous contents were kept at {candidate}";
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        return $"Could not restore {snapshot.Path} ({failure.Message}) nor keep a copy of its previous contents ({string.Join("; ", errors)})";
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "snapshot" : safe;
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

    private static bool HasReShade(string binaryDirectory)
    {
        foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll", "dxgi.dll", "d3d9.dll", "d3d11.dll", "opengl32.dll" })
        {
            var path = Path.Combine(binaryDirectory, name);
            if (File.Exists(path) && BinarySignatureInspector.LooksLikeReShade(path)) return true;
        }
        return false;
    }

}
