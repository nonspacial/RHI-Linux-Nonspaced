using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #76: the busy gate has one owner at a time, so concurrent operations cannot clear each other's gate; #77: the
/// running operation is cancellable from the busy overlay and the window is usable again at once.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OperationCoordinatorTests
{
    [Fact]
    public void SecondClaim_IsRefused_UntilTheOwnerReleases()
    {
        var gate = new OperationCoordinator();
        var first = gate.TryBegin("background-update");
        Assert.NotNull(first);
        Assert.Null(gate.TryBegin("dlss-batch"));
        Assert.True(gate.IsBusy);

        first!.Dispose();
        Assert.False(gate.IsBusy);
        using var second = gate.TryBegin("dlss-batch");
        Assert.NotNull(second);
    }

    [Fact]
    public void AStaleScope_NeverClearsTheCurrentOwnersGate()
    {
        var gate = new OperationCoordinator();
        var first = gate.TryBegin("a")!;
        first.Dispose();
        using var second = gate.TryBegin("b")!;

        first.Dispose(); // disposed twice: a finally running late must not drop b's overlay
        Assert.True(gate.IsBusy);
        Assert.Equal("b", gate.CurrentKind);
    }

    [Fact]
    public void Cancel_CancelsOnlyTheRunningOperationsToken_Once()
    {
        var gate = new OperationCoordinator();
        using var op = gate.TryBegin("install")!;
        Assert.True(gate.CanCancel);
        Assert.True(gate.CancelCommand.CanExecute(null));

        gate.CancelCommand.Execute(null);

        Assert.True(op.Token.IsCancellationRequested);
        Assert.True(op.WasCancelled(new OperationCanceledException()));
        Assert.False(gate.CanCancel); // pressed once; the operation ends on its own
        Assert.True(gate.IsBusy);
    }

    [Fact]
    public void NonCancellableOperation_HidesCancel_AndIgnoresIt()
    {
        var gate = new OperationCoordinator();
        using var op = gate.TryBegin("repair-after-move", cancellable: false)!;
        Assert.False(gate.CanCancel);
        gate.Cancel();
        Assert.False(op.Token.IsCancellationRequested);
    }

    [Fact]
    public void LinkedToken_CancelsTheOperation()
    {
        using var loop = new CancellationTokenSource();
        var gate = new OperationCoordinator();
        using var op = gate.TryBegin("background-update", linkedToken: loop.Token)!;
        loop.Cancel();
        Assert.True(op.Token.IsCancellationRequested);
        Assert.False(op.CancelRequested); // the user did not press Cancel
    }

    // UI-08 verification: a long-running pass holds the gate; the DLSS batch is refused and the overlay stays up.
    [Fact]
    public async Task DlssBatch_WhileAnotherOperationRuns_IsRefused_AndTheGateStaysHeld()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        var background = fx.Vm.Operations.TryBegin("background-update")!;

        var refused = await Assert.ThrowsAsync<OperationRefusedException>(
            () => fx.Vm.Dlss.ApplyDlssBatchAsync([game], new LinuxDlssVersionDefaults("310.1.0", "", "", "", "")));
        Assert.Equal("Another RHI operation is already running.", refused.Message);
        await Assert.ThrowsAsync<OperationRefusedException>(() => fx.Vm.Dlss.RestoreDlssBatchAsync([game]));
        Assert.Null(await fx.Vm.Dlss.GetDlssBatchCandidatesAsync());
        Assert.True(fx.Vm.IsBusy);

        background.Dispose();
        Assert.False(fx.Vm.IsBusy);
    }

    [Fact]
    public async Task DialogWriters_AreRefused_WhileAnotherOperationRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        // The selection's analysis writes its own "Ready" line: wait for that analysis to end (its Completed event,
        // raised after the VM wrote the line), never for a status line within a time limit (#164).
        await AddAndAnalyseAsync(fx, "Tunic");
        using var running = fx.Vm.Operations.TryBegin("background-update")!;

        string? refusal = null;
        // The status lines these refusals wrote themselves (the line changes once: the same refusal repeats).
        var lines = LinesWrittenBy(fx, () =>
        {
            Assert.False(fx.Vm.SetOptiScalerAdvancedSetting("x", "y").Success);
            Assert.False(fx.Vm.SetRenoDxCompatibilitySetting("x", "y").Success);
            Assert.False(fx.Vm.SetDlssg2030Preferences("RTX 20", "dxgi.dll").Success);
            Assert.False(fx.Vm.SetStandaloneDlssEnablerPreferences("dxgi.dll").Success);
            refusal = fx.Vm.ReShadeProfile.ApplyGlobalReShadeSettingsToSelectedGame(new());
        });
        Assert.False((await fx.Vm.UpdateDlssg2030Async()).Success);
        Assert.Equal("Another RHI operation is already running.", refusal);
        Assert.All(lines, line => Assert.StartsWith("Another RHI operation is already running.", line));
        Assert.True(fx.Vm.IsBusy);
    }

    // UI-09 verification: a request that never answers; Cancel brings the window back within a second.
    [Fact]
    public async Task StalledDownload_Cancel_ReleasesTheOverlayWithinASecond()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.HangWhen = request => request.RequestUri?.Host == "api.github.com";

        var busy = BusyAsync(fx);
        var load = fx.Vm.Extras.LoadNeuralVersionHistoryCommand.ExecuteAsync(null);
        await busy;
        Assert.True(fx.Vm.Operations.CanCancel);

        var started = DateTime.UtcNow;
        fx.Vm.Operations.CancelCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fx.Vm.IsBusy);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.StartsWith("Operation cancelled.", fx.Vm.StatusText);
    }

    [Fact]
    public async Task StalledComponentCheck_Cancel_SaysCancelled_NotFailed()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndAnalyseAsync(fx, "Tunic");
        fx.HangWhen = _ => true;

        var busy = BusyAsync(fx);
        var check = fx.Vm.Extras.CheckShortFuseUpdateCommand.ExecuteAsync(null);
        await busy;
        fx.Vm.Operations.Cancel();
        await check.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fx.Vm.IsBusy);
        Assert.StartsWith("Operation cancelled.", fx.Vm.StatusText);
    }

    // w2/vmsplit review finding 1: once the operation starts changing game files, Cancel is hidden and refused, so the
    // operation runs to completion or full rollback and its real result is shown.
    [Fact]
    public void OnceTheOperationStartsWriting_CancelIsHiddenAndRefused()
    {
        var gate = new OperationCoordinator();
        using var op = gate.TryBegin("component")!;
        Assert.True(gate.CanCancel);

        RHI.Core.Deployment.OperationCancellation.BeginWrites(op.Token);

        Assert.True(op.WritesStarted);
        Assert.False(gate.CanCancel);
        Assert.False(gate.CancelCommand.CanExecute(null));
        gate.Cancel();
        Assert.False(op.CancelRequested);
        Assert.False(op.Token.IsCancellationRequested);
    }

    // w2/vmsplit review finding 2: "Repair after move" rewrites every record and store in one commit, so it is never
    // cancellable: while it runs (here held at its confirmation) Cancel is hidden and pressing it changes nothing.
    [Fact]
    public async Task RepairAfterMove_IsNotCancellable_WhileItRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Moved Game");
        var addon = Path.Combine(game.InstallPath, "ue-dof-fix.addon64");
        File.WriteAllBytes(addon, [1, 2, 3, 4]);
        var oldFolder = Path.Combine(fx.Root, "old-library", "Moved Game");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = oldFolder, GameName = "Moved Game", Component = ManagedComponentKind.DofFix, BinaryDirectory = oldFolder, Version = "1",
            Files = [new LinuxDeployedFile
            {
                Path = Path.Combine(oldFolder, "ue-dof-fix.addon64"), ExistedBeforeInstall = false,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(addon)))
            }]
        });
        await AddAndAnalyseAsync(fx, "Moved Game");
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.ConfirmAsync = (_, _, _) => { asked.TrySetResult(); return answer.Task; };

        var repair = fx.Vm.RepairAfterMoveCommand.ExecuteAsync(null);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("repair-after-move", fx.Vm.Operations.CurrentKind);
        Assert.False(fx.Vm.Operations.CanCancel);
        fx.Vm.Operations.Cancel();
        answer.SetResult(true);
        await repair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("cancelled", fx.Vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(game.InstallPath, fx.Records.Find(fx.Vm.SelectedGame!, ManagedComponentKind.DofFix)?.BinaryDirectory);
    }

    // Deterministic waits (#164): each awaits the event that marks the state, set up before the action that causes it.
    // The bound is only a hang guard (a test that would otherwise block forever), never the thing a pass depends on.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    /// <summary>Adds the fixture game as a manual entry (which selects it) and waits until its analysis has ended.</summary>
    private static async Task AddAndAnalyseAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var analysed = new TaskCompletionSource<AnalysisOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == name) analysed.TrySetResult(outcome);
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
            {
                Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
            });
            Assert.True(added, message);
            await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        Assert.False(fx.Vm.IsSelectionProbeBusy);
    }

    /// <summary>Completes when the shell's busy gate is claimed (already, or by the action started after this call).</summary>
    private static Task BusyAsync(ViewModelFixture fx)
    {
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(fx.Vm.IsBusy) && fx.Vm.IsBusy) busy.TrySetResult();
        }
        fx.Vm.PropertyChanged += OnChanged;
        if (fx.Vm.IsBusy) busy.TrySetResult();
        return busy.Task.WaitAsync(HangGuard).ContinueWith(t =>
        {
            fx.Vm.PropertyChanged -= OnChanged;
            t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    /// <summary>The status lines <paramref name="action"/> itself wrote on this thread (never another writer's).</summary>
    private static List<string> LinesWrittenBy(ViewModelFixture fx, Action action)
    {
        var thread = Environment.CurrentManagedThreadId;
        var lines = new List<string>();
        void Record(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText) && Environment.CurrentManagedThreadId == thread) lines.Add(fx.Vm.StatusText);
        }
        fx.Vm.PropertyChanged += Record;
        try { action(); }
        finally { fx.Vm.PropertyChanged -= Record; }
        Assert.NotEmpty(lines);
        return lines;
    }
}
