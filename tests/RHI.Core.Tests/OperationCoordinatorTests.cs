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
        var tunic = fx.Game("Tunic");
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = "Tunic", InstallPath = tunic.InstallPath, ExecutablePath = tunic.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        // The selection's analysis writes its own "Ready" line; let it land before asserting the status line.
        await WaitUntil(() => !fx.Vm.IsSelectionProbeBusy && fx.Vm.StatusText.StartsWith("Ready", StringComparison.Ordinal));
        using var running = fx.Vm.Operations.TryBegin("background-update")!;

        Assert.False(fx.Vm.SetOptiScalerAdvancedSetting("x", "y").Success);
        Assert.False(fx.Vm.SetRenoDxCompatibilitySetting("x", "y").Success);
        Assert.False(fx.Vm.SetDlssg2030Preferences("RTX 20", "dxgi.dll").Success);
        Assert.False(fx.Vm.SetStandaloneDlssEnablerPreferences("dxgi.dll").Success);
        Assert.False((await fx.Vm.UpdateDlssg2030Async()).Success);
        Assert.Equal("Another RHI operation is already running.", fx.Vm.ApplyGlobalReShadeSettingsToSelectedGame(new()));
        Assert.StartsWith("Another RHI operation is already running.", fx.Vm.StatusText);
        Assert.True(fx.Vm.IsBusy);
    }

    // UI-09 verification: a request that never answers; Cancel brings the window back within a second.
    [Fact]
    public async Task StalledDownload_Cancel_ReleasesTheOverlayWithinASecond()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.HangWhen = request => request.RequestUri?.Host == "api.github.com";

        var load = fx.Vm.Extras.LoadNeuralVersionHistoryCommand.ExecuteAsync(null);
        await WaitUntil(() => fx.Vm.IsBusy);
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
        var tunic = fx.Game("Tunic");
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = "Tunic", InstallPath = tunic.InstallPath, ExecutablePath = tunic.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        await WaitUntil(() => !fx.Vm.IsSelectionProbeBusy);
        fx.HangWhen = _ => true;

        var check = fx.Vm.Extras.CheckShortFuseUpdateCommand.ExecuteAsync(null);
        await WaitUntil(() => fx.Vm.IsBusy);
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
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = "Moved Game", InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        await WaitUntil(() => !fx.Vm.IsSelectionProbeBusy);
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
