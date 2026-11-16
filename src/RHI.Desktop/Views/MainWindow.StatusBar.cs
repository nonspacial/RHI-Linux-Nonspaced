using Avalonia.Threading;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

// #298 (upstream 2.9.2 MainWindow.StatusBar): keeps the status bar's idle countdown current and lets a status line leave
// on time. Ticks only while the window is shown (not while it sits in the tray).
public sealed partial class MainWindow
{
    private DispatcherTimer? _statusBarTimer;

    private void InitializeStatusBar()
    {
        _statusBarTimer = new DispatcherTimer { Interval = StatusBarPresenter.RefreshInterval };
        _statusBarTimer.Tick += (_, _) =>
        {
            if (IsVisible && DataContext is MainWindowViewModel vm) vm.StatusBar.Refresh();
        };
        Opened += (_, _) => _statusBarTimer.Start();
        Closed += (_, _) => _statusBarTimer.Stop();
    }
}
