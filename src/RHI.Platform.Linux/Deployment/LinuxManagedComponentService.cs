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
    private readonly ManagedComponentRowDecorator _rows; // #212

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
        _rows = new ManagedComponentRowDecorator(_records, _reshade, _renodx, _lumaFramework, _lumaPolicy, _dlssg2030,
            _dlssEnablerStandalone, _rtx40Mfg, _rtxEncore, _dxvkHdr, _updateInclusion);
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

    // w4/encore (#158): RTX Encore's ⚙ choices and checks.
    public RtxEncoreDeploymentService? RtxEncore => _rtxEncore;

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

}
