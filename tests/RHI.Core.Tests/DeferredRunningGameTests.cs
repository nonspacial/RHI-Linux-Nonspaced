using System.Collections.Concurrent;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-deferred-" + Guid.NewGuid().ToString("N"));

    public DeferredRunningGameTests() => Directory.CreateDirectory(_root);

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
        var clock = new Clock();
        var passes = 0;
        var retry = new DeferredGameRetry(service.IsRunning, () => true, async (closed, token) =>
        {
            passes++;
            var result = await service.RunAsync([closed], _ => [], LinuxUpdateRunMode.Background, token);
            return result.RunningGames.Count > 0;
        }, DeferredGameRetry.DefaultInterval, clock.Delay);

        var first = await service.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);
        Assert.Equal((0, 1), (first.ChangedCount, first.DeferredCount));
        Assert.Equal([game], first.RunningGames);
        var run = retry.Add(first.RunningGames, CancellationToken.None);

        await clock.WaitingAsync(); // poll 1: still running
        clock.Release();
        await clock.WaitingAsync();
        Assert.Equal(0, passes);
        probe.Running = false; // the user closed the game
        clock.Release();
        await run.WaitAsync(HangGuard); // nothing left to wait for: the poll ends

        Assert.Equal(1, passes);
        Assert.Equal([ManagedComponentKind.ReLimiter], backend.UpdateCalls);
        Assert.Equal("1-new", records.Find(game, ManagedComponentKind.ReLimiter)!.Version);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], clock.Waits);
        Assert.Empty(retry.Pending);
    }

    [Fact]
    public async Task StillRunning_GetsNoPass_AndEveryPollWaitsTheInterval()
    {
        var probe = new Probe { Running = true };
        var clock = new Clock();
        var passes = 0;
        using var stop = new CancellationTokenSource();
        var retry = new DeferredGameRetry(probe.IsRunning, () => true, (_, _) => { passes++; return Task.FromResult(false); },
            TimeSpan.FromSeconds(60), clock.Delay);
        var run = retry.Add([Game("Long Session")], stop.Token);

        for (var i = 0; i < 3; i++)
        {
            await clock.WaitingAsync();
            clock.Release();
        }
        await clock.WaitingAsync();
        stop.Cancel();
        await run.WaitAsync(HangGuard); // ends quietly when the loop stops

        Assert.Equal(0, passes);
        Assert.Equal(3, probe.Calls);
        Assert.Equal(4, clock.Waits.Count);
        Assert.Single(retry.Pending);
    }

    [Fact]
    public async Task AutomaticUpdatesTurnedOff_RunNothing_AndForgetTheGames()
    {
        var probe = new Probe { Running = true };
        var clock = new Clock();
        var wanted = true;
        var passes = 0;
        var retry = new DeferredGameRetry(probe.IsRunning, () => wanted, (_, _) => { passes++; return Task.FromResult(false); },
            TimeSpan.FromSeconds(60), clock.Delay);
        var run = retry.Add([Game("Off Quest")], CancellationToken.None);

        await clock.WaitingAsync();
        probe.Running = false;
        wanted = false; // Settings: automatic updates off while the poll waits
        clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal((0, 0), (passes, probe.Calls));
        Assert.Empty(retry.Pending);

        // Off before the first wait: no poll at all.
        var idle = new DeferredGameRetry(probe.IsRunning, () => false, (_, _) => { passes++; return Task.FromResult(false); },
            TimeSpan.FromSeconds(60), clock.Delay);
        await idle.Add([Game("Off Again")], CancellationToken.None).WaitAsync(HangGuard);
        Assert.Equal((0, 1), (passes, clock.Waits.Count));
        Assert.Empty(idle.Pending);
    }

    [Fact]
    public async Task ProbeThatThrows_KeepsWaiting_WithoutSpinning_AndIsLoggedOnce()
    {
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var probe = new Probe { Throws = true };
        var clock = new Clock();
        var passes = 0;
        var retry = new DeferredGameRetry(probe.IsRunning, () => true, (_, _) => { passes++; return Task.FromResult(false); },
            TimeSpan.FromSeconds(60), clock.Delay);
        Task run;
        using (AppLog.UseInThisFlow(log)) run = retry.Add([Game("Probe Quest")], CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            await clock.WaitingAsync();
            clock.Release();
        }
        await clock.WaitingAsync();
        Assert.Equal((0, 3, 4), (passes, probe.Calls, clock.Waits.Count)); // one probe call per poll, each after a full wait
        probe.Throws = false;
        clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(1, passes);
        Assert.Single(File.ReadAllLines(log.FilePath), line => line.Contains("running-game probe failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PassThatMustWaitAgain_KeepsTheGame_UntilAPassIsDone()
    {
        // The game started again before its pass (or another operation took the gate first): asked again next poll.
        var probe = new Probe();
        var clock = new Clock();
        var answers = new Queue<bool>([true, false]);
        var passes = 0;
        var retry = new DeferredGameRetry(probe.IsRunning, () => true, (_, _) => { passes++; return Task.FromResult(answers.Dequeue()); },
            TimeSpan.FromSeconds(60), clock.Delay);
        var run = retry.Add([Game("Again Quest")], CancellationToken.None);

        await clock.WaitingAsync();
        clock.Release();
        await clock.WaitingAsync();
        Assert.Equal(1, passes);
        Assert.Single(retry.Pending);
        clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(2, passes);
        Assert.Empty(retry.Pending);
    }

    [Fact]
    public async Task GamesAddedWhilePolling_JoinTheSamePoll_EachOnce()
    {
        var probe = new Probe { Running = true };
        var clock = new Clock();
        var passed = new List<string>();
        var retry = new DeferredGameRetry(probe.IsRunning, () => true, (game, _) => { passed.Add(game.Name); return Task.FromResult(false); },
            TimeSpan.FromSeconds(60), clock.Delay);
        var first = Game("First Quest");
        var run = retry.Add([first], CancellationToken.None);
        await clock.WaitingAsync();

        var joined = retry.Add([Game("Second Quest"), first], CancellationToken.None);
        Assert.Same(run, joined);
        Assert.Equal(2, retry.Pending.Count);
        probe.Running = false;
        clock.Release();
        await run.WaitAsync(HangGuard);

        Assert.Equal(["First Quest", "Second Quest"], passed.Order());
        Assert.Single(clock.Waits);
    }

    [Fact]
    public async Task Pass_ListsEachRunningGameOnce_AndSaysWhenAnotherPassHeldIt()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var running = Game("Running Quest");
        var idle = Game("Idle Quest");
        foreach (var game in new[] { running, idle })
        {
            Record(records, game, ManagedComponentKind.ReLimiter);
            Record(records, game, ManagedComponentKind.RenoDX);
        }
        var backend = new UpdatingBackend(records) { Hold = true };
        var service = new LinuxUpdateAllService(records, backend, new Probe { RunningName = running.Name });

        var pass = service.RunAsync([running, idle], _ => [], LinuxUpdateRunMode.Background);
        await backend.Entered.Task.WaitAsync(HangGuard);
        var refused = await service.RunAsync([idle], _ => [], LinuxUpdateRunMode.Background);
        backend.Release.SetResult();
        var result = await pass.WaitAsync(HangGuard);

        Assert.True(refused.AnotherPassRunning);
        Assert.Empty(refused.RunningGames);
        Assert.False(result.AnotherPassRunning);
        Assert.Equal([running], result.RunningGames);
        Assert.Equal((2, 2), (result.ChangedCount, result.DeferredCount));
        Assert.True(service.IsRunning(running));
        Assert.False(service.IsRunning(idle));
    }

    [Theory]
    [InlineData("en-US", "Deferred updates ran after the game closed: Closing Quest. Update All: 1 updated · 1 current · 0 deferred · 0 failed.")]
    [InlineData("pt-BR", "Atualizações adiadas feitas depois que o jogo fechou: Closing Quest. Atualizar tudo: 1 atualizado(s) · 1 em dia · 0 adiado(s) · 0 com falha.")]
    public void ClosedGamePassStatus_NamesTheGame_ThenTheCounts(string language, string expected)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var game = Game("Closing Quest");
            var result = new LinuxUpdateAllResult([
                new LinuxUpdateItemResult(game.Name, ManagedComponentKind.ReLimiter, true, true, false, "updated"),
                new LinuxUpdateItemResult(game.Name, ManagedComponentKind.RenoDX, true, false, false, "current")
            ]);

            Assert.Equal(expected, UpdatesViewModel.ClosedGamePassStatus(game, result));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // Through the real view model: the background pass defers the running game, the poll sees it close and gives it its
    // own pass (one ReLimiter release check, the only request this game's pass makes), after the busy gate is free.
    [Fact]
    public async Task ViewModel_BackgroundPass_RetriesTheClosedGame_OnceTheGateIsFree()
    {
        var game = Game("Vm Quest");
        var checks = new ConcurrentQueue<bool>();
        var checkSeen = new SemaphoreSlim(0);
        ViewModelFixture? fixture = null;
        HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsoluteUri == ReLimiterLatest)
            {
                checks.Enqueue(fixture!.Vm.Operations.CurrentKind == "test");
                checkSeen.Release();
            }
            return null; // offline: the check cannot tell, nothing changes
        }
        var probe = new Probe { Running = true };
        using var fx = fixture = await ViewModelFixture.StartAsync([game], httpResponse: Answer, runningProbe: probe);
        var clock = new Clock();
        fx.Vm.Updates.DeferredRetryDelay = clock.Delay;
        Record(fx.Records, game, ManagedComponentKind.ReLimiter);

        TurnAutomaticUpdates(fx, on: true); // the loop's first pass runs at once
        await clock.WaitingAsync(); // that pass deferred the game: the poll waits
        Assert.Empty(checks);
        var retry = fx.Vm.Updates.DeferredRetry!;
        Assert.Equal([game.Name], retry.Pending.Select(g => g.Name));

        clock.Release(); // still running
        await clock.WaitingAsync();
        Assert.Empty(checks);

        var held = fx.Vm.Operations.TryBegin("test");
        Assert.NotNull(held);
        probe.Running = false;
        clock.Release(); // closed, but an operation holds the gate: the pass waits for it, no new poll needed
        await WaitingForTheGateAsync(fx.Vm.Operations);
        held!.Dispose();
        Assert.True(await checkSeen.WaitAsync(HangGuard), "no pass after the gate was freed");
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Equal([false], checks); // once, and never while the test held the gate
        Assert.Empty(retry.Pending);
        fx.Vm.Updates.StopBackgroundLoop();
    }

    [Fact]
    public async Task ViewModel_TurningAutomaticUpdatesOff_StopsWaitingForTheGame()
    {
        var game = Game("Off Vm Quest");
        var checks = 0;
        HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsoluteUri == ReLimiterLatest) Interlocked.Increment(ref checks);
            return null;
        }
        var probe = new Probe { Running = true };
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: Answer, runningProbe: probe);
        var clock = new Clock();
        fx.Vm.Updates.DeferredRetryDelay = clock.Delay;
        Record(fx.Records, game, ManagedComponentKind.ReLimiter);
        TurnAutomaticUpdates(fx, on: true);
        await clock.WaitingAsync();
        var retry = fx.Vm.Updates.DeferredRetry!;
        var asked = probe.Calls; // the pass's own look

        probe.Running = false;
        TurnAutomaticUpdates(fx, on: false); // stops the loop and its poll; the waiting game is forgotten
        await retry.Run!.WaitAsync(HangGuard);

        Assert.Null(fx.Vm.Updates.DeferredRetry);
        Assert.Equal(0, checks);
        Assert.Equal(asked, probe.Calls);
    }

    [Fact]
    public async Task ViewModel_ManualUpdateAll_StillOnlyReportsTheRunningGameAsDeferred()
    {
        var game = Game("Manual Quest");
        var probe = new Probe { Running = true };
        using var fx = await ViewModelFixture.StartAsync([game], runningProbe: probe);
        fx.Vm.Updates.DeferredRetryDelay = new Clock().Delay;
        Record(fx.Records, game, ManagedComponentKind.ReLimiter);
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.StartsWith("Update All: 0 updated · 0 current · 1 deferred · 0 failed.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Null(fx.Vm.Updates.DeferredRetry);
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

    private const string ReLimiterLatest = "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest";

    private static void TurnAutomaticUpdates(ViewModelFixture fx, bool on)
    {
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AutoUpdateComponents = on;
        fx.Vm.Settings.SaveLinuxSettings(settings);
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

    private sealed class Probe : IGameRunningProbe
    {
        private int _calls;
        public volatile bool Running;
        public volatile bool Throws;
        public string? RunningName { get; init; }
        public int Calls => Volatile.Read(ref _calls);

        public bool IsRunning(DetectedGame game)
        {
            Interlocked.Increment(ref _calls);
            if (Throws) throw new IOException("/proc is not readable");
            return RunningName is null ? Running : game.Name == RunningName;
        }
    }

    /// <summary>Every check finds an update; every update changes the record (counts as updated).</summary>
    private sealed class UpdatingBackend(LinuxDeploymentRecordStore records) : ILinuxManagedUpdateBackend
    {
        public ConcurrentQueue<ManagedComponentKind> UpdateCalls { get; } = new();
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagedUpdateCheck(true, true, "update"));

        public async Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            UpdateCalls.Enqueue(component);
            var record = records.Find(game, component)!;
            record.Version += "-new";
            records.Upsert(record);
            return ComponentOperationResult.Ok("updated");
        }
    }
}
