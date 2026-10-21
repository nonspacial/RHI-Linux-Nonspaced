using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed class LinuxManagedComponentService : ILinuxManagedUpdateBackend
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

    public void Decorate(DetectedGame game, IReadOnlyList<ManagedComponent> components)
    {
        // One ledger read per selection (#32): every lookup below is served from this snapshot.
        var recordsByKind = _records.FindAll(game)
            .GroupBy(record => record.Component)
            .ToDictionary(group => group.Key, group => group.First());
        LinuxDeploymentRecord? Find(ManagedComponentKind kind) => recordsByKind.GetValueOrDefault(kind);

        var reShadePresent = components.Any(c => c.Kind == ManagedComponentKind.ReShade && c.Availability == ComponentAvailability.Installed);
        var managedRenoDx = Find(ManagedComponentKind.RenoDX);
        var managedRenoDxUpgrade = Find(ManagedComponentKind.RenoDXUpgrade);
        var managedReLimiter = Find(ManagedComponentKind.ReLimiter);
        var managedDisplayCommander = Find(ManagedComponentKind.DisplayCommander);
        var managedMfgAdaUnlock = Find(ManagedComponentKind.MfgAdaUnlock);
        var managedDlssg2030 = Find(ManagedComponentKind.Dlssg2030Unlock);
        var managedRtx40Mfg = Find(ManagedComponentKind.Rtx40MfgStandalone);
        var managedDlss5Feeder = Find(ManagedComponentKind.Dlss5Feeder);
        var managedDlss5Bridge = Find(ManagedComponentKind.Dlss5Dx11Bridge);
        var managedOptiScaler = Find(ManagedComponentKind.OptiScaler);
        var managedShortFuse = Find(ManagedComponentKind.ShortFuse);
        var detectedDlssNow = components.Any(c => c.Kind == ManagedComponentKind.Dlss && c.Availability == ComponentAvailability.Installed);
        var nativeDlssBeforeManagedMethod = InferNativeDlssBeforeManagedNeuralRendering(
            detectedDlssNow, managedShortFuse, managedDlss5Feeder);
        var managedDofFix = Find(ManagedComponentKind.DofFix);
        var managedReFramework = Find(ManagedComponentKind.REFramework);
        var managedLumaFramework = Find(ManagedComponentKind.LumaFramework);

        // ComponentProbe deliberately refuses to classify a bare external dinput8.dll as REFramework.
        // A valid RHI ownership record is stronger evidence and must be reflected before ReShade's
        // RE Engine dependency requirement is evaluated before the REFramework runtime folder exists.
        var reFrameworkComponent = components.FirstOrDefault(c => c.Kind == ManagedComponentKind.REFramework);
        if (managedReFramework is not null && reFrameworkComponent is not null &&
            reFrameworkComponent.Availability == ComponentAvailability.Available &&
            managedReFramework.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
        {
            reFrameworkComponent.Availability = ComponentAvailability.Installed;
            reFrameworkComponent.ManagedByRhi = true;
        }

        foreach (var component in components)
        {
            if (component.Availability == ComponentAvailability.Unsupported)
            {
                component.ActionAllowed = false;
                continue;
            }

            // w4/encore (#158): hidden where it cannot apply, with or without a confirmed patch target (never RHI's own install).
            if (component.Kind == ManagedComponentKind.RtxEncore && component.Availability == ComponentAvailability.Available
                && Find(ManagedComponentKind.RtxEncore) is null && HideRtxEncore(game, component, components))
                continue;

            if (!game.HasConfirmedPatchTarget)
            {
                component.ActionAllowed = false;
                component.Note = "Alvo de modificações não definido. O executável de inicialização/launcher foi preservado, mas o RHI não instalará, atualizará ou removerá arquivos locais de componentes até você selecionar o executável real do jogo em Editar jogo.";
                continue;
            }

            var record = Find(component.Kind);
            component.ManagedByRhi = record is not null;
            if (component.Kind is ManagedComponentKind.ReShade or ManagedComponentKind.RenoDX or ManagedComponentKind.RenoDXUpgrade or ManagedComponentKind.OptiScaler or
                ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander or ManagedComponentKind.MfgAdaUnlock or ManagedComponentKind.Dlssg2030Unlock or ManagedComponentKind.DlssEnablerStandalone or ManagedComponentKind.Rtx40MfgStandalone or
                ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse or
                ManagedComponentKind.DofFix or ManagedComponentKind.REFramework or ManagedComponentKind.LumaFramework or
                ManagedComponentKind.RtxEncore)
            {
                if (component.Kind == ManagedComponentKind.REFramework && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                if (component.Kind == ManagedComponentKind.LumaFramework && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                if (component.Kind == ManagedComponentKind.Dlssg2030Unlock && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                if (component.Kind == ManagedComponentKind.DlssEnablerStandalone && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                // w5/reshade-core (#92): ReShade's Vulkan layer lives in the prefix; its record decides, and the row says which ReShade.
                if (component.Kind == ManagedComponentKind.ReShade && ReShadeVulkanLayer.IsLayerRecord(record))
                {
                    component.DisplayName = ReShadeVulkanLayer.RowName;
                    if (component.Availability == ComponentAvailability.Available &&
                        record!.Files.Any(file => !file.AllowUserChanges && File.Exists(file.Path)))
                        component.Availability = ComponentAvailability.Installed;
                }

                if (component.Kind is ManagedComponentKind.Rtx40MfgStandalone or ManagedComponentKind.RtxEncore && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                if (component.Availability == ComponentAvailability.Installed && record is null)
                {
                    component.ActionAllowed = false;
                    component.Note = string.IsNullOrWhiteSpace(component.Note)
                        ? "Detected on disk but not installed by RHI Linux. Remove/update is locked to protect the external setup."
                        : component.Note + " External installation is protected from removal.";
                    continue;
                }

                component.ActionAllowed = true;
                if (component.Kind == ManagedComponentKind.ReShade &&
                    (component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable) &&
                    record is not null)
                {
                    var dependentName = managedRenoDx is not null ? "RenoDX HDR"
                        : managedRenoDxUpgrade is not null ? "RenoDX Upgrade"
                        : managedReLimiter is not null ? "ReLimiter"
                        : managedDisplayCommander is not null ? "Display Commander"
                        : managedMfgAdaUnlock is not null ? "MFG Ada Unlock"
                        : managedDlss5Feeder is not null ? "DLSS5 Feeder"
                        : managedDlss5Bridge is not null ? "DLSS5 DX11 Bridge"
                        : managedShortFuse is not null ? "ShortFuse"
                        : managedDofFix is not null ? "Universal UE DOF Fix"
                        : managedLumaFramework is not null ? "Luma Framework"
                        : null;
                    if (dependentName is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = $"Remove RHI-managed {dependentName} first; its add-on depends on ReShade.";
                    }
                }

                if (component.Kind == ManagedComponentKind.ReShade && component.Availability == ComponentAvailability.Available)
                {
                    // w5/reshade-core (#92): on a Vulkan game the row is the explicit "install ReShade as a Vulkan layer" choice.
                    if (record is null && ReShadeVulkanLayer.IsVulkanTarget(game))
                    {
                        component.DisplayName = ReShadeVulkanLayer.RowName;
                        var notOffered = ReShadeVulkanLayer.OfferRefusal(game);
                        component.ActionAllowed = notOffered is null;
                        component.Note = notOffered ?? ReShadeVulkanLayer.RowNote;
                    }
                    else if (XnaGacReShade.IsListed(game)) // w7/reshade (#201): XNA games take the GAC path
                    {
                        var notOffered = XnaGacReShade.OfferRefusal(game);
                        component.ActionAllowed = notOffered is null;
                        component.Note = notOffered ?? XnaGacReShade.RowNote;
                    }
                    else if (ReShadeDeploymentService.SelectProxyName(game.EffectiveGraphicsApi) is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Safe game-local ReShade deployment is currently enabled for DirectX 8/9/10/11/12 and OpenGL targets; this API needs a different integration path.";
                    }
                    else if (game.PrefixPath is null && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                    {
                        component.ActionAllowed = false;
                        component.Note = "Start the Windows game once so its Wine/Proton prefix is created before installing ReShade.";
                    }
                    else if (managedOptiScaler is not null &&
                             // w3/dllnames (#120): only when ReShade's name is the one OptiScaler holds.
                             (_reshade?.ResolveProxyName(game) is not { } proxy ||
                              managedOptiScaler.Files.Any(f => Path.GetFileName(f.Path).Equals(proxy.Name, StringComparison.OrdinalIgnoreCase))))
                    {
                        component.Note = "Will coexist with RHI-managed OptiScaler as ReShade64.dll; RHI enables OptiScaler's LoadReshade setting automatically.";
                    }
                    else
                    {
                        component.Note = "Full add-on ReShade build. Use only with single-player/offline titles; add-on injection can trigger anti-cheat.";
                    }
                }

                if (component.Kind == ManagedComponentKind.ReShade && component.Availability == ComponentAvailability.Available &&
                    REFrameworkDeploymentService.IsGameEligible(game) &&
                    !components.Any(c => c.Kind == ManagedComponentKind.REFramework &&
                        c.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
                {
                    component.Note = "Jogos RE Engine exigem RE Framework. Ao instalar ReShade, o RHI instalará automaticamente o RE Framework primeiro e só continuará se a dependência ficar válida.";
                }

                if (component.Kind == ManagedComponentKind.REFramework)
                {
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        if (!REFrameworkDeploymentService.IsGameEligible(game))
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = REFrameworkDeploymentService.EligibilityReason(game);
                        }
                        else if (string.IsNullOrWhiteSpace(game.PrefixPath))
                        {
                            component.ActionAllowed = false;
                            component.Note = "Start the RE Engine game once so its Wine/Proton prefix exists before installing RE Framework.";
                        }
                        else if (REFrameworkDeploymentService.FindCaseInsensitiveFile(
                                     REFrameworkDeploymentService.ResolveDeployDirectory(game),
                                     REFrameworkDeploymentService.DllFileName) is not null && record is null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "An external dinput8.dll already exists beside the game executable. RHI will not overwrite or adopt that proxy.";
                        }
                        else
                        {
                            component.Note = REFrameworkDeploymentService.EligibilityReason(game) +
                                " Installs the upstream monolithic REFramework.zip dinput8.dll with a per-game native,builtin Wine override and hash-guarded removal.";
                        }
                    }
                    else if (component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable &&
                             record is not null && managedReFramework is not null &&
                             Find(ManagedComponentKind.ReShade) is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Remove RHI-managed ReShade first; this RE Engine ReShade path depends on RE Framework.";
                    }
                }

                if (component.Kind == ManagedComponentKind.LumaFramework)
                {
                    component.UnrealEngine = game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true; // w5/ini-presets hook (#189)
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        var named = _lumaFramework.GetCachedNamedMatch(game);
                        var generic = named is null ? _lumaFramework.GetCachedGenericMatch(game) : null;
                        var eligible = named is not null
                            ? LumaFrameworkDeploymentService.IsNamedGameEligible(game, named)
                            : LumaFrameworkDeploymentService.IsGameEligible(game, generic);
                        var reason = named is not null
                            ? LumaFrameworkDeploymentService.NamedEligibilityReason(game, named)
                            : LumaFrameworkDeploymentService.EligibilityReason(game, generic);

                        if (!eligible)
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = named is null ? reason + GenericLumaMetadataSummary(generic) : reason;
                        }
                        else if (Find(ManagedComponentKind.ReShade) is null && reShadePresent &&
                                 !(managedRenoDx is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible))
                        {
                            component.ActionAllowed = false;
                            component.Note = "Luma Framework exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará nem sobrescreverá automaticamente.";
                        }
                        else if (Find(ManagedComponentKind.ReShade) is null &&
                                 !(managedRenoDx is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible))
                        {
                            component.Note = "Luma Framework exige ReShade gerenciado pelo RHI; a instalação do Luma instalará ReShade automaticamente antes do pacote Luma. Em DX9, dgVoodoo2 continua sendo preparado automaticamente quando exigido pelo catálogo.";
                        }
                        else if (managedRenoDx is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
                        {
                            component.ActionAllowed = false;
                            component.Note = named is not null
                                ? $"Named Luma match '{named.Name}' is available, but this title is not listed in upstream lumaRenodxCompat. Remove RHI-managed RenoDX HDR first."
                                : "Remove RHI-managed RenoDX HDR before enabling Luma; this title is not listed in upstream lumaRenodxCompat.";
                        }
                        else if (named is not null)
                        {
                            component.Note = reason +
                                $" Package: {Path.GetFileName(new Uri(named.DownloadUrl!).AbsolutePath)}. RHI preserves existing ReShade/DLSS state and verifies every extracted Luma file before managing it." +
                                " " + _lumaPolicy.GetCachedOrDefault(game).Summary;
                        }
                        else
                        {
                            component.Note = reason +
                                " Uses the upstream Luma-Unreal_Engine.zip package, GitHub SHA-256 when published, ReShade AddonPath routing, and reversible [Luma] INI changes." +
                                GenericLumaMetadataSummary(generic) + " " + _lumaPolicy.GetCachedOrDefault(game).Summary;
                        }
                    }
                }

                if (component.Kind == ManagedComponentKind.RenoDX && component.Availability == ComponentAvailability.Available)
                {
                    var renoDxPrepared = _renodx.TryGetPreparedCompatibility(game, out var preparedRenoDx);
                    // #141: an unlinked title keeps RenoDX only through a manifest snapshot override (the catalogue
                    // returns nothing else for it); without a prepared match it stays unsupported.
                    var unlinkedOverride = game.WikiUnlinkedByManifest && renoDxPrepared && preparedRenoDx is not null;
                    if (game.WikiUnlinkedByManifest && !unlinkedOverride)
                    {
                        component.Availability = ComponentAvailability.Unsupported;
                        component.ActionAllowed = false;
                        component.Note = "Upstream wikiUnlinks explicitly removes this title from RenoDX named and generic deployment.";
                    }
                    else if (!string.IsNullOrWhiteSpace(game.ExternalModUrl))
                    {
                        component.ActionAllowed = false;
                        component.Note = $"Upstream forceExternalOnly requires external installation ({game.ExternalModLabel ?? "Open external mod"}): {game.ExternalModUrl}";
                    }
                    else if (renoDxPrepared && preparedRenoDx is null)
                    {
                        component.Availability = ComponentAvailability.Unsupported;
                        component.ActionAllowed = false;
                        component.Note = "Nenhum perfil RenoDX específico foi encontrado no catálogo upstream e este jogo não corresponde aos fallbacks genéricos seguros de Unreal/Unity. A instalação automática foi desativada para evitar aplicar um add-on incorreto.";
                    }
                    else if (preparedRenoDx is not null && UntrustedRenoDxLink(preparedRenoDx) is { } refused)
                    {
                        // DEP-06: never offer a download RHI would refuse; say why instead.
                        component.ActionAllowed = false;
                        component.Note = refused;
                    }
                    else if (!reShadePresent && !(managedLumaFramework is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible))
                    {
                        component.Note = "RenoDX HDR exige ReShade com suporte completo a add-ons; o RHI instalará ReShade automaticamente antes do RenoDX.";
                    }
                    else if (managedLumaFramework is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Remove RHI-managed Luma Framework before installing RenoDX HDR; this title is not listed in upstream lumaRenodxCompat.";
                    }
                    else if (managedLumaFramework is not null)
                    {
                        component.Note = "Upstream compatibility data allows RenoDX HDR and Luma Framework to coexist for this title. Existing file-protection and dependency checks still apply.";
                    }
                    else if (unlinkedOverride)
                    {
                        component.Note = "Upstream wikiUnlinks removes this title from RenoDX wiki and generic matching; the upstream manifest's snapshot override is installed instead: "
                            + (preparedRenoDx!.SnapshotUrl64 ?? preparedRenoDx.SnapshotUrl32);
                    }
                }

                if (component.Kind == ManagedComponentKind.RenoDXUpgrade && component.Availability == ComponentAvailability.Available)
                {
                    if (!reShadePresent)
                    {
                        component.Note = "RenoDX Upgrade é um add-on do ReShade; o RHI instalará ReShade automaticamente antes do Upgrade.";
                    }
                    else if (RenoDxUpgradeDeploymentService.ResolveArchitecture(game) is not (PeArchitecture.X86 or PeArchitecture.X64))
                    {
                        component.ActionAllowed = false;
                        component.Note = "RenoDX Upgrade requires a detected 32-bit or 64-bit Windows executable.";
                    }
                    else
                    {
                        var coexistence = managedRenoDx is not null || managedLumaFramework is not null
                            ? " Upstream describes RenoDX Upgrade as unnecessary alongside RenoDX/Luma; RHI Linux leaves the choice available rather than silently removing another HDR framework."
                            : "";
                        component.Note = "Upstream RenoDX Upgrade snapshot: inverse tone mapping/resource upgrades for DX9+ games. Use with RenoFX HDR Toolkit for full HDR conversion. The correct addon32/addon64 file is selected from current upstream data." + coexistence;
                    }
                }

                if (component.Kind == ManagedComponentKind.DofFix)
                {
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        if (!DofFixDeploymentService.IsGameEligible(game))
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = DofFixDeploymentService.EligibilityReason(game);
                        }
                        else
                        {
                            var dependencyNote = reShadePresent
                                ? ""
                                : " ReShade será instalado automaticamente antes do DOF Fix.";
                            component.Note = DofFixDeploymentService.EligibilityReason(game) +
                                dependencyNote +
                                " RHI Linux instala o add-on upstream renodx-universal_ue-dof-fix.addon64 com verificação de integridade e restauração segura.";
                        }
                    }
                }

                if (component.Kind is ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander)
                {
                    if (component.Availability == ComponentAvailability.Available && !reShadePresent &&
                        !((component.Kind == ManagedComponentKind.ReLimiter && components.Any(c => c.Kind == ManagedComponentKind.DisplayCommander && c.Availability == ComponentAvailability.Installed)) ||
                          (component.Kind == ManagedComponentKind.DisplayCommander && components.Any(c => c.Kind == ManagedComponentKind.ReLimiter && c.Availability == ComponentAvailability.Installed))))
                    {
                        component.Note = $"{component.DisplayName} é um add-on do ReShade; o RHI instalará ReShade automaticamente antes deste componente.";
                    }
                    else if (component.Kind == ManagedComponentKind.ReLimiter &&
                             components.Any(c => c.Kind == ManagedComponentKind.DisplayCommander && c.Availability == ComponentAvailability.Installed))
                    {
                        component.ActionAllowed = false;
                        component.Note = "ReLimiter and Display Commander are mutually exclusive in upstream RHI. Remove Display Commander first.";
                    }
                    else if (component.Kind == ManagedComponentKind.DisplayCommander &&
                             components.Any(c => c.Kind == ManagedComponentKind.ReLimiter && c.Availability == ComponentAvailability.Installed))
                    {
                        component.ActionAllowed = false;
                        component.Note = "Display Commander and ReLimiter are mutually exclusive in upstream RHI. Remove ReLimiter first.";
                    }
                    else if (component.Availability == ComponentAvailability.Available)
                    {
                        component.Note = component.Kind == ManagedComponentKind.ReLimiter
                            ? "Latest official ReLimiter ReShade add-on from GitHub. RHI respects [ADDON] AddonPath, initializes relimiter.ini only when absent, and preserves user config on update/remove."
                            : "Latest official Display Commander ReShade add-on from GitHub. RHI respects [ADDON] AddonPath, initializes DisplayCommander.ini only when absent, and preserves user config on update/remove.";
                    }
                }

                if (component.Kind is ManagedComponentKind.MfgAdaUnlock or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge)
                {
                    if (component.Availability == ComponentAvailability.Available && !reShadePresent &&
                        !(component.Kind == ManagedComponentKind.MfgAdaUnlock && (managedDlssg2030 is not null || managedRtx40Mfg is not null)) &&
                        !(component.Kind == ManagedComponentKind.Dlss5Feeder && NeuralRenderingRuntimeOwnership.OptiScalerOwner(managedOptiScaler) is not null))
                    {
                        component.Note = $"{component.DisplayName} é um add-on do ReShade; o RHI instalará ReShade automaticamente antes deste componente.";
                    }
                    else if (component.Kind == ManagedComponentKind.MfgAdaUnlock && component.Availability == ComponentAvailability.Available)
                    {
                        if (managedDlssg2030 is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock; os métodos de desbloqueio de Frame Generation são mutuamente exclusivos no RHI-Linux.";
                        }
                        else if (managedRtx40Mfg is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock; mantenha uma única autoridade de Frame Generation.";
                        }
                        else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || GameArchitecture.Of(game) != PeArchitecture.X64)
                        {
                            component.ActionAllowed = false;
                            component.Note = "MFG Ada Unlock is a 64-bit ReShade add-on.";
                        }
                        else if (!components.Any(c => c.Kind == ManagedComponentKind.Streamline && c.Availability == ComponentAvailability.Installed))
                        {
                            component.ActionAllowed = false;
                            component.Note = "MFG Ada Unlock requires a game with Streamline/DLSS frame generation. Streamline was not detected.";
                        }
                        else
                        {
                            component.Note = "Add-on por jogo. Exige Geração de Quadros DLSS fornecida pelo jogo via Streamline e uma nvngx_dlssg.dll moderna compatível.";
                        }
                    }
                    else if (component.Kind == ManagedComponentKind.Dlss5Dx11Bridge && component.Availability == ComponentAvailability.Available)
                    {
                        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || GameArchitecture.Of(game) != PeArchitecture.X64)
                        {
                            component.ActionAllowed = false;
                            component.Note = "DLSS5 DX11 Bridge is currently enabled only for 64-bit Windows games.";
                        }
                        else if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.Vulkan))
                        {
                            component.ActionAllowed = false;
                            component.Note = "O método DX11 Bridge é destinado a jogos DX11/Vulkan com DLSS nativo. Este alvo não é DX11/Vulkan.";
                        }
                        else if (!nativeDlssBeforeManagedMethod)
                        {
                            component.ActionAllowed = false;
                            component.Note = "DLSS5 DX11 Bridge requires native DLSS for this method; DLSS injected by another RHI Neural Rendering method does not count as native game capability.";
                        }
                        else
                        {
                            component.Note = managedShortFuse is not null || managedDlss5Feeder is not null
                                ? "Troca de método: instalar o DX11 Bridge removerá transacionalmente primeiro o método ShortFuse/Feeder atualmente gerenciado pelo RHI."
                                : "Add-on DLSS5 DX11 Bridge para jogos DX11/Vulkan com DLSS nativo.";
                        }
                    }
                    else if (component.Kind == ManagedComponentKind.Dlss5Feeder && component.Availability == ComponentAvailability.Available)
                    {
                        var feederArchitecture = string.IsNullOrWhiteSpace(game.ExecutablePath)
                            ? PeArchitecture.Unknown
                            : GameArchitecture.Of(game);
                        var feederApiSupported = feederArchitecture == PeArchitecture.X86
                            ? game.EffectiveGraphicsApi is GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX11
                            : feederArchitecture == PeArchitecture.X64
                                && game.EffectiveGraphicsApi is GraphicsApi.DirectX9 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12;
                        if (feederArchitecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
                        {
                            component.ActionAllowed = false;
                            component.Note = "DLSS5 Feeder requires a recognized x86/x64 Windows executable.";
                        }
                        else if (!feederApiSupported)
                        {
                            component.ActionAllowed = false;
                            component.Note = $"DLSS5 Feeder does not have a verified orchestration path for {feederArchitecture} {game.EffectiveGraphicsApi}.";
                        }
                        else if (nativeDlssBeforeManagedMethod)
                        {
                            component.ActionAllowed = false;
                            component.Note = "This game already has native DLSS. Feeder Full is the no-DLSS path; use the native DLSS/Neural Rendering workflow instead.";
                        }
                        else if (NeuralRenderingRuntimeOwnership.OptiScalerOwner(managedOptiScaler) is { } nrOwner)
                        {
                            component.ActionAllowed = false;
                            component.Note = NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner);
                        }
                        else
                        {
                            component.Note = managedShortFuse is not null || managedDlss5Bridge is not null
                                ? "Troca de método: instalar o Feeder Full removerá transacionalmente primeiro o método ShortFuse/DX11 Bridge atualmente gerenciado pelo RHI."
                                : feederArchitecture == PeArchitecture.X86
                                    ? "DLSS5 Feeder 32-bit: selecione RenoDX DLSS5 para usar addon32 + host64. Deep Fried Chicken permanece restrito ao layout 64-bit comprovado."
                                    : "DLSS5 Feeder Full: Feeder + Deep Fried Chicken ou RenoDX DLSS5 com runtimes SR/NR e pilha de shaders isolada.";
                        }
                    }
                }

                if (component.Kind == ManagedComponentKind.Dlssg2030Unlock)
                {
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        if (!Dlssg2030UnlockDeploymentService.IsGameEligible(game))
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = Dlssg2030UnlockDeploymentService.EligibilityReason(game);
                        }
                        else if (managedRtx40Mfg is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "Universal MFG já está ativo. Remova-o antes de instalar RTX 20 FG Unlock legado.";
                        }
                        else if (managedMfgAdaUnlock is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "MFG Ada Unlock já está ativo. Remova-o antes de instalar RTX 20 FG Unlock legado.";
                        }
                        else if (managedShortFuse is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "ShortFuse já gerencia a pilha DLSS/Frame Generation. Remova-o antes de usar o RTX 20 FG Unlock legado.";
                        }
                        else
                        {
                            var preferences = _dlssg2030.GetPreferences(game);
                            component.Note = $"Legado/Experimental sob Proton/VKD3D: RTX 20 / SM75, proxy {preferences.ProxyName}. RTX 30 migrou para Universal MFG. Exige jogo x64 DirectX 12; o componente RTX 20 usa uma fonte upstream fixada para manter comportamento reproduzível.";
                        }
                    }
                    else if (record is not null)
                    {
                        var gpu = record.Metadata.GetValueOrDefault("FG2030.GpuGeneration") ?? Dlssg2030UnlockDeploymentService.GpuGenRtx30;
                        var proxy = record.Metadata.GetValueOrDefault("FG2030.ProxyName") ?? Dlssg2030UnlockDeploymentService.DefaultProxyName;
                        component.Note = $"FG Unlock legado gerenciado pelo RHI · {gpu} · {proxy}. Registros RTX 30 antigos continuam removíveis/atualizáveis, mas novas instalações RTX 30 usam Universal MFG.";
                    }
                }

                if (component.Kind == ManagedComponentKind.DlssEnablerStandalone)
                {
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        if (!StandaloneDlssEnablerDeploymentService.IsGameEligible(game))
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = StandaloneDlssEnablerDeploymentService.EligibilityReason(game);
                        }
                        else
                        {
                            var preferences = _dlssEnablerStandalone.GetPreferences(game);
                            component.Note = $"Standalone, independente do OptiScaler · proxy {preferences.ProxyName}. O RHI protege slots existentes, aplica override Wine por jogo e valida o pacote x64; a compatibilidade sob Wine/Proton deve ser confirmada no jogo.";
                        }
                    }
                    else if (record is not null)
                    {
                        var proxy = record.Metadata.GetValueOrDefault("DLSSEnabler.ProxyName") ?? StandaloneDlssEnablerDeploymentService.DefaultProxyName;
                        component.Note = $"DLSS Enabler Standalone gerenciado pelo RHI · proxy {proxy}. Instalação independente do OptiScaler; remoção/update são protegidos por hash e restauram o override Wine anterior.";
                    }
                }

                if (component.Kind == ManagedComponentKind.Rtx40MfgStandalone)
                {
                    if (component.Availability == ComponentAvailability.Available)
                    {
                        var preferences = _rtx40Mfg.GetPreferences(game);
                        if (!Rtx40MfgStandaloneDeploymentService.IsGameEligible(game, preferences.GpuGeneration, managedOptiScaler))
                        {
                            component.Availability = ComponentAvailability.Unsupported;
                            component.ActionAllowed = false;
                            component.Note = Rtx40MfgStandaloneDeploymentService.EligibilityReason(game, preferences.GpuGeneration, managedOptiScaler);
                        }
                        else if (managedMfgAdaUnlock is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "MFG Ada Unlock já está ativo. Remova-o antes de instalar Universal MFG.";
                        }
                        else if (managedDlssg2030 is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar Universal MFG.";
                        }
                        else if (managedShortFuse is not null)
                        {
                            component.ActionAllowed = false;
                            component.Note = "ShortFuse já gerencia Frame Generation. Remova-o antes de instalar Universal MFG.";
                        }
                        else
                        {
                            component.Note = preferences.GpuGeneration == Rtx40MfgStandaloneDeploymentService.GpuGenRtx30
                                ? $"Universal MFG Experimental · RTX 30 · DirectX 12 · proxy {preferences.ProxyName}. O suporte RTX 30 é experimental upstream; o RHI valida integridade/arquitetura, protege arquivos existentes e Backspace abre o menu."
                                : $"Universal MFG Experimental · RTX 40 · proxy {preferences.ProxyName}. DirectX 12 é o caminho principal e Vulkan permanece experimental; o RHI valida integridade/arquitetura e protege arquivos existentes.";
                        }
                    }
                    else if (record is not null)
                    {
                        var gpu = record.Metadata.GetValueOrDefault("UniversalMFG.GpuGeneration") ?? Rtx40MfgStandaloneDeploymentService.GpuGenRtx40;
                        var proxy = record.Metadata.GetValueOrDefault("UniversalMFG.ProxyName")
                                    ?? record.Metadata.GetValueOrDefault("RTX40MFG.ProxyName")
                                    ?? Rtx40MfgStandaloneDeploymentService.DefaultProxyName;
                        component.Note = $"Universal MFG Experimental gerenciado pelo RHI · {gpu} · proxy {proxy}. Update/removal são protegidos por hash e preservam o override Wine anterior.";
                    }
                }

                // Not for RHI's own install (its file may be gone): that row stays, removable, whatever the game is now.
                if (component.Kind == ManagedComponentKind.RtxEncore && component.Availability == ComponentAvailability.Available && record is null)
                    DecorateRtxEncore(game, component, components);

                // w4/encore (#158): RTX Encore with its Neural Rendering file owns nvngx_dlssnr.dll (one owner, #157).
                if (component.Kind is ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder &&
                    component.Availability == ComponentAvailability.Available &&
                    Find(ManagedComponentKind.RtxEncore) is { } encoreRecord && NeuralRenderingRuntimeOwnership.ListsRuntime(encoreRecord))
                {
                    component.ActionAllowed = false;
                    component.Note = NeuralRenderingRuntimeOwnership.RefusalFor(NeuralRenderingRuntimeOwnership.RtxEncoreOwnerName);
                    continue;
                }

                if (component.Kind == ManagedComponentKind.ShortFuse && component.Availability == ComponentAvailability.Available)
                {
                    if (managedRtx40Mfg is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.";
                    }
                    else if (managedDlssg2030 is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.";
                    }
                    else if (Find(ManagedComponentKind.ReShade) is null && reShadePresent && managedOptiScaler is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "ShortFuse exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará automaticamente.";
                    }
                    else if (Find(ManagedComponentKind.ReShade) is null && managedOptiScaler is null)
                    {
                        component.Note = "ShortFuse exige ReShade gerenciado pelo RHI; o ReShade será instalado automaticamente antes do ShortFuse. Se Auto Config estiver ativo, o Ultimate ASI Loader continua sendo instalado automaticamente pelo próprio fluxo do ShortFuse.";
                    }
                    else if (managedOptiScaler is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "ShortFuse auto-setup will not rewrite an active OptiScaler/ReShade chain. Remove OptiScaler first.";
                    }
                    else if (game.EffectiveGraphicsApi == GraphicsApi.OpenGL)
                    {
                        component.ActionAllowed = false;
                        component.Note = "ShortFuse upstream supports 64-bit Windows targets except OpenGL; this game is detected as OpenGL.";
                    }
                    else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || GameArchitecture.Of(game) != PeArchitecture.X64)
                    {
                        component.ActionAllowed = false;
                        component.Note = "ShortFuse is enabled for detected 64-bit Windows games.";
                    }
                    else if (game.PrefixPath is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Start the game once so its Wine/Proton prefix exists before installing ShortFuse.";
                    }
                    else
                    {
                        var switchNote = managedDlss5Feeder is not null || managedDlss5Bridge is not null
                            ? " Installing ShortFuse will transactionally remove the currently RHI-managed Feeder/DX11 Bridge method first."
                            : "";
                        component.Note = "ShortFuse: pilha DLSS/Streamline transacional. A configuração automática ASI é opcional; quando ativada usa ReShade64.asi + prioridade UAL winmm→version→dinput8 + HookStreamline/HookDirectX." + switchNote;
                    }
                }

                if (component.Kind == ManagedComponentKind.OptiScaler && component.Availability == ComponentAvailability.Available)
                {
                    if (managedShortFuse is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Remove ShortFuse before changing the OptiScaler/ReShade proxy chain.";
                    }
                    else if (game.EffectiveGraphicsApi is not (GraphicsApi.DirectX11 or GraphicsApi.DirectX12))
                    {
                        component.ActionAllowed = false;
                        component.Note = "O OptiScaler suporta mais APIs, mas o deployment automático de proxy é habilitado apenas para jogos DX11/DX12 detectados. Vulkan exige primeiro uma escolha explícita de loader.";
                    }
                    else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || GameArchitecture.Of(game) != PeArchitecture.X64)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Automatic OptiScaler deployment is currently limited to detected 64-bit Windows executables.";
                    }
                    else if (game.PrefixPath is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Start the game once so its Wine/Proton prefix exists before installing OptiScaler.";
                    }
                    else
                    {
                        var nrOwnershipNote = managedDlss5Feeder is not null ? OptiScalerDeploymentService.FeederBlocksNrVariantsNote : "";
                        component.Note = (reShadePresent
                            ? "OptiScaler Stable/Nightly/DLSS NR/F5 DLSSNR. Choose the channel in ⚙ before installing; RHI will migrate its managed ReShade to ReShade64.dll for deterministic coexistence. Offline/single-player only."
                            : "OptiScaler Stable/Nightly/DLSS NR/F5 DLSSNR from the upstream GitHub channels. Choose the channel in ⚙ before installing; requires a supported temporal upscaler input. Offline/single-player only.") + nrOwnershipNote;
                    }
                }

                if (record is not null)
                {
                    component.InstalledVersion = record.Version;
                    if (component.ActionAllowed || component.Kind != ManagedComponentKind.ReShade ||
                        (managedRenoDx is null && managedRenoDxUpgrade is null && managedReLimiter is null && managedDisplayCommander is null &&
                         managedMfgAdaUnlock is null && managedDlss5Feeder is null && managedDlss5Bridge is null && managedShortFuse is null &&
                         managedDofFix is null && managedReFramework is null && managedLumaFramework is null))
                    {
                        var channel = component.Kind == ManagedComponentKind.ReShade && record.Metadata.TryGetValue("ReShade.Channel", out var value)
                            ? $" · {value}"
                            : "";
                        var consumer = "";
                        if (component.Kind == ManagedComponentKind.Dlss5Feeder &&
                            record.Metadata.TryGetValue("Feeder.Consumer", out var consumerName))
                        {
                            var consumerVersionText = record.Metadata.TryGetValue("Feeder.ConsumerVersion", out var consumerVersion) &&
                                                      !string.IsNullOrWhiteSpace(consumerVersion)
                                ? $" {consumerVersion}"
                                : "";
                            consumer = $" · {consumerName}{consumerVersionText}";
                        }
                        component.Note = $"Managed by RHI Linux{channel}{consumer}{(string.IsNullOrWhiteSpace(record.Version) ? "" : $" · {record.Version}")}.";
                    }
                    // w4/encore (#158): every RTX Encore row says it is not confirmed under Proton yet.
                    if (component.Kind == ManagedComponentKind.RtxEncore && component.ActionAllowed)
                        component.Note = RtxEncoreDeploymentService.NotYetConfirmedNote;
                }
            }
            else
            {
                component.ActionAllowed = false;
            }
        }

        // w3/dllnames (#91): add-ons are not offered over the No Addons build; the note says why (last, so it wins).
        if (ReShadeDeploymentService.LacksAddonSupport(Find(ManagedComponentKind.ReShade)))
            foreach (var component in components.Where(c => LinuxComponentDependencyPolicy.IsReShadeAddon(c.Kind) && c.Availability == ComponentAvailability.Available))
            {
                component.ActionAllowed = false;
                component.Note = ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + component.DisplayName;
            }
        // w5/reshade-core (#92 review nit 8): on a Vulkan game with no ReShade nothing installs ReShade by itself (the layer
        // is the user's choice on its own row), so an add-on row never promises an automatic ReShade first; ShortFuse
        // moves ReShade's DLL, which the layer does not have, so it is not offered there.
        if (Find(ManagedComponentKind.ReShade) is null && ReShadeVulkanLayer.IsVulkanTarget(game)
            && !components.Any(c => c.Kind == ManagedComponentKind.ReShade && c.Availability == ComponentAvailability.Installed))
            foreach (var component in components.Where(c => LinuxComponentDependencyPolicy.IsReShadeAddon(c.Kind)
                         && c.Availability == ComponentAvailability.Available && c.ActionAllowed && Find(c.Kind) is null))
            {
                if (component.Kind == ManagedComponentKind.ShortFuse) component.ActionAllowed = false;
                component.Note = component.Kind == ManagedComponentKind.ShortFuse
                    ? ReShadeVulkanLayer.DllOnlyOnVulkanGameMessage
                    : ReShadeVulkanLayer.NotOnRequestMessage;
            }
        // #170: an installed add-on RHI turned off for No Addons says so (its Remove stays available).
        foreach (var component in components.Where(c => ReShadeAddonSwitch.IsOff(Find(c.Kind))))
            component.Note = ReShadeAddonSwitch.OffNote;

        _dxvkHdr?.Decorate(game, components, Find(ManagedComponentKind.DxvkHdr)); // w7/dxvk-hdr hook (#200): the DX9 HDR row
        DecorateModCredits(game, components, managedRenoDx, managedLumaFramework);
        DecorateUpdateInclusion(game, components); // w5/update-policy (#178)
    }

    // w4/encore (#158): RTX Encore's row before install. Hidden (Unsupported) where it cannot apply: not a 64-bit Windows
    // DX11/DX12/Vulkan game, or a game without its own DLSS (everything RTX Encore can do under Proton works through it).
    private bool HideRtxEncore(DetectedGame game, ManagedComponent component, IReadOnlyList<ManagedComponent> components)
    {
        // The probe's own-DLSS answer (an OptiScaler/ folder does not count), the rule the install applies.
        var ineligible = _rtxEncore is null ? RtxEncoreDeploymentService.NotInstalledMessage : RtxEncoreDeploymentService.IneligibleReason(game, component.GameShipsDlss);
        if (ineligible is null) return false;
        component.Availability = ComponentAvailability.Unsupported;
        component.ActionAllowed = false;
        component.Note = ineligible;
        return true;
    }

    private void DecorateRtxEncore(DetectedGame game, ManagedComponent component, IReadOnlyList<ManagedComponent> components)
    {
        if (_rtxEncore is null || HideRtxEncore(game, component, components)) return;
        var name = _rtxEncore.ChosenName(game);
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
        {
            component.ActionAllowed = false;
            component.Note = RtxEncoreDeploymentService.PrefixMessage;
        }
        else if (RtxEncoreDeploymentService.ResolveBinaryDirectory(game) is null)
        {
            component.ActionAllowed = false;
            component.Note = RtxEncoreDeploymentService.BinaryFolderMessage;
        }
        else if (!_rtxEncore.OfferedNames(game).Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            component.ActionAllowed = false;
            component.Note = RtxEncoreDeploymentService.NameNotOfferedPrefix + name;
        }
        else if (_rtxEncore.NeuralRenderingChosen(game)
                 && NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, game, ManagedComponentKind.RtxEncore) is { } owner)
        {
            component.ActionAllowed = false;
            component.Note = NeuralRenderingRuntimeOwnership.RefusalFor(owner);
        }
        else
        {
            component.Note = RtxEncoreDeploymentService.NotYetConfirmedNote;
        }
    }

    // Upstream 2.8.1: the detail shows which RenoDX/Luma add-on file is installed and who made each mod. Credit what
    // RHI installed, or what it would install on a confirmed patch target; an external install, a stale record or an
    // unconfirmed target's not-installed row has no trustworthy author. Runs on the UI thread (Decorate is called
    // synchronously from selection/re-evaluation), so it never reads the game's files: it uses the RenoDX/Luma records
    // Decorate already loaded (the record store re-reads deployments.json only when its mtime/size changed), the
    // catalogue caches and what the probe found.
    private void DecorateModCredits(
        DetectedGame game,
        IReadOnlyList<ManagedComponent> components,
        LinuxDeploymentRecord? renoDxRecord,
        LinuxDeploymentRecord? lumaRecord)
    {
        foreach (var component in components)
        {
            if (component.Kind is not (ManagedComponentKind.RenoDX or ManagedComponentKind.LumaFramework)) continue;
            component.InstalledAddonFiles = [];
            component.Author = null;
            var installed = component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
            var record = !installed ? null
                : component.Kind == ManagedComponentKind.RenoDX ? renoDxRecord : lumaRecord;
            if (installed && record is null) continue;
            // Decorate has already turned an ineligible Luma row into Unsupported for a confirmed target, so an
            // Available row there is installable; without a confirmed target RHI installs nothing.
            if (!installed && !(component.Availability == ComponentAvailability.Available && game.HasConfirmedPatchTarget)) continue;

            component.InstalledAddonFiles = ModAuthorCredit.AddonFileNames(record, component.DetectedAddonFiles);
            component.Author = component.Kind == ManagedComponentKind.RenoDX
                ? ModAuthorCredit.ForRenoDx(_renodx.TryGetPreparedCompatibility(game, out var entry) ? entry : null, game)
                : ModAuthorCredit.ForLuma(record, _lumaFramework.GetCachedNamedMatch(game));
        }
    }

    // The refusal text when every link of the entry is outside the RenoDX host allow-list; null when one is usable.
    internal static string? UntrustedRenoDxLink(RenoDxModEntry entry)
    {
        var urls = new[] { entry.UrlFor(PeArchitecture.X64), entry.UrlFor(PeArchitecture.X86) }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (urls.Length == 0 || urls.Any(RenoDxDeploymentService.DownloadPolicy.IsTrusted)) return null;
        return new UntrustedDownloadException(urls[0], RenoDxDeploymentService.DownloadPolicy).Message;
    }

    // Every mutating entry point goes through this: an unconfirmed patch target, or records left
    // pointing at the folder the game was moved from (Refs #38), refuse before anything is touched.
    private readonly NrCostScalerService? _nrCostScaler; // w3/parity (#96)
    private readonly RtxEncoreDeploymentService? _rtxEncore; // w4/encore (#158)
    private readonly UpdateInclusionStore? _updateInclusion; // w5/update-policy (#178)
    private readonly DlssAutoFollowStore? _dlssFollow; // w5/update-policy (#177)
    private readonly DxvkHdrDeploymentService? _dxvkHdr; // w7/dxvk-hdr (#200)

    /// <summary>#178: what the game's update inclusion (and Settings, for every game) leaves out of Update All.</summary>
    public UpdateExclusion UpdateExclusionFor(DetectedGame game, ManagedComponentKind component)
        => _updateInclusion?.ExclusionFor(game, component) ?? UpdateExclusion.None;

    // #178: installed RHI-managed rows Update All takes part in say when the inclusion leaves them out (or that its file
    // cannot be read); the DLSS and Streamline rows say so only while Follow newest is on for that family and skips this
    // game (only the game's own choice applies there).
    private void DecorateUpdateInclusion(DetectedGame game, IReadOnlyList<ManagedComponent> components)
    {
        if (_updateInclusion is null) return;
        foreach (var component in components)
        {
            var installed = component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
            if (!installed) continue;
            if (component.Kind is ManagedComponentKind.Dlss or ManagedComponentKind.Streamline)
                component.UpdateExclusion = _updateInclusion.FollowsNewest(component.Kind)
                                            && _updateInclusion.ExclusionFor(game, component.Kind) is var exclusion
                                            && exclusion is UpdateExclusion.ThisGame or UpdateExclusion.Unreadable
                    ? exclusion
                    : UpdateExclusion.None;
            else if (component.ManagedByRhi && LinuxUpdateAllService.IsSupported(component.Kind))
                component.UpdateExclusion = _updateInclusion.ExclusionFor(game, component.Kind);
        }
    }

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

    /// <summary>
    /// #92: the ReShade row's (and profile Apply's) install on a game without a DLL ReShade whose API is Vulkan takes the
    /// Vulkan-layer path; that explicit choice is the only way the layer is installed (dependency installs never ask).
    /// </summary>
    private ReShadeInstallOptions ReShadeRowOptions(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        // #186: a new install takes the manifest's legacy version for the game; an installed ReShade keeps its own profile.
        var options = record is null ? ReShadeDeploymentService.DefaultOptionsFor(game) : _reshade.GetInstalledOptions(game);
        options.VulkanLayer = ReShadeVulkanLayer.IsLayerRecord(record) || (record is null && ReShadeVulkanLayer.IsVulkanTarget(game));
        return options;
    }

    /// <summary>#186: the versions the ReShade profile's Legacy channel offers (the RHI manifest's list, else 6.4.1).</summary>
    public Task<IReadOnlyList<string>> LegacyReShadeVersionsAsync(CancellationToken cancellationToken = default)
        => _reshade.LegacyVersionsAsync(cancellationToken);

    /// <summary>
    /// #92 (upstream PAR-D07): why a graphics-API override change must wait while ReShade is installed, or null. A
    /// Vulkan-layer ReShade on a game that would no longer be Vulkan, or a DLL ReShade on a game that would become Vulkan,
    /// would leave the wrong ReShade for the API; RHI refuses until ReShade is removed (upstream removes it on the switch;
    /// RHI never removes a component the user did not ask to remove). Changes within DirectX are never refused.
    /// </summary>
    public string? ReShadeApiSwitchRefusal(DetectedGame game, GraphicsApi? newOverride)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return null;
        var after = game.ShallowClone();
        after.UserGraphicsApiOverride = newOverride;
        var layer = ReShadeVulkanLayer.IsLayerRecord(record);
        if (layer && !ReShadeVulkanLayer.IsVulkanTarget(after)) return ReShadeVulkanLayer.ApiSwitchLayerInstalledMessage;
        if (!layer && ReShadeVulkanLayer.IsVulkanTarget(after) && !ReShadeVulkanLayer.IsVulkanTarget(game)) return ReShadeVulkanLayer.ApiSwitchDllInstalledMessage;
        return null;
    }

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

    // w3/parity (#96): the NR Cost Scaler's own install and removal, through the same guard and history as every component.
    public Task<ComponentOperationResult> InstallNrCostScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallGuard(game) is { } guard ? Task.FromResult(guard)
            : _nrCostScaler is null ? Task.FromResult(ComponentOperationResult.Fail(NrCostScalerService.NoOwnerMessage))
            : Track(game, ManagedComponentKind.NrCostScaler, ComponentUpdateAction.Install, () => _nrCostScaler.InstallAsync(game, cancellationToken));

    public Task<ComponentOperationResult> RemoveNrCostScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : _nrCostScaler is null ? Task.FromResult(ComponentOperationResult.Fail(NrCostScalerService.NotInstalledMessage))
            : Track(game, ManagedComponentKind.NrCostScaler, ComponentUpdateAction.Remove, () => _nrCostScaler.RemoveAsync(game, cancellationToken));

    /// <summary>The NR Cost Scaler service (null in builds and tests constructed without it).</summary>
    public NrCostScalerService? NrCostScaler => _nrCostScaler;

    /// <summary>w7/dxvk-hdr (#200): the game a ReShade install targets (a Vulkan view beside DX9 HDR; see ReShadeDeploymentService.ViewForDxvkHdr).</summary>
    private DetectedGame ReShadeViewForDxvkHdr(DetectedGame game) => _reshade is null ? game : _reshade.ViewForDxvkHdr(game);

    /// <summary>w7/dxvk-hdr (#200): the DX9 HDR service (null in builds and tests constructed without it).</summary>
    public DxvkHdrDeploymentService? DxvkHdr => _dxvkHdr;

    /// <summary>w7/dxvk-hdr (#200): the ⚙ preset choice, behind the patch-target guard (a conf rewrite, not an update: no history entry).</summary>
    public Task<ComponentOperationResult> SetDxvkHdrPresetAsync(DetectedGame game, int preset, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : _dxvkHdr is null ? Task.FromResult(ComponentOperationResult.Fail(DxvkHdrDeploymentService.NotInstalledMessage))
            : _dxvkHdr.SetPresetAsync(game, preset, cancellationToken);

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

    /// <summary>
    /// True when the game was moved and "Repair after move" has something to update: records, or entries in the
    /// per-game stores (this service's and <paramref name="libraryStores"/>) that still name the old folder (Refs #147).
    /// </summary>
    public bool NeedsRelocationRepair(DetectedGame game, IReadOnlyList<IGameStateStore>? libraryStores = null)
        => _relocation.NeedsRepair(game, RepairStores(libraryStores));

    /// <summary>The records "Repair after move" would update, for the confirmation shown before adopting.</summary>
    public IReadOnlyList<RelocatedRecord> RelocationPreview(DetectedGame game) => _relocation.Preview(game);

    /// <summary>
    /// Explicit repair after a move: re-points the game's records at its current folder once every
    /// owned file is verified there and carries its per-game state with them (Addon Manager files,
    /// preferences, opt-ins, update history, plus <paramref name="libraryStores"/> from the UI:
    /// favourites/hidden/edits/HDR and the preference stores it owns). All or nothing.
    /// </summary>
    public RelocationRepairResult RepairAfterMove(DetectedGame game, IReadOnlyList<IGameStateStore>? libraryStores = null)
        => _relocation.Repair(game, RepairStores(libraryStores), out _);

    /// <summary>
    /// Every per-game store "Repair after move" rewrites: the ones this service owns plus <paramref name="libraryStores"/>.
    /// A per-game store missing here loses its entries when the game moves (a contract test walks the app's object graph).
    /// </summary>
    internal IReadOnlyList<IGameStateStore> RepairStores(IReadOnlyList<IGameStateStore>? libraryStores = null)
    {
        IGameStateStore?[] owned =
        [
            _addonManager, _neuralVersionPins, _updateLog,
            _dlssg2030?.GameState, _rtx40Mfg?.GameState, _dlssEnablerStandalone?.GameState,
            _shortFuse?.GameState, _lumaPolicy?.GameState,
            _reshade?.DllNames, // w3/dllnames (#120); RTX Encore's name and NR choice live in the same store (#158)
            // w5/update-policy rows
            _updateInclusion, // #178 per-game update inclusion
            _dlssFollow, // #177 Follow newest: games a pass skipped, held per game
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            _shaderSets, // #179 per-game Global / Select / Off and Select lists
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
        ];
        return owned.OfType<IGameStateStore>().Concat(libraryStores ?? []).ToArray();
    }

    private IReadOnlyList<string> BuildDependencyIndex(DetectedGame game, IReadOnlyList<string>? indexedFiles = null)
    {
        if (indexedFiles is not null) return indexedFiles;
        var root = Directory.Exists(game.InstallPath)
            ? game.InstallPath
            : game.BinaryDirectory;
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? Array.Empty<string>()
            : GameFileIndexer.Build(root, 8);
    }

    private enum DependencySatisfaction
    {
        Missing,
        Managed,
        External,
        BrokenManaged
    }

    private DependencySatisfaction InspectDependency(
        DetectedGame game,
        ManagedComponentKind target,
        ManagedComponentKind dependency,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
    {
        var record = _records.Find(game, dependency);
        if (record is not null)
        {
            var hasPayload = record.Files.Any(file =>
                !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path));
            return hasPayload ? DependencySatisfaction.Managed : DependencySatisfaction.BrokenManaged;
        }

        if (LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(target, dependency))
            return DependencySatisfaction.Missing;

        if (dependency == ManagedComponentKind.ReShade &&
            !string.IsNullOrWhiteSpace(game.BinaryDirectory) &&
            GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return DependencySatisfaction.External;

        if (dependency == ManagedComponentKind.REFramework &&
            ComponentProbe.Probe(game, indexedFiles, cancellationToken)
                .Any(component => component.Kind == ManagedComponentKind.REFramework &&
                                  component.Availability == ComponentAvailability.Installed))
            return DependencySatisfaction.External;

        return DependencySatisfaction.Missing;
    }

    /// <summary>
    /// The managed dependencies an install of <paramref name="target"/> would add automatically right now, in install
    /// order (nested first: RE Framework before ReShade on an RE Engine game). Read from the same plan the install
    /// executes (<see cref="PlanManagedDependencies"/>), without touching anything: dependencies already managed or
    /// supplied externally are skipped, and when the install would refuse (a broken managed dependency anywhere in the
    /// graph, a conflicting component) nothing is added. Used to show the manifest warnings of those components before
    /// a user-initiated install (upstream 2.8.0 #131). May index the game folder (RE Framework check): call it off the
    /// UI thread.
    /// </summary>
    public IReadOnlyList<ManagedComponentKind> PendingAutoDependencies(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, target).Count == 0)
            return Array.Empty<ManagedComponentKind>();

        IReadOnlyList<string>? index = null;
        var plan = PlanManagedDependencies(game, target, () => index ??= BuildDependencyIndex(game, indexedFiles), cancellationToken);
        return plan.Refusal is null
            ? plan.Steps.Select(step => step.Dependency).ToArray()
            : Array.Empty<ManagedComponentKind>();
    }

    /// <summary>One automatic dependency to install, and the component that requires it.</summary>
    internal readonly record struct ManagedDependencyStep(ManagedComponentKind Parent, ManagedComponentKind Dependency);

    /// <summary>What an install of a target does to its managed dependencies: the steps in install order, or a refusal.</summary>
    internal sealed record ManagedDependencyPlan(IReadOnlyList<ManagedDependencyStep> Steps, ComponentOperationResult? Refusal);

    /// <summary>
    /// The single automatic-dependency walk shared by <see cref="PendingAutoDependencies"/> and the install. Reads the
    /// record store and the game folder only. Order: nested dependencies first, each Missing dependency once.
    /// </summary>
    internal ManagedDependencyPlan PlanManagedDependencies(
        DetectedGame game,
        ManagedComponentKind target,
        Func<IReadOnlyList<string>> index,
        CancellationToken cancellationToken)
    {
        var refusal = DependencyConflictGuard(game, target);
        if (refusal is not null) return new ManagedDependencyPlan(Array.Empty<ManagedDependencyStep>(), refusal);

        var steps = new List<ManagedDependencyStep>();
        var visiting = new HashSet<ManagedComponentKind>();
        refusal = Walk(target);
        return refusal is null
            ? new ManagedDependencyPlan(steps, null)
            : new ManagedDependencyPlan(Array.Empty<ManagedDependencyStep>(), refusal);

        ComponentOperationResult? Walk(ManagedComponentKind component)
        {
            if (!visiting.Add(component))
                return ComponentOperationResult.Fail($"Dependency cycle detected while preparing {DisplayDependencyName(component)}.");
            try
            {
                foreach (var dependency in LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, component))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var satisfaction = InspectDependency(game, component, dependency, DependencyFiles(dependency, index), cancellationToken);
                    if (satisfaction == DependencySatisfaction.BrokenManaged)
                        return ComponentOperationResult.Fail(
                            $"A dependência gerenciada {DisplayDependencyName(dependency)} possui registro de ownership, mas seu payload não está presente. Repare ou remova esse estado antes de instalar {DisplayDependencyName(component)}.");

                    // An external dependency is a preservation boundary. It may satisfy ordinary add-ons,
                    // but RHI does not recurse into or adopt the external stack. RHI-managed dependencies,
                    // on the other hand, must keep their own transitive dependency graph valid.
                    if (satisfaction == DependencySatisfaction.External) continue;

                    var nested = Walk(dependency);
                    if (nested is not null) return nested;
                    if (satisfaction == DependencySatisfaction.Missing && !steps.Any(step => step.Dependency == dependency))
                        steps.Add(new ManagedDependencyStep(component, dependency));
                }
                return null;
            }
            finally
            {
                visiting.Remove(component);
            }
        }
    }

    // Only the RE Framework check reads the file index; other dependencies are checked by record or fixed file names.
    private static IReadOnlyList<string> DependencyFiles(ManagedComponentKind dependency, Func<IReadOnlyList<string>> index)
        => dependency == ManagedComponentKind.REFramework ? index() : Array.Empty<string>();

    internal async Task<ComponentOperationResult> InstallWithManagedDependenciesAsync(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles,
        Func<Task<ComponentOperationResult>> targetInstaller,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? index = null;
        IReadOnlyList<string> Index() => index ??= BuildDependencyIndex(game, indexedFiles);
        ManagedDependencyPlan plan;
        try
        {
            plan = PlanManagedDependencies(game, target, Index, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ComponentOperationResult.Fail("Instalação cancelada durante a resolução automática de dependências.");
        }
        catch (Exception ex)
        {
            return ComponentOperationResult.Fail($"Falha inesperada durante a resolução automática de dependências: {ex.Message}");
        }
        if (plan.Refusal is not null) return plan.Refusal;

        var installedThisAttempt = new List<ManagedComponentKind>();
        var dependencyChanges = new List<string>();

        ComponentOperationResult dependencies;
        try
        {
            dependencies = await InstallPlannedDependenciesAsync(
                game, plan, Index, installedThisAttempt, dependencyChanges, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage("Instalação cancelada durante a resolução automática de dependências.", rollback));
        }
        catch (Exception ex)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage($"Falha inesperada durante a resolução automática de dependências: {ex.Message}", rollback));
        }

        if (!dependencies.Success)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage(dependencies.Message, rollback));
        }

        ComponentOperationResult result;
        try
        {
            result = await targetInstaller().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage("Instalação cancelada antes de concluir o componente solicitado.", rollback));
        }
        catch (Exception ex)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage($"Falha inesperada ao instalar {DisplayDependencyName(target)}: {ex.Message}", rollback));
        }

        if (!result.Success)
        {
            var rollback = await RollBackAutoDependenciesAsync(game, installedThisAttempt, CancellationToken.None).ConfigureAwait(false);
            return ComponentOperationResult.Fail(AppendRollbackMessage(result.Message, rollback));
        }

        result = await ApplyAddonSetAfterReShadeAsync(game, target, installedThisAttempt, result, cancellationToken).ConfigureAwait(false); // w7/shader-sets hook (#179)
        if (installedThisAttempt.Count == 0) return result;

        var changed = dependencyChanges
            .Concat(result.ChangedFiles ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var names = string.Join(" → ", installedThisAttempt.Select(DisplayDependencyName));
        // Keep the target's structural flags (e.g. FromCachedSource) along with its message.
        return result with
        {
            Message = $"Dependências instaladas automaticamente ({names}). {result.Message}",
            ChangedFiles = changed
        };
    }

    // Executes a plan from PlanManagedDependencies step by step. Each step is re-checked right before it runs, since
    // an earlier step (or the user, meanwhile) may have changed what is there.
    private async Task<ComponentOperationResult> InstallPlannedDependenciesAsync(
        DetectedGame game,
        ManagedDependencyPlan plan,
        Func<IReadOnlyList<string>> index,
        List<ManagedComponentKind> installedThisAttempt,
        List<string> dependencyChanges,
        CancellationToken cancellationToken)
    {
        foreach (var (parent, dependency) in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var satisfaction = InspectDependency(game, parent, dependency, DependencyFiles(dependency, index), cancellationToken);
            if (satisfaction is DependencySatisfaction.Managed or DependencySatisfaction.External) continue;
            if (satisfaction == DependencySatisfaction.BrokenManaged)
                return ComponentOperationResult.Fail(
                    $"A dependência gerenciada {DisplayDependencyName(dependency)} ficou inconsistente durante a resolução automática.");

            var before = _records.Find(game, dependency);
            var install = await InstallDependencyCoreAsync(game, dependency, cancellationToken).ConfigureAwait(false);
            if (!install.Success)
                return ComponentOperationResult.Fail(
                    $"Não foi possível instalar automaticamente a dependência {DisplayDependencyName(dependency)} exigida por {DisplayDependencyName(parent)}: {install.Message}");

            if (before is null && _records.Find(game, dependency) is not null)
                installedThisAttempt.Add(dependency);
            if (install.ChangedFiles is { Count: > 0 })
                dependencyChanges.AddRange(install.ChangedFiles.Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        return ComponentOperationResult.Ok("Dependencies ready.");
    }

    private Task<ComponentOperationResult> InstallDependencyCoreAsync(
        DetectedGame game,
        ManagedComponentKind dependency,
        CancellationToken cancellationToken)
        => dependency switch
        {
            ManagedComponentKind.ReShade => InstallReShadeForShaderSetAsync(game, ReShadeDeploymentService.DefaultOptionsFor(game), update: false, cancellationToken), // w7/shader-sets hook (#179 review major 2)
            ManagedComponentKind.REFramework => _reFramework.InstallAsync(game, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail(
                $"{DisplayDependencyName(dependency)} is declared as an automatic dependency but has no dependency installer."))
        };

    private async Task<string?> RollBackAutoDependenciesAsync(
        DetectedGame game,
        IReadOnlyList<ManagedComponentKind> installedThisAttempt,
        CancellationToken cancellationToken)
    {
        if (installedThisAttempt.Count == 0) return null;

        var failures = new List<string>();
        for (var index = installedThisAttempt.Count - 1; index >= 0; index--)
        {
            var dependency = installedThisAttempt[index];

            // If ReShade was also created by this attempt but could not be rolled back, keep the
            // newly created RE Framework dependency in place rather than leaving managed ReShade
            // on an RE Engine title with its required framework removed underneath it. If ReShade
            // predated this attempt, removing a newly added RE Framework restores the exact prior
            // state and remains the transactional behavior.
            if (dependency == ManagedComponentKind.REFramework &&
                installedThisAttempt.Contains(ManagedComponentKind.ReShade) &&
                _records.Find(game, ManagedComponentKind.ReShade) is not null)
            {
                failures.Add("RE Framework: preserved because the ReShade dependency created by this attempt could not be rolled back safely.");
                continue;
            }

            ComponentOperationResult result;
            try
            {
                result = dependency switch
                {
                    ManagedComponentKind.ReShade => await _reshade.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                    ManagedComponentKind.REFramework => await _reFramework.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                    _ => ComponentOperationResult.Fail("No rollback handler is available.")
                };
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{DisplayDependencyName(dependency)}: {ex.Message}");
                continue;
            }

            if (!result.Success) failures.Add($"{DisplayDependencyName(dependency)}: {result.Message}");
        }

        return failures.Count == 0
            ? "As dependências instaladas nesta tentativa foram revertidas."
            : "Rollback parcial das dependências: " + string.Join(" | ", failures);
    }

    private static string AppendRollbackMessage(string message, string? rollback)
        => string.IsNullOrWhiteSpace(rollback) ? message : $"{message} {rollback}";

    /// <summary>
    /// Fixed text before "component: file" when Luma/ShortFuse would install their ReShade beside a ReShade RHI did not
    /// install (PT-BR presentation pair).
    /// </summary>
    public const string ExternalReShadeBlocksDependencyPrefix =
        "This component needs ReShade installed by RHI, but a ReShade RHI Linux did not install is already in this game folder; it was left alone and nothing was installed. Remove that ReShade first, or keep using it without this component. Component and file: ";

    private ComponentOperationResult? DependencyConflictGuard(DetectedGame game, ManagedComponentKind target)
    {
        // w3/dllnames (#91): a ReShade add-on cannot load in the No Addons build.
        if (LinuxComponentDependencyPolicy.IsReShadeAddon(target) && ReShadeDeploymentService.LacksAddonSupport(_records.Find(game, ManagedComponentKind.ReShade)))
            return ComponentOperationResult.Fail(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + DisplayDependencyName(target));

        if (target == ManagedComponentKind.ReLimiter && _records.Find(game, ManagedComponentKind.DisplayCommander) is not null)
            return ComponentOperationResult.Fail("Display Commander já está ativo. ReLimiter e Display Commander são mutuamente exclusivos.");
        if (target == ManagedComponentKind.DisplayCommander && _records.Find(game, ManagedComponentKind.ReLimiter) is not null)
            return ComponentOperationResult.Fail("ReLimiter já está ativo. Display Commander e ReLimiter são mutuamente exclusivos.");

        if (target == ManagedComponentKind.RenoDX && _records.Find(game, ManagedComponentKind.LumaFramework) is not null &&
            !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
            return ComponentOperationResult.Fail("Luma Framework já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.");
        if (target == ManagedComponentKind.LumaFramework && _records.Find(game, ManagedComponentKind.RenoDX) is not null &&
            !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible)
            return ComponentOperationResult.Fail("RenoDX HDR já está ativo e este jogo não permite coexistência RenoDX/Luma pelo manifesto. Nenhuma dependência foi alterada.");

        if (target == ManagedComponentKind.MfgAdaUnlock && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock.");
        if (target == ManagedComponentKind.MfgAdaUnlock && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock.");

        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.OptiScaler) is not null)
            return ComponentOperationResult.Fail("OptiScaler já está ativo. ShortFuse não reescreve automaticamente essa cadeia ReShade/OptiScaler.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.");
        // w4/encore (#158): never switch away from RTX Encore's Neural Rendering file; the user removes it first. Refused
        // here, before any dependency (ReShade) is installed.
        if (target is ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder
            && NeuralRenderingRuntimeOwnership.RtxEncoreOwner(_records, game) is { } encoreOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(encoreOwner));

        if ((target is ManagedComponentKind.LumaFramework or ManagedComponentKind.ShortFuse) &&
            _records.Find(game, ManagedComponentKind.ReShade) is null &&
            !string.IsNullOrWhiteSpace(game.BinaryDirectory) &&
            Directory.Exists(game.BinaryDirectory) &&
            GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
        {
            // dllnames re-review nit 4: the same plain next step as the direct install's refusal, with the file.
            return ComponentOperationResult.Fail(ExternalReShadeBlocksDependencyPrefix +
                $"{DisplayDependencyName(target)}: {ProxyPayloadLocator.FindReShadeOnDisk(game.BinaryDirectory) ?? game.BinaryDirectory}");
        }

        if (target == ManagedComponentKind.Dlss5Feeder &&
            NeuralRenderingRuntimeOwnership.OptiScalerOwner(_records, game) is { } nrOwner)
            return ComponentOperationResult.Fail(NeuralRenderingRuntimeOwnership.RefusalFor(nrOwner));

        return null;
    }

    public static string DisplayDependencyName(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReShade => "ReShade",
        ManagedComponentKind.REFramework => "RE Framework",
        ManagedComponentKind.RenoDX => "RenoDX HDR",
        ManagedComponentKind.RenoDXUpgrade => "RenoDX Upgrade",
        ManagedComponentKind.ReLimiter => "ReLimiter",
        ManagedComponentKind.DisplayCommander => "Display Commander",
        ManagedComponentKind.MfgAdaUnlock => "MFG Ada Unlock",
        ManagedComponentKind.Dlss5Feeder => "DLSS5 Feeder",
        ManagedComponentKind.Dlss5Dx11Bridge => "DLSS5 DX11 Bridge",
        ManagedComponentKind.ShortFuse => "ShortFuse",
        ManagedComponentKind.DofFix => "Universal UE DOF Fix",
        ManagedComponentKind.LumaFramework => "Luma Framework",
        ManagedComponentKind.OptiScaler => "OptiScaler",
        ManagedComponentKind.UltimateAsiLoader => "Ultimate ASI Loader",
        ManagedComponentKind.Dlssg2030Unlock => "RTX 20 FG Unlock",
        ManagedComponentKind.DlssEnablerStandalone => "DLSS Enabler (Standalone)",
        ManagedComponentKind.Rtx40MfgStandalone => "Universal MFG",
        ManagedComponentKind.NrCostScaler => NrCostScalerService.DisplayName, // w3/parity (#96)
        ManagedComponentKind.RtxEncore => RtxEncoreDeploymentService.DisplayName, // w4/encore (#158)
        ManagedComponentKind.ReShadeAddonManager => ReShadeAddonManagerService.DisplayName, // #194
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        ManagedComponentKind.DxvkHdr => DxvkHdrDeploymentService.DisplayName,
        _ => kind.ToString()
    };

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

    public IReadOnlyList<ReShadeShaderPackInfo> AvailableReShadeShaderPacks => _reshade.AvailableShaderPacks;
    /// <summary>#194 (upstream CacheAllShaders): stages every shader pack not in the cache yet; cache only.</summary>
    public Task<int> PrefetchShaderPacksAsync(CancellationToken cancellationToken = default) => _reshade.PrefetchShaderPacksAsync(cancellationToken);
    public string CustomShaderRoot => _reshade.CustomShaderRoot;
    public string CustomAddonRoot => _addonManager.CustomRoot;

    public ComponentOperationResult ImportCustomAddonFile(string sourcePath)
        => _addonManager.ImportCustomAddonFile(sourcePath);

    /// <summary>#195: a dropped or watched RenoDX add-on file installed as the game's RenoDX mod (install guards, update history).</summary>
    public Task<ComponentOperationResult> InstallLocalRenoDxAddonAsync(DetectedGame game, string addonPath, CancellationToken cancellationToken = default)
        => (InstallGuard(game) ?? AddonOffGuard(game, ManagedComponentKind.RenoDX)) is { } guard
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Install, () => _renodx.InstallLocalAddonAsync(game, addonPath, cancellationToken));

    /// <summary>#195: a dropped ReShade add-on that is not RenoDX's, installed into the game.</summary>
    public const string DroppedAddonInstalledPrefix = "Add-on installed for this game; Shaders/Add-ons lists it (untick it there to take it out): ";
    public const string DroppedAddon32BitOn64BitMessage = "This add-on is 32-bit and the game is 64-bit; it needs the add-on's .addon64 file.";
    public const string DroppedAddon64BitOn32BitMessage = "This add-on is 64-bit and the game is 32-bit; it needs the add-on's .addon32 file.";

    /// <summary>
    /// #195 (upstream <c>ProcessDroppedAddon</c> for an add-on that is not RenoDX's: copied into the game beside it): the
    /// file is imported into Custom Add-ons (never over a different one) and added to the game's Shaders/Add-ons selection,
    /// whose journaled apply places it, records it and takes it out again when it is unticked. RenoDX is left alone.
    /// </summary>
    public async Task<ComponentOperationResult> InstallDroppedAddonForGameAsync(DetectedGame game, string addonPath, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        if (!string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) && File.Exists(addonPath)
            && GameArchitecture.Of(game) is var architecture and (PeArchitecture.X86 or PeArchitecture.X64)
            && PeArchitectureDetector.Detect(addonPath) is var addon and (PeArchitecture.X86 or PeArchitecture.X64) && addon != architecture)
            return ComponentOperationResult.Fail(addon == PeArchitecture.X86 ? DroppedAddon32BitOn64BitMessage : DroppedAddon64BitOn32BitMessage);
        var imported = _addonManager.ImportCustomAddonFile(addonPath);
        if (!imported.Success || imported.ChangedFiles is not [var copy, ..]) return imported;
        var id = "custom-" + Path.GetFileNameWithoutExtension(copy);
        // w7 integration (#195 × #179): the add-ons the game gets now plus this one, saved as the game's own list ("Choose for
        // this game") when that differs from what it gets, so a later global edit never takes the add-on out again and the
        // global set itself is never edited. Without the shader-set store the recorded selection is the game's list.
        var selected = EffectiveAddons(game) is { HeldByNoAddons: false } effective
            ? effective.Ids
            : (await _addonManager.GetSnapshotAsync(game, cancellationToken).ConfigureAwait(false)).SelectedIds;
        IReadOnlyList<string> wanted = selected.Contains(id, StringComparer.OrdinalIgnoreCase) ? selected : [.. selected, id];
        if (RecordExplicitAddonChoice(game, wanted) is { } notSaved) return ComponentOperationResult.Fail(notSaved);
        var applied = await ApplyReShadeAddonSelectionAsync(game, wanted, cancellationToken).ConfigureAwait(false);
        var placed = Path.Combine(RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!), Path.GetFileName(copy));
        return applied.Success ? ComponentOperationResult.Ok(DroppedAddonInstalledPrefix + placed, copy, placed) : applied;
    }

    /// <summary>#195: the game's RenoDX record (the drop dialog names the add-on it replaces).</summary>
    public LinuxDeploymentRecord? GetRenoDxRecord(DetectedGame game) => _records.Find(game, ManagedComponentKind.RenoDX);

    /// <summary>#195: the add-on file name the RenoDX catalogue matched for the game this session (its snapshot), else null.</summary>
    public string? RenoDxCatalogueFileName(DetectedGame game)
        => _renodx.TryGetPreparedCompatibility(game, out var mod) && mod?.UrlFor(GameArchitecture.Of(game)) is { } url
           && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : null;

    public Task<IReadOnlyList<ReShadeShaderPackInfo>> RefreshReShadeShaderPackCatalogAsync(
        CancellationToken cancellationToken = default)
        => _reshade.RefreshShaderPackCatalogAsync(cancellationToken);

    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> GetReShadeShaderPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
        => _reshade.ListShaderPackFilesAsync(packId, cancellationToken);

    public void SetReShadeShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => _reshade.SetShaderPackExcludedFiles(packId, excluded);

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
            ? _addonManager.ApplySelectionAsync(game, selectedIds, cancellationToken)
            : Task.FromResult(guard);
    }

    public bool IsReShadeManaged(DetectedGame game) => _reshade.IsManaged(game);

    // w3/dllnames (#120): the per-game ReShade DLL name.
    /// <summary>The store of per-game proxy DLL names chosen before install.</summary>
    public DllNamePreferenceStore ReShadeDllNames => _reshade.DllNames;

    /// <summary>
    /// The names OptiScaler's DLL-name dropdown offers on <paramref name="game"/> (#172): OptiScaler's names except those a
    /// file of another RHI component sits under (RHI's own ReShade moves aside for OptiScaler, so its name stays offered).
    /// </summary>
    public IReadOnlyList<string> OptiScalerProxyNamesFor(DetectedGame game)
    {
        var held = OptiScalerNamesHeld(game);
        return OptiScalerDeploymentService.ProxyNames.Where(name => !held.ContainsKey(name)).ToArray();
    }

    /// <summary>
    /// The name "Automatic" stands for on <paramref name="game"/> (#172): <see cref="OptiScalerDeploymentService.DefaultProxyName"/>,
    /// or the first of OptiScaler's names no other RHI component holds when that one is held (dxgi.dll again when every
    /// name is held; the install then refuses with the holder's name).
    /// </summary>
    public string OptiScalerAutomaticProxyName(DetectedGame game)
        => OptiScalerProxyNamesFor(game).FirstOrDefault() ?? OptiScalerDeploymentService.DefaultProxyName;

    /// <summary>
    /// The name OptiScaler is (or will be) installed under on <paramref name="game"/> (#172): the recorded one while it is
    /// installed, else the saved choice while it is still offered, else <see cref="OptiScalerAutomaticProxyName"/>.
    /// </summary>
    public string OptiScalerProxyNameFor(DetectedGame game)
    {
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is { } record) return OptiScalerDeploymentService.GetProxyName(record);
        // No ReShade service (a test wiring OptiScaler alone) = no saved choice.
        return _reshade?.DllNames.GetOptiScaler(game) is { } chosen && OptiScalerProxyNamesFor(game).Contains(chosen, StringComparer.OrdinalIgnoreCase)
            ? chosen
            : OptiScalerAutomaticProxyName(game);
    }

    /// <summary>
    /// The saved OptiScaler name the next install skips because another RHI component holds it, with that component's
    /// name ("winmm.dll (Ultimate ASI Loader)"); null when OptiScaler is installed, nothing is saved or the name is free.
    /// </summary>
    public string? OptiScalerSkippedChoice(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is null
           && _reshade?.DllNames.GetOptiScaler(game) is { } chosen
           && OptiScalerNamesHeld(game).TryGetValue(chosen, out var holder)
            ? $"{chosen} ({DisplayDependencyName(holder)})"
            : null;

    /// <summary>Appended to an OptiScaler install that skipped the saved DLL name (#172); saved name and component follow.</summary>
    public const string OptiScalerSavedNameSkippedPrefix =
        " The DLL name saved for OptiScaler is used by another component RHI installed on this game, so OptiScaler was installed under the automatic name instead. Saved name and component: ";

    // OptiScaler's names that a file of another RHI component sits under in the game folder, with that component.
    private Dictionary<string, ManagedComponentKind> OptiScalerNamesHeld(DetectedGame game)
    {
        var held = new Dictionary<string, ManagedComponentKind>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory)) return held;
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.BinaryDirectory));
        foreach (var record in _records.FindAll(game).Where(record => record.Component is not (ManagedComponentKind.OptiScaler or ManagedComponentKind.ReShade)))
            foreach (var file in record.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)))
            {
                var full = Path.GetFullPath(file.Path);
                if (string.Equals(Path.GetDirectoryName(full), directory, StringComparison.Ordinal)
                    && OptiScalerDeploymentService.ProxyNames.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
                    held.TryAdd(Path.GetFileName(full), record.Component);
            }
        return held;
    }

    // Every OptiScaler install path (#172): the name OptiScalerProxyNameFor decides, and a plain sentence when the saved
    // choice was skipped because another RHI component holds it.
    private async Task<ComponentOperationResult> InstallOptiScalerUnderChosenNameAsync(
        DetectedGame game, OptiScalerVariant variant, string? nightlyBuild, CancellationToken cancellationToken)
    {
        var skipped = OptiScalerSkippedChoice(game);
        var result = await _optiscaler.InstallAsync(game, variant, nightlyBuild, OptiScalerProxyNameFor(game), cancellationToken).ConfigureAwait(false);
        return result.Success && skipped is not null
            ? result with { Message = result.Message + OptiScalerSavedNameSkippedPrefix + skipped }
            : result;
    }

    /// <summary>The name the next ReShade install on <paramref name="game"/> uses, and why; null when its API has no proxy.</summary>
    public ProxyNameResolution? ResolveReShadeProxyName(DetectedGame game) => _reshade.ResolveProxyName(game);

    /// <summary>The file name RHI's ReShade is installed under on <paramref name="game"/>, or null when RHI has not installed it.</summary>
    public string? InstalledReShadeProxyName(DetectedGame game)
        => ProxyPayloadLocator.ReShadePayload(_records.Find(game, ManagedComponentKind.ReShade)) is { } payload ? Path.GetFileName(payload.Path) : null;

    // w7/reshade (#196): in-place DLL rename and Reset DLL names (upstream DllOverrideService, Reset DLL Names).
    /// <summary>The name ReShade's "Automatic" stands for on <paramref name="game"/> (the saved choice left out), or null.</summary>
    public string? ReShadeAutomaticProxyName(DetectedGame game) => _reshade.AutomaticProxyName(game);

    /// <summary>
    /// What the ReShade DLL-name dropdown offers while RHI's ReShade is installed (#196): the offered names it can be
    /// renamed to now, or the refusal that keeps its name.
    /// </summary>
    public (IReadOnlyList<string> Targets, string? Refusal) ReShadeRenameOptions(DetectedGame game)
        => _reshade.RenameOptions(game, ProxyDllNames.OfferedFor(GameArchitecture.Of(game) == PeArchitecture.X86));

    /// <summary>What OptiScaler's DLL-name dropdown offers while RHI's OptiScaler is installed (#196).</summary>
    public (IReadOnlyList<string> Targets, string? Refusal) OptiScalerRenameOptions(DetectedGame game)
        => _optiscaler.RenameOptions(game, OptiScalerProxyNamesFor(game));

    /// <summary>
    /// Renames RHI's installed ReShade on <paramref name="game"/> to <paramref name="name"/> (null = Automatic) in place, in
    /// one journaled operation with its Wine override, record and saved choice (#196).
    /// </summary>
    public async Task<ComponentOperationResult> RenameReShadeProxyAsync(DetectedGame game, string? name, CancellationToken cancellationToken = default)
        => InstallGuard(game) ?? await _reshade.RenameProxyAsync(game, name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Renames RHI's installed OptiScaler on <paramref name="game"/> to <paramref name="name"/> (null = Automatic,
    /// <see cref="OptiScalerAutomaticProxyName"/>) in place (#196); the same name only saves the choice.
    /// </summary>
    public async Task<ComponentOperationResult> RenameOptiScalerProxyAsync(DetectedGame game, string? name, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        if (_optiscaler.RenameBlocker(game) is { } blocked) return ComponentOperationResult.Fail(blocked);
        var installed = OptiScalerDeploymentService.GetProxyName(_records.Find(game, ManagedComponentKind.OptiScaler)!);
        var target = name ?? OptiScalerAutomaticProxyName(game);
        var choice = name is null ? null : target;
        try
        {
            if (target.Equals(installed, StringComparison.OrdinalIgnoreCase))
            {
                _reshade?.DllNames.SetOptiScaler(game, choice);
                return ComponentOperationResult.Ok(ProxyRename.NameKeptPrefix + installed);
            }
            var staged = _reshade?.DllNames.StageChoices(game, choices => choices.OptiScaler = choice);
            return await _optiscaler.RenameProxyAsync(game, target, staged, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ComponentOperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Whether "Reset DLL names" has anything to do on <paramref name="game"/> (#196): a saved ReShade or OptiScaler name,
    /// or RHI's ReShade / OptiScaler installed under another name than its automatic one that Reset can rename back. A name
    /// another component decides (the OptiScaler chain's ReShade64.dll, ShortFuse, the DirectX 9 translation, an XNA GAC
    /// install; review 1) is not Reset's to change, so it does not count. Reads the ledger and the choice file only.
    /// </summary>
    public bool HasDllNamesToReset(DetectedGame game)
        => _reshade?.DllNames.HasReShadeOrOptiScalerChoice(game) == true
           || ReShadeNameToReset(game) || OptiScalerNameToReset(game);

    // RHI's ReShade sits under another name than its automatic one and nothing else decides its name (review 1).
    private bool ReShadeNameToReset(DetectedGame game)
        => _reshade is { } reShade && InstalledReShadeProxyName(game) is { } installed && reShade.AutomaticProxyName(game) is { } automatic
           && !installed.Equals(automatic, StringComparison.OrdinalIgnoreCase)
           && reShade.RenameBlocker(game) is null;

    // RHI's OptiScaler sits under another name than its automatic one and may be renamed in place (review 1).
    private bool OptiScalerNameToReset(DetectedGame game)
        => _records.Find(game, ManagedComponentKind.OptiScaler) is { } optiScaler
           && !OptiScalerDeploymentService.GetProxyName(optiScaler).Equals(OptiScalerAutomaticProxyName(game), StringComparison.OrdinalIgnoreCase)
           && _optiscaler.RenameBlocker(game) is null;

    /// <summary>
    /// "Reset DLL names" (#196, upstream): every ReShade / OptiScaler DLL-name choice of <paramref name="game"/> goes back to
    /// Automatic, and an installed one under another name is renamed back to its automatic name in place (OptiScaler first,
    /// as upstream; each its own journaled rename). A rename that is refused keeps that component's name and choice; the
    /// result says so, the other one still goes back.
    /// </summary>
    public async Task<ComponentOperationResult> ResetDllNamesAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (InstallGuard(game) is { } guard) return guard;
        var renamed = new List<string>();
        var refused = new List<string>();
        async Task RenameAsync(Func<Task<ComponentOperationResult>> rename)
        {
            var result = await rename().ConfigureAwait(false);
            (result.Success ? renamed : refused).Add(result.Message);
        }
        try
        {
            // A name another component decides stays; only the saved choice goes back to automatic (review 1).
            if (OptiScalerNameToReset(game))
                await RenameAsync(() => RenameOptiScalerProxyAsync(game, null, cancellationToken)).ConfigureAwait(false);
            else _reshade?.DllNames.SetOptiScaler(game, null);
            if (_reshade is { } reShadeService)
            {
                if (ReShadeNameToReset(game))
                    await RenameAsync(() => reShadeService.RenameProxyAsync(game, null, cancellationToken)).ConfigureAwait(false);
                else reShadeService.DllNames.SetReShade(game, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refused.Add(ex.Message);
        }
        return refused.Count > 0
            ? ComponentOperationResult.Fail(string.Join(" ", refused.Concat(renamed)))
            : ComponentOperationResult.Ok(string.Join(" ", renamed.Prepend(DllNamesResetMessage)));
    }

    /// <summary>The line of a Reset DLL names that put everything back (PT-BR presentation pair); rename lines follow.</summary>
    public const string DllNamesResetMessage = "Every DLL name of this game is back to automatic.";

    /// <summary>
    /// The profile the ReShade section and the Shaders/Add-ons dialog start from: the installed profile, with the packs the
    /// game gets now (#179, its effective set; review major 1: a new game's empty record would otherwise read as an explicit
    /// "no packs" choice and turn it into Select on an untouched Apply).
    /// </summary>
    public ReShadeInstallOptions GetReShadeOptions(DetectedGame game)
    {
        var options = _reshade.GetInstalledOptions(game);
        if (EffectiveShaderPacks(game) is { } effective) options.ShaderPackIds = effective.ToArray(); // w7/shader-sets hook (#179)
        return options;
    }

    public async Task<ComponentOperationResult> ApplyReShadeAsync(
        DetectedGame game,
        ReShadeInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        if (RecordExplicitShaderChoice(game, options.ShaderPackIds) is { } notSaved) return ComponentOperationResult.Fail(notSaved); // w7/shader-sets hook (#179): a changed list makes the game Select
        game = ReShadeViewForDxvkHdr(game); // w7/dxvk-hdr hook (#200): ReShade beside DX9 HDR takes the Vulkan layer
        // w5/reshade-core (#92): Apply on a Vulkan game is the explicit layer choice too; its shaders need no D3DCompiler.
        options.VulkanLayer = options.VulkanLayer || ReShadeRowOptions(game).VulkanLayer;
        return await Track(game, ManagedComponentKind.ReShade, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.ReShade,
            indexedFiles: null,
            async () =>
            {
                if (!options.VulkanLayer && options.ShaderPackIds.Count > 0 && string.IsNullOrWhiteSpace(options.D3DCompiler47Path))
                {
                    var compiler = await EnsureNativeD3DCompilerDependencyAsync(
                        game,
                        explicitCompilerPath: null,
                        requiredBy: "ReShade shader packs",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (compiler is not null) return compiler;
                }
                return await _reshade.InstallAsync(game, options, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    public Task<ReShadeUpdateStatus> CheckReShadeUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _reshade.CheckUpdateAsync(game, cancellationToken);

    public Task<ComponentOperationResult> InstallD3DCompilerWithProtontricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        return guard is null
            ? _d3dCompilerTools.InstallWithProtontricksAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    public Task<ComponentOperationResult> InstallD3DCompilerWithWinetricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        return guard is null
            ? _d3dCompilerTools.InstallWithWinetricksAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    private async Task<ComponentOperationResult?> EnsureNativeD3DCompilerDependencyAsync(
        DetectedGame game,
        string? explicitCompilerPath,
        string requiredBy,
        CancellationToken cancellationToken)
    {
        // An explicit compiler selection belongs to the requesting install transaction itself; the
        // backend validates PE architecture/signature and stages it without invoking helper tools.
        if (!string.IsNullOrWhiteSpace(explicitCompilerPath)) return null;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution requires a detected Windows executable.");
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution requires an initialized Wine/Proton prefix.");

        var architecture = GameArchitecture.Of(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution could not determine x86/x64 architecture.");
        if (ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture) is not null)
            return null;

        var install = game.SteamAppId.HasValue
            ? await _d3dCompilerTools.InstallDependencyWithProtontricksAsync(game, cancellationToken).ConfigureAwait(false)
            : D3DCompilerToolInstaller.CanInstallWithWinetricks(game)
                ? await _d3dCompilerTools.InstallDependencyWithWinetricksAsync(game, cancellationToken).ConfigureAwait(false)
                : D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game)
                    ? ComponentOperationResult.Fail(D3DCompilerToolInstaller.RuntimeUnknownMessagePrefix + game.PrefixPath)
                    : ComponentOperationResult.Fail("No safe Protontricks/Winetricks route is available for this prefix.");

        return install.Success
            ? null
            : ComponentOperationResult.Fail($"Não foi possível instalar automaticamente D3DCompiler_47, dependência de {requiredBy}: {install.Message}");
    }

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

    public async Task<ComponentOperationResult> InstallLocalLumaPackageAsync(
        DetectedGame game,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        return await Track(game, ManagedComponentKind.LumaFramework, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.LumaFramework,
            indexedFiles: null,
            () => _lumaFramework.InstallLocalPackageAsync(game, packagePath, cancellationToken),
            cancellationToken)).ConfigureAwait(false);
    }

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

    public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        if (_records.Find(game, component) is null)
            return new ManagedUpdateCheck(false, false, $"{component} is not managed by RHI Linux.");

        // A managed component on a game without a confirmed patch target is blocked, as
        // UpdateManagedAsync would refuse it: Update All reports a failure, not "current".
        var patchGuard = PatchTargetUpdateGuard(game);
        if (patchGuard is not null) return patchGuard;

        return HeldByLayer(game, component, component switch // w3/parity (#96)
        {
            ManagedComponentKind.ReShade => await CheckReShadeManagedUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.RenoDX => await _renodx.CheckUpdateAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ReLimiter => await _relimiter.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.DisplayCommander => await _displayCommander.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.OptiScaler => await _optiscaler.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.REFramework => await _reFramework.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.LumaFramework => await _lumaFramework.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.DofFix => await _dofFix.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.DlssEnablerStandalone => await _dlssEnablerStandalone.CheckManagedUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Rtx40MfgStandalone => await _rtx40Mfg.CheckManagedUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.RtxEncore when _rtxEncore is not null => await _rtxEncore.CheckManagedUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.CheckUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Dx11Bridge), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.CheckUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ShortFuse => await _shortFuse.CheckManagedUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ReShadeAddonManager => await _addonManager.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false), // #194
            // w7 lane rows: update checks (one arm each)
            // w7/smalls rows
            ManagedComponentKind.NrCostScaler when _nrCostScaler is not null => await _nrCostScaler.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false), // #199
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => await _dxvkHdr.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            _ => new ManagedUpdateCheck(true, false, $"{component} não participa do fluxo Atualizar tudo.")
        });
    }

    // w3/parity (#96): an NR method's update found while NR Cost Scaler wraps its runtime is held, not attempted (the
    // update itself is refused by LayerGuard): Update All reports it as held by the scaler, never as a failure.
    private ManagedUpdateCheck HeldByLayer(DetectedGame game, ManagedComponentKind component, ManagedUpdateCheck check)
        => check.UpdateAvailable && NrCostScalerService.OwnerChangeRefusal(_records, game, component) is { } layered
            ? check with { UpdateAvailable = false, Message = layered, HeldBy = NrCostScalerService.DisplayName }
            // #170: an add-on turned off for No Addons is held by ReShade's channel, not updated.
            : check.UpdateAvailable && AddonOffGuard(game, component) is { } off
                ? check with { UpdateAvailable = false, Message = off.Message, HeldBy = ReShadeNoAddonsName }
                : check;

    /// <summary>What holds an add-on turned off for No Addons in Update All's summary (#170).</summary>
    public const string ReShadeNoAddonsName = "ReShade No Addons";

    private async Task<ManagedUpdateCheck> CheckReShadeManagedUpdateAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var status = await _reshade.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(status.IsInstalled, status.UpdateAvailable, status.Message, status.InstalledVersion, status.LatestVersion,
            CouldNotCheck: status.CouldNotCheck);
    }

    public Task<ComponentOperationResult> UpdateManagedAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, component) ?? AddonOffGuard(game, component); // w3/parity (#96), #170
        if (guard is not null) return Task.FromResult(guard);

        return Track(game, component, ComponentUpdateAction.Update, () => component switch
        {
            ManagedComponentKind.ReShade => InstallReShadeForShaderSetAsync(game, _reshade.GetInstalledOptions(game), update: true, cancellationToken), // w7/shader-sets hook (#179 review major 2)
            ManagedComponentKind.RenoDX => _renodx.UpdateAsync(game, indexedFiles, cancellationToken),
            ManagedComponentKind.ReLimiter => _relimiter.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DisplayCommander => _displayCommander.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.OptiScaler => _optiscaler.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DlssEnablerStandalone => _dlssEnablerStandalone.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.Rtx40MfgStandalone => _rtx40Mfg.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.RtxEncore when _rtxEncore is not null => _rtxEncore.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.ShortFuse
                => SwapNeuralAddonVersionCoreAsync(game, component, cancellationToken),
            ManagedComponentKind.ReShadeAddonManager => _addonManager.UpdateAsync(game, cancellationToken), // #194
            // w7 lane rows: updates (one arm each)
            // w7/smalls rows
            ManagedComponentKind.NrCostScaler when _nrCostScaler is not null => _nrCostScaler.UpdateAsync(game, cancellationToken), // #199
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => _dxvkHdr.UpdateAsync(game, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} não participa do fluxo Atualizar tudo."))
        });
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

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game) ?? LayerGuard(game, component) ?? AddonOffGuard(game, component) // w3/parity (#96), #170
                    ?? VulkanLayerConflict(game, component); // w5/reshade-core (#92)
        if (guard is not null) return guard;
        if (component == ManagedComponentKind.ReShade) game = ReShadeViewForDxvkHdr(game); // w7/dxvk-hdr hook (#200): ReShade beside DX9 HDR takes the Vulkan layer

        return await Track(game, component, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            component,
            indexedFiles,
            () => InstallCoreAsync(game, component, indexedFiles, cancellationToken),
            cancellationToken)).ConfigureAwait(false);
    }

    private Task<ComponentOperationResult> InstallCoreAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken)
        => component switch
        {
            ManagedComponentKind.ReShade => InstallReShadeForShaderSetAsync(game, ReShadeRowOptions(game), update: false, cancellationToken), // w5/reshade-core (#92); w7/shader-sets hook (#179 review major 2)
            ManagedComponentKind.RenoDX => _renodx.InstallAsync(game, indexedFiles, cancellationToken),
            ManagedComponentKind.RenoDXUpgrade => _renodxUpgrade.InstallAsync(game, cancellationToken),
            ManagedComponentKind.OptiScaler => InstallOptiScalerUnderChosenNameAsync(game, OptiScalerVariant.Stable, null, cancellationToken), // #172
            ManagedComponentKind.ReLimiter => _relimiter.InstallAsync(game, cancellationToken),
            ManagedComponentKind.DisplayCommander => _displayCommander.InstallAsync(game, cancellationToken),
            ManagedComponentKind.MfgAdaUnlock => InstallMfgAdaUnlockAsync(game, cancellationToken),
            ManagedComponentKind.Dlssg2030Unlock => _dlssg2030.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.DlssEnablerStandalone => _dlssEnablerStandalone.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.Rtx40MfgStandalone => _rtx40Mfg.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.RtxEncore when _rtxEncore is not null => _rtxEncore.InstallAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Feeder => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.Dlss5Feeder, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.Dlss5Dx11Bridge, cancellationToken),
            ManagedComponentKind.ShortFuse => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.ShortFuse, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.InstallAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.InstallAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.InstallAsync(game, cancellationToken),
            // w7 lane rows: installs (one arm each)
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => _dxvkHdr.InstallAsync(game, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} installation is not available yet."))
        };

    private Task<ComponentOperationResult> InstallMfgAdaUnlockAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (_records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return Task.FromResult(ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar MFG Ada Unlock."));
        if (_records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return Task.FromResult(ComponentOperationResult.Fail("Universal MFG já está ativo. Remova-o antes de instalar MFG Ada Unlock."));
        return _mfgAdaUnlock.InstallAsync(game, cancellationToken);
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

    public Task<ComponentOperationResult> RemoveAsync(
        DetectedGame game,
        ManagedComponentKind component,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, component); // w3/parity (#96)
        if (guard is not null) return Task.FromResult(guard);

        if (component == ManagedComponentKind.REFramework &&
            REFrameworkDeploymentService.IsGameEligible(game) &&
            _records.Find(game, ManagedComponentKind.ReShade) is not null)
        {
            return Task.FromResult(ComponentOperationResult.Fail(
                "Remove RHI-managed ReShade first. This RE Engine ReShade installation depends on RE Framework."));
        }

        if (component == ManagedComponentKind.ReShade)
        {
            var dependent = Enum.GetValues<ManagedComponentKind>()
                .Where(kind =>
                    LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, kind).Contains(ManagedComponentKind.ReShade) &&
                    _records.Find(game, kind) is not null)
                .Select(kind => (ManagedComponentKind?)kind)
                .FirstOrDefault();
            if (dependent is { } dependentKind)
            {
                return Task.FromResult(ComponentOperationResult.Fail(
                    $"Remove RHI-managed {DisplayDependencyName(dependentKind)} first. ReShade removal is blocked while a managed ReShade add-on depends on it."));
            }
        }

        return Track(game, component, ComponentUpdateAction.Remove, () => component switch
        {
            ManagedComponentKind.ReShade => _reshade.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.RenoDX => _renodx.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.RenoDXUpgrade => _renodxUpgrade.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.OptiScaler => _optiscaler.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.ReLimiter => _relimiter.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.DisplayCommander => _displayCommander.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.MfgAdaUnlock => _mfgAdaUnlock.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.Dlssg2030Unlock => _dlssg2030.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.DlssEnablerStandalone => _dlssEnablerStandalone.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.Rtx40MfgStandalone => _rtx40Mfg.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.RtxEncore when _rtxEncore is not null => _rtxEncore.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.RemoveAsync(game, cancellationToken),
            // w7 lane rows: removals (one arm each)
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => _dxvkHdr.RemoveAsync(game, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} removal is not available yet."))
        });
    }

    public LumaPolicySnapshot GetLumaPolicy(DetectedGame game)
        => _lumaPolicy.GetCachedOrDefault(game);

    public LumaPolicySnapshot SetLumaModePreference(DetectedGame game, RHI.Platform.Linux.Paths.LumaModePreference preference)
        => _lumaPolicy.SetPreference(game, preference);

    private static string GenericLumaMetadataSummary(LumaGenericGameEntry? metadata)
    {
        if (metadata is null) return " No exact generic-wiki metadata entry matched this launcher title.";
        var dlss = metadata.DlssFsrBlocked ? "blocked"
            : metadata.DlssFsrSupported ? "supported"
            : "not marked";
        var summary = $" Luma wiki metadata: {metadata.Name}; UE {metadata.UeVersion ?? "unknown"}; HDR {(metadata.HdrSupported ? "yes" : "no")}; DLSS/FSR {dlss}; Engine.ini keys {metadata.EngineIniKeys.Count}.";
        if (!string.IsNullOrWhiteSpace(metadata.LaunchArgs))
            summary += $" Advisory launch arguments: {metadata.LaunchArgs}. RHI Linux does not silently rewrite Steam/Heroic launch options.";
        if (!string.IsNullOrWhiteSpace(metadata.Notes))
            summary += $" Notes: {metadata.Notes}";
        return summary;
    }

    // w3/journal (#159): RE Framework ⚙ "Delete _storage_ folder", behind the patch-target guard.
    public Task<ComponentOperationResult> DeleteReFrameworkStorageFolderAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? _reFramework.DeleteStorageFolderAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    // w5/ini-presets hook (#181): INI deploys and "apply to all games" refuse a game where any install would be refused.
    public string? IniWriteRefusal(DetectedGame game) => PatchTargetGuard(game)?.Message;

    // w5/ini-presets hook (#181 review 2): INI deploys journaled on the component's record (set by the app's composition).
    public UserIniService? UserIni { get; set; }

    // w5/ini-presets hook (#180): the ReShade preset service (journaled; set by the app's composition).
    public ReShadePresetService? ReShadePresets { get; set; }

    // w5/ini-presets hook (#189): the Luma ⚙ settings service (journaled; set by the app's composition).
    public LumaSettingsService? LumaSettings { get; set; }

    // w5/ini-presets hook (#182 review 9): the RenoDX ⚙ UE-Extended switch is a RenoDX update carrying the choice, guarded
    // and tracked like Update; the choice is saved only by that update's own stamped record.
    public Task<ComponentOperationResult> UpdateRenoDxUeExtendedAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, bool on, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.RenoDX) ?? AddonOffGuard(game, ManagedComponentKind.RenoDX);
        if (guard is not null) return Task.FromResult(guard);
        return Track(game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Update,
            () => _renodx.UpdateAsync(game, indexedFiles, cancellationToken, on ? "on" : "off"));
    }

    // w5/ini-presets hook (#182): RenoDX peak brightness (journaled; set by the app's composition).
    public RenoDxPeakNits? RenoDxPeakNits { get; set; }

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
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message);
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
        return new(result.Success ? ShaderSetOutcome.Changed : ShaderSetOutcome.Failed, result.Message);
    }

    /// <summary>The message when the shader and add-on choices cannot be read (PT-BR pair).</summary>
    public const string ShaderSetsUnreadableMessage = "The shader and add-on choices cannot be read; nothing was changed.";

    /// <summary>
    /// Appended to a ReShade install or update that could not take the game's shader packs because no native
    /// D3DCompiler_47 could be had for its prefix (review major 2; PT-BR pair). ReShade itself is installed.
    /// </summary>
    public const string ShaderPacksNotInstalledNote =
        " The game's shader packs were not installed: they need a native D3DCompiler_47 in its Wine prefix. Install it with the D3DCompiler 47 row in the ReShade section, then Apply in Shaders/Add-ons.";

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

/// <summary>#179: one game's outcome and the message of the operation (empty when nothing ran).</summary>
public sealed record ShaderSetApplyResult(ShaderSetOutcome Outcome, string Message);

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
