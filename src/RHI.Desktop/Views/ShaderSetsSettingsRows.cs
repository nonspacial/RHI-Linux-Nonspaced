using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// #179: Settings → Shaders/Add-ons → the global shader mode (Off / RHI managed / Custom folder, upstream's "Shaders"
/// beside "Cache All Shaders") and "Global shaders…" / "Global add-ons…", which open the Shaders/Add-ons dialog for the
/// global set. A change applies at once to the games set to Global (not on Save; Reset leaves it).
/// </summary>
internal sealed class ShaderSetsSettingsRows
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public ShaderSetsSettingsRows(MainWindowViewModel vm, Action<string> showStatus)
    {
        var profile = vm.ReShadeProfile;
        profile.LoadSetModes(vm.SelectedGame);
        var mode = new ComboBox
        {
            Name = "GlobalShaderMode", MinWidth = 230, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = profile.GlobalShaderModeOptions, SelectedItem = profile.SelectedGlobalShaderMode,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OptionItem>((item, _) => new TextBlock { Text = item?.Display }),
        };
        ToolTip.SetTip(mode, T("Sets.GlobalShaderModeTip"));
        ToolTip.SetShowDelay(mode, MiddleEllipsisText.TooltipDelay);
        mode.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (mode.SelectedItem is not OptionItem<GlobalShaderMode> chosen || ReferenceEquals(chosen, profile.SelectedGlobalShaderMode)) return;
            mode.IsEnabled = false;
            try
            {
                profile.SelectedGlobalShaderMode = chosen;
                if (profile.ModeChange is { } change) await change;
                showStatus(vm.StatusText);
            }
            finally
            {
                mode.SelectedItem = profile.SelectedGlobalShaderMode; // a refused change shows the saved mode again
                mode.IsEnabled = true;
            }
        }, nameof(ShaderSetsSettingsRows));

        Button Edit(string name, string key, ShaderAddonSetPart part)
        {
            var button = new Button { Name = name, Content = T(key), Padding = new Thickness(12, 6) };
            button.Click += async (_, _) => await UiExceptionGuard.RunAsync(
                () => ShaderSetDialogs.ShowGlobalAsync(button, vm, part, showStatus), nameof(ShaderSetsSettingsRows));
            return button;
        }

        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        buttons.Children.Add(Edit("EditGlobalShaders", "Sets.EditGlobalShaders", ShaderAddonSetPart.Shaders));
        buttons.Children.Add(Edit("EditGlobalAddons", "Sets.EditGlobalAddons", ShaderAddonSetPart.Addons));

        var panel = new StackPanel { Spacing = 5, Name = "ShaderSetsRows", IsVisible = profile.HasShaderSets };
        panel.Children.Add(new TextBlock { Text = T("Sets.GlobalShaderMode"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(mode);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = T("Sets.GlobalNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Control = panel;
    }

    public Control Control { get; }
}
