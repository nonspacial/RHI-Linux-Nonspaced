using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>The OptiScaler ⚙ dialog's layout; hosts the nightly-build row (#156) and the presets row (#187).</summary>
public sealed partial class OptiScalerSettingsDialogView : UserControl
{
    private Control _nightly;
    private Control _presets;

    public OptiScalerSettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        _nightly = this.FindControl<Control>("NightlyBuildSlot")!;
        _presets = this.FindControl<Control>("PresetsSlot")!;
        DataContextChanged += (_, _) =>
        {
            var model = DataContext as OptiScalerSettingsDialogViewModel;
            _nightly = DialogSlot.Fill(_nightly, model is null ? null : new OptiScalerNightlyBuildRow(model.NightlyBuild));
            _presets = DialogSlot.Fill(_presets, model?.Presets is { } slots
                ? OptiScalerPresetsRow.Create(slots, model.Snapshot, line => model.Message = line, model.RequestReopen)
                : null);
        };
    }
}
