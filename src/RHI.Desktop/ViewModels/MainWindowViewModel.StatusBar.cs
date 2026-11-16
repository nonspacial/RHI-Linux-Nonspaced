using Avalonia.Threading;

namespace RHI.Desktop.ViewModels;

// #298 (upstream 2.9.2 status bar): the bottom-left text — background activity, else when RHI last checked for updates and
// when the next check is due (the automatic pass's cadence, UpdatesViewModel, #292).
public partial class MainWindowViewModel
{
    private StatusBarPresenter? _statusBar;

    /// <summary>What RHI is doing in the background now (scan, update check, Follow newest, custom folders).</summary>
    public BackgroundActivity Activity { get; } = new();

    /// <summary>The status bar's text. Settings' countdown panel can read <see cref="StatusBarPresenter.LastCheck"/>,
    /// <see cref="StatusBarPresenter.NextCheck"/> and format them with <see cref="StatusBarText.Countdown"/>.</summary>
    public StatusBarPresenter StatusBar => _statusBar ??= new StatusBarPresenter(
        () => Updates?.Now() ?? DateTimeOffset.UtcNow,
        () => Updates?.LastPass,
        () => Updates?.NextPass,
        Activity,
        action => Dispatcher.UIThread.Post(action));

    /// <summary>Marks a background activity for the status bar until the scope is disposed.</summary>
    internal IDisposable BeginActivity(string textKey) => Activity.Begin(T(textKey));
}
