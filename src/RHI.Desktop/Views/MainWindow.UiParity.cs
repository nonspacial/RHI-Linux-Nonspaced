using Avalonia.Input;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

// #309 small UI parity (upstream 2.8.9): the store badge opens the game's Steam Community page on a left click.
public sealed partial class MainWindow
{
    private void StoreBadge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { StoreBadgeOpensCommunity: true } vm) return;
        if (!e.GetCurrentPoint(sender as Avalonia.Visual).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        vm.OpenSteamCommunityCommand.Execute(null);
    }
}
