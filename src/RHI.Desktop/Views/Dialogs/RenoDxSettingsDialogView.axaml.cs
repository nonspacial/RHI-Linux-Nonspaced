using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>The RenoDX ⚙ dialog's layout; hosts the ini-presets lane's peak-brightness / UE-Extended rows (#182).</summary>
public sealed partial class RenoDxSettingsDialogView : UserControl
{
    private Control _peak;

    public RenoDxSettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        _peak = this.FindControl<Control>("PeakSlot")!;
        DataContextChanged += (_, _) => _peak = DialogSlot.Fill(_peak,
            DataContext is RenoDxSettingsDialogViewModel { IniPresets: { } presets } model
                ? RenoDxPeakPanel.Create(presets, line => model.Message = line)
                : null);
    }
}
