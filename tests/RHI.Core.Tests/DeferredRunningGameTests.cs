using System.Collections.Concurrent;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #242 (upstream 2.8.8 <c>79f5680</c>, AutoUpdateService retry queue): with automatic updates on, a game the background
/// pass deferred because it was running gets a pass of its own soon after it closes, not at the next scheduled pass. The
/// poll is driven by a test clock (<see cref="Clock"/>): every wait is seen and released by the test, never timed.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DeferredRunningGameTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private const string ReLimiterLatest = "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-deferred-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly Clock _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task RunningGame_IsDeferred_ThenUpdatedOnceAfterItCloses()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var game = Game("Closing Quest");
        Record(records, game, ManagedComponentKind.ReLimiter);
        var probe = new Probe { Running = true };
        var backend = new UpdatingBackend(records);
        var service = new LinuxUpdateAllService(records, backend, probe);
        var passes = 0;
        var retry = Retry(service.IsRunning, async (closed, token) =>
        {
            passes++;
            return (await service.RunAsync([closed], _ => [], LinuxUpdateRunMode.Background, token)).RunningGames.Count > 0;
        });

        var first = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);
        Assert.Equal((0, 1), (first.ChangedCount, first.DeferredCount));
        var run = retry.Add(first.RunningGames, CancellationToken.None);
        await _clock.WaitingAsync();
        await Polls(1); // still running
        Assert.Equal(0, passes);
        probe.Running = false; // the user closed the game
        _clock.Release();
        await run.WaitAsync(HangGuard); // nothing left to wait for: the poll ends

        Assert.Equal(1, passes);
        Assert.Equal([ManagedComponentKind.ReLimiter], backend.UpdateCalls);
        Assert.Equal("1-new", records.Find(game, ManagedComponentKind.ReLimiter)!.Version);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], _clock.Waits);
        Assert.Empty(retry.Pending);
        Assert.False(service.IsRunning(game));
    }

    [Fact]
    public async Task StillRunning_GetsNoPass_AndEveryPollWaitsTheInterval()
    {
        var probe = new Probe { Running = true };
        var passes = 0;
        using var stop = new CancellationTokenSource();
        var retry = Retry(probe.IsRunning, (_, _) => { passes++; return Task.FromResult(false); });
        var run = retry.Add([Game("Long Session")], stop.Token);

        await _clock.WaitingAsync();
        await Polls(3);
        stop.Cancel();
        await run.WaitAsync(HangGuard); // ends quietly when the loop stops

        Assert.Equal((0, 3, 4), (passes, probe.Calls, _clock.Waits.Count));
        Assert.Single(retry.Pending);
    }

    [Fact]
    public async Task AutomaticUpdatesTurnedOff_RunNothing_AndForgetTheGames()
    {
        var probe = new Probe { Running = true };
        var wanted = true;
        var passes = 0;
        var retry = Retry(probe.IsRunning, (_, _) => { passes++; return Task.FromResult(false); }, () => wanted);
        var run = retry.Add([Game("Off Quest")], CancellationToken.None);

        await _clock.WaitingAsync();
        probe.Running = false;
        wanted = false; // Settings: automatic updates off while the poll waits
        _clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal((0, 0), (passes, probe.Calls));
        Assert.Empty(retry.Pending);
        // Off before the first wait: no poll at all.
        var idle = Retry(probe.IsRunning, (_, _) => { passes++; return Task.FromResult(false); }, () => false);
        await idle.Add([Game("Off Again")], CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal((0, 1), (passes, _clock.Waits.Count));
        Assert.Empty(idle.Pending);
    }

    [Fact]
    public async Task ProbeThatThrows_KeepsWaiting_WithoutSpinning_AndIsLoggedOnce()
    {
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var probe = new Probe { Throws = true };
        var passes = 0;
        var retry = Retry(probe.IsRunning, (_, _) => { passes++; return Task.FromResult(false); });
        Task run;
        using (AppLog.UseInThisFlow(log)) run = retry.Add([Game("Probe Quest")], CancellationToken.None);

        await _clock.WaitingAsync();
        await Polls(3);
        Assert.Equal((0, 3, 4), (passes, probe.Calls, _clock.Waits.Count)); // one probe call per poll, each after a full wait
        probe.Throws = false;
        _clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(1, passes);
        Assert.Single(File.ReadAllLines(log.FilePath), line => line.Contains("running-game probe failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassThatThrows_DropsTheGame_AndIsLogged()
    {
        // Left to the next scheduled pass (upstream drops it too): retrying a failing pass every minute only repeats it.
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var passes = 0;
        var retry = Retry(new Probe().IsRunning, (_, _) => { passes++; throw new IOException("ledger is not readable"); });
        Task run;
        using (AppLog.UseInThisFlow(log)) run = retry.Add([Game("Failing Quest")], CancellationToken.None);

        await _clock.WaitingAsync();
        _clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(1, passes);
        Assert.Empty(retry.Pending);
        Assert.Contains(File.ReadAllLines(log.FilePath), line => line.Contains("Failing Quest", StringComparison.Ordinal)
            && line.Contains("ledger is not readable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GamesAddedWhilePolling_JoinTheSamePoll_EachOnce()
    {
        var probe = new Probe { Running = true };
        var passed = new List<string>();
        var retry = Retry(probe.IsRunning, (game, _) => { passed.Add(game.Name); return Task.FromResult(false); });
        var first = Game("First Quest");
        var run = retry.Add([first], CancellationToken.None);
        await _clock.WaitingAsync();

        Assert.Same(run, retry.Add([Game("Second Quest"), first], CancellationToken.None));
        Assert.Equal(2, retry.Pending.Count);
        probe.Running = false;
        _clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(["First Quest", "Second Quest"], passed.Order());
        Assert.Single(_clock.Waits);
    }

    [Theory]
    [InlineData("en-US", "Deferred updates ran after the game closed: Closing Quest. Update All: 1 updated · 1 current · 0 deferred · 0 failed.")]
    [InlineData("pt-BR", "Atualizações adiadas feitas depois que o jogo fechou: Closing Quest. Atualizar tudo: 1 atualizado(s) · 1 em dia · 0 adiado(s) · 0 com falha.")]
    public void ClosedGamePassStatus_NamesTheGame_ThenTheCounts(string language, string expected)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var result = new LinuxUpdateAllResult([
                new LinuxUpdateItemResult("Closing Quest", ManagedComponentKind.ReLimiter, true, true, false, "updated"),
                new LinuxUpdateItemResult("Closing Quest", ManagedComponentKind.RenoDX, true, false, false, "current")
            ]);
            Assert.Equal(expected, UpdatesViewModel.ClosedGamePassStatus(Game("Closing Quest"), result));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // Through the real view model: the background pass defers the running game, the poll sees it close and gives it its
    // own pass (one ReLimiter release check, the only request this game's pass makes), after the busy gate is free.
    [Fact]
    public async Task ViewModel_BackgroundPass_RetriesTheClosedGame_OnceTheGateIsFree()
    {
        var game = Game("Vm Quest");
        var checks = new Checks();
        var probe = new Probe { Running = true };
        var (fx, retry) = await DeferredInViewModelAsync(game, probe, checks.Answer);
        using var disposeFx = fx;
        checks.Gate = fx.Vm.Operations;
        Assert.Equal([game.Name], retry.Pending.Select(g => g.Name));
        await Polls(1); // still running
        Assert.Equal(0, checks.Count);

        var held = fx.Vm.Operations.TryBegin("test")!;
        probe.Running = false;
        _clock.Release(); // closed, but an operation holds the gate: the pass waits for it, no new poll needed
        await WaitingForTheGateAsync(fx.Vm.Operations);
        held.Dispose();
        Assert.True(await checks.Seen.WaitAsync(HangGuard), "no pass after the gate was freed");
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Equal([false], checks.WhileTestHeldTheGate); // once, and never while the test held the gate
        Assert.Empty(retry.Pending);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    [Fact]
    public async Task ViewModel_GameStartedAgainBeforeItsPass_KeepsWaiting()
    {
        var game = Game("Again Vm Quest");
        var checks = new Checks();
        // The background pass sees it running, the poll sees it closed, its own pass sees it running again.
        var probe = new Probe { Answers = new Queue<bool>([true, false, true]) };
        var (fx, retry) = await DeferredInViewModelAsync(game, probe, checks.Answer);
        using var disposeFx = fx;

        _clock.Release();
        await _clock.WaitingAsync(); // the pass deferred it again: the poll waits for it once more

        Assert.Equal((3, 0), (probe.Calls, checks.Count));
        Assert.Equal([game.Name], retry.Pending.Select(g => g.Name));
        fx.Vm.Updates.StopBackgroundLoop();
    }

    [Fact]
    public async Task ViewModel_GameNoLongerInTheLibrary_GetsNoPass()
    {
        var checks = new Checks();
        var probe = new Probe { Running = true };
        var (fx, retry) = await DeferredInViewModelAsync(Game("Gone Quest"), probe, checks.Answer);
        using var disposeFx = fx;

        // A refresh no longer lists the game (removed while it was running): its records stay, but no pass is run for it.
        var library = (LibraryViewModel)typeof(MainWindowViewModel)
            .GetField("_library", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fx.Vm)!;
        library.SetGames([]);
        probe.Running = false;
        _clock.Release();
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Equal(0, checks.Count);
        Assert.Empty(retry.Pending);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    // End to end with a real update (a Custom ReShade whose chosen DLL the user replaced: its check is local), and the
    // status line in PT-BR.
    [Fact]
    public async Task ViewModel_ClosedGamePassThatUpdates_SaysSo_InTheUiLanguage()
    {
        var game = Game("Custom Vm Quest");
        game.PrefixPath = Directory.CreateDirectory(Path.Combine(_root, "pfx")).FullName; // an initialised Wine prefix
        File.WriteAllText(Path.Combine(game.PrefixPath, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var custom = Path.Combine(_root, "custom", "ReShade64.dll");
        WriteCustomReShade(custom, 1);
        var probe = new Probe { Running = true };
        var lines = new ConcurrentQueue<string>();
        UiLocalizationService.Apply("pt-BR");
        try
        {
            var (fx, retry) = await DeferredInViewModelAsync(game, probe, language: "pt-BR", seed: async fx =>
            {
                var installed = await fx.Managed.ApplyReShadeAsync(fx.Vm.AllLibraryGames.Single(),
                    new ReShadeInstallOptions { Channel = ReShadeChannel.Custom, CustomDllPath = custom, ManageIni = false });
                Assert.True(installed.Success, installed.Message);
                WriteCustomReShade(custom, 2); // the user put a newer build there: the check finds an update
                fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
            });
            using var disposeFx = fx;
            Assert.DoesNotContain(lines, line => line.StartsWith("Atualizações adiadas", StringComparison.Ordinal));

            probe.Running = false;
            _clock.Release();
            await retry.Run!.WaitAsync(HangGuard);

            // (The offline fixture's own notices may follow the line.)
            Assert.StartsWith("Atualizações adiadas feitas depois que o jogo fechou: Custom Vm Quest. Atualizar tudo: 1 atualizado(s) · 0 em dia · 0 adiado(s) · 0 com falha. (segundo plano)",
                lines.Distinct().Single(line => line.StartsWith("Atualizações adiadas", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(custom), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
            fx.Vm.Updates.StopBackgroundLoop();
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task ViewModel_TurningAutomaticUpdatesOff_StopsWaitingForTheGame()
    {
        var checks = new Checks();
        var probe = new Probe { Running = true };
        var (fx, retry) = await DeferredInViewModelAsync(Game("Off Vm Quest"), probe, checks.Answer);
        using var disposeFx = fx;
        var asked = probe.Calls; // the pass's own look

        probe.Running = false;
        TurnAutomaticUpdates(fx, on: false); // stops the loop and its poll; the waiting game is forgotten
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Null(fx.Vm.Updates.DeferredRetry);
        Assert.Equal((0, asked), (checks.Count, probe.Calls));
    }

    [Fact]
    public async Task ViewModel_ManualUpdateAll_StillOnlyReportsTheRunningGameAsDeferred()
    {
        var game = Game("Manual Quest");
        using var fx = await ViewModelFixture.StartAsync([game], runningProbe: new Probe { Running = true });
        fx.Vm.Updates.DeferredRetryDelay = _clock.Delay;
        Record(fx.Records, game, ManagedComponentKind.ReLimiter);
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.StartsWith("Update All: 0 updated · 0 current · 1 deferred · 0 failed.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Null(fx.Vm.Updates.DeferredRetry);
    }

    // Correction round 1, review F4: the loop stopping (a Settings change) while a closed game's pass runs ends the poll
    // quietly; it is not a failed pass in rhi.log.
    [Fact]
    public async Task LoopStoppedDuringAClosedGamesPass_IsNotLoggedAsAFailure()
    {
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var retry = Retry(new Probe().IsRunning, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return false;
        });
        Task run;
        using (AppLog.UseInThisFlow(log)) run = retry.Add([Game("Stopped Quest")], stop.Token);

        await _clock.WaitingAsync();
        _clock.Release();
        await started.Task.WaitAsync(HangGuard);
        stop.Cancel();
        await run.WaitAsync(HangGuard);

        Assert.True(run.IsCompletedSuccessfully);
        Assert.DoesNotContain(File.Exists(log.FilePath) ? File.ReadAllLines(log.FilePath) : Array.Empty<string>(),
            line => line.Contains("update pass for a game that closed failed", StringComparison.Ordinal));
    }

    // Review F2: the closed game's pass that changes nothing writes no status line (decision 2: none after every session).
    [Fact]
    public async Task ViewModel_ClosedGamePassThatChangesNothing_WritesNoStatusLine()
    {
        var checks = new Checks();
        var probe = new Probe { Running = true };
        var (fx, retry) = await DeferredInViewModelAsync(Game("Quiet Vm Quest"), probe, checks.Answer);
        using var disposeFx = fx;
        var lines = new ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };

        probe.Running = false;
        _clock.Release();
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Equal(1, checks.Count); // the pass ran (offline: its check could not tell, nothing changed)
        Assert.DoesNotContain(lines, line => line.StartsWith("Deferred updates ran", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Update All:", StringComparison.Ordinal));
        fx.Vm.Updates.StopBackgroundLoop();
    }

    // Review F3: another waiter (a queued library refresh) takes the gate between the closed game's wait for it and its
    // own claim: the pass waits for the gate again and runs only once it holds it.
    [Fact]
    public async Task ViewModel_GateTakenBeforeTheClosedGamesClaim_WaitsForItAgain()
    {
        var game = Game("Race Vm Quest");
        var checks = new Checks();
        var probe = new Probe { Running = true };
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: checks.Answer, runningProbe: probe);
        Record(fx.Records, game, ManagedComponentKind.ReLimiter);
        checks.Gate = fx.Vm.Operations;
        var shell = (IPanelHost)fx.Vm;
        var (host, race) = GateRaceHost.Over(shell);
        var updates = new UpdatesViewModel(host, fx.Vm.Updates.UpdateAll) { DeferredRetryDelay = _clock.Delay };
        shell.Settings.AutoUpdateComponents = true; // in memory only: the fixture's own loop stays off
        using var loop = new CancellationTokenSource();
        var pass = (Task)typeof(UpdatesViewModel).GetMethod("RunBackgroundComponentPassAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(updates, [loop.Token])!;
        await pass.WaitAsync(HangGuard);
        await _clock.WaitingAsync();
        var retry = updates.DeferredRetry!;

        race.ClaimOnRead(skip: 1); // WhenIdleAsync's read passes; the claim's read finds the gate taken
        probe.Running = false;
        _clock.Release();
        using (var queued = await race.Claimed.Task.WaitAsync(HangGuard))
        {
            await WaitingForTheGateAsync(fx.Vm.Operations);
            Assert.Equal(0, checks.Count);
            Assert.Equal(1, race.ReadsAfterClaim); // w12 follow-up (re-review update-all N2): the wait again, not yet the claim
        }
        Assert.True(await checks.Seen.WaitAsync(HangGuard), "no pass after the gate was freed");
        // Exactly two reads after the claiming read — wait again, then claim — so the claim landed on the claim's own read
        // (an extra read before WhenIdleAsync would move it onto the wait and leave the loop untested).
        Assert.Equal(2, race.ReadsAfterClaim);
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Equal(["background-update"], checks.Kinds);
        Assert.Empty(retry.Pending);
        Assert.False(fx.Vm.Operations.IsBusy);
        loop.Cancel();
    }

    // Review F3: Cancel pressed during the closed game's pass says so (background) and the game is done.
    [Fact]
    public async Task ViewModel_ClosedGamePassCancelled_SaysCancelled_AndForgetsTheGame()
    {
        var probe = new Probe { Running = true };
        var (fx, retry) = await DeferredInViewModelAsync(Game("Cancel Vm Quest"), probe);
        using var disposeFx = fx;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.HangWhen = request =>
        {
            if (request.RequestUri!.AbsoluteUri != ReLimiterLatest) return false;
            asked.TrySetResult();
            return true; // the release check stalls until cancelled
        };
        var lines = new ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };

        probe.Running = false;
        _clock.Release();
        await asked.Task.WaitAsync(HangGuard);
        Assert.Equal("background-update", fx.Vm.Operations.CurrentKind);
        fx.Vm.Operations.Cancel();
        await retry.Run!.WaitAsync(HangGuard);

        // (The offline fixture's own notice may follow the line.)
        Assert.Contains(lines, line => line.StartsWith("Operation cancelled. (background)", StringComparison.Ordinal));
        Assert.Empty(retry.Pending);
        Assert.False(fx.Vm.Operations.IsBusy);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    private DeferredGameRetry Retry(Func<DetectedGame, bool> isRunning, Func<DetectedGame, CancellationToken, Task<bool>> pass, Func<bool>? wanted = null)
        => new(isRunning, wanted ?? (() => true), pass, DeferredGameRetry.DefaultInterval, _clock.Delay);

    // The poll waits: lets it through n waits, returning each time it waits again.
    private async Task Polls(int n)
    {
        for (var i = 0; i < n; i++)
        {
            _clock.Release();
            await _clock.WaitingAsync();
        }
    }

    // The real view model with automatic updates turned on: its first pass deferred the running game (a ReLimiter record
    // unless the seed installs something) and the poll waits on the test clock.
    private async Task<(ViewModelFixture Fx, DeferredGameRetry Retry)> DeferredInViewModelAsync(DetectedGame game, Probe probe,
        Func<HttpRequestMessage, HttpResponseMessage?>? answer = null, string language = "en-US", Func<ViewModelFixture, Task>? seed = null)
    {
        var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: answer, runningProbe: probe); // a Settings save applies it
        fx.Vm.Updates.DeferredRetryDelay = _clock.Delay;
        if (seed is null) Record(fx.Records, game, ManagedComponentKind.ReLimiter);
        else await seed(fx);
        TurnAutomaticUpdates(fx, on: true); // the loop's first pass runs at once
        await _clock.WaitingAsync();
        return (fx, fx.Vm.Updates.DeferredRetry!);
    }

    // The pass parked in OperationCoordinator.WhenIdleAsync (its idle signal exists only while someone waits for the gate).
    private static async Task WaitingForTheGateAsync(OperationCoordinator operations)
    {
        var idle = typeof(OperationCoordinator).GetField("_idle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var deadline = DateTime.UtcNow + HangGuard;
        while (idle.GetValue(operations) is null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the closed game's pass never waited for the gate");
            await Task.Delay(10);
        }
    }

    private static void TurnAutomaticUpdates(ViewModelFixture fx, bool on)
    {
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AutoUpdateComponents = on;
        fx.Vm.Settings.SaveLinuxSettings(settings);
    }

    private static void WriteCustomReShade(string path, byte build)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PeArchitectureDetectorTests.WritePe(path, 0x8664, 1_100_000);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 512;
        stream.Write(System.Text.Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
        stream.Position = 4096;
        stream.WriteByte(build);
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "", StringComparison.Ordinal) + ".exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        return new DetectedGame
        {
            Name = name, Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            GraphicsApi = GraphicsApi.DirectX11
        };
    }

    private static void Record(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind component)
        => records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = component,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1/x.zip"
        });

    /// <summary>A clock the test drives: each wait is recorded and signalled, and lasts until the test releases it.</summary>
    private sealed class Clock
    {
        private readonly SemaphoreSlim _waiting = new(0);
        private readonly SemaphoreSlim _release = new(0);
        public ConcurrentQueue<TimeSpan> Waits { get; } = new();

        public async Task Delay(TimeSpan wait, CancellationToken cancellationToken)
        {
            Waits.Enqueue(wait);
            _waiting.Release();
            await _release.WaitAsync(cancellationToken);
        }

        public async Task WaitingAsync()
        {
            if (!await _waiting.WaitAsync(HangGuard)) throw new TimeoutException("the poll never waited");
        }

        public void Release() => _release.Release();
    }

    /// <summary>Counts the game's ReLimiter release checks (offline: the check cannot tell, nothing changes).</summary>
    private sealed class Checks
    {
        private int _count;
        public OperationCoordinator? Gate { get; set; }
        public SemaphoreSlim Seen { get; } = new(0);
        public ConcurrentQueue<bool> WhileTestHeldTheGate { get; } = new();
        public ConcurrentQueue<string?> Kinds { get; } = new();
        public int Count => Volatile.Read(ref _count);

        public HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsoluteUri != ReLimiterLatest) return null;
            WhileTestHeldTheGate.Enqueue(Gate?.CurrentKind == "test");
            Kinds.Enqueue(Gate?.CurrentKind);
            Interlocked.Increment(ref _count);
            Seen.Release();
            return null;
        }
    }

    /// <summary>
    /// The shell as a panel sees it, except that once armed one read of <see cref="IPanelHost.Operations"/> (after
    /// <c>skip</c> reads) first claims the gate as another operation would ("queued"): the race a second waiter can win
    /// between the pass's wait for the gate and its own claim. Every other member is the shell's own.
    /// </summary>
    public class GateRaceHost : System.Reflection.DispatchProxy
    {
        private readonly object _lock = new();
        private IPanelHost _inner = null!;
        private int _skip = -1;
        private int _readsAfterClaim = -1;

        /// <summary>Reads of <see cref="IPanelHost.Operations"/> after the claiming read (-1 before it).</summary>
        public int ReadsAfterClaim { get { lock (_lock) return _readsAfterClaim; } }

        public TaskCompletionSource<OperationScope> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static (IPanelHost Host, GateRaceHost Race) Over(IPanelHost inner)
        {
            var host = Create<IPanelHost, GateRaceHost>();
            var race = (GateRaceHost)(object)host;
            race._inner = inner;
            return (host, race);
        }

        public void ClaimOnRead(int skip)
        {
            lock (_lock) _skip = skip;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Operations")
            {
                bool claim;
                lock (_lock)
                {
                    if (_readsAfterClaim >= 0) _readsAfterClaim++;
                    claim = _skip >= 0 && _skip-- == 0;
                    if (claim) _readsAfterClaim = 0;
                }
                if (claim) Claimed.SetResult(_inner.Operations.TryBegin("queued") ?? throw new InvalidOperationException("the gate was not free"));
            }
            try { return targetMethod.Invoke(_inner, args); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }
    }

    private sealed class Probe : IGameRunningProbe
    {
        private int _calls;
        public volatile bool Running;
        public volatile bool Throws;
        public Queue<bool>? Answers { get; init; }
        public int Calls => Volatile.Read(ref _calls);

        public bool IsRunning(DetectedGame game)
        {
            Interlocked.Increment(ref _calls);
            if (Throws) throw new IOException("/proc is not readable");
            if (Answers is not null) lock (Answers) return Answers.Dequeue();
            return Running;
        }
    }

    /// <summary>Every check finds an update; every update changes the record (counts as updated).</summary>
    private sealed class UpdatingBackend(LinuxDeploymentRecordStore records) : ILinuxManagedUpdateBackend
    {
        public ConcurrentQueue<ManagedComponentKind> UpdateCalls { get; } = new();

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagedUpdateCheck(true, true, "update"));

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            UpdateCalls.Enqueue(component);
            var record = records.Find(game, component)!;
            record.Version += "-new";
            records.Upsert(record);
            return Task.FromResult(ComponentOperationResult.Ok("updated"));
        }
    }
}
