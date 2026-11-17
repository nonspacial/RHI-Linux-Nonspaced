using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Upstream 2.9.4 Game settings (DetailLayoutHost.GameSettings): the ⚙ beside the game's title opens settings that apply to
/// this game only, one row each — a label and a one-line description on the left, the control on the right. Rows here:
/// Block Steam updates (a switch and what the block holds) and Display modes (#311). Built in code so tests can build it without a window.
/// </summary>
internal static class GameSettingsView
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public sealed record Parts(StackPanel Root, ToggleSwitch? SteamSwitch);

    /// <param name="close">Closes the panel.</param>
    public static Parts Build(MainWindowViewModel vm, Action close)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(close);
        var root = new StackPanel { Spacing = 14, Margin = new Thickness(4), MaxWidth = 520, Name = "GameSettingsPanel" };
        root.Children.Add(new TextBlock { Text = vm.GameSettingsTitle, FontSize = 16, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Text = T("Gs.Intro"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });

        ToggleSwitch? steam = null;
        if (vm.GameSettingsHasSteamRow)
        {
            steam = new ToggleSwitch
            {
                Name = "BlockSteamUpdatesSwitch", IsChecked = vm.SteamUpdatesBlocked, OnContent = T("Ini.On"), OffContent = T("Ini.Off")
            };
            var status = new TextBlock
            {
                Name = "SteamUpdateBlockStatus", Text = vm.SteamUpdateBlockStatus, FontSize = 12, Classes = { "secondary" },
                TextWrapping = TextWrapping.Wrap, IsVisible = vm.SteamUpdateBlockStatus.Length > 0
            };
            var syncing = false;
            steam.IsCheckedChanged += async (_, _) =>
            {
                if (syncing) return;
                steam.IsEnabled = false; // the password prompt may be open
                try { await vm.SetSteamUpdatesBlockedAsync(steam.IsChecked == true); }
                finally
                {
                    steam.IsEnabled = true;
                    syncing = true;
                    try { steam.IsChecked = vm.SteamUpdatesBlocked; } // a refused change springs back
                    finally { syncing = false; }
                    status.Text = vm.SteamUpdateBlockStatus;
                    status.IsVisible = status.Text.Length > 0;
                }
            };
            root.Children.Add(Row(T("Steam.BlockUpdates"), T("Steam.BlockUpdatesTip"), steam, status));
        }

        // w14/display-modes (#311, upstream 2.9.3 AddDisplayModeRow): Display modes · Choose… opens the game's display mode settings.
        if (vm.GameSettingsHasDisplayModesRow && vm.SelectedGame is { } dmGame && vm.DisplayModes.OpenGameSettings is { } openDisplayModes)
        {
            var choose = new Button { Name = "GameSettingsDisplayModes", Classes = { "rhi" }, Content = T("Dm.GsChoose") };
            choose.Click += (_, _) =>
            {
                close();
                _ = openDisplayModes(dmGame);
            };
            root.Children.Add(Row(T("Dm.CardTitle"), vm.DisplayModes.Enabled
                ? UiLocalizationService.Format("Dm.GsDesc", vm.DisplayModes.ActiveModeName(dmGame)) : T("Dm.GsDescOff"), choose));
        }

        // w14/layouts-a (#304): Remove from RHI moved to the Management section, as upstream; Block Steam updates stays here.
        return new Parts(root, steam);
    }

    // Upstream's AddGameSettingRow: label and description on the left, the control on the right.
    private static Grid Row(string label, string description, Control control, Control? below = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = description, FontSize = 12, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
        if (below is not null) text.Children.Add(below);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }
}
