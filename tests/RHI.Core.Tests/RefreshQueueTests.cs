using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Refresh and Update All under the busy gate (Refs #68, DET-29): a Refresh clicked while another operation runs is
/// queued and says so (it used to be dropped without a word), repeated clicks join it, and a manual Update All can be
/// cancelled from the overlay.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RefreshQueueTests
{
    [Fact]
    public async Task RefreshWhileAnotherOperationRuns_IsQueued_SaysSo_AndRunsWhenItEnds()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var held = fx.Vm.Operations.TryBegin("background-update")!;

        var refresh = fx.Vm.RefreshCommand.ExecuteAsync(null);
        var again = fx.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.StartsWith(UiLocalizationService.Text("Vm.RefreshQueued"), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(refresh.IsCompleted);
        Assert.Equal("background-update", fx.Vm.Operations.CurrentKind);
        var kinds = new List<string?>();
        fx.Vm.Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationCoordinator.CurrentKind) && fx.Vm.Operations.CurrentKind is { } kind) kinds.Add(kind);
        };

        held.Dispose();
        await Task.WhenAll(refresh, again).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["refresh"], kinds); // one refresh for both clicks
        Assert.StartsWith(LibraryScanStatusText.Done(0), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(fx.Vm.IsBusy);
    }

    [Fact]
    public async Task RefreshWhenIdle_RunsAtOnce()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        Assert.StartsWith(LibraryScanStatusText.Done(0), fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualUpdateAll_CanBeCancelledFromTheOverlay()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Managed Game");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DofFix,
            BinaryDirectory = game.InstallPath, Version = "1.0"
        });
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        await WaitUntil(() => fx.Vm.CanUpdateAll && !fx.Vm.IsSelectionProbeBusy);
        var hung = 0;
        var stall = 1;
        // Update All's check stalls; once Cancel is pressed nothing stalls (the re-analysis after it must not).
        fx.HangWhen = _ => Volatile.Read(ref stall) == 1 && fx.Vm.Operations.CurrentKind == "update-all" && Interlocked.Increment(ref hung) > 0;

        var update = fx.Vm.UpdateAllCommand.ExecuteAsync(null);
        await WaitUntil(() => Volatile.Read(ref hung) > 0);
        Assert.Equal("update-all", fx.Vm.Operations.CurrentKind);
        Assert.True(fx.Vm.Operations.CanCancel);
        Volatile.Write(ref stall, 0);
        fx.Vm.Operations.CancelCommand.Execute(null);
        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(fx.Vm.IsBusy);
        Assert.StartsWith(UiLocalizationService.Text("Vm.OperationCancelled"), fx.Vm.StatusText, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
