using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

// #309 small UI parity (upstream 2.8.9): the store badge opens the game's Steam Community page on a left click. Upstream
// 2.9.4: the ⚙ beside the title opens Game settings.
public sealed partial class MainWindow
{
    private void StoreBadge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { StoreBadgeOpensCommunity: true } vm) return;
        if (!e.GetCurrentPoint(sender as Avalonia.Visual).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        vm.OpenSteamCommunityCommand.Execute(null);
    }

    private void GameSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control anchor) return;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Content = GameSettingsView.Build(vm, flyout.Hide).Root;
        flyout.ShowAt(anchor);
    }
}
