using Avalonia;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Ux;

namespace RHI.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The one app log: failures the UI only summarises (detectors, upstream data, store writes) and, through
        // UiExceptionGuard (the only unhandled-exception hook, installed by App), every unexpected exception.
        AppLog.Current = new AppLog(LinuxPaths.AppLogPath);
        // w3/parity hook: a desktop shortcut (--launch <GameKey>, #162) or an nxm:// link (#99) goes to the RHI already
        // running (single-instance hand-off); this process then exits without a window. Otherwise it starts as before
        // and handles the request itself once its library has loaded.
        PendingHandoff = HandoffRequest.FromArguments(args);
        if (PendingHandoff is { } request && InstanceHandoff.TrySend(request)) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The request this process was started with and could not pass on (handled by <see cref="App"/>).</summary>
    internal static HandoffRequest? PendingHandoff { get; private set; }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
