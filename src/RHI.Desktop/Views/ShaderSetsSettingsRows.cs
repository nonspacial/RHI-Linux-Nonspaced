using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #179 layout B: Settings → Shaders/Add-ons → "Global shader and add-on set": one "Global shaders and add-ons…" button
/// that opens the Shaders/Add-ons dialog for the global set (its Save applies at once to the games set to "Use the global
/// set", not on the Settings dialog's Save; Reset leaves it), and one line saying who uses it.
/// </summary>
internal sealed class ShaderSetsSettingsRows
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public ShaderSetsSettingsRows(MainWindowViewModel vm, Action<string> showStatus)
    {
        var edit = new Button
        {
            Name = "EditGlobalSets", Content = T("Sets.EditGlobal"), Padding = new Thickness(12, 6), HorizontalAlignment = HorizontalAlignment.Left,
        };
        edit.Click += async (_, _) => await UiExceptionGuard.RunAsync(
            () => ShaderSetDialogs.ShowGlobalAsync(edit, vm, showStatus), nameof(ShaderSetsSettingsRows));

        var panel = new StackPanel { Spacing = 5, Name = "ShaderSetsRows", IsVisible = vm.ReShadeProfile.HasShaderSets };
        panel.Children.Add(new TextBlock { Text = T("Sets.GlobalHeading"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(edit);
        panel.Children.Add(new TextBlock { Text = T("Sets.GlobalNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Control = panel;
    }

    public Control Control { get; }
}
