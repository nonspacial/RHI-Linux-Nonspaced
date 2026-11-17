using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-c (upstream 2.9.0 top bar: "Update All (N)" or "All up to date"): the library-wide check-only pass runs Update
/// All's own checks over every game it covers, updates nothing, leaves out what the update inclusion leaves out, and stops at
/// GitHub's request limit, saying how many games it did not reach. The top bar claims "All up to date" only once every game
/// it covers is checked; the game list marks the games with updates from the same results.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdateCheckPassTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-check-pass-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly LinuxDeploymentRecordStore _records;

    public UpdateCheckPassTests()
    {
        _records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        UiLocalizationService.Apply("en-US"); // the expected lines are English (the UI language is process-wide)
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ── The pass ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pass_CountsTheGamesWithAnUpdate_AndChangesNothing()
    {
        var updated = Game("Has Update", ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX);
        var current = Game("Current", ManagedComponentKind.ReShade);
        var bare = Game("No Components");
        var offline = Game("Offline", ManagedComponentKind.RenoDX);
        var backend = new Backend
        {
            [("Has Update", ManagedComponentKind.RenoDX)] = new(true, true, "newer"),
            [("Offline", ManagedComponentKind.RenoDX)] = new(true, false, "offline", CouldNotCheck: true),
        };

        var result = await Service(backend).CheckOnlyAsync([updated, current, bare, offline], NoFiles, stop: null);

        Assert.NotNull(result);
        Assert.False(result!.CutShort);
        Assert.True(result.Games[updated]);
        Assert.False(result.Games[current]);
        Assert.False(result.Games[bare]); // nothing RHI installed: nothing to update
        Assert.False(result.Games.ContainsKey(offline)); // could not tell: keeps what was known
        Assert.Equal(0, backend.Updates); // a check never updates
    }

    [Fact]
    public async Task Pass_LeavesOutWhatTheUpdateInclusionLeavesOut_AndHeldUpdates()
    {
        var excluded = Game("Excluded", ManagedComponentKind.ReShade);
        var held = Game("Held", ManagedComponentKind.ShortFuse);
        var backend = new Backend
        {
            [("Excluded", ManagedComponentKind.ReShade)] = new(true, true, "newer"),
            [("Held", ManagedComponentKind.ShortFuse)] = new(true, true, "held", HeldBy: "NR Cost Scaler"),
        };
        backend.Excluded.Add(("Excluded", ManagedComponentKind.ReShade));

        var result = await Service(backend).CheckOnlyAsync([excluded, held], NoFiles, stop: null);

        Assert.False(result!.Games[excluded]);
        Assert.False(result.Games[held]);
        Assert.DoesNotContain(("Excluded", ManagedComponentKind.ReShade), backend.Checked);
    }

    [Fact]
    public async Task Pass_StopsAtGitHubsRequestLimit_AndCountsTheGamesItDidNotReach()
    {
        var games = new[] { Game("One", ManagedComponentKind.RenoDX), Game("Two", ManagedComponentKind.RenoDX), Game("Three", ManagedComponentKind.RenoDX) };
        var backend = new Backend();
        var limited = false;
        backend.OnCheck = () => limited = true; // the first game's lookup used the last request

        var result = await Service(backend).CheckOnlyAsync(games, NoFiles, stop: () => limited);

        Assert.True(result!.CutShort);
        Assert.Equal(2, result.NotChecked);
        Assert.Equal([games[0]], result.Games.Keys);
    }

    [Fact]
    public async Task Pass_AsksForTheFileIndex_OnlyForGamesWithComponents()
    {
        var asked = new List<string>();
        var games = new[] { Game("Bare"), Game("Modded", ManagedComponentKind.RenoDX) };
        await Service(new Backend()).CheckOnlyAsync(games, (game, _) =>
        {
            asked.Add(game.Name);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }, stop: null);
        Assert.Equal(["Modded"], asked);
    }

    [Fact]
    public async Task Pass_RunsOneAtATime()
    {
        var backend = new Backend { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(backend);
        var first = service.CheckOnlyAsync([Game("Slow", ManagedComponentKind.RenoDX)], NoFiles, stop: null);
        Assert.Null(await service.CheckOnlyAsync([Game("Other")], NoFiles, stop: null));
        backend.Gate.SetResult();
        Assert.NotNull(await first);
    }

    // ── The top bar ────────────────────────────────────────────────────────────

    [Fact]
    public void TopBar_SaysAllUpToDate_OnlyOnceEveryCoveredGameIsChecked()
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
        var bar = new TopBarPresenter(() => DateTimeOffset.UtcNow, () => null, () => covered);
        Assert.False(bar.AllUpToDate); // nothing checked yet
        Assert.Equal("Update All", bar.UpdateAllText);

        bar.NoteCheckPass(new Dictionary<string, bool> { ["a"] = false }, notChecked: 1);
        Assert.False(bar.AllUpToDate); // b was not reached
        Assert.Contains("1 game(s) were not checked", bar.UpdateAllTip, StringComparison.Ordinal);

        bar.NoteCheckPass(new Dictionary<string, bool> { ["a"] = false, ["b"] = false }, notChecked: 0);
        Assert.True(bar.AllUpToDate);
        Assert.Equal("All up to date", bar.UpdateAllText);
        Assert.DoesNotContain("not checked", bar.UpdateAllTip, StringComparison.Ordinal);

        covered.Add("c"); // a game added since
        bar.LibraryChanged();
        Assert.False(bar.AllUpToDate);
    }

    [Fact]
    public void TopBar_CountsUpdates_FromThePassAndFromAGamesOwnAnalysis()
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
        var bar = new TopBarPresenter(() => DateTimeOffset.UtcNow, () => null, () => covered);
        bar.NoteCheckPass(new Dictionary<string, bool> { ["a"] = true, ["b"] = false }, notChecked: 0);
        Assert.Equal(1, bar.UpdateCount);
        Assert.False(bar.AllUpToDate);
        Assert.True(bar.HasUpdate("a"));

        bar.NoteGame("a", hasUpdate: false); // opened and analysed since: current now
        Assert.True(bar.AllUpToDate);

        bar.UpdateAllRan(); // nothing is known until the next check
        Assert.False(bar.AllUpToDate);
        Assert.Equal(0, bar.UpdateCount);
    }

    [Fact]
    public void TopBar_HiddenGamesAreNotCovered()
    {
        var bar = new TopBarPresenter(() => DateTimeOffset.UtcNow, () => null, () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shown" });
        bar.NoteCheckPass(new Dictionary<string, bool> { ["shown"] = false, ["hidden"] = true }, notChecked: 0);
        Assert.Equal(0, bar.UpdateCount);
        Assert.True(bar.AllUpToDate);
    }

    // ── Through the shell ──────────────────────────────────────────────────────

    [Fact]
    public async Task Shell_CheckPass_CoversTheLibrary_AndMarksTheRows()
    {
        using var fx = await ViewModelFixture.StartAsync([GameIn("Alpha"), GameIn("Beta")]);
        Assert.False(fx.Vm.TopBar.AllUpToDate);

        await fx.Vm.Updates.RunCheckOnlyPassAsync(CancellationToken.None);

        Assert.True(fx.Vm.TopBar.AllUpToDate); // no game has a component RHI installed
        Assert.NotNull(fx.Vm.Updates.LastCheck);
        var alpha = fx.Vm.Games.Single(row => row.Name == "Alpha");
        Assert.False(alpha.HasUpdate);

        fx.Vm.TopBar.NoteGame(LinuxGameLibraryStore.GameKey(alpha.Game), hasUpdate: true);
        Assert.True(alpha.HasUpdate);
        Assert.Equal(1, fx.Vm.TopBar.UpdateCount);
    }

    [Fact]
    public async Task Shell_CheckPass_CutShortByTheRateLimit_SaysSo_AndClaimsNothing()
    {
        using var fx = await ViewModelFixture.StartAsync([GameIn("Alpha"), GameIn("Beta")]);
        fx.Vm.Updates.GitHubRateLimited = () => true;

        await fx.Vm.Updates.RunCheckOnlyPassAsync(CancellationToken.None);

        Assert.False(fx.Vm.TopBar.AllUpToDate);
        Assert.StartsWith("Update check cut short by GitHub's request limit: 2 game(s) not checked.", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_CheckLoop_StartsOnlyWhenTheAppTurnsItOn()
    {
        using var fx = await ViewModelFixture.StartAsync([GameIn("Alpha")]);
        Assert.Null(fx.Vm.Updates.LastCheckPass); // tests and the fixture: off
        await fx.Vm.Updates.RequestCheckPass(); // an Update All's request too
        Assert.Null(fx.Vm.Updates.LastCheckPass);

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.Updates.LoopDelay = async (wait, token) => { ran.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        fx.Vm.Updates.CheckLoopEnabled = true;
        fx.Vm.Updates.RestartCheckLoop();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the first pass ran, then the loop waits its interval
        Assert.NotNull(fx.Vm.Updates.LastCheckPass);
        Assert.True(fx.Vm.TopBar.AllUpToDate);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    [Fact]
    public void Strings_ExistInBothLanguages()
    {
        foreach (var key in new[] { "Top.AllUpToDate", "Top.AllUpToDateTip", "Top.UpdateAllFoundTip", "Top.CheckCutShort", "Top.CheckCutShortStatus", "Sb.ActCheckingLibrary", "Lib.UpdateTip" })
        {
            // Read from the tables, not by switching the UI language (other tests read it).
            var row = Assert.Single(RHI.Desktop.Localization.LayoutsCUiStrings.Entries.Concat(RHI.Desktop.Localization.UiStrings.Entries), e => e.Key == key);
            Assert.False(string.IsNullOrWhiteSpace(row.EnUs));
            Assert.NotEqual(row.EnUs, row.PtBr);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly Func<DetectedGame, CancellationToken, Task<IReadOnlyList<string>>> NoFiles = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);

    private LinuxUpdateAllService Service(Backend backend) => new(_records, backend);

    private DetectedGame Game(string name, params ManagedComponentKind[] components)
    {
        var game = GameIn(name);
        foreach (var component in components)
            _records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = component, BinaryDirectory = game.BinaryDirectory!, Version = "1"
            });
        return game;
    }

    private DetectedGame GameIn(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }

    private sealed class Backend : Dictionary<(string Game, ManagedComponentKind Component), ManagedUpdateCheck>, ILinuxManagedUpdateBackend
    {
        public HashSet<(string, ManagedComponentKind)> Excluded { get; } = [];
        public List<(string, ManagedComponentKind)> Checked { get; } = [];
        public int Updates { get; private set; }
        public Action? OnCheck { get; set; }
        public TaskCompletionSource? Gate { get; set; }

        public async Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles,
            CancellationToken cancellationToken = default)
        {
            if (Gate is { } gate) await gate.Task;
            Checked.Add((game.Name, component));
            OnCheck?.Invoke();
            return TryGetValue((game.Name, component), out var check) ? check : new ManagedUpdateCheck(true, false, "current");
        }

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles,
            CancellationToken cancellationToken = default)
        {
            Updates++;
            throw new InvalidOperationException("a check pass never updates");
        }

        public UpdateExclusion UpdateExclusionFor(DetectedGame game, ManagedComponentKind component)
            => Excluded.Contains((game.Name, component)) ? UpdateExclusion.ThisGame : UpdateExclusion.None;
    }
}
