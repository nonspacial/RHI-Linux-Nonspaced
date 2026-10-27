using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    // w3/parity (#96): the NR Cost Scaler's own install and removal, through the same guard and history as every component.
    public Task<ComponentOperationResult> InstallNrCostScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallGuard(game) is { } guard ? Task.FromResult(guard)
            : _nrCostScaler is null ? Task.FromResult(ComponentOperationResult.Fail(NrCostScalerService.NoOwnerMessage))
            : Track(game, ManagedComponentKind.NrCostScaler, ComponentUpdateAction.Install, () => _nrCostScaler.InstallAsync(game, cancellationToken));

    public Task<ComponentOperationResult> RemoveNrCostScalerAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : _nrCostScaler is null ? Task.FromResult(ComponentOperationResult.Fail(NrCostScalerService.NotInstalledMessage))
            : Track(game, ManagedComponentKind.NrCostScaler, ComponentUpdateAction.Remove, () => _nrCostScaler.RemoveAsync(game, cancellationToken));

    /// <summary>w7/dxvk-hdr (#200): the ⚙ preset choice, behind the patch-target guard (a conf rewrite, not an update: no history entry).</summary>
    public Task<ComponentOperationResult> SetDxvkHdrPresetAsync(DetectedGame game, int preset, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : _dxvkHdr is null ? Task.FromResult(ComponentOperationResult.Fail(DxvkHdrDeploymentService.NotInstalledMessage))
            : _dxvkHdr.SetPresetAsync(game, preset, cancellationToken);

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
            // w11/nr-gpu (#230): the add-on check as before, plus a "Latest" NR DLL that is not the one made for this card.
            ManagedComponentKind.Dlss5Feeder => await WithNeuralRenderingMoveAsync(game, component,
                await _dlss5Feeder.CheckUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ShortFuse => await WithNeuralRenderingMoveAsync(game, component,
                await _shortFuse.CheckManagedUpdateAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ReShadeAddonManager => await _addonManager.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false), // #194
            // w7 lane rows: update checks (one arm each)
            // w7/smalls rows
            ManagedComponentKind.NrCostScaler when _nrCostScaler is not null => await _nrCostScaler.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false), // #199
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => await _dxvkHdr.CheckUpdateAsync(game, cancellationToken).ConfigureAwait(false),
            // w10 lane rows: update checks (one arm each)
            // w10/dlss-presets rows
            // w10/archives rows
            // w10/journal-holds rows
            // w11 lane rows: update checks (one arm each)
            // w11/download-idle rows
            // w11/nr-gpu rows
            // w11/nightly-switch rows
            // w11/nr-check rows
            // w12 lane rows: update checks (one arm each)
            // w12/mfg rows
            // w12/shaders rows
            // w12/update-all rows
            // w12/github-cache rows
            // w12/luma rows
            // w12/epic rows
            // w12/nr-logs rows
            // w12/pins rows
            // w12/hdr-wsi rows
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
            ManagedComponentKind.Dlss5Dx11Bridge => SwapNeuralAddonVersionCoreAsync(game, component, cancellationToken),
            // w11/nr-gpu (#230): the add-on as before, and a "Latest" NR DLL moved onto the one made for this card.
            ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.ShortFuse => UpdateNeuralRenderingOwnerAsync(game, component, cancellationToken),
            ManagedComponentKind.ReShadeAddonManager => _addonManager.UpdateAsync(game, cancellationToken), // #194
            // w7 lane rows: updates (one arm each)
            // w7/smalls rows
            ManagedComponentKind.NrCostScaler when _nrCostScaler is not null => _nrCostScaler.UpdateAsync(game, cancellationToken), // #199
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            ManagedComponentKind.DxvkHdr when _dxvkHdr is not null => _dxvkHdr.UpdateAsync(game, cancellationToken),
            // w10 lane rows: updates (one arm each)
            // w10/dlss-presets rows
            // w10/archives rows
            // w10/journal-holds rows
            // w11 lane rows: updates (one arm each)
            // w11/download-idle rows
            // w11/nr-gpu rows
            // w11/nightly-switch rows
            // w11/nr-check rows
            // w12 lane rows: updates (one arm each)
            // w12/mfg rows
            // w12/shaders rows
            // w12/update-all rows
            // w12/github-cache rows
            // w12/luma rows
            // w12/epic rows
            // w12/nr-logs rows
            // w12/pins rows
            // w12/hdr-wsi rows
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} não participa do fluxo Atualizar tudo."))
        });
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
            // w10 lane rows: installs (one arm each)
            // w10/dlss-presets rows
            // w10/archives rows
            // w10/journal-holds rows
            // w11 lane rows: installs (one arm each)
            // w11/download-idle rows
            // w11/nr-gpu rows
            // w11/nightly-switch rows
            // w11/nr-check rows
            // w12 lane rows: installs (one arm each)
            // w12/mfg rows
            // w12/shaders rows
            // w12/update-all rows
            // w12/github-cache rows
            // w12/luma rows
            // w12/epic rows
            // w12/nr-logs rows
            // w12/pins rows
            // w12/hdr-wsi rows
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
            // w10 lane rows: removals (one arm each)
            // w10/dlss-presets rows
            // w10/archives rows
            // w10/journal-holds rows
            // w11 lane rows: removals (one arm each)
            // w11/download-idle rows
            // w11/nr-gpu rows
            // w11/nightly-switch rows
            // w11/nr-check rows
            // w12 lane rows: removals (one arm each)
            // w12/mfg rows
            // w12/shaders rows
            // w12/update-all rows
            // w12/github-cache rows
            // w12/luma rows
            // w12/epic rows
            // w12/nr-logs rows
            // w12/pins rows
            // w12/hdr-wsi rows
            _ => Task.FromResult(ComponentOperationResult.Fail($"{component} removal is not available yet."))
        });
    }

    // w3/journal (#159): RE Framework ⚙ "Delete _storage_ folder", behind the patch-target guard.
    public Task<ComponentOperationResult> DeleteReFrameworkStorageFolderAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        return guard is null
            ? _reFramework.DeleteStorageFolderAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    // w5/ini-presets hook (#182 review 9): the RenoDX ⚙ UE-Extended switch is a RenoDX update carrying the choice, guarded
    // and tracked like Update; the choice is saved only by that update's own stamped record.
    public Task<ComponentOperationResult> UpdateRenoDxUeExtendedAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, bool on, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.RenoDX) ?? AddonOffGuard(game, ManagedComponentKind.RenoDX);
        if (guard is not null) return Task.FromResult(guard);
        return Track(game, ManagedComponentKind.RenoDX, ComponentUpdateAction.Update,
            () => _renodx.UpdateAsync(game, indexedFiles, cancellationToken, on ? "on" : "off"));
    }
}
