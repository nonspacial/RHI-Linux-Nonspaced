using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        UiExceptionGuard.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var githubTokenPath = Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "github_api.txt");
            var githubToken = new GitHubApiTokenHandler(githubTokenPath);
            // Subscribed before any service can send; the post runs once this method returns.
            MainWindowViewModel? tokenNoticeTarget = null;
            githubToken.TokenRejected += (_, _) => Dispatcher.UIThread.Post(() => tokenNoticeTarget?.ReportGitHubTokenRejected());
            githubToken.TokenFileExposed += (_, e) => Dispatcher.UIThread.Post(() => tokenNoticeTarget?.ReportGitHubTokenFileExposed(e.Restricted));
            var http = new HttpClient(githubToken) { Timeout = TimeSpan.FromMinutes(5) };
            var records = new LinuxDeploymentRecordStore();
            var wineOverrides = new WineDllOverrideService();
            // Roll back deployments a crash interrupted and sweep their leftovers (#23) before any
            // service exists that could deploy (installs, start-up auto-update).
            // The ledger lets a deferred override undo see a later install that depends on it (w2 wine review, major 1).
            var journal = LinuxDeploymentRecovery.CreateJournal(wineOverrides, records: records);
            var recovery = RunDeploymentStartup(desktop, journal, records);
            var services = AppComposition.Create(http, records, wineOverrides, journal);
            UiLocalizationService.Apply(services.SettingsStore.Load().UiLanguage);
            var vm = services.CreateViewModel();
            tokenNoticeTarget = vm;
            UiExceptionGuard.SetReporter(message =>
            {
                if (Dispatcher.UIThread.CheckAccess()) vm.StatusText = message;
                else Dispatcher.UIThread.Post(() => vm.StatusText = message);
            });
            if (recovery.NeedsNotice)
                Dispatcher.UIThread.Post(() => vm.ReportDeploymentRecovery(recovery));
            desktop.Exit += (_, _) => vm.Dispose();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
                UpdateLogStore = services.UpdateLog,
                HdrModsCatalog = new AvailableHdrModsCatalog(services.RenoDxCatalog, services.LumaCatalog),
                UpstreamManifest = services.UpstreamManifest
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>This process's registration; held (flock) for the app's lifetime so other instances skip their sweep.</summary>
    private static DeploymentInstanceGate? s_instanceGate;

    /// <summary>
    /// Start-up recovery and leftover sweep, synchronously: nothing may deploy until both are done.
    /// Never throws; every outcome goes to the recovery log and the returned summary.
    /// </summary>
    private static DeploymentRecoverySummary RunDeploymentStartup(
        IClassicDesktopStyleApplicationLifetime desktop, DeploymentJournal journal, LinuxDeploymentRecordStore records)
    {
        try
        {
            s_instanceGate = DeploymentInstanceGate.Register(LinuxDeploymentRecovery.InstanceDirectory);
            desktop.Exit += (_, _) => s_instanceGate?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("deployment-recovery", "Could not register this RHI instance; the leftover sweep is skipped", ex);
        }
        // Normally an empty directory listing; runs off the UI thread's context (no deadlock).
        var report = Task.Run(() => LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, s_instanceGate)).GetAwaiter().GetResult();
        return LinuxDeploymentRecovery.Record(report);
    }
}
