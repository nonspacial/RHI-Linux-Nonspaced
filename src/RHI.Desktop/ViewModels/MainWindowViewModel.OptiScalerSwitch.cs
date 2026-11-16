using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>#273 (w14/optiscaler-2): a preset saved on another OptiScaler version switches the selected game to it.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Asks, then reinstalls the selected game's OptiScaler as <paramref name="target"/>, keeping its OptiScaler.ini values
    /// (<see cref="LinuxManagedComponentService.SwitchOptiScalerVersionAsync"/>). True with the line to add when switched;
    /// false with the line to show when declined, refused or failed (the preset is then not applied).
    /// </summary>
    /// <summary>#272: the ⚙'s All Settings for <paramref name="game"/> (one per dialog: its unapplied changes are its own).</summary>
    internal OptiScalerAllSettingsPresenter NewOptiScalerAllSettings(DetectedGame game) => new(this, _managedComponents, game);

    /// <summary>#272: the ⚙'s Save ini / Load saved ini for <paramref name="game"/>.</summary>
    internal OptiScalerIniLibraryPresenter NewOptiScalerIniLibrary(DetectedGame game) => new(_managedComponents, game);

    /// <summary>
    /// #272: ANR FG into the selected game's OptiScaler custom folder for the preset that asks for it (the newest release,
    /// verified; the build's support checked in its DLL), as one operation.
    /// </summary>
    internal async Task<ComponentOperationResult> EnsureAnrFgForPresetAsync()
    {
        if (SelectedGame is not { } game) return ComponentOperationResult.Fail(OptiScalerDeploymentService.AnrFgNotManagedMessage);
        using var op = BeginOperation("component");
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        StatusText = T("Osp.AnrFgInstalling");
        var result = await _managedComponents.EnsureAnrFgAsync(game, op.Token);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    internal async Task<(bool Switched, string Line)> SwitchOptiScalerVersionForPresetAsync(OptiScalerVariant target, string presetName)
    {
        if (SelectedGame is not { } game) return (false, "");
        var label = OptionLists.OptiScalerVariantsWith(target).Display(target);
        if (ConfirmAsync is { } confirm
            && !await confirm(T("Osp.SwitchTitle"), UiLocalizationService.Format("Osp.SwitchAsk", presetName, label), T("Osp.SwitchConfirm")))
            return (false, UiLocalizationService.Format("Osp.SwitchDeclined", presetName));
        if (!await F5Driver.ConfirmInstallAsync(game, target)) return (false, UiLocalizationService.Format("Osp.SwitchDeclined", presetName));
        using var op = BeginOperation("component");
        if (op is null) return (false, T("Vm.AnotherRHIOperationAlreadyRunning"));
        StatusText = UiLocalizationService.Format("Osp.Switching", label);
        var result = await _managedComponents.SwitchOptiScalerVersionAsync(game, target, op.Token);
        if (result.Success)
        {
            Runtime.RememberOptiScalerVariant(game, target);
            await ReprobeGameAsync(game);
        }
        return (result.Success, UiLocalizationService.Present(result.Message));
    }
}
