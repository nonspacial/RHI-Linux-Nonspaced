using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

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
        if (kind == ManagedComponentKind.LumaFramework)
        {
            // #189: Luma has no INI template; its ⚙ is upstream's HDR-on-first-boot and TAA rows.
            panel.Children.Add(LumaSection(presenter, Show));
            panel.Children.Add(result);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.TemplateSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(buttons);
            panel.Children.Add(result);
        }
        if (kind == ManagedComponentKind.ReShade && presenter.PresetsApply()) panel.Children.Add(PresetSection(presenter, openFolder, Show)); // #180: RHI's ReShade only

        return new Window
        {
            Title = $"{UiLocalizationService.Format("Ini.SettingsTitle", componentName)} — {vm.SelectedGame?.Name}",
            Width = 600,
            Height = kind == ManagedComponentKind.ReShade ? 560 : 320,
            MinWidth = 420,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
    }

    /// <summary>
    /// #180: install a preset from the presets folder (and, when ticked, the shader packs it needs), open the folder, and
    /// the presets RHI installed in this game with Remove. With no preset in the folder the picker and its buttons are
    /// hidden and one line says where presets go.
    /// </summary>
    private static Control PresetSection(IniPresetsPresenter presenter, Func<string, string?> openFolder, Action<string> show)
    {
        var section = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        section.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PresetSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var empty = new TextBlock { Text = UiLocalizationService.Text("Ini.PresetNone"), TextWrapping = TextWrapping.Wrap, Opacity = 0.76 };
        var picker = new ComboBox { Name = "PresetPicker", MinWidth = 220, MaxWidth = 420 };
        var withPacks = new CheckBox { Name = "PresetWithPacks", Content = UiLocalizationService.Text("Ini.PresetWithPacks"), IsChecked = true };
        ToolTip.SetTip(withPacks, UiLocalizationService.Text("Ini.PresetWithPacksTip"));
        ToolTip.SetShowDelay(withPacks, 2000);
        var install = new Button { Name = "PresetInstall", Classes = { "rhi" }, Content = UiLocalizationService.Text("Ini.PresetInstall"), Margin = new Avalonia.Thickness(0, 0, 8, 8) };
        var openPresets = new Button { Name = "PresetOpenFolder", Classes = { "rhi" }, Content = UiLocalizationService.Text("Ini.PresetOpenFolder"), Margin = new Avalonia.Thickness(0, 0, 8, 8) };
        ToolTip.SetTip(openPresets, UiLocalizationService.Format("Ini.TemplatesFolderTip", presenter.PresetsFolder));
        ToolTip.SetShowDelay(openPresets, 2000);
        var installed = new StackPanel { Spacing = 6 };

        void Refresh()
        {
            var available = presenter.AvailablePresets();
            picker.ItemsSource = available.Select(path => Path.GetFileName(path)).ToList();
            if (available.Count > 0 && picker.SelectedIndex < 0) picker.SelectedIndex = 0;
            picker.IsVisible = withPacks.IsVisible = install.IsVisible = available.Count > 0;
            empty.IsVisible = available.Count == 0;

            installed.Children.Clear();
            installed.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.PresetInstalledHeading"), Opacity = 0.76, TextWrapping = TextWrapping.Wrap });
            foreach (var preset in presenter.InstalledPresets())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var name = new TextBlock { Text = preset.FileName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(name, preset.FileName);
                ToolTip.SetShowDelay(name, 2000);
                var remove = new Button { Classes = { "rhi", "remove" }, Content = UiLocalizationService.Text("Ui.ActionRemove") };
                ToolTip.SetTip(remove, UiLocalizationService.Text("Ini.PresetRemoveTip"));
                ToolTip.SetShowDelay(remove, 2000);
                var fileName = preset.FileName;
                remove.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
                {
                    remove.IsEnabled = false;
                    show(await presenter.RemovePresetAsync(fileName));
                    Refresh();
                }, nameof(ComponentIniDialog));
                row.Children.Add(name);
                row.Children.Add(remove);
                Grid.SetColumn(remove, 1);
                installed.Children.Add(row);
            }
            installed.IsVisible = installed.Children.Count > 1;
        }

        install.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (picker.SelectedIndex < 0) return;
            var path = presenter.AvailablePresets()[picker.SelectedIndex];
            install.IsEnabled = false;
            try { show(await presenter.InstallPresetAsync(path, withPacks.IsChecked == true)); }
            finally
            {
                install.IsEnabled = true;
                Refresh();
            }
        }, nameof(ComponentIniDialog));
        openPresets.Click += (_, _) =>
        {
            if ((presenter.PreparePresetsFolder() ?? openFolder(presenter.PresetsFolder)) is { } error) show(error);
            Refresh();
        };

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(install);
        buttons.Children.Add(openPresets);
        section.Children.Add(empty);
        section.Children.Add(picker);
        section.Children.Add(withPacks);
        section.Children.Add(buttons);
        section.Children.Add(installed);
        Refresh();
        return section;
    }

    /// <summary>
    /// #189: HDR on first boot (Default / Off / On) and TAA (Off / On); each change is written at once and a refused one
    /// goes back to what the files hold. TAA is hidden when no Engine.ini location can be found for the game.
    /// </summary>
    private static Control LumaSection(IniPresetsPresenter presenter, Action<string> show)
    {
        var snapshot = presenter.LumaSettings();
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Ini.LumaSection"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hdr = new ComboBox { Name = "LumaHdr", ItemsSource = OptionLists.LumaHdrMode.Items, MinWidth = 160, SelectedItem = OptionLists.LumaHdrMode.Display(snapshot?.Hdr ?? LumaHdrMode.Default) };
        ToolTip.SetTip(hdr, UiLocalizationService.Text("Ini.LumaHdrTip"));
        ToolTip.SetShowDelay(hdr, 2000);
        var taa = new ComboBox { Name = "LumaTaa", ItemsSource = OptionLists.OffOn.Items, MinWidth = 160, SelectedItem = OptionLists.OffOn.Display(snapshot?.TaaOn == true) };
        ToolTip.SetTip(taa, UiLocalizationService.Text("Ini.LumaTaaTip"));
        ToolTip.SetShowDelay(taa, 2000);
        var reverting = false;

        void Sync()
        {
            var now = presenter.LumaSettings();
            reverting = true;
            hdr.SelectedItem = OptionLists.LumaHdrMode.Display(now?.Hdr ?? LumaHdrMode.Default);
            taa.SelectedItem = OptionLists.OffOn.Display(now?.TaaOn == true);
            reverting = false;
        }

        hdr.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (reverting || hdr.SelectedItem is not string chosen) return;
            show(await presenter.SetLumaHdrAsync(OptionLists.LumaHdrMode.Value(chosen)));
            Sync();
        }, nameof(ComponentIniDialog));
        taa.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (reverting || taa.SelectedItem is not string chosen) return;
            show(await presenter.SetLumaTaaAsync(OptionLists.OffOn.Value(chosen)));
            Sync();
        }, nameof(ComponentIniDialog));

        var rows = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, control, visible) in new (string, Control, bool)[]
                 {
                     (UiLocalizationService.Text("Ini.LumaHdr"), hdr, true),
                     ("TAA", taa, snapshot?.EngineIniPath is not null)
                 })
        {
            var item = new StackPanel { Spacing = 5, Margin = new Avalonia.Thickness(0, 0, 16, 8), IsVisible = visible };
            item.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            item.Children.Add(control);
            rows.Children.Add(item);
        }
        section.Children.Add(rows);
        return section;
    }
}
