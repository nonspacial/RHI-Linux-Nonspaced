using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets: the ⚙ of the ReShade, ReLimiter and Display Commander rows (#181; upstream 2.8.7 Rs/Ul/Dc cog):
/// "Deploy X.ini" from the user's template, and the INI templates folder. Secondary information sits in 2 s tooltips;
/// the result of an action is the line under the buttons (and the status bar).
/// </summary>
public static class ComponentIniDialog
{
    public static Window Create(MainWindowViewModel vm, ManagedComponentKind kind, string componentName, Func<string, string?> openFolder)
    {
        var presenter = vm.IniPresets;
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Format("Ini.SettingsTitle", componentName),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82, IsVisible = false };
        void Show(string line)
        {
            result.Text = line;
            result.IsVisible = true;
        }

        var file = IniPresetsPresenter.IniFileName(kind);
        var deploy = new Button
        {
            Name = "IniDeploy",
            Classes = { "rhi" },
            Content = UiLocalizationService.Format("Ini.DeployFile", file),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(deploy, UiLocalizationService.Text(kind == ManagedComponentKind.ReShade ? "Ini.DeployReShadeTip" : "Ini.DeployAddonTip"));
        ToolTip.SetShowDelay(deploy, 2000);
        deploy.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            deploy.IsEnabled = false;
            try { Show(await presenter.DeployForSelectedAsync(kind)); }
            finally { deploy.IsEnabled = true; }
        }, nameof(ComponentIniDialog));

        var open = new Button
        {
            Name = "IniOpenTemplates",
            Classes = { "rhi" },
            Content = UiLocalizationService.Text("Names.OpenOptiScalerTemplates"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(open, UiLocalizationService.Format("Ini.TemplatesFolderTip", presenter.TemplatesFolder));
        ToolTip.SetShowDelay(open, 2000);
        open.Click += (_, _) =>
        {
            if ((presenter.PrepareTemplatesFolder() ?? openFolder(presenter.TemplatesFolder)) is { } error) Show(error);
        };

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        deploy.Margin = new Avalonia.Thickness(0, 0, 8, 8);
        open.Margin = new Avalonia.Thickness(0, 0, 8, 8);
        buttons.Children.Add(deploy);
        buttons.Children.Add(open);
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.TemplateSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        panel.Children.Add(result);

        return new Window
        {
            Title = $"{UiLocalizationService.Format("Ini.SettingsTitle", componentName)} — {vm.SelectedGame?.Name}",
            Width = 600,
            Height = 320,
            MinWidth = 420,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
    }
}
