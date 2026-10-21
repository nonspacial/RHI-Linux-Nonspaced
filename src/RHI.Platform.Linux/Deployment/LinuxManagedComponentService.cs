using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService : ILinuxManagedUpdateBackend
{
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DeploymentRelocation _relocation;
    private readonly ReShadeDeploymentService _reshade;
    private readonly ReShadeAddonManagerService _addonManager;
    private readonly RenoDxDeploymentService _renodx;
    private readonly RenoDxUpgradeDeploymentService _renodxUpgrade;
    private readonly OptiScalerDeploymentService _optiscaler;
    private readonly GitHubReShadeAddonDeploymentService _relimiter;
    private readonly GitHubReShadeAddonDeploymentService _displayCommander;
    private readonly UpstreamRhiAddonDeploymentService _mfgAdaUnlock;
    private readonly Dlssg2030UnlockDeploymentService _dlssg2030;
    private readonly StandaloneDlssEnablerDeploymentService _dlssEnablerStandalone;
    private readonly Rtx40MfgStandaloneDeploymentService _rtx40Mfg;
    private readonly Dlss5FeederFullDeploymentService _dlss5Feeder;
    private readonly UpstreamRhiAddonDeploymentService _dlss5Bridge;
    private readonly D3DCompilerToolInstaller _d3dCompilerTools;
    private readonly UltimateAsiLoaderDeploymentService _ultimateAsiLoader;
    private readonly ShortFuseDeploymentService _shortFuse;
    private readonly DofFixDeploymentService _dofFix;
    private readonly REFrameworkDeploymentService _reFramework;
    private readonly LumaFrameworkDeploymentService _lumaFramework;
    private readonly LumaPolicyService _lumaPolicy;
    private readonly NeuralAddonVersionPreferenceStore _neuralVersionPins;
    private readonly ComponentUpdateRecorder? _history;
    private readonly ComponentUpdateLogStore? _updateLog;

    public LinuxManagedComponentService(
        LinuxDeploymentRecordStore records,
        ReShadeDeploymentService reshade,
        ReShadeAddonManagerService addonManager,
        RenoDxDeploymentService renodx,
        RenoDxUpgradeDeploymentService renodxUpgrade,
        OptiScalerDeploymentService optiscaler,
        GitHubReShadeAddonDeploymentService relimiter,
        GitHubReShadeAddonDeploymentService displayCommander,
        UpstreamRhiAddonDeploymentService mfgAdaUnlock,
        Dlssg2030UnlockDeploymentService dlssg2030,
        StandaloneDlssEnablerDeploymentService dlssEnablerStandalone,
        Rtx40MfgStandaloneDeploymentService rtx40Mfg,
        Dlss5FeederFullDeploymentService dlss5Feeder,
        UpstreamRhiAddonDeploymentService dlss5Bridge,
        D3DCompilerToolInstaller d3dCompilerTools,
        UltimateAsiLoaderDeploymentService ultimateAsiLoader,
        ShortFuseDeploymentService shortFuse,
        DofFixDeploymentService dofFix,
        REFrameworkDeploymentService reFramework,
        LumaFrameworkDeploymentService lumaFramework,
        LumaPolicyService lumaPolicy,
        NeuralAddonVersionPreferenceStore? neuralVersionPins = null,
        ComponentUpdateLogStore? updateLog = null,
        NrCostScalerService? nrCostScaler = null, // w3/parity (#96)
        RtxEncoreDeploymentService? rtxEncore = null, // w4/encore (#158)
        UpdateInclusionStore? updateInclusion = null, // w5/update-policy (#178)
        DlssAutoFollowStore? dlssFollow = null, // w5/update-policy (#177): its per-game holds join Repair after move
        DxvkHdrDeploymentService? dxvkHdr = null) // w7/dxvk-hdr (#200)
    {
        _dlssFollow = dlssFollow;
        _dxvkHdr = dxvkHdr; // w7/dxvk-hdr (#200)
        _nrCostScaler = nrCostScaler;
        _rtxEncore = rtxEncore;
        _updateInclusion = updateInclusion;
        _records = records;
        _relocation = new DeploymentRelocation(records);
        _reshade = reshade;
        _addonManager = addonManager;
        if (reshade is not null && addonManager is not null) reshade.AddonManagerFiles ??= addonManager.ReconcileForReShade; // #170/#168: No Addons turns these off too (through their record)
        _renodx = renodx;
        _renodxUpgrade = renodxUpgrade;
        _optiscaler = optiscaler;
        _relimiter = relimiter;
        _displayCommander = displayCommander;
        _mfgAdaUnlock = mfgAdaUnlock;
        _dlssg2030 = dlssg2030;
        _dlssEnablerStandalone = dlssEnablerStandalone;
        _rtx40Mfg = rtx40Mfg;
        _dlss5Feeder = dlss5Feeder;
        _dlss5Bridge = dlss5Bridge;
        _d3dCompilerTools = d3dCompilerTools;
        _ultimateAsiLoader = ultimateAsiLoader;
        _shortFuse = shortFuse;
        _dofFix = dofFix;
        _reFramework = reFramework;
        _lumaFramework = lumaFramework;
        _lumaPolicy = lumaPolicy;
        _neuralVersionPins = neuralVersionPins ?? new NeuralAddonVersionPreferenceStore();
        _updateLog = updateLog;
        _history = updateLog is null ? null : new ComponentUpdateRecorder(records, updateLog);
    }

    // Records the operation in the update history (when configured); refusals made before
    // anything is attempted (patch-target guards, dependency locks) are deliberately not wrapped.
    private Task<ComponentOperationResult> Track(
        DetectedGame game,
        ManagedComponentKind component,
        ComponentUpdateAction intent,
        Func<Task<ComponentOperationResult>> operation)
        => _history is null ? operation() : _history.TrackAsync(game, component, intent, operation);

    public async Task PrepareGameAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _lumaFramework.PrimeNamedMatchAsync(game, cancellationToken).ConfigureAwait(false);
            await _lumaPolicy.PrimeAsync(game, cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Luma catalog metadata is optional enrichment. Generic/offline component probing continues.
        }

        try
        {
            await _renodx.PrepareCompatibilityAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // A transient upstream RenoDX catalog failure must not misclassify the title as unsupported.
            // The install path can retry and return its normal controlled network/catalog error.
        }
    }

    // Every mutating entry point goes through this: an unconfirmed patch target, or records left
    // pointing at the folder the game was moved from (Refs #38), refuse before anything is touched.
    private readonly NrCostScalerService? _nrCostScaler; // w3/parity (#96)
    private readonly RtxEncoreDeploymentService? _rtxEncore; // w4/encore (#158)
    private readonly UpdateInclusionStore? _updateInclusion; // w5/update-policy (#178)
    private readonly DlssAutoFollowStore? _dlssFollow; // w5/update-policy (#177)
    private readonly DxvkHdrDeploymentService? _dxvkHdr; // w7/dxvk-hdr (#200)

    private ComponentOperationResult? PatchTargetGuard(DetectedGame game)
        => XboxWriteGate.Refusal(game) is { } busy ? ComponentOperationResult.Fail(busy) // w3/scan hook (Refs #90)
            : game.HasConfirmedPatchTarget
            ? _relocation.Guard(game)
            : ComponentOperationResult.Fail(
                "Alvo de modificações não definido. Use Editar jogo para selecionar o executável real do jogo; o executável do launcher nunca é usado como destino de patches.");

    // Installs also refuse an Xbox card whose exe cannot be read (encrypted on disk) when its package declares no
    // architecture either: no installer can tell its bitness, so it says why (Refs #90, #167). Removal and updates of
    // what exists never ask this.
    private ComponentOperationResult? InstallGuard(DetectedGame game) // w3/scan hook
        => PatchTargetGuard(game) ?? (XboxWriteGate.InstallRefusal(game) is { } install ? ComponentOperationResult.Fail(install) : null);

    /// <summary>#92: ShortFuse and OptiScaler move ReShade's DLL, which a Vulkan-layer ReShade does not have beside the game.</summary>
    private ComponentOperationResult? VulkanLayerConflict(DetectedGame game, ManagedComponentKind component)
        => component is ManagedComponentKind.ShortFuse or ManagedComponentKind.OptiScaler
           && ReShadeVulkanLayer.IsLayerRecord(_records.Find(game, ManagedComponentKind.ReShade))
            ? ComponentOperationResult.Fail(ReShadeVulkanLayer.NeedsDllReShadePrefix + DisplayDependencyName(component))
            : null;

    // w3/parity (#96): an nvngx_dlssnr.dll owner is not reinstalled, updated, swapped or removed under the NR Cost Scaler.
    private ComponentOperationResult? LayerGuard(DetectedGame game, ManagedComponentKind component)
        => NrCostScalerService.OwnerChangeRefusal(_records, game, component) is { } layered ? ComponentOperationResult.Fail(layered) : null;

    /// <summary>
    /// #170: an add-on RHI turned off for ReShade's No Addons build is not installed over or updated (that would put a
    /// loadable copy back while ReShade cannot load it); removing it stays possible.
    /// </summary>
    private ComponentOperationResult? AddonOffGuard(DetectedGame game, ManagedComponentKind component)
        => ReShadeAddonSwitch.IsOff(_records.Find(game, component))
            ? ComponentOperationResult.Fail(ReShadeAddonSwitch.HeldPrefix + DisplayDependencyName(component))
            : null;

    /// <summary>The NR Cost Scaler service (null in builds and tests constructed without it).</summary>
    public NrCostScalerService? NrCostScaler => _nrCostScaler;

    /// <summary>w7/dxvk-hdr (#200): the DX9 HDR service (null in builds and tests constructed without it).</summary>
    public DxvkHdrDeploymentService? DxvkHdr => _dxvkHdr;

    private ManagedUpdateCheck? PatchTargetUpdateGuard(DetectedGame game)
    {
        if (!game.HasConfirmedPatchTarget)
            return new ManagedUpdateCheck(true, false,
                "Alvo de modificações não definido. Atualizações locais estão bloqueadas até Editar jogo confirmar o executável real do jogo.",
                Blocked: true);
        return _relocation.Guard(game) is { } moved
            ? new ManagedUpdateCheck(true, false, moved.Message, Blocked: true)
            : null;
    }

    public bool IsManaged(DetectedGame game, ManagedComponentKind kind)
        => _records.Find(game, kind) is not null;

    /// <summary>
    /// What a changed Neural Rendering add-on pin does to the installed add-on: nothing yet (not installed: the pin
    /// applies at install), an in-place swap, or nothing until a reinstall — a legacy add-on-only DLSS5 Feeder cannot
    /// be swapped in place, so its pin is kept for the next install instead of failing and reverting (#115).
    /// </summary>
    public NeuralPinChange PlanNeuralPinChange(DetectedGame game, ManagedComponentKind kind)
    {
        if (!IsManaged(game, kind)) return NeuralPinChange.SaveOnly;
        if (kind == ManagedComponentKind.Dlss5Feeder && !IsDlss5FeederFullManaged(game)) return NeuralPinChange.ReinstallNeeded;
        return NeuralPinChange.SwapInPlace;
    }

    public bool IsDlss5FeederFullManaged(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        return record is not null && record.Metadata.TryGetValue("Feeder.FullStack", out var value) && value == "1";
    }

    public Task<ReShadeAddonManagerSnapshot> GetReShadeAddonManagerSnapshotAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _addonManager.GetSnapshotAsync(game, cancellationToken);

    public Task<ComponentOperationResult> ApplyReShadeAddonSelectionAsync(
        DetectedGame game,
        IReadOnlyList<string> selectedIds,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        // w3/dllnames (#91): no add-on selection for the No Addons build (clearing the selection stays possible).
        if (guard is null && selectedIds.Count > 0 && ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade)))
            guard = ComponentOperationResult.Fail(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + "ReShade Addon Manager");
        return guard is null
            ? (AddonSelectionForTesting ?? _addonManager.ApplySelectionAsync)(game, selectedIds, cancellationToken)
            : Task.FromResult(guard);
    }

    /// <summary>Test seam: the Addon Manager's own apply inside <see cref="ApplyReShadeAddonSelectionAsync"/> (VM tests run offline).</summary>
    internal Func<DetectedGame, IReadOnlyList<string>, CancellationToken, Task<ComponentOperationResult>>? AddonSelectionForTesting { get; set; }

    public async Task<ComponentOperationResult> InstallDlss5FeederAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game); // merge review nit 4: the same bitness/encrypted-exe answer as every installer (#167)
        if (guard is not null) return guard;
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.Dlss5Feeder) is { } layered) return ComponentOperationResult.Fail(layered); // w3/parity (#96)

        var preflight = FeederLayoutPlanner.ValidateTarget(game, consumer, requireReShade: false);
        if (preflight is not null) return preflight;
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            (string.IsNullOrWhiteSpace(deepFriedChickenPackagePath) || !File.Exists(deepFriedChickenPackagePath)))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z antes que dependências sejam alteradas.");
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            !DeepFriedChickenPackageVerifier.IsSupportedDeepFriedChickenArchive(deepFriedChickenPackagePath!))
            return ComponentOperationResult.Fail("O pacote consumidor do Deep Fried Chicken deve ser um arquivo oficial .zip ou .7z.");

        return await Track(game, ManagedComponentKind.Dlss5Feeder, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.Dlss5Feeder,
            indexedFiles: null,
            async () =>
            {
                var compiler = await EnsureNativeD3DCompilerDependencyAsync(game, d3dCompiler47Path, "DLSS5 Feeder", cancellationToken).ConfigureAwait(false);
                if (compiler is not null) return compiler;

                var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
                if (cleanup is not null) return cleanup;
                var pin = _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder);
                return await _dlss5Feeder.InstallAsync(game, consumer, deepFriedChickenPackagePath, d3dCompiler47Path, pin,
                    _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    public Task<ComponentOperationResult> InstallDlss5FeederAsync(
        DetectedGame game,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
        => InstallDlss5FeederAsync(game, Dlss5FeederConsumer.DeepFriedChicken, deepFriedChickenPackagePath, d3dCompiler47Path, cancellationToken);

    public string? GetNeuralAddonVersionPin(DetectedGame game, ManagedComponentKind kind)
        => _neuralVersionPins.Get(game, kind);

    public void SetNeuralAddonVersionPin(DetectedGame game, ManagedComponentKind kind, string? releaseTag)
        => _neuralVersionPins.Set(game, kind, releaseTag);

    public string? GetNeuralRenderingDllVersionPin(DetectedGame game)
        => _neuralVersionPins.GetNeuralRenderingDllVersion(game);

    public void SetNeuralRenderingDllVersionPin(DetectedGame game, string? version)
        => _neuralVersionPins.SetNeuralRenderingDllVersion(game, version);

    /// <summary>
    /// Swaps the nvngx_dlssnr.dll owned by the installed ShortFuse or DLSS5 Feeder Full stack to the
    /// stored NR DLL pin (or the newest manifest version). DX11 Bridge owns no NR DLL on Linux; its
    /// game's own DLL stays under the regular DLSS swap row.
    /// </summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return Task.FromResult(guard);
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.ShortFuse) is { } layered) return Task.FromResult(ComponentOperationResult.Fail(layered)); // w3/parity (#96)
        var version = _neuralVersionPins.GetNeuralRenderingDllVersion(game);
        if (IsShortFuseManaged(game))
            return TrackNeuralRenderingDll(game, ManagedComponentKind.ShortFuse,
                () => _shortFuse.SwapNeuralRenderingDllAsync(game, version, cancellationToken));
        if (IsDlss5FeederFullManaged(game))
            return TrackNeuralRenderingDll(game, ManagedComponentKind.Dlss5Feeder,
                () => _dlss5Feeder.SwapNeuralRenderingDllAsync(game, version, cancellationToken));
        return Task.FromResult(ComponentOperationResult.Fail("No ShortFuse or DLSS5 Feeder Full stack owns nvngx_dlssnr.dll for this game; the version applies at the next install."));
    }

    /// <summary>
    /// Brings an installed Neural Rendering add-on to its stored pin (or latest) by replacing only
    /// the add-on file(s) in place; the rest of the method's stack is untouched (upstream RHI 2.7.6).
    /// </summary>
    public Task<ComponentOperationResult> SwapNeuralAddonVersionAsync(
        DetectedGame game,
        ManagedComponentKind kind,
        CancellationToken cancellationToken = default)
    {
        // #170: a swap would put a loadable copy back (DX11 Bridge) or miss the renamed file (Feeder, ShortFuse) while
        // No Addons has the add-on turned off; it is refused like an update, before any download.
        var guard = PatchTargetGuard(game) ?? AddonOffGuard(game, kind);
        if (guard is not null) return Task.FromResult(guard);
        if (kind is not (ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse))
            return SwapNeuralAddonVersionCoreAsync(game, kind, cancellationToken);
        // Logged like Update All's NR updates (which reach the same core through UpdateManagedAsync).
        return Track(game, kind, ComponentUpdateAction.Update, () => SwapNeuralAddonVersionCoreAsync(game, kind, cancellationToken));
    }

    // The NR DLL version lives in the owning record's metadata, not in its Version, so the plain
    // ledger diff in Track would miss it.
    private Task<ComponentOperationResult> TrackNeuralRenderingDll(
        DetectedGame game,
        ManagedComponentKind owner,
        Func<Task<ComponentOperationResult>> swap)
        => _history is null
            ? swap()
            : _history.TrackOwnedFileAsync(game, owner, NeuralRenderingDllSwap.DllName,
                static record => record.Metadata.TryGetValue(NeuralRenderingDllSwap.MetaDllVersion, out var v) ? v : null,
                swap);

    private Task<ComponentOperationResult> SwapNeuralAddonVersionCoreAsync(
        DetectedGame game,
        ManagedComponentKind kind,
        CancellationToken cancellationToken)
        => kind switch
        {
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.SwapFeederVersionAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.UpdateAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.SwapAddonVersionAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{kind} is not a Neural Rendering add-on."))
        };

    public Task<IReadOnlyList<GitHubReleaseVersion>> ListNeuralAddonVersionsAsync(
        ManagedComponentKind kind,
        CancellationToken cancellationToken = default)
        => kind switch
        {
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.ListVersionsAsync(cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.ListVersionsAsync(cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.ListVersionsAsync(cancellationToken),
            _ => Task.FromException<IReadOnlyList<GitHubReleaseVersion>>(new ArgumentOutOfRangeException(nameof(kind)))
        };

    public LinuxDeploymentRecord? GetShortFuseRecord(DetectedGame game) => _shortFuse.GetRecord(game);

    public bool IsShortFuseManaged(DetectedGame game) => _shortFuse.IsManaged(game);

    public bool GetShortFuseAutoConfigEnabled(DetectedGame game) => _shortFuse.GetAutoConfigEnabled(game);

    public void SetShortFuseAutoConfigEnabled(DetectedGame game, bool enabled) => _shortFuse.SetAutoConfigEnabled(game, enabled);

    public bool GetShortFuseZzzLoadOrderEnabled(DetectedGame game) => _shortFuse.GetZzzLoadOrderEnabled(game);

    /// <summary>ZZZ state as ReShade will see it: the owned add-on's on-disk name when managed, else the preference.</summary>
    public bool GetShortFuseZzzLoadOrderInEffect(DetectedGame game) => _shortFuse.GetZzzLoadOrderInEffect(game);

    public Task<ComponentOperationResult> SetShortFuseZzzLoadOrderAsync(DetectedGame game, bool enabled, CancellationToken cancellationToken = default)
    {
        // #170: the rename needs the add-on under its own name; while No Addons has it turned off nothing changes.
        var guard = PatchTargetGuard(game) ?? AddonOffGuard(game, ManagedComponentKind.ShortFuse);
        return guard is null ? _shortFuse.SetZzzLoadOrderAsync(game, enabled, cancellationToken) : Task.FromResult(guard);
    }

    public Task<ShortFuseUpdateStatus> CheckShortFuseUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken);

    public Task<ShortFuseUpdateStatus> CheckShortFuseLatestReleaseAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, cancellationToken);

    public async Task<ComponentOperationResult> InstallShortFuseAsync(DetectedGame game, bool autoConfigEnabled = false, CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.ShortFuse) is { } layered) return ComponentOperationResult.Fail(layered); // w3/parity (#96)
        return await Track(game, ManagedComponentKind.ShortFuse, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.ShortFuse,
            indexedFiles: null,
            async () =>
            {
                var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.ShortFuse, cancellationToken).ConfigureAwait(false);
                if (cleanup is not null) return cleanup;
                return await _shortFuse.InstallAsync(game, autoConfigEnabled, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse),
                    _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// The Extras ShortFuse Remove: the generic removal itself, so both Remove buttons give one answer. Removal is the
    /// user's own choice and works while No Addons has the add-on turned off (#170; it deletes the off file).
    /// </summary>
    public Task<ComponentOperationResult> RemoveShortFuseAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => RemoveAsync(game, ManagedComponentKind.ShortFuse, cancellationToken);

    public LinuxDeploymentRecord? GetUltimateAsiLoaderRecord(DetectedGame game)
        => _ultimateAsiLoader.GetRecord(game);

    public LinuxDeploymentRecord? GetDlssg2030Record(DetectedGame game)
        => _dlssg2030.GetRecord(game);

    public Dlssg2030Preferences GetDlssg2030Preferences(DetectedGame game)
        => _dlssg2030.GetPreferences(game);

    public ComponentOperationResult SetDlssg2030Preferences(DetectedGame game, string gpuGeneration, string proxyName)
        => PatchTargetGuard(game) ?? _dlssg2030.SetPreferences(game, gpuGeneration, proxyName);

    public Task<Dlssg2030UpdateStatus> CheckDlssg2030UpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _dlssg2030.CheckLatestAsync(game, cancellationToken);

    public Task<ComponentOperationResult> UpdateDlssg2030Async(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.Dlssg2030Unlock, ComponentUpdateAction.Update, () => _dlssg2030.UpdateAsync(game, cancellationToken))
            : Task.FromResult(guard);
    }

    public LinuxDeploymentRecord? GetStandaloneDlssEnablerRecord(DetectedGame game)
        => _dlssEnablerStandalone.GetRecord(game);

    // w4/encore (#158): RTX Encore's ⚙ choices and checks.
    public RtxEncoreDeploymentService? RtxEncore => _rtxEncore;

    public LinuxDeploymentRecord? GetRtxEncoreRecord(DetectedGame game) => _rtxEncore?.GetRecord(game);

    public ComponentOperationResult SetRtxEncoreName(DetectedGame game, string name)
        => _rtxEncore is null ? ComponentOperationResult.Fail(RtxEncoreDeploymentService.NotInstalledMessage) : _rtxEncore.SetName(game, name);

    public Task<RtxEncoreUpdateStatus> CheckRtxEncoreUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _rtxEncore is null
            ? Task.FromResult(new RtxEncoreUpdateStatus(false, null, null, false, RtxEncoreDeploymentService.NotInstalledMessage))
            : _rtxEncore.CheckLatestAsync(game, cancellationToken);

    /// <summary>The Neural Rendering file choice: saved before install, applied in place (journaled, history) once installed.</summary>
    public Task<ComponentOperationResult> SetRtxEncoreNeuralRenderingAsync(DetectedGame game, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_rtxEncore is null) return Task.FromResult(ComponentOperationResult.Fail(RtxEncoreDeploymentService.NotInstalledMessage));
        if (_rtxEncore.GetRecord(game) is null) return _rtxEncore.SetNeuralRenderingAsync(game, enabled, cancellationToken);
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.RtxEncore);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.RtxEncore, ComponentUpdateAction.Update, () => _rtxEncore.SetNeuralRenderingAsync(game, enabled, cancellationToken));
    }

    public LinuxDeploymentRecord? GetRtx40MfgRecord(DetectedGame game)
        => _rtx40Mfg.GetRecord(game);

    public Rtx40MfgPreferences GetRtx40MfgPreferences(DetectedGame game)
        => _rtx40Mfg.GetPreferences(game);

    public ComponentOperationResult SetRtx40MfgPreferences(DetectedGame game, string proxyName)
        => PatchTargetGuard(game) ?? _rtx40Mfg.SetPreferences(game, proxyName);

    public ComponentOperationResult SetRtx40MfgPreferences(DetectedGame game, string gpuGeneration, string proxyName)
        => PatchTargetGuard(game) ?? _rtx40Mfg.SetPreferences(game, gpuGeneration, proxyName);

    public Task<Rtx40MfgUpdateStatus> CheckRtx40MfgUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _rtx40Mfg.CheckLatestAsync(game, cancellationToken);

    public Task<ComponentOperationResult> UpdateRtx40MfgAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.Rtx40MfgStandalone, ComponentUpdateAction.Update, () => _rtx40Mfg.UpdateAsync(game, cancellationToken))
            : Task.FromResult(guard);
    }

    public StandaloneDlssEnablerPreferences GetStandaloneDlssEnablerPreferences(DetectedGame game)
        => _dlssEnablerStandalone.GetPreferences(game);

    public ComponentOperationResult SetStandaloneDlssEnablerPreferences(DetectedGame game, string proxyName)
        => PatchTargetGuard(game) ?? _dlssEnablerStandalone.SetPreferences(game, proxyName);

    public Task<DlssEnablerUpdateStatus> CheckStandaloneDlssEnablerUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _dlssEnablerStandalone.CheckLatestAsync(game, cancellationToken);

    public Task<ComponentOperationResult> UpdateStandaloneDlssEnablerAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.DlssEnablerStandalone, ComponentUpdateAction.Update, () => _dlssEnablerStandalone.UpdateAsync(game, cancellationToken))
            : Task.FromResult(guard);
    }

    public Task<RenoDxCompatibilitySnapshot> GetRenoDxCompatibilitySettingsAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _renodx.GetCompatibilitySettingsAsync(game, cancellationToken);

    public ComponentOperationResult SetRenoDxCompatibilitySetting(DetectedGame game, string key, string value)
        => PatchTargetGuard(game) ?? _renodx.SetCompatibilitySetting(game, key, value);

    public OptiScalerAdvancedSnapshot GetOptiScalerAdvancedSettings(DetectedGame game)
        => _optiscaler.GetAdvancedSettings(game);

    public ComponentOperationResult SetOptiScalerAdvancedSetting(DetectedGame game, string id, string value)
        => PatchTargetGuard(game) ?? _optiscaler.SetAdvancedSetting(game, id, value);

    public async Task<ComponentOperationResult> SetOptiScalerAdvancedSettingAsync(
        DetectedGame game,
        string id,
        string value,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard ?? await _optiscaler.SetAdvancedSettingAsync(game, id, value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The global OptiScaler overlay key (Refs #187, upstream <c>ApplyOsHotkey_Click</c>): sets <c>[Menu] ShortcutKey</c>
    /// in every INI template and in the RHI-managed <c>OptiScaler.ini</c> of every game in <paramref name="games"/>. One game
    /// is one unit (Cancel stops between games); a game whose patch target or Xbox job refuses writes, or whose file
    /// cannot be written, keeps its file and is counted as failed; games without RHI's OptiScaler are not counted.
    /// </summary>
    public OptiScalerMenuKeyResult ApplyOptiScalerMenuKeyToAll(IReadOnlyList<DetectedGame> games, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(games);
        var updated = 0;
        var failures = new List<(string Game, string Reason)>();
        var templates = 0;
        try
        {
            if (_optiscaler.IniTemplates?.ApplyMenuKey(key) is { } written)
            {
                templates = written.Updated;
                failures.AddRange(written.Failures); // each template that kept its file, by name
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add((_optiscaler.IniTemplates!.Directory, ex.Message)); // the folder could not be listed
        }
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var unit = new OperationCancellation(cancellationToken);
            if (_optiscaler.GetRecord(game) is null) continue;
            if (PatchTargetGuard(game) is { } refused)
            {
                failures.Add((game.Name, refused.Message));
                continue;
            }
            OperationCancellation.BeginWrites(unit.Token);
            var result = _optiscaler.SetMenuKey(game, key);
            if (result is null) continue;
            if (result.Success) updated++;
            else failures.Add((game.Name, result.Message));
        }
        return new OptiScalerMenuKeyResult(templates, updated, failures);
    }

    public IReadOnlyList<string> GetUltimateAsiLoaderProxyNames(DetectedGame game)
        => UltimateAsiLoaderDeploymentService.SupportedNamesFor(game);

    public string GetUltimateAsiLoaderRecommendedProxy(DetectedGame game)
        => UltimateAsiLoaderDeploymentService.RecommendedDefaultFor(game);

    public Task<UalUpdateStatus> CheckUltimateAsiLoaderUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _ultimateAsiLoader.CheckLatestAsync(game, cancellationToken);

    public Task<ComponentOperationResult> InstallUltimateAsiLoaderAsync(
        DetectedGame game,
        string dllName,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.UltimateAsiLoader, ComponentUpdateAction.Install, () => _ultimateAsiLoader.InstallAsync(game, dllName, cancellationToken))
            : Task.FromResult(guard);
    }

    public Task<ComponentOperationResult> UpdateUltimateAsiLoaderAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.UltimateAsiLoader, ComponentUpdateAction.Update, () => _ultimateAsiLoader.UpdateAsync(game, cancellationToken))
            : Task.FromResult(guard);
    }

    public Task<ComponentOperationResult> RemoveUltimateAsiLoaderAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return Task.FromResult(guard);
        return IsShortFuseManaged(game)
            ? Task.FromResult(ComponentOperationResult.Fail("Remove ShortFuse first. UAL teardown is locked while ShortFuse depends on the ASI loader chain."))
            : Track(game, ManagedComponentKind.UltimateAsiLoader, ComponentUpdateAction.Remove, () => _ultimateAsiLoader.RemoveAsync(game, cancellationToken));
    }

    public OptiScalerVariant? GetInstalledOptiScalerVariant(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        return record is null ? null : OptiScalerDeploymentService.GetInstalledVariant(record);
    }

    /// <summary>Nightly installs take the game's saved nightly build pin (#156); null pin = Latest.</summary>
    public Task<ComponentOperationResult> InstallOptiScalerAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
        => InstallOptiScalerAsync(game, variant,
            variant == OptiScalerVariant.Nightly ? _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler) : null,
            cancellationToken);

    /// <summary>Installs OptiScaler with an explicit nightly build pin (Nightly only; null = Latest). For #136's Control UE FG.</summary>
    public Task<ComponentOperationResult> InstallOptiScalerAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        string? nightlyBuild,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler) // w3/parity (#96)
                    ?? VulkanLayerConflict(game, ManagedComponentKind.OptiScaler); // w5/reshade-core (#92)
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install,
                () => InstallOptiScalerUnderChosenNameAsync(game, variant, nightlyBuild, cancellationToken)); // #172
    }

    /// <summary>
    /// The nightly build shown for the game (#156): the installed record's pin while OptiScaler Nightly is installed (its
    /// choice is fixed until removal, as upstream), otherwise the saved preference. Null = Latest.
    /// </summary>
    public string? GetOptiScalerNightlyBuild(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        if (record is not null)
            return OptiScalerDeploymentService.GetPinnedNightlyBuild(record);
        var saved = _neuralVersionPins.Get(game, ManagedComponentKind.OptiScaler);
        return OptiScalerPackageProvider.IsNightlyBuild(saved) ? saved : null;
    }

    /// <summary>Saves the nightly build used at the next Nightly install (null = Latest). Throws when the file cannot be written.</summary>
    public void SetOptiScalerNightlyBuild(DetectedGame game, string? build)
    {
        if (build is not null && !OptiScalerPackageProvider.IsNightlyBuild(build))
            throw new ArgumentException(OptiScalerDeploymentService.NightlyBuildInvalidPrefix + build, nameof(build));
        _neuralVersionPins.Set(game, ManagedComponentKind.OptiScaler, build);
    }

    public Task<IReadOnlyList<string>> ListOptiScalerNightlyBuildsAsync(CancellationToken cancellationToken = default)
        => _optiscaler.ListNightlyBuildsAsync(cancellationToken);

    /// <summary>
    /// w3/dllnames (#136): Control UE's optional frame-generation step after its RenoDX install — OptiScaler Nightly pinned
    /// to <see cref="ControlUeFrameGenerationService.RequiredNightlyBuild"/> as winmm.dll with its FG stack and settings,
    /// and FgSlHdr10 in reshade.ini. Guarded like an OptiScaler install; recorded in the OptiScaler update history.
    /// </summary>
    public Task<ComponentOperationResult> InstallControlUeFrameGenerationAsync(DetectedGame game, bool useHdr, CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install,
                () => new ControlUeFrameGenerationService(_optiscaler, _records).InstallAsync(game, useHdr, cancellationToken));
    }

    private async Task<ComponentOperationResult> InstallNeuralRenderingManagedAsync(
        DetectedGame game,
        ManagedComponentKind target,
        CancellationToken cancellationToken)
    {
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.Dlss5Feeder)
        {
            var compiler = await EnsureNativeD3DCompilerDependencyAsync(
                game,
                explicitCompilerPath: null,
                requiredBy: "DLSS5 Feeder",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (compiler is not null) return compiler;
        }

        var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, target, cancellationToken).ConfigureAwait(false);
        if (cleanup is not null) return cleanup;

        return target switch
        {
            ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.InstallAsync(
                game,
                Dlss5FeederConsumer.RenoDxDlss5,
                deepFriedChickenPackagePath: null,
                d3dCompiler47Path: null,
                releaseTag: _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder),
                neuralRenderingDllVersion: _neuralVersionPins.GetNeuralRenderingDllVersion(game),
                cancellationToken: cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.InstallAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Dx11Bridge), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ShortFuse => await _shortFuse.InstallAsync(game, _shortFuse.GetAutoConfigEnabled(game), _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse),
                _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false),
            _ => ComponentOperationResult.Fail($"{target} is not a Neural Rendering method.")
        };
    }

    private async Task<ComponentOperationResult?> RemoveConflictingNeuralRenderingMethodsAsync(
        DetectedGame game,
        ManagedComponentKind target,
        CancellationToken cancellationToken)
    {
        var conflicts = NeuralRenderingConflictsFor(target);

        foreach (var conflict in conflicts)
        {
            if (_records.Find(game, conflict) is null) continue;
            var result = conflict switch
            {
                ManagedComponentKind.ShortFuse => await _shortFuse.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                _ => ComponentOperationResult.Ok("No conflicting method.")
            };

            if (!result.Success)
                return ComponentOperationResult.Fail($"Neural Rendering method switch aborted while removing {DisplayNeuralRenderingMethod(conflict)}: {result.Message}");
        }

        return null;
    }

    public bool HasNativeDlssForNeuralRendering(DetectedGame game, bool dlssDetectedNow)
        => InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow,
            _records.Find(game, ManagedComponentKind.ShortFuse),
            _records.Find(game, ManagedComponentKind.Dlss5Feeder));

    internal static bool InferNativeDlssBeforeManagedNeuralRendering(
        bool dlssDetectedNow,
        LinuxDeploymentRecord? shortFuseRecord,
        LinuxDeploymentRecord? feederRecord)
    {
        if (!dlssDetectedNow) return false;

        // ShortFuse and Feeder can create nvngx_dlss.dll themselves. Picker/recommendation
        // policy must reflect the game's capability before the active managed method, not
        // the payload injected by that method. ExistedBeforeInstall is authoritative when
        // the managed record tracks the SR DLL. If no matching file is tracked while a
        // managed method is active, fail safe as native/external rather than assuming the
        // current DLL was created by RHI.
        foreach (var record in new[] { shortFuseRecord, feederRecord })
        {
            if (record is null) continue;
            var tracked = record.Files.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.Path) &&
                Path.GetFileName(file.Path).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
            if (tracked is not null) return tracked.ExistedBeforeInstall;
            return true;
        }

        return true;
    }

    internal static IReadOnlyList<ManagedComponentKind> NeuralRenderingConflictsFor(ManagedComponentKind target) => target switch
    {
        ManagedComponentKind.ShortFuse => new[] { ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge },
        ManagedComponentKind.Dlss5Feeder => new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Dx11Bridge },
        ManagedComponentKind.Dlss5Dx11Bridge => new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder },
        _ => Array.Empty<ManagedComponentKind>()
    };

    /// <summary>
    /// Mirrors the upstream RHI Neural Rendering default selection policy. The recommendation
    /// does not bypass Linux eligibility/ownership gates; it only identifies the upstream
    /// preferred method for the selected game's observed capabilities.
    /// </summary>
    public static ManagedComponentKind RecommendedNeuralRenderingMethod(DetectedGame game, bool hasNativeDlss)
    {
        var architecture = GameArchitecture.Of(game);
        if (architecture == PeArchitecture.X86)
            return ManagedComponentKind.Dlss5Feeder;
        if (!hasNativeDlss)
            return ManagedComponentKind.Dlss5Feeder;
        return game.EffectiveGraphicsApi is GraphicsApi.DirectX11 or GraphicsApi.Vulkan
            ? ManagedComponentKind.Dlss5Dx11Bridge
            : ManagedComponentKind.ShortFuse;
    }

    private static string DisplayNeuralRenderingMethod(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ShortFuse => "ShortFuse",
        ManagedComponentKind.Dlss5Feeder => "DLSS5 Feeder",
        ManagedComponentKind.Dlss5Dx11Bridge => "DLSS5 DX11 Bridge",
        _ => kind.ToString()
    };

    public LumaPolicySnapshot GetLumaPolicy(DetectedGame game)
        => _lumaPolicy.GetCachedOrDefault(game);

    public LumaPolicySnapshot SetLumaModePreference(DetectedGame game, RHI.Platform.Linux.Paths.LumaModePreference preference)
        => _lumaPolicy.SetPreference(game, preference);

    // w5/ini-presets hook (#181): INI deploys and "apply to all games" refuse a game where any install would be refused.
    public string? IniWriteRefusal(DetectedGame game) => PatchTargetGuard(game)?.Message;

    // w5/ini-presets hook (#181 review 2): INI deploys journaled on the component's record (set by the app's composition).
    public UserIniService? UserIni { get; set; }

    // w5/ini-presets hook (#180): the ReShade preset service (journaled; set by the app's composition).
    public ReShadePresetService? ReShadePresets { get; set; }

    // w5/ini-presets hook (#189): the Luma ⚙ settings service (journaled; set by the app's composition).
    public LumaSettingsService? LumaSettings { get; set; }

    // w5/ini-presets hook (#182): RenoDX peak brightness (journaled; set by the app's composition).
    public RenoDxPeakNits? RenoDxPeakNits { get; set; }

    // w8/renodx hook (#217/#218): the RenoDX service for the ⚙'s Engine.ini switches (journaled; writes guarded by IniWriteRefusal).
    public RenoDxDeploymentService RenoDx => _renodx;

    // ── w7/shader-sets (#179): global shader / add-on sets, per-game Global / Select / Off ──────────────────────────

    /// <summary>Fixed text before the reason when a game's own shader or add-on choice could not be saved (PT-BR pair).</summary>
    public const string ShaderChoiceNotSavedPrefix = "Nothing was changed: the game's shader and add-on choice could not be saved: ";
    /// <summary>Fixed text before the reason when ReShade was installed but the game's add-ons were not (PT-BR pair).</summary>
    public const string AddonSetNotAppliedPrefix = " The game's add-ons were not applied: ";
    /// <summary>The note after ReShade's message when the game's add-ons were applied with it (PT-BR pair).</summary>
    public const string AddonSetAppliedNote = " The game's add-ons were applied.";

    private GlobalShaderAddonStore? _shaderSets;

    /// <summary>
    /// #179: the global sets and per-game choices (set by the app's composition; the Addon Manager reads the same store).
    /// The shader-pack service ReShade uses is given it by the composition too (<see cref="ReShadeShaderPackService.Sets"/>).
    /// </summary>
    public GlobalShaderAddonStore? ShaderSets
    {
        get => _shaderSets;
        set
        {
            _shaderSets = value;
            if (_addonManager is not null) _addonManager.Sets = value;
        }
    }

    /// <summary>True when RHI's ReShade is installed on the game (the only games a shader or add-on set reaches).</summary>
    public bool HasRhiReShade(DetectedGame game) => _records.Find(game, ManagedComponentKind.ReShade) is not null;

    /// <summary>The packs <paramref name="game"/> gets now (null without a readable store).</summary>
    public IReadOnlyList<string>? EffectiveShaderPacks(DetectedGame game)
        => _shaderSets is { } sets && sets.ChoiceFor(game) is { } choice
            ? ReShadeShaderPackService.EffectivePackIds(choice, sets.GlobalShaderPackIds)
            : null;

    /// <summary>The Addon Manager add-ons <paramref name="game"/> gets now (null without a readable store).</summary>
    public ReShadeAddonEffectiveSet? EffectiveAddons(DetectedGame game) => _addonManager?.EffectiveSelectionFor(game);

    /// <summary>True when the game's Addon Manager record holds exactly <paramref name="addonIds"/> (none: no record either).</summary>
    public bool AddonSelectionMatches(DetectedGame game, IReadOnlyList<string> addonIds)
        => _addonManager is not null && SameIds(_addonManager.RecordedSelection(game), addonIds) && (addonIds.Count == 0) != _addonManager.HasRecord(game);

    /// <summary>The Addon Manager's catalogue (the global add-ons dialog has no game).</summary>
    public Task<IReadOnlyList<ReShadeAddonCatalogItem>> RefreshReShadeAddonCatalogAsync(CancellationToken cancellationToken = default)
        => _addonManager.RefreshCatalogItemsAsync(cancellationToken);

    /// <summary>The selection the game's Addon Manager record holds now (empty without one).</summary>
    public IReadOnlyList<string> RecordedAddonSelection(DetectedGame game) => _addonManager?.RecordedSelection(game) ?? [];

    /// <summary>
    /// A pack list the user chose for this game (profile Apply, the Shaders/Add-ons dialog, a preset with its shaders):
    /// when it differs from what the game gets now, the game switches to Select with exactly that list (upstream). Returns
    /// why it could not be saved (nothing may then be installed), or null.
    /// </summary>
    public string? RecordExplicitShaderChoice(DetectedGame game, IReadOnlyList<string> packIds)
    {
        if (_shaderSets is not { } sets || EffectiveShaderPacks(game) is not { } now || SamePacks(now, packIds)) return null;
        return SaveChoice(() => sets.SetShaderMode(game, ShaderAddonMode.Select, packIds));
    }

    /// <summary>As <see cref="RecordExplicitShaderChoice"/>, for the game's Addon Manager add-ons.</summary>
    public string? RecordExplicitAddonChoice(DetectedGame game, IReadOnlyList<string> addonIds)
    {
        if (_shaderSets is not { } sets || EffectiveAddons(game) is not { HeldByNoAddons: false } now || SameIds(now.Ids, addonIds)) return null;
        return SaveChoice(() => sets.SetAddonMode(game, ShaderAddonMode.Select, addonIds));
    }

    /// <summary>Saves a game's shader mode (and its Select list when given); returns why it was not saved, or null.</summary>
    public string? SetShaderMode(DetectedGame game, ShaderAddonMode mode, IReadOnlyList<string>? packIds = null)
        => _shaderSets is { } sets ? SaveChoice(() => sets.SetShaderMode(game, mode, packIds)) : null;

    /// <summary>Saves a game's add-on mode (and its Select list when given); returns why it was not saved, or null.</summary>
    public string? SetAddonMode(DetectedGame game, ShaderAddonMode mode, IReadOnlyList<string>? addonIds = null)
        => _shaderSets is { } sets ? SaveChoice(() => sets.SetAddonMode(game, mode, addonIds)) : null;

    private static string? SaveChoice(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shader-sets", "Could not save a game's shader and add-on choice", ex);
            return ShaderChoiceNotSavedPrefix + ex.Message;
        }
    }

    private static bool SameIds(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));

    // Two pack lists that deploy the same packs (a ReShade record holds the list with its dependencies added).
    private bool SamePacks(IEnumerable<string> a, IEnumerable<string> b)
        => ReShadeShaderPackService.DeployedPackIds(a, _reshade.AvailableShaderPacks)
            .SetEquals(ReShadeShaderPackService.DeployedPackIds(b, _reshade.AvailableShaderPacks));

    /// <summary>
    /// Brings the game's RHI shader packs to its effective set WITHOUT re-running ReShade's install (review major 3:
    /// <see cref="ReShadeDeploymentService.SyncShaderPacksAsync"/>, one journaled operation on ReShade's record; ReShade's
    /// DLL and version stay). A native D3DCompiler_47 is got first when packs arrive, as profile Apply does. Logged in the
    /// update history as ReShade (reshade-shaders) when the pack list changes. Nothing without RHI's ReShade or when the
    /// packs are already the effective set.
    /// </summary>
    public async Task<ShaderSetApplyResult> ApplyShaderSetAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return new(ShaderSetOutcome.NotApplicable, "");
        if (EffectiveShaderPacks(game) is not { } packs) return new(ShaderSetOutcome.Failed, ShaderSetsUnreadableMessage);
        if (SamePacks(ReShadeDeploymentService.RecordedShaderPacks(record), packs)) return new(ShaderSetOutcome.Unchanged, "");
        if (PatchTargetGuard(game) is { } guard) return new(ShaderSetOutcome.Failed, guard.Message);
        if (!ReShadeVulkanLayer.IsLayerRecord(record) && packs.Count > 0
            && string.IsNullOrWhiteSpace(_reshade.GetInstalledOptions(game).D3DCompiler47Path)
            && await EnsureNativeD3DCompilerDependencyAsync(game, null, "ReShade shader packs", cancellationToken).ConfigureAwait(false) is { } compiler)
            return new(ShaderSetOutcome.Failed, compiler.Message);
        Task<ComponentOperationResult> Sync() => _reshade.SyncShaderPacksAsync(game, packs, cancellationToken);
        var result = await (_history is null
            ? Sync()
            : _history.TrackOwnedFileAsync(game, ManagedComponentKind.ReShade, ShaderPackHistoryLabel, ShaderPackHistoryVersion, Sync)).ConfigureAwait(false);
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message, result.Landed);
    }

    /// <summary>
    /// Review minor 4: the game's deployed packs are not its effective set (a batch was cancelled, the game was running, or
    /// a crash rolled a change back). The Shaders/Add-ons dialog says so; its Apply, or the global set's Save, catches up.
    /// </summary>
    public bool ShaderSetBehind(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.ReShade) is { } record && EffectiveShaderPacks(game) is { } packs
           && !SamePacks(ReShadeDeploymentService.RecordedShaderPacks(record), packs);

    /// <summary>As <see cref="ShaderSetBehind"/>, for the game's Addon Manager add-ons (never while No Addons holds them).</summary>
    public bool AddonSetBehind(DetectedGame game)
        => _addonManager is not null && _records.Find(game, ManagedComponentKind.ReShade) is not null
           && EffectiveAddons(game) is { HeldByNoAddons: false } effective
           && !(SameIds(_addonManager.RecordedSelection(game), effective.Ids) && (effective.Ids.Count == 0 || _addonManager.HasRecord(game)));

    /// <summary>The update history's name for ReShade's shader packs ("ReShade (reshade-shaders)"; the folder's own name).</summary>
    internal const string ShaderPackHistoryLabel = "reshade-shaders";

    // The "version" the history shows for a pack change: the deployed pack ids, or a dash for none.
    private static string ShaderPackHistoryVersion(LinuxDeploymentRecord record)
        => ReShadeDeploymentService.RecordedShaderPacks(record) is { Count: > 0 } ids ? string.Join(", ", ids) : "—";

    /// <summary>
    /// Brings the game's Addon Manager add-ons to its effective set (one journaled Addon Manager operation, logged in the
    /// update history like any Addon Manager change). Held while ReShade is the No Addons build; nothing without RHI's
    /// ReShade or when the add-ons already match.
    /// </summary>
    public async Task<ShaderSetApplyResult> ApplyAddonSetAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (_records.Find(game, ManagedComponentKind.ReShade) is null) return new(ShaderSetOutcome.NotApplicable, "");
        if (EffectiveAddons(game) is not { } effective) return new(ShaderSetOutcome.Failed, ShaderSetsUnreadableMessage);
        if (effective.HeldByNoAddons) return new(ShaderSetOutcome.Held, ReShadeAddonSwitch.HeldPrefix + ReShadeAddonManagerService.DisplayName);
        if (!AddonSetBehind(game)) return new(ShaderSetOutcome.Unchanged, "");
        var result = await Track(game, ManagedComponentKind.ReShadeAddonManager, ComponentUpdateAction.Update,
            () => ApplyReShadeAddonSelectionAsync(game, effective.Ids, cancellationToken)).ConfigureAwait(false);
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message, result.Landed);
    }

    /// <summary>The message when the shader and add-on choices cannot be read (PT-BR pair).</summary>
    public const string ShaderSetsUnreadableMessage = "The shader and add-on choices cannot be read; nothing was changed.";

    /// <summary>
    /// Appended to a ReShade install or update that could not take the game's shader packs because no native
    /// D3DCompiler_47 could be had for its prefix (review major 2; PT-BR pair). ReShade itself is installed.
    /// </summary>
    public const string ShaderPacksNotInstalledNote =
        " The game's shader packs were not installed: they need a native D3DCompiler_47 in its Wine prefix. Install it with Protontricks or Winetricks on the D3DCompiler 47 row in the ReShade section, then Apply in Shaders/Add-ons; a DLL picked there with Browse takes effect with the ReShade profile's Apply instead.";

    /// <summary>
    /// Review major 2: ReShade's install and update read the game's effective set inside ReShade's own install
    /// (<see cref="ReShadeShaderPackService.WithEffectiveSelection"/>), so every path that reaches it — the ReShade row,
    /// ReShade installed for RenoDX/Luma/ShortFuse/Feeder, Update All and a managed update — first gets a native
    /// D3DCompiler_47 for that set, as profile Apply and the shader batch do. When none can be had ReShade still installs
    /// (the hook then keeps the caller's own list) and the result says why the packs are missing.
    /// </summary>
    private async Task<ComponentOperationResult> InstallReShadeForShaderSetAsync(DetectedGame game, ReShadeInstallOptions options, bool update,
        CancellationToken cancellationToken)
    {
        if (_shaderSets is not null && !options.VulkanLayer && string.IsNullOrWhiteSpace(options.D3DCompiler47Path)
            && !ReShadeVulkanLayer.IsLayerRecord(_records.Find(game, ManagedComponentKind.ReShade))
            && EffectiveShaderPacks(game) is { Count: > 0 }
            && await EnsureNativeD3DCompilerDependencyAsync(game, null, "ReShade shader packs", cancellationToken).ConfigureAwait(false) is { } compiler)
            AppLog.Warn("shader-sets", $"{game.Name}: ReShade goes on without the game's shader packs: {compiler.Message}");
        var result = update
            ? await _reshade.UpdateAsync(game, cancellationToken).ConfigureAwait(false)
            : await _reshade.InstallAsync(game, options, cancellationToken).ConfigureAwait(false);
        return result.Success && EffectiveShaderPacks(game) is { Count: > 0 } effective
               && !SamePacks(_reshade.GetInstalledOptions(game).ShaderPackIds, effective)
            ? result with { Message = result.Message + ShaderPacksNotInstalledNote }
            : result;
    }

    /// <summary>
    /// After a ReShade install (the ReShade row, profile Apply, or ReShade installed for another component): a game with
    /// no Addon Manager add-ons yet gets its effective add-ons, as upstream deploys the game's add-ons after ReShade. Its
    /// own journaled operation; a failure leaves ReShade installed and is said after ReShade's message.
    /// </summary>
    private async Task<ComponentOperationResult> ApplyAddonSetAfterReShadeAsync(DetectedGame game, ManagedComponentKind target,
        IReadOnlyCollection<ManagedComponentKind> installedThisAttempt, ComponentOperationResult result, CancellationToken cancellationToken)
    {
        if (_shaderSets is null || _addonManager is null || !result.Success) return result;
        if (target != ManagedComponentKind.ReShade && !installedThisAttempt.Contains(ManagedComponentKind.ReShade)) return result;
        if (_addonManager.HasRecord(game) || EffectiveAddons(game) is not { HeldByNoAddons: false, Ids.Count: > 0 } effective) return result;
        ComponentOperationResult addons;
        try
        {
            addons = await ApplyReShadeAddonSelectionAsync(game, effective.Ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            addons = ComponentOperationResult.Fail(ex is OperationCanceledException ? "cancelled." : ex.Message);
        }
        return result with { Message = result.Message + (addons.Success ? AddonSetAppliedNote : AddonSetNotAppliedPrefix + addons.Message) };
    }

}

/// <summary>#179: what bringing one game to its effective shader or add-on set did.</summary>
public enum ShaderSetOutcome { Changed, Unchanged, Held, NotApplicable, Failed }

/// <summary>
/// #179: one game's outcome and the message of the operation (empty when nothing ran). <paramref name="Landed"/> (w8 final
/// review 1): the change was written and the save then reported an error; Message is the note saying so
/// (<see cref="ComponentOperationResult.Landed"/>).
/// </summary>
public sealed record ShaderSetApplyResult(ShaderSetOutcome Outcome, string Message, bool Landed = false);

/// <summary>Outcome of changing a Neural Rendering add-on pin (see <see cref="LinuxManagedComponentService.PlanNeuralPinChange"/>).</summary>
public enum NeuralPinChange
{
    /// <summary>Not installed: the pin is used by the next install.</summary>
    SaveOnly,
    /// <summary>Installed: the add-on is swapped to the pinned version now.</summary>
    SwapInPlace,
    /// <summary>Installed as a legacy add-on-only DLSS5 Feeder: the pin applies once it is removed and installed again.</summary>
    ReinstallNeeded
}

/// <summary>What setting the global OptiScaler overlay key changed (Refs #187): templates and games updated, games that kept their file.</summary>
public sealed record OptiScalerMenuKeyResult(int Templates, int Games, IReadOnlyList<(string Game, string Reason)> Failures);
