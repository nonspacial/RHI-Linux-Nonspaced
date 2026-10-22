using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #233 through the view models and Update All: a stalled download reads as its own failure line (EN and PT-BR), never
/// as "Operation cancelled."; a real Cancel still does; a family whose check or update stalls counts as could not be
/// checked / failed for that game and the pass carries on to the next game; nothing reaches the game folder.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DownloadIdleTimeoutViewModelTests : IDisposable
{
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(300);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-idle-vm-{Guid.NewGuid():N}");

    public DownloadIdleTimeoutViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("en-US", "The download stopped: no data arrived for 1 seconds. Source: GitHub API")]
    [InlineData("pt-BR", "O download parou: nenhum dado chegou durante 1 segundos. Origem: GitHub API")]
    public async Task StalledCheck_ShowsTheStallLine_NeverTheCancelLine(string language, string stallLine)
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, httpResponse: request
            => request.RequestUri!.AbsoluteUri == UltimateAsiLoaderDeploymentService.GitHubApiUrl ? StalledHttp.Stalled(bytesFirst: 10) : null);
        await AddAndAnalyseAsync(fx, "Tunic");
        UiLocalizationService.Apply(language);
        try
        {
            await fx.Vm.Extras.CheckUalUpdateCommand.ExecuteAsync(null).WaitAsync(StalledHttp.HangGuard);

            Assert.False(fx.Vm.IsBusy);
            Assert.Contains(stallLine, fx.Vm.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
            if (language == "pt-BR") Assert.DoesNotContain("The download stopped", fx.Vm.StatusText, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task CancelDuringAStalledCheck_ShowsTheCancelLine_NotTheStallLine()
    {
        TrustedDownload.IdleLimitForTesting = TimeSpan.FromSeconds(30);
        var body = new StallingStream(10);
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request
            => request.RequestUri!.AbsoluteUri == UltimateAsiLoaderDeploymentService.GitHubApiUrl ? StalledHttp.Stalled(stream: body) : null);
        await AddAndAnalyseAsync(fx, "Tunic");

        var check = fx.Vm.Extras.CheckUalUpdateCommand.ExecuteAsync(null);
        await body.Stalled.WaitAsync(StalledHttp.HangGuard); // the body read is waiting: Cancel lands inside it
        Assert.True(fx.Vm.Operations.CanCancel);
        fx.Vm.Operations.CancelCommand.Execute(null);
        await check.WaitAsync(StalledHttp.HangGuard);

        Assert.False(fx.Vm.IsBusy);
        Assert.StartsWith(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(DownloadStalledException.MessagePrefix, fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // A check that stalls (Display Commander's release list), an update whose download stalls (ReLimiter's add-on), then
    // a game after both: the pass ends, the stalled check is "could not be checked", the stalled update failed, the next
    // game was still checked, and nothing reached the stalled update's game folder.
    [Theory]
    [InlineData(LinuxUpdateRunMode.Manual)]
    [InlineData(LinuxUpdateRunMode.Background)]
    public async Task UpdateAll_AFamilyThatStalls_CountsForThatGame_AndThePassCarriesOn(LinuxUpdateRunMode mode)
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var (stalledCheck, stalledUpdate, after) = (PeGame("Check Stalls"), PeGame("Update Stalls"), PeGame("After"));
        using var fx = await ViewModelFixture.StartAsync([stalledCheck, stalledUpdate, after], httpResponse: Route);
        var games = new[] { stalledCheck, stalledUpdate, after }.Select(game => fx.Vm.AllLibraryGames.Single(listed => listed.Name == game.Name)).ToArray();
        SeedRecords(fx, games);

        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync(games, _ => [], mode)
            .WaitAsync(StalledHttp.HangGuard);

        var check = Assert.Single(result.Items, item => item.GameName == "Check Stalls");
        Assert.True(check.Unchecked, check.Message);
        Assert.Contains("The download stopped: no data arrived for 1 seconds. Source: GitHub API", check.Message, StringComparison.Ordinal);
        var update = Assert.Single(result.Items, item => item.GameName == "Update Stalls");
        Assert.False(update.Success, update.Message);
        Assert.False(update.Unchecked, update.Message);
        Assert.Contains("The download stopped: no data arrived for 1 seconds. Source: ", update.Message, StringComparison.Ordinal);
        Assert.Single(result.Items, item => item.GameName == "After" && item.Unchecked); // the pass went on to the next game
        Assert.Equal((0, 0, 1, 2), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        Assert.False(File.Exists(Path.Combine(games[1].BinaryDirectory!, "relimiter.addon64")));
        Assert.Equal("1.0", fx.Records.Find(games[1], ManagedComponentKind.ReLimiter)!.Version);
        Assert.Empty(Directory.EnumerateFiles(games[1].BinaryDirectory!, "*" + AtomicFile.TempMarker + "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UpdateAll_StatusLine_CountsTheStalls_NeverSaysCancelled()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var (stalledCheck, stalledUpdate, after) = (PeGame("Check Stalls"), PeGame("Update Stalls"), PeGame("After"));
        using var fx = await ViewModelFixture.StartAsync([stalledCheck, stalledUpdate, after], httpResponse: Route);
        SeedRecords(fx, new[] { stalledCheck, stalledUpdate, after }.Select(game => fx.Vm.AllLibraryGames.Single(listed => listed.Name == game.Name)).ToArray());
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(StalledHttp.HangGuard);

        Assert.False(fx.Vm.IsBusy);
        Assert.StartsWith("Update All: 0 updated · 0 current · 0 deferred · 1 failed · 2 could not be checked", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains(DownloadStalledException.MessagePrefix, fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // #233 audit: a manual install and the background pass take the same busy gate, so two stagings of one OptiScaler
    // package never run at once in the app: while the background pass holds the gate, an OptiScaler install asks nothing.
    [Fact]
    public async Task OptiScalerInstall_WhileTheBackgroundPassRuns_DoesNotStart()
    {
        var requests = new List<string>();
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request =>
        {
            lock (requests) requests.Add(request.RequestUri!.AbsoluteUri);
            return null;
        });
        await AddAndAnalyseAsync(fx, "Tunic");
        lock (requests) requests.Clear();
        using var background = fx.Vm.Operations.TryBegin("background-update")!; // what the background pass claims

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(StalledHttp.HangGuard);

        Assert.Equal("background-update", fx.Vm.Operations.CurrentKind);
        lock (requests) Assert.DoesNotContain(requests, url => url.Contains("optiscaler", StringComparison.OrdinalIgnoreCase));
        Assert.Null(fx.Vm.Operations.TryBegin("component")); // and a manual operation cannot claim the gate either
    }

    private const string ReLimiterApi = "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest";
    private const string ReLimiterAsset = "https://github.com/rhi-tests/relimiter/releases/download/v3.3.5/relimiter.addon64";
    private const string DisplayCommanderApi = "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest";

    private static HttpResponseMessage? Route(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri switch
    {
        DisplayCommanderApi => StalledHttp.Stalled(bytesFirst: 10),
        ReLimiterApi => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"tag_name":"v3.3.5","name":"v3.3.5","body":"","assets":[{"name":"relimiter.addon64","browser_download_url":"{{ReLimiterAsset}}"}]}""")
        },
        ReLimiterAsset => StalledHttp.Stalled(bytesFirst: 8192),
        _ => null,
    };

    private static void SeedRecords(ViewModelFixture fx, IReadOnlyList<DetectedGame> games)
    {
        LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind) => new()
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
            Version = "1.0", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1.0/x.zip"
        };
        fx.Records.Upsert(Record(games[0], ManagedComponentKind.DisplayCommander));
        fx.Records.Upsert(Record(games[1], ManagedComponentKind.ReLimiter));
        fx.Records.Upsert(Record(games[2], ManagedComponentKind.DofFix));
    }

    /// <summary>A Windows game with a real x64 exe and a ReShade on disk (what ReLimiter's update asks for).</summary>
    private DetectedGame PeGame(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var reshade = CacheForeignFileRejectionTests.Pe(0x8664, 2 * 1024 * 1024, 9);
        System.Text.Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 1024);
        File.WriteAllBytes(Path.Combine(dir, "dxgi.dll"), reshade);
        return new DetectedGame { Name = name, Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir };
    }

    private static async Task AddAndAnalyseAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        File.WriteAllBytes(game.ExecutablePath!, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7)); // x64: the UAL check runs
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
            {
                Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
            });
            Assert.True(added, message);
            await analysed.Task.WaitAsync(StalledHttp.HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(StalledHttp.HangGuard);
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
