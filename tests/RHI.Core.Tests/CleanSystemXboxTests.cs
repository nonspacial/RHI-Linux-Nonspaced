using RHI.Core.Services;
using RHI.Desktop;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// Owner requirement: on a system with no Xodus and no Xbox installer app at all (no breadcrumb folder, no default state
/// folder, no Xodus installs, an empty or missing <c>~/Games</c>), Xbox detection is completely silent: no Xbox card, no
/// scan warning, nothing in rhi.log, nothing Xbox-related in any card menu, no dialog. The app's own composition (the
/// detector list <c>App</c> uses) runs against a whole fake system: HOME, XDG folders, PATH and XDG_DATA_DIRS all point
/// into an empty root for this test's async flow only. The Settings tab for Xbox / Game Pass folders still shows.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CleanSystemXboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-clean-system-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanSystem_XboxDetectionIsSilent_AndTheSettingsTabStillShows(bool emptyGamesFolder)
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        if (emptyGamesFolder) Directory.CreateDirectory(Path.Combine(home, "Games"));
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var share = Directory.CreateDirectory(Path.Combine(_root, "share")).FullName;
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var language = UiLocalizationService.CurrentLanguage;
        using (LinuxPaths.UseEnvironmentInThisFlow(new Dictionary<string, string> { ["HOME"] = home, ["PATH"] = bin, ["XDG_DATA_DIRS"] = share }))
        using (AppLog.UseInThisFlow(log))
        using (var http = new HttpClient(new Offline()))
        {
            Assert.Equal(home, LinuxPaths.Home);
            Assert.StartsWith(home + "/", LinuxPaths.StateHome, StringComparison.Ordinal);
            var wineOverrides = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
            var services = AppComposition.Create(http, new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(), wineOverrides,
                RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wineOverrides));

            var scan = await services.Scanner.ScanWithReportAsync(null);

            Assert.Contains(scan.Detectors, d => d.Name == RHI.Platform.Linux.Detection.Xbox.XboxGameDetector.SourceName);
            Assert.Empty(scan.Games);
            Assert.All(scan.Detectors, d =>
            {
                Assert.Null(d.Error);
                Assert.True(d.Warnings.Count == 0, $"{d.Name}: {string.Join(" | ", d.Warnings)}");
            });
            // Offline here, so the upstream manifest step reports itself; nothing about Xbox may appear.
            Assert.DoesNotContain("Xbox", LibraryScanStatusText.Problems(scan, null) ?? "", StringComparison.OrdinalIgnoreCase);
            // Offline, the upstream data steps log their fetch failures; the scan itself logs nothing else.
            var noise = (File.Exists(log.FilePath) ? File.ReadAllLines(log.FilePath) : [])
                .Where(line => !line.Contains("[pcgw-data]", StringComparison.Ordinal) &&
                               !line.Contains("[scan] Upstream RHI manifest not applied", StringComparison.Ordinal))
                .ToList();
            Assert.True(noise.Count == 0, "rhi.log: " + string.Join("\n", noise));
            Assert.False(Directory.Exists(Path.Combine(home, ".local", "state", XboxInstallerApp.FolderName))); // read, never created

            try
            {
                using var vm = services.CreateViewModel();
                var dialogs = new List<string>();
                vm.ConfirmAsync = (title, message, _) => { dialogs.Add(title + ": " + message); return Task.FromResult(false); };
                await vm.Initialization.WaitAsync(TimeSpan.FromSeconds(60));

                Assert.Empty(vm.Games);
                Assert.Empty(dialogs);
                Assert.DoesNotContain("Xbox", vm.StatusText ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(File.Exists(log.FilePath) ? File.ReadAllLines(log.FilePath) : [],
                    line => line.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || line.Contains(XboxInstallerApp.ProductTag, StringComparison.OrdinalIgnoreCase));
                // Nothing Xbox-related on an ordinary card's menu either.
                var card = new RHI.Core.Models.DetectedGame { Name = "Plain", InstallPath = home, Source = "Manual" };
                Assert.DoesNotContain(vm.CardMenuFor(new GameRowViewModel(card)), item => item.Id == "show-in-owning-app");

                // The Settings tab is there: its list (empty), its plain description naming Xodus, and Add.
                var folders = Assert.IsType<XboxFoldersPresenter>(vm.Settings.XboxFolders);
                Assert.Empty(folders.Folders);
                Assert.Contains("Xodus", XboxFoldersPresenter.Intro, StringComparison.Ordinal);
                Assert.Equal("https://github.com/xodus-gaming/xodus", XboxFoldersPresenter.XodusUrl);
                Assert.False(string.IsNullOrWhiteSpace(XboxFoldersPresenter.AddText));
            }
            finally
            {
                UiLocalizationService.Apply(language);
            }
        }
    }

    [Fact]
    public async Task UserAddedFolderThatWentMissing_StillWarns()
    {
        // The one Xbox warning a clean system may still see: a folder the user added that is gone.
        var gone = Path.Combine(_root, "gone", "xbox-state");
        var inputs = new RHI.Platform.Linux.Detection.Xbox.XboxDiscoveryInputs(
            Path.Combine(_root, "home"), [], [gone], null, new RHI.Platform.Linux.Detection.Xbox.OwningAppLocator([], []));

        var (games, warnings) = await DetectorDiagnostics.CollectAsync(() =>
            Task.FromResult(RHI.Platform.Linux.Detection.Xbox.XboxGameDetector.DetectIn(inputs, CancellationToken.None)));

        Assert.Empty(games);
        Assert.Contains(warnings, warning => warning.Contains(gone, StringComparison.Ordinal));
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }
}
