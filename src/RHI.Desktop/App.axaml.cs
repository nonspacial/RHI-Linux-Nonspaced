using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var detectors = new IGameDetector[]
            {
                new SteamGameDetector(),
                new HeroicGameDetector(),
                new LutrisGameDetector(),
                new WinePrefixDetector()
            };
            var scanner = new GameLibraryScanner(detectors);
            var launcher = new LinuxGameLauncher();
            var deployment = new TransactionalFileDeploymentService();
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var dlss = new DlssManagerService(http, deployment, Path.Combine(LinuxPaths.CacheHome, "rhi-linux"));
            var vm = new MainWindowViewModel(scanner, launcher, dlss);
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
