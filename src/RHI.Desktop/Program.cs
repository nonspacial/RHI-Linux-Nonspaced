using Avalonia;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The one app log: failures the UI only summarises (detectors, upstream data, store writes) and, through
        // UiExceptionGuard (the only unhandled-exception hook, installed by App), every unexpected exception.
        AppLog.Current = new AppLog(LinuxPaths.AppLogPath);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
