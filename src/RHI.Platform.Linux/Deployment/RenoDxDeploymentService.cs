using System.Security.Cryptography;
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
            return new ManagedUpdateCheck(true, false, "Game executable is unavailable; RenoDX update check skipped.", record.Version);
        var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return new ManagedUpdateCheck(true, false, "Game architecture is unknown; RenoDX update check skipped.", record.Version);
        try
        {
            var mod = await _catalog.ResolveAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
            if (mod is null) return new ManagedUpdateCheck(true, false, "No current RenoDX catalog match was found; installed payload was preserved.", record.Version);
            var latestUrl = mod.UrlFor(architecture);
            if (string.IsNullOrWhiteSpace(latestUrl))
                return new ManagedUpdateCheck(true, false, "Current RenoDX catalog match has no compatible payload for this architecture.", record.Version);
            var update = !string.Equals(record.SourceUrl, latestUrl, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(record.Version, mod.Name, StringComparison.OrdinalIgnoreCase);
            return new ManagedUpdateCheck(true, update,
                update ? $"RenoDX catalog update available: {record.Version ?? "unknown"} → {mod.Name}." : $"RenoDX catalog entry {mod.Name} is current.",
                record.Version, mod.Name);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedUpdateCheck(true, false, $"RenoDX update check failed: {ex.Message}", record.Version);
        }
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
            var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
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
        if (game.WikiUnlinkedByManifest)
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
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".addon64", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".addon32", StringComparison.OrdinalIgnoreCase))
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

        foreach (var existing in Directory.EnumerateFiles(deployDir, "renodx*.addon*", SearchOption.TopDirectoryOnly))
        {
            var existingName = Path.GetFileName(existing);
            if (IsAuxiliaryRenoDxAddon(existingName)) continue;
            if (managedOldPath is not null && Path.GetFullPath(existing) == Path.GetFullPath(managedOldPath)) continue;
            if (Path.GetFullPath(existing) == Path.GetFullPath(target) && existingRecord is not null) continue;
            return ComponentOperationResult.Fail($"Foreign RenoDX protection: another RenoDX add-on already exists at {existing}. RHI Linux will not overwrite or stack it automatically.");
        }

        try
        {
            var cached = await DownloadAsync(url, fileName, cancellationToken).ConfigureAwait(false);
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
                    var oldHash = await Sha256Async(oldManagedFile.Path, cancellationToken).ConfigureAwait(false);
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

            RenoDxIniOverrideApplyResult iniApply;
            UpstreamEngineIniApplyResult engineIniApply = new(previousEngineIniState, 0, 0, null);
            try
            {
                await CopyAtomicAsync(cached, target, cancellationToken).ConfigureAwait(false);
                var previousIniState = existingRecord is not null &&
                    existingRecord.Metadata.TryGetValue(RenoDxIniOverrideManager.MetadataKey, out var previousIniJson)
                    ? RenoDxIniOverrideManager.Deserialize(previousIniJson)
                    : null;
                iniApply = RenoDxIniOverrideManager.Apply(game.BinaryDirectory, requestedIniOverrides, previousIniState);

                if (!string.IsNullOrWhiteSpace(game.EngineIniFileOverride))
                {
                    engineIniApply = await _engineIniFiles.ApplyAsync(
                        game, game.EngineIniFileOverride!, previousEngineIniState, cancellationToken).ConfigureAwait(false);
                }
                else if (previousEngineIniState is not null)
                {
                    UpstreamEngineIniFileManager.Cleanup(previousEngineIniState);
                    engineIniApply = new UpstreamEngineIniApplyResult(null, 0, 0, null);
                }
            }
            catch
            {
                try
                {
                    if (targetExistedBeforeCopy && targetBeforeCopy is not null)
                        await File.WriteAllBytesAsync(target, targetBeforeCopy, CancellationToken.None).ConfigureAwait(false);
                    else if (!targetExistedBeforeCopy && File.Exists(target))
                        File.Delete(target);
                    if (iniPathBeforeApply is not null && iniBeforeApply is not null)
                        await File.WriteAllBytesAsync(iniPathBeforeApply, iniBeforeApply, CancellationToken.None).ConfigureAwait(false);
                    if (engineIniSnapshotPath is not null)
                    {
                        if (engineIniBeforeExisted && engineIniBeforeApply is not null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(engineIniSnapshotPath)!);
                            await File.WriteAllBytesAsync(engineIniSnapshotPath, engineIniBeforeApply, CancellationToken.None).ConfigureAwait(false);
                        }
                        else if (File.Exists(engineIniSnapshotPath))
                        {
                            File.Delete(engineIniSnapshotPath);
                        }
                    }
                }
                catch { }
                throw;
            }

            var newHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);

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
            catch
            {
                // Nothing would own the Control UE changes; undo them rather than orphan them.
                if (controlApplied is not null)
                    try { await _controlUe.RestoreAsync(controlApplied).ConfigureAwait(false); } catch { }
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

        try
        {
            if (!RenoDxIniOverrideManager.SetUserValue(game.BinaryDirectory, key, value, out var iniPath))
                return ComponentOperationResult.Fail("O ReShade.ini ainda não contém uma seção [renodx]. Execute o jogo uma vez com o RenoDX instalado.");
            if (string.IsNullOrWhiteSpace(iniPath))
                return ComponentOperationResult.Fail("O caminho do ReShade.ini não pôde ser resolvido após salvar a configuração do RenoDX.");
            return ComponentOperationResult.Ok($"Configuração do RenoDX salva: {key}={value} → {iniPath}", iniPath);
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
                var current = await Sha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return ComponentOperationResult.Fail($"Protected modified file: {file.Path} no longer matches the RHI-managed hash, so it was not deleted.");
            }

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
                RenoDxIniOverrideManager.Cleanup(iniState);
                UpstreamEngineIniFileManager.Cleanup(engineIniState);
                foreach (var file in record.Files)
                    if (File.Exists(file.Path)) File.Delete(file.Path);
            }
            catch
            {
                try
                {
                    if (iniSnapshotPath is not null && iniSnapshot is not null)
                        await File.WriteAllBytesAsync(iniSnapshotPath, iniSnapshot, CancellationToken.None).ConfigureAwait(false);
                    if (engineIniSnapshotPath is not null && engineIniSnapshot is not null)
                        await File.WriteAllBytesAsync(engineIniSnapshotPath, engineIniSnapshot, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
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


    private static async Task CopyAtomicAsync(string source, string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("RenoDX target directory is invalid.");
        Directory.CreateDirectory(directory);
        var temp = target + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            const int bufferSize = 1024 * 1024;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, bufferSize, cancellationToken).ConfigureAwait(false);

            if (new FileInfo(source).Length != new FileInfo(temp).Length)
                throw new IOException("RenoDX copy verification failed.");

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static bool IsAuxiliaryRenoDxAddon(string fileName)
        => fileName.Contains("dlss5", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("mfgunlock", StringComparison.OrdinalIgnoreCase) ||
           fileName.Contains("mfg_unlock", StringComparison.OrdinalIgnoreCase);

    internal async Task<string> DownloadAsync(string url, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);
        var cache = Path.Combine(_cacheDir, fileName);
        var source = cache + ".source";
        if (await IsCacheCurrentAsync(url, cache, source, cancellationToken).ConfigureAwait(false))
            return cache;
        CacheStamp.Invalidate(cache);
        var temp = cache + $".tmp-{Guid.NewGuid():N}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (!BinarySignatureInspector.HasPeSignature(temp, 100 * 1024))
        {
            File.Delete(temp);
            throw new InvalidOperationException("Downloaded RenoDX file is not a valid PE add-on (possible HTML/error response).");
        }
        if (File.Exists(source)) File.Delete(source);
        File.Move(temp, cache, overwrite: true);
        await File.WriteAllTextAsync(source, url, CancellationToken.None).ConfigureAwait(false);
        CacheStamp.Write(cache, url);
        return cache;
    }

    // Upstream 2.7.6-2.7.9: reuse the cached add-on unless the remote size explicitly differs; a failed
    // HEAD or missing Content-Length keeps the cache. The .source sidecar stops a same-named add-on
    // from another repository being served from cache.
    private async Task<bool> IsCacheCurrentAsync(string url, string cache, string source, CancellationToken cancellationToken)
    {
        if (!File.Exists(cache) || !File.Exists(source)) return false;
        if (!string.Equals((await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false)).Trim(), url, StringComparison.Ordinal))
            return false;
        // The .source sidecar names the URL; the stamp proves the bytes are the ones RHI downloaded (#142).
        if (!CacheStamp.IsValid(cache, url)) return false;
        if (!BinarySignatureInspector.HasPeSignature(cache, 100 * 1024)) return false;

        long? remoteSize = null;
        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) remoteSize = response.Content.Headers.ContentLength;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        return remoteSize is null or <= 0 || remoteSize.Value == new FileInfo(cache).Length;
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

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
