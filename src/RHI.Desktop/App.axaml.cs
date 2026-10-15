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
            // GitHub API answers are revalidated with ETags for the session (#35): a 304 costs no rate limit.
            var http = new HttpClient(new GitHubConditionalCacheHandler(githubToken)) { Timeout = TimeSpan.FromMinutes(5) };
            var records = new LinuxDeploymentRecordStore();
            // Roll back deployments a crash interrupted and sweep their leftovers (#23) before any
            // service exists that could deploy (installs, start-up auto-update).
            var (wineOverrides, journal) = AppComposition.CreateWineRecovery(records);
            var recovery = RunDeploymentStartup(desktop, journal, records);
            var services = AppComposition.Create(http, records, wineOverrides, journal,
                displayModes: new RHI.Platform.Linux.Hdr.DisplayModeSwitcher()); // w5/shell-launch (#191): only the app touches the display
            UiLocalizationService.Apply(services.SettingsStore.Load().UiLanguage);
            var vm = services.CreateViewModel();
            vm.Parity = services.Parity; // w3/parity hook
            tokenNoticeTarget = vm;
            UiExceptionGuard.SetReporter(message =>
            {
                if (Dispatcher.UIThread.CheckAccess()) vm.StatusText = message;
                else Dispatcher.UIThread.Post(() => vm.StatusText = message);
            });
            if (recovery.NeedsNotice)
                Dispatcher.UIThread.Post(() => vm.ReportDeploymentRecovery(recovery));
            desktop.Exit += (_, _) => vm.Dispose();
            if (services.Parity is { } parity) // w3/parity hook: start-up update check (#100), single-instance hand-off (#162, #99)
            {
                UiExceptionGuard.Forget(parity.AppUpdate.NotifyAtStartupAsync(vm.Initialization, vm.ReportParityNotice), "AppUpdateCheck");
                parity.GameLinks.Prefetch(); // #174/#183: the lists behind the card menu's web pages (Nexus, PCGamingWiki, Ultra+, …; 24 h caches)
                if (services.NewModsSources is { } newMods) UiExceptionGuard.Forget(vm.NewMods.CheckAsync(vm.Initialization, newMods), "NewMods"); // #185
                var handoff = RHI.Platform.Linux.Ux.InstanceHandoff.Listen(request =>
                    Dispatcher.UIThread.Post(() => UiExceptionGuard.Forget(parity.HandleHandoffAsync(vm, request), "handoff")));
                desktop.Exit += (_, _) => handoff?.Dispose();
                if (Program.PendingHandoff is { } pending)
                    Dispatcher.UIThread.Post(() => UiExceptionGuard.Forget(parity.HandleHandoffAsync(vm, pending), "handoff"));
            }
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
                UpdateLogStore = services.UpdateLog,
                HdrModsCatalog = services.HdrModsCatalog, // w3/sync #126 hook; #197: built by AppComposition
                UpstreamManifest = services.UpstreamManifest
            };
            if (services.Parity is { } windowParity) ((MainWindow)desktop.MainWindow).AttachParity(windowParity); // w3/parity hook (#99)
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
