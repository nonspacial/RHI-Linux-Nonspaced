using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

public sealed class LinuxManagedComponentService : ILinuxManagedUpdateBackend
{
    private readonly LinuxDeploymentRecordStore _records;
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
        ComponentUpdateLogStore? updateLog = null)
    {
        _records = records;
        _reshade = reshade;
        _addonManager = addonManager;
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
        var reShadePresent = components.Any(c => c.Kind == ManagedComponentKind.ReShade && c.Availability == ComponentAvailability.Installed);
        var managedRenoDx = _records.Find(game, ManagedComponentKind.RenoDX);
        var managedRenoDxUpgrade = _records.Find(game, ManagedComponentKind.RenoDXUpgrade);
        var managedReLimiter = _records.Find(game, ManagedComponentKind.ReLimiter);
        var managedDisplayCommander = _records.Find(game, ManagedComponentKind.DisplayCommander);
        var managedMfgAdaUnlock = _records.Find(game, ManagedComponentKind.MfgAdaUnlock);
        var managedDlssg2030 = _records.Find(game, ManagedComponentKind.Dlssg2030Unlock);
        var managedRtx40Mfg = _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone);
        var managedDlss5Feeder = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        var managedDlss5Bridge = _records.Find(game, ManagedComponentKind.Dlss5Dx11Bridge);
        var managedOptiScaler = _records.Find(game, ManagedComponentKind.OptiScaler);
        var managedShortFuse = _records.Find(game, ManagedComponentKind.ShortFuse);
        var detectedDlssNow = components.Any(c => c.Kind == ManagedComponentKind.Dlss && c.Availability == ComponentAvailability.Installed);
        var nativeDlssBeforeManagedMethod = InferNativeDlssBeforeManagedNeuralRendering(
            detectedDlssNow, managedShortFuse, managedDlss5Feeder);
        var managedDofFix = _records.Find(game, ManagedComponentKind.DofFix);
        var managedReFramework = _records.Find(game, ManagedComponentKind.REFramework);
        var managedLumaFramework = _records.Find(game, ManagedComponentKind.LumaFramework);

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

            if (!game.HasConfirmedPatchTarget)
            {
                component.ActionAllowed = false;
                component.Note = "Alvo de modificações não definido. O executável de inicialização/launcher foi preservado, mas o RHI não instalará, atualizará ou removerá arquivos locais de componentes até você selecionar o executável real do jogo em Editar jogo.";
                continue;
            }

            var record = _records.Find(game, component.Kind);
            component.ManagedByRhi = record is not null;
            if (component.Kind is ManagedComponentKind.ReShade or ManagedComponentKind.RenoDX or ManagedComponentKind.RenoDXUpgrade or ManagedComponentKind.OptiScaler or
                ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander or ManagedComponentKind.MfgAdaUnlock or ManagedComponentKind.Dlssg2030Unlock or ManagedComponentKind.DlssEnablerStandalone or ManagedComponentKind.Rtx40MfgStandalone or
                ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse or
                ManagedComponentKind.DofFix or ManagedComponentKind.REFramework or ManagedComponentKind.LumaFramework)
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

                if (component.Kind == ManagedComponentKind.Rtx40MfgStandalone && record is not null &&
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
                    if (ReShadeDeploymentService.SelectProxyName(game.EffectiveGraphicsApi) is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Safe game-local ReShade deployment is currently enabled for DirectX 8/9/10/11/12 and OpenGL targets; this API needs a different integration path.";
                    }
                    else if (game.PrefixPath is null && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                    {
                        component.ActionAllowed = false;
                        component.Note = "Start the Windows game once so its Wine/Proton prefix is created before installing ReShade.";
                    }
                    else if (managedOptiScaler is not null)
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
                             _records.Find(game, ManagedComponentKind.ReShade) is not null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "Remove RHI-managed ReShade first; this RE Engine ReShade path depends on RE Framework.";
                    }
                }

                if (component.Kind == ManagedComponentKind.LumaFramework)
                {
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
                        else if (_records.Find(game, ManagedComponentKind.ReShade) is null && reShadePresent &&
                                 !(managedRenoDx is not null && !_lumaPolicy.GetCachedOrDefault(game).RenoDxCompatible))
                        {
                            component.ActionAllowed = false;
                            component.Note = "Luma Framework exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará nem sobrescreverá automaticamente.";
                        }
                        else if (_records.Find(game, ManagedComponentKind.ReShade) is null &&
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
                    if (game.WikiUnlinkedByManifest)
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
                    else if (_renodx.TryGetPreparedCompatibility(game, out var preparedRenoDx) && preparedRenoDx is null)
                    {
                        component.Availability = ComponentAvailability.Unsupported;
                        component.ActionAllowed = false;
                        component.Note = "Nenhum perfil RenoDX específico foi encontrado no catálogo upstream e este jogo não corresponde aos fallbacks genéricos seguros de Unreal/Unity. A instalação automática foi desativada para evitar aplicar um add-on incorreto.";
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
                        !(component.Kind == ManagedComponentKind.Dlss5Feeder && managedOptiScaler is not null && OptiScalerDeploymentService.GetInstalledVariant(managedOptiScaler) == OptiScalerVariant.DlssNr))
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
                        else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
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
                        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
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
                            : (game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath));
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
                        else if (managedOptiScaler is not null && OptiScalerDeploymentService.GetInstalledVariant(managedOptiScaler) == OptiScalerVariant.DlssNr)
                        {
                            component.ActionAllowed = false;
                            component.Note = "Remove OptiScaler DLSS NR first. Feeder and that OptiScaler variant would both own nvngx_dlssnr.dll; safe shared-file management is not yet enabled in the Linux port.";
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
                    else if (_records.Find(game, ManagedComponentKind.ReShade) is null && reShadePresent && managedOptiScaler is null)
                    {
                        component.ActionAllowed = false;
                        component.Note = "ShortFuse exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; o RHI não o adotará automaticamente.";
                    }
                    else if (_records.Find(game, ManagedComponentKind.ReShade) is null && managedOptiScaler is null)
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
                    else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
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
                    else if (string.IsNullOrWhiteSpace(game.ExecutablePath) || PeArchitectureDetector.Detect(game.ExecutablePath) != PeArchitecture.X64)
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
                        var nrOwnershipNote = managedDlss5Feeder is not null
                            ? " DLSS NR is temporarily blocked while Feeder is managed because both would own nvngx_dlssnr.dll; Stable/Nightly remain available."
                            : "";
                        component.Note = (reShadePresent
                            ? "OptiScaler Stable/Nightly/DLSS NR. Choose the channel in ⚙ before installing; RHI will migrate its managed ReShade to ReShade64.dll for deterministic coexistence. Offline/single-player only."
                            : "OptiScaler Stable/Nightly/DLSS NR from the upstream GitHub channels. Choose the channel in ⚙ before installing; requires a supported temporal upscaler input. Offline/single-player only.") + nrOwnershipNote;
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
                }
            }
            else
            {
                component.ActionAllowed = false;
            }
        }
    }

    private static ComponentOperationResult? PatchTargetGuard(DetectedGame game)
        => game.HasConfirmedPatchTarget
            ? null
            : ComponentOperationResult.Fail(
                "Alvo de modificações não definido. Use Editar jogo para selecionar o executável real do jogo; o executável do launcher nunca é usado como destino de patches.");

    private static ManagedUpdateCheck? PatchTargetUpdateGuard(DetectedGame game)
        => game.HasConfirmedPatchTarget
            ? null
            : new ManagedUpdateCheck(false, false,
                "Alvo de modificações não definido. Atualizações locais estão bloqueadas até Editar jogo confirmar o executável real do jogo.");

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

    private async Task<ComponentOperationResult> InstallWithManagedDependenciesAsync(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string>? indexedFiles,
        Func<Task<ComponentOperationResult>> targetInstaller,
        CancellationToken cancellationToken)
    {
        var conflict = DependencyConflictGuard(game, target);
        if (conflict is not null) return conflict;

        var index = BuildDependencyIndex(game, indexedFiles);
        var installedThisAttempt = new List<ManagedComponentKind>();
        var dependencyChanges = new List<string>();
        var visiting = new HashSet<ManagedComponentKind>();

        ComponentOperationResult dependencies;
        try
        {
            dependencies = await EnsureManagedDependenciesAsync(
                game, target, index, installedThisAttempt, dependencyChanges, visiting, cancellationToken).ConfigureAwait(false);
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

        if (installedThisAttempt.Count == 0) return result;

        var changed = dependencyChanges
            .Concat(result.ChangedFiles ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var names = string.Join(" → ", installedThisAttempt.Select(DisplayDependencyName));
        return ComponentOperationResult.Ok(
            $"Dependências instaladas automaticamente ({names}). {result.Message}",
            changed);
    }

    private async Task<ComponentOperationResult> EnsureManagedDependenciesAsync(
        DetectedGame game,
        ManagedComponentKind target,
        IReadOnlyList<string> indexedFiles,
        List<ManagedComponentKind> installedThisAttempt,
        List<string> dependencyChanges,
        HashSet<ManagedComponentKind> visiting,
        CancellationToken cancellationToken)
    {
        if (!visiting.Add(target))
            return ComponentOperationResult.Fail($"Dependency cycle detected while preparing {DisplayDependencyName(target)}.");

        try
        {
            foreach (var dependency in LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var satisfaction = InspectDependency(game, target, dependency, indexedFiles, cancellationToken);
                if (satisfaction == DependencySatisfaction.BrokenManaged)
                    return ComponentOperationResult.Fail(
                        $"A dependência gerenciada {DisplayDependencyName(dependency)} possui registro de ownership, mas seu payload não está presente. Repare ou remova esse estado antes de instalar {DisplayDependencyName(target)}.");

                // An external dependency is a preservation boundary. It may satisfy ordinary add-ons,
                // but RHI does not recurse into or adopt the external stack. RHI-managed dependencies,
                // on the other hand, must keep their own transitive dependency graph valid.
                if (satisfaction == DependencySatisfaction.External) continue;

                var nested = await EnsureManagedDependenciesAsync(
                    game, dependency, indexedFiles, installedThisAttempt, dependencyChanges, visiting, cancellationToken).ConfigureAwait(false);
                if (!nested.Success) return nested;

                satisfaction = InspectDependency(game, target, dependency, indexedFiles, cancellationToken);
                if (satisfaction == DependencySatisfaction.Managed) continue;
                if (satisfaction == DependencySatisfaction.External) continue;
                if (satisfaction == DependencySatisfaction.BrokenManaged)
                    return ComponentOperationResult.Fail(
                        $"A dependência gerenciada {DisplayDependencyName(dependency)} ficou inconsistente durante a resolução automática.");

                var before = _records.Find(game, dependency);
                var install = await InstallDependencyCoreAsync(game, dependency, cancellationToken).ConfigureAwait(false);
                if (!install.Success)
                    return ComponentOperationResult.Fail(
                        $"Não foi possível instalar automaticamente a dependência {DisplayDependencyName(dependency)} exigida por {DisplayDependencyName(target)}: {install.Message}");

                if (before is null && _records.Find(game, dependency) is not null)
                    installedThisAttempt.Add(dependency);
                if (install.ChangedFiles is { Count: > 0 })
                    dependencyChanges.AddRange(install.ChangedFiles.Where(path => !string.IsNullOrWhiteSpace(path)));
            }

            return ComponentOperationResult.Ok("Dependencies ready.");
        }
        finally
        {
            visiting.Remove(target);
        }
    }

    private Task<ComponentOperationResult> InstallDependencyCoreAsync(
        DetectedGame game,
        ManagedComponentKind dependency,
        CancellationToken cancellationToken)
        => dependency switch
        {
            ManagedComponentKind.ReShade => _reshade.InstallAsync(game, cancellationToken),
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

    private ComponentOperationResult? DependencyConflictGuard(DetectedGame game, ManagedComponentKind target)
    {
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

        if ((target is ManagedComponentKind.LumaFramework or ManagedComponentKind.ShortFuse) &&
            _records.Find(game, ManagedComponentKind.ReShade) is null &&
            !string.IsNullOrWhiteSpace(game.BinaryDirectory) &&
            Directory.Exists(game.BinaryDirectory) &&
            GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
        {
            return ComponentOperationResult.Fail(
                $"{DisplayDependencyName(target)} exige ReShade gerenciado pelo RHI. Um ReShade externo foi detectado e será preservado; nenhuma dependência foi alterada.");
        }

        if (target == ManagedComponentKind.Dlss5Feeder &&
            _records.Find(game, ManagedComponentKind.OptiScaler) is { } opti &&
            OptiScalerDeploymentService.GetInstalledVariant(opti) == OptiScalerVariant.DlssNr)
            return ComponentOperationResult.Fail("OptiScaler DLSS NR já está ativo. Feeder e essa variante compartilham nvngx_dlssnr.dll sem ledger de ownership compartilhado.");

        return null;
    }

    internal static string DisplayDependencyName(ManagedComponentKind kind) => kind switch
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
        _ => kind.ToString()
    };

    public bool IsManaged(DetectedGame game, ManagedComponentKind kind)
        => _records.Find(game, kind) is not null;

    public bool IsDlss5FeederFullManaged(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        return record is not null && record.Metadata.TryGetValue("Feeder.FullStack", out var value) && value == "1";
    }

    public IReadOnlyList<ReShadeShaderPackInfo> AvailableReShadeShaderPacks => _reshade.AvailableShaderPacks;
    public string CustomShaderRoot => _reshade.CustomShaderRoot;
    public string CustomAddonRoot => _addonManager.CustomRoot;

    public ComponentOperationResult ImportCustomAddonFile(string sourcePath)
        => _addonManager.ImportCustomAddonFile(sourcePath);

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
        return guard is null
            ? _addonManager.ApplySelectionAsync(game, selectedIds, cancellationToken)
            : Task.FromResult(guard);
    }

    public bool IsReShadeManaged(DetectedGame game) => _reshade.IsManaged(game);

    public ReShadeInstallOptions GetReShadeOptions(DetectedGame game) => _reshade.GetInstalledOptions(game);

    public async Task<ComponentOperationResult> ApplyReShadeAsync(
        DetectedGame game,
        ReShadeInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return guard;
        return await Track(game, ManagedComponentKind.ReShade, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.ReShade,
            indexedFiles: null,
            async () =>
            {
                if (options.ShaderPackIds.Count > 0 && string.IsNullOrWhiteSpace(options.D3DCompiler47Path))
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
        var guard = PatchTargetGuard(game);
        return guard is null
            ? _d3dCompilerTools.InstallWithProtontricksAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    public Task<ComponentOperationResult> InstallD3DCompilerWithWinetricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
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

        var architecture = PeArchitectureDetector.Detect(game.ExecutablePath);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution could not determine x86/x64 architecture.");
        if (ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture) is not null)
            return null;

        var install = game.SteamAppId.HasValue
            ? await _d3dCompilerTools.InstallDependencyWithProtontricksAsync(game, cancellationToken).ConfigureAwait(false)
            : D3DCompilerToolInstaller.CanInstallWithWinetricks(game)
                ? await _d3dCompilerTools.InstallDependencyWithWinetricksAsync(game, cancellationToken).ConfigureAwait(false)
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
        var guard = PatchTargetGuard(game);
        if (guard is not null) return guard;

        var preflight = Dlss5FeederFullDeploymentService.ValidateTarget(game, consumer, requireReShade: false);
        if (preflight is not null) return preflight;
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            (string.IsNullOrWhiteSpace(deepFriedChickenPackagePath) || !File.Exists(deepFriedChickenPackagePath)))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z antes que dependências sejam alteradas.");
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            !Dlss5FeederFullDeploymentService.IsSupportedDeepFriedChickenArchive(deepFriedChickenPackagePath!))
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
        var version = _neuralVersionPins.GetNeuralRenderingDllVersion(game);
        if (IsShortFuseManaged(game)) return _shortFuse.SwapNeuralRenderingDllAsync(game, version, cancellationToken);
        if (IsDlss5FeederFullManaged(game)) return _dlss5Feeder.SwapNeuralRenderingDllAsync(game, version, cancellationToken);
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
        var guard = PatchTargetGuard(game);
        return guard is null ? SwapNeuralAddonVersionCoreAsync(game, kind, cancellationToken) : Task.FromResult(guard);
    }

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
        var guard = PatchTargetGuard(game);
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

    public Task<ShortFuseUpdateStatus> CheckShortFuseUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken);

    public Task<ShortFuseUpdateStatus> CheckShortFuseLatestReleaseAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, cancellationToken);

    public async Task<ComponentOperationResult> InstallShortFuseAsync(DetectedGame game, bool autoConfigEnabled = false, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return guard;
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

    public Task<ComponentOperationResult> RemoveShortFuseAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? Track(game, ManagedComponentKind.ShortFuse, ComponentUpdateAction.Remove, () => _shortFuse.RemoveAsync(game, cancellationToken))
            : Task.FromResult(guard);
    }

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
        var guard = PatchTargetGuard(game);
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
        var patchGuard = PatchTargetUpdateGuard(game);
        if (patchGuard is not null) return patchGuard;

        if (_records.Find(game, component) is null)
            return new ManagedUpdateCheck(false, false, $"{component} is not managed by RHI Linux.");

        return component switch
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
            ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.CheckUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Dx11Bridge), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.CheckUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ShortFuse => await _shortFuse.CheckManagedUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken).ConfigureAwait(false),
            _ => new ManagedUpdateCheck(true, false, $"{component} não participa do fluxo Atualizar tudo.")
        };
    }

    private async Task<ManagedUpdateCheck> CheckReShadeManagedUpdateAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var status = await _reshade.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false);
        return new ManagedUpdateCheck(status.IsInstalled, status.UpdateAvailable, status.Message, status.InstalledVersion, status.LatestVersion);
    }

    public Task<ComponentOperationResult> UpdateManagedAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return Task.FromResult(guard);

        return Track(game, component, ComponentUpdateAction.Update, () => component switch
        {
            ManagedComponentKind.ReShade => _reshade.InstallAsync(game, _reshade.GetInstalledOptions(game), cancellationToken),
            ManagedComponentKind.RenoDX => _renodx.UpdateAsync(game, indexedFiles, cancellationToken),
            ManagedComponentKind.ReLimiter => _relimiter.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DisplayCommander => _displayCommander.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.OptiScaler => _optiscaler.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.DlssEnablerStandalone => _dlssEnablerStandalone.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.Rtx40MfgStandalone => _rtx40Mfg.UpdateAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.ShortFuse
                => SwapNeuralAddonVersionCoreAsync(game, component, cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} não participa do fluxo Atualizar tudo."))
        });
    }

    public OptiScalerVariant? GetInstalledOptiScalerVariant(DetectedGame game)
    {
        var record = _optiscaler.GetRecord(game);
        return record is null ? null : OptiScalerDeploymentService.GetInstalledVariant(record);
    }

    public Task<ComponentOperationResult> InstallOptiScalerAsync(
        DetectedGame game,
        OptiScalerVariant variant,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Install, () => _optiscaler.InstallAsync(game, variant, cancellationToken));
    }

    public async Task<ComponentOperationResult> InstallAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return guard;

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
            ManagedComponentKind.ReShade => _reshade.InstallAsync(game, cancellationToken),
            ManagedComponentKind.RenoDX => _renodx.InstallAsync(game, indexedFiles, cancellationToken),
            ManagedComponentKind.RenoDXUpgrade => _renodxUpgrade.InstallAsync(game, cancellationToken),
            ManagedComponentKind.OptiScaler => _optiscaler.InstallAsync(game, cancellationToken),
            ManagedComponentKind.ReLimiter => _relimiter.InstallAsync(game, cancellationToken),
            ManagedComponentKind.DisplayCommander => _displayCommander.InstallAsync(game, cancellationToken),
            ManagedComponentKind.MfgAdaUnlock => InstallMfgAdaUnlockAsync(game, cancellationToken),
            ManagedComponentKind.Dlssg2030Unlock => _dlssg2030.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.DlssEnablerStandalone => _dlssEnablerStandalone.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.Rtx40MfgStandalone => _rtx40Mfg.InstallAsync(game, cancellationToken: cancellationToken),
            ManagedComponentKind.Dlss5Feeder => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.Dlss5Feeder, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.Dlss5Dx11Bridge, cancellationToken),
            ManagedComponentKind.ShortFuse => InstallNeuralRenderingManagedAsync(game, ManagedComponentKind.ShortFuse, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.InstallAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.InstallAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.InstallAsync(game, cancellationToken),
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
        var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
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
        var guard = PatchTargetGuard(game);
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
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.DofFix => _dofFix.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.REFramework => _reFramework.RemoveAsync(game, cancellationToken),
            ManagedComponentKind.LumaFramework => _lumaFramework.RemoveAsync(game, cancellationToken),
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


}
