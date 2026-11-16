using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>
/// The RenoDX ⚙'s layout (#297: the shared ⚙ layout); its first card hosts UE-Extended and the maximum nits
/// (<see cref="RenoDxCogRows"/>, #182) above the Engine.ini rows, and shows only when one of them applies.
/// </summary>
public sealed partial class RenoDxSettingsDialogView : UserControl
{
    public RenoDxSettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        var card = this.FindControl<CogCard>("MainCard")!;
        var rows = this.FindControl<StackPanel>("MainRows")!;
        DataContextChanged += (_, _) =>
        {
            rows.Children.Clear();
            if (DataContext is not RenoDxSettingsDialogViewModel model) return;
            if (model.IniPresets is { } presets) rows.Children.AddRange(RenoDxCogRows.Create(presets, line => model.Message = line));
            rows.IsVisible = rows.Children.Count > 0;
            card.IsVisible = rows.Children.Count > 0 || model.HasEngineIniRows;
        };
    }
}
