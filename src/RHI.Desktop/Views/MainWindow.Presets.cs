using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets (#180; upstream <c>DragDropHandler.Preset</c>): a dropped <c>.ini</c> is checked as a ReShade preset,
/// kept in the presets folder, then installed into a game the user picks (the selected game is chosen first; only games
/// whose ReShade RHI manages are offered), with the shader packs it needs when ticked.
/// </summary>
public partial class MainWindow
{
    private async Task OfferDroppedPresetAsync(string path)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var presenter = vm.IniPresets;
        var (stored, refusal) = presenter.StorePreset(path);
        if (stored is null)
        {
            vm.StatusText = refusal ?? vm.StatusText;
            return;
        }
        var targets = presenter.PresetTargets();
        if (targets.Count == 0)
        {
            vm.StatusText = TF("Ini.PresetStoredNoGames", Path.GetFileName(stored));
            return;
        }

        var games = new ComboBox
        {
            Name = "PresetDropGame",
            ItemsSource = targets.Select(game => game.Name).ToList(),
            SelectedIndex = Math.Max(0, targets.ToList().FindIndex(game => ReferenceEquals(game, vm.SelectedGame))),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 560
        };
        var withPacks = new CheckBox { Content = T("Ini.PresetWithPacks"), IsChecked = true };
        ToolTip.SetTip(withPacks, T("Ini.PresetWithPacksTip"));
        ToolTip.SetShowDelay(withPacks, 2000);
        var install = new Button { Content = T("Ini.PresetInstall"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(install);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = TF("Ini.PresetDropText", Path.GetFileName(stored)), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        panel.Children.Add(games);
        panel.Children.Add(withPacks);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = T("Ini.PresetDropTitle"),
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        install.Click += (_, _) => dialog.Close(true);
        if (!await ShowFittedDialogAsync<bool>(dialog) || games.SelectedIndex < 0) return;
        await presenter.InstallPresetAsync(stored, withPacks.IsChecked == true, targets[games.SelectedIndex]);
    }
}
