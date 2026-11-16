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
