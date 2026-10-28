using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #244 end to end: a GitHub API release lookup that never answers gives up after the API timeout (here shortened): a
/// single check shows the timeout line in the UI's language, Update All counts the family as could not be checked; never
/// "current", never "Operation cancelled.".
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GitHubApiTimeoutViewModelTests : IDisposable
{
    private const string DisplayCommanderApi = "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-ghc-vm-{Guid.NewGuid():N}");

    public GitHubApiTimeoutViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("en-US", "The server took too long to answer: RHI stopped waiting after 1 second. Source: GitHub API")]
    [InlineData("pt-BR", "O servidor demorou demais para responder: o RHI parou de esperar após 1 segundo. Origem: GitHub API")]
    public async Task UalCheck_ApiTimeout_ShowsTheTimeoutLine_NeverTheCancelLine(string language, string timeoutLine)
    {
        GitHubConditionalCacheHandler.ApiTimeoutForTesting = TimeSpan.FromMilliseconds(300);
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, gitHubCache: true,
            hangWhen: request => request.RequestUri!.AbsoluteUri == UltimateAsiLoaderDeploymentService.GitHubApiUrl);
        await AddAndAnalyseAsync(fx, "Tunic");
        UiLocalizationService.Apply(language);
        try
        {
            await fx.Vm.Extras.CheckUalUpdateCommand.ExecuteAsync(null).WaitAsync(StalledHttp.HangGuard);

            Assert.False(fx.Vm.IsBusy);
            Assert.True(fx.Vm.StatusText.Contains(timeoutLine, StringComparison.Ordinal), fx.Vm.StatusText);
            Assert.DoesNotContain(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
            if (language == "pt-BR") Assert.DoesNotContain("took too long", fx.Vm.StatusText, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task UpdateAll_ApiTimeout_CountsAsCouldNotBeChecked()
    {
        GitHubConditionalCacheHandler.ApiTimeoutForTesting = TimeSpan.FromMilliseconds(300);
        var game = PeGame("Display Commander Game");
        using var fx = await ViewModelFixture.StartAsync([game], gitHubCache: true,
            hangWhen: request => request.RequestUri!.AbsoluteUri == DisplayCommanderApi);
        var listed = fx.Vm.AllLibraryGames.Single(candidate => candidate.Name == game.Name);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(listed), GameName = listed.Name, Component = ManagedComponentKind.DisplayCommander,
            BinaryDirectory = listed.BinaryDirectory!, Version = "1.0", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1.0/x.zip"
        });
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);

        // Without the API timeout the hung lookup would hold the pass until the hang guard.
        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(StalledHttp.HangGuard);

        Assert.False(fx.Vm.IsBusy);
        Assert.StartsWith("Update All: 0 updated · 0 current · 0 deferred · 0 failed · 1 could not be checked", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
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

    /// <summary>A Windows game with a real x64 exe and a ReShade on disk (what a ReShade add-on's update asks for).</summary>
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
}
