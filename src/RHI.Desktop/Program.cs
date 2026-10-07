using Avalonia;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Failures the UI only summarises (detectors, upstream data, crashes) are kept for bug reports.
        AppLog.Current = new AppLog(LinuxPaths.AppLogPath);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Warn("unhandled", e.ExceptionObject?.ToString() ?? "unknown exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.Warn("unobserved-task", e.Exception.ToString());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
