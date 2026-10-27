using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using static RHI.Platform.Linux.Deployment.LinuxManagedComponentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The component rows of the selected game (#212): availability, notes, credits and update inclusion.
/// Read-only; runs on the UI thread.
/// </summary>
internal sealed class ManagedComponentRowDecorator
{
    private readonly LinuxDeploymentRecordStore _records;
    private readonly ReShadeDeploymentService _reshade;
    private readonly RenoDxDeploymentService _renodx;
    private readonly LumaFrameworkDeploymentService _lumaFramework;
    private readonly LumaPolicyService _lumaPolicy;
    private readonly Dlssg2030UnlockDeploymentService _dlssg2030;
    private readonly StandaloneDlssEnablerDeploymentService _dlssEnablerStandalone;
    private readonly Rtx40MfgStandaloneDeploymentService _rtx40Mfg;
    private readonly RtxEncoreDeploymentService? _rtxEncore; // w4/encore (#158)
    private readonly DxvkHdrDeploymentService? _dxvkHdr; // w7/dxvk-hdr (#200)
    private readonly UpdateInclusionStore? _updateInclusion; // w5/update-policy (#178)

    internal ManagedComponentRowDecorator(
        LinuxDeploymentRecordStore records,
        ReShadeDeploymentService reshade,
        RenoDxDeploymentService renodx,
        LumaFrameworkDeploymentService lumaFramework,
        LumaPolicyService lumaPolicy,
        Dlssg2030UnlockDeploymentService dlssg2030,
        StandaloneDlssEnablerDeploymentService dlssEnablerStandalone,
        Rtx40MfgStandaloneDeploymentService rtx40Mfg,
        RtxEncoreDeploymentService? rtxEncore,
        DxvkHdrDeploymentService? dxvkHdr,
        UpdateInclusionStore? updateInclusion)
    {
        _records = records;
        _reshade = reshade;
        _renodx = renodx;
        _lumaFramework = lumaFramework;
        _lumaPolicy = lumaPolicy;
        _dlssg2030 = dlssg2030;
        _dlssEnablerStandalone = dlssEnablerStandalone;
        _rtx40Mfg = rtx40Mfg;
        _rtxEncore = rtxEncore;
        _dxvkHdr = dxvkHdr;
        _updateInclusion = updateInclusion;
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

                if (component.Kind is ManagedComponentKind.RtxEncore && record is not null &&
                    component.Availability == ComponentAvailability.Available &&
                    record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)))
                {
                    component.Availability = ComponentAvailability.Installed;
                }

                // #236/#237/#238: RHI's record alone makes the row Installed, so Remove is offered after the user deleted
                // the file (an update never puts it back, and Install refuses or would re-create it behind the record).
                if (component.Kind is ManagedComponentKind.Rtx40MfgStandalone or ManagedComponentKind.MfgAdaUnlock && record is not null &&
                    component.Availability == ComponentAvailability.Available)
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
                    // #236/#237/#238: a file of RHI's removed outside RHI is never put back by an update; the row (Installed
                    // through the record above) says so beside its Remove.
                    if (component.Kind is ManagedComponentKind.Rtx40MfgStandalone or ManagedComponentKind.MfgAdaUnlock
                        && record.Files.Any(file => !string.IsNullOrWhiteSpace(file.Path) && !File.Exists(file.Path)))
                        component.Note = ManagedUpdateFileGuard.RemovedOutsideRhiRowNote;
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

    /// <summary>
    /// w11/nr-gpu: the Neural Rendering notes on the installed NR owners' rows, after every other note (the existing
    /// Note line; it wraps). #230: a hand-picked NR version made for another card generation stays as picked, and says so.
    /// #231: an installed 310.8.0.x NR DLL on an R615-or-newer driver (it crashes there) — ShortFuse, DLSS5 Feeder and
    /// OptiScaler DLSS NR / F5. Update All never asks about it.
    /// </summary>
    internal void DecorateNeuralRendering(DetectedGame game, IReadOnlyList<ManagedComponent> components, string? nrPin, RtxGeneration card,
        string? driver, Func<LinuxDeploymentRecord, string?> installedNr)
    {
        foreach (var component in components)
        {
            if (component.Kind is not (ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.OptiScaler)
                || !component.ManagedByRhi
                || component.Availability is not (ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable)) continue;
            if (component.Kind != ManagedComponentKind.OptiScaler && NeuralRenderingGpuPolicy.MadeForAnotherGeneration(nrPin, card))
                component.Note = AppendNote(component.Note, LinuxManagedComponentService.NrPinOtherGenerationPrefix + nrPin);
            if (!OptiScalerF5DriverPolicy.IsR615OrNewer(driver) || _records.Find(game, component.Kind) is not { } record
                || !(component.Kind == ManagedComponentKind.OptiScaler
                    ? NeuralRenderingRuntimeOwnership.OptiScalerOwner(record) is not null
                    : NeuralRenderingRuntimeOwnership.ListsRuntime(record))) continue;
            if (installedNr(record) is { } version && OptiScalerF5DriverPolicy.IsFaultingNrDll(version))
                component.Note = AppendNote(component.Note, OptiScalerF5DriverPolicy.FaultingNrDllNotePrefix + version);
        }
    }

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrWhiteSpace(note) ? addition : note.TrimEnd() + " " + addition;

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
