using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w2/vmsplit review finding 1: the user's Cancel is honoured only before an operation's first change to game files;
/// from then on it runs to completion or full rollback. Batches (Update All, the DLSS batch) give each unit its own
/// cancellation, so Cancel still stops them between units.
/// </summary>
public sealed class OperationCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-operation-cancel-" + Guid.NewGuid().ToString("N"));

    public OperationCancellationTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void CancelBeforeTheWritePhase_IsAccepted_AndTheWritePhaseNeverStarts()
    {
        using var cancel = new OperationCancellation();

        Assert.True(cancel.TryCancel());

        Assert.True(cancel.Token.IsCancellationRequested);
        Assert.True(cancel.CancelAccepted);
        Assert.Throws<OperationCanceledException>(() => OperationCancellation.BeginWrites(cancel.Token));
        Assert.False(cancel.WritesStarted);
    }

    [Fact]
    public void CancelAfterTheWritePhaseBegan_IsRefused_AndTheTokenNeverFires()
    {
        using var cancel = new OperationCancellation();
        var raised = 0;
        cancel.WritesStarting += () => raised++;

        var after = OperationCancellation.BeginWrites(cancel.Token);
        OperationCancellation.BeginWrites(cancel.Token);

        Assert.Equal(CancellationToken.None, after);
        Assert.True(cancel.WritesStarted);
        Assert.Equal(1, raised);
        Assert.False(cancel.TryCancel());
        Assert.False(cancel.Token.IsCancellationRequested);
        Assert.False(cancel.CancelAccepted);
    }

    [Fact]
    public void LinkedToken_IsGatedTheSameWay()
    {
        using var outer = new CancellationTokenSource();
        using var beforeWrites = new OperationCancellation(outer.Token);
        using var outer2 = new CancellationTokenSource();
        using var writing = new OperationCancellation(outer2.Token);
        OperationCancellation.BeginWrites(writing.Token);

        outer.Cancel();
        outer2.Cancel();

        Assert.True(beforeWrites.Token.IsCancellationRequested);
        Assert.False(writing.Token.IsCancellationRequested);
    }

    [Fact]
    public void PlainToken_IsCheckedOnce_ThenIgnored()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => OperationCancellation.BeginWrites(cancelled.Token));

        using var live = new CancellationTokenSource();
        Assert.Equal(CancellationToken.None, OperationCancellation.BeginWrites(live.Token));
        Assert.Equal(CancellationToken.None, OperationCancellation.BeginWrites(CancellationToken.None));
    }

    [Fact]
    public void DisposedOperation_ForgetsItsToken()
    {
        var cancel = new OperationCancellation();
        var token = cancel.Token;
        cancel.Dispose();

        Assert.Equal(CancellationToken.None, OperationCancellation.BeginWrites(token));
        Assert.False(cancel.TryCancel());
    }

    [Fact]
    public async Task TransactionalBatch_CancelAfterItStarted_ReplacesEveryFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "batch")).FullName;
        var pairs = Enumerable.Range(0, 3).Select(i =>
        {
            var source = Path.Combine(_root, $"source-{i}.dll");
            var target = Path.Combine(dir, $"target-{i}.dll");
            File.WriteAllBytes(source, [(byte)(0x40 + i), 1, 2]);
            File.WriteAllBytes(target, [(byte)(0x10 + i)]);
            return (Source: source, Target: target);
        }).ToArray();
        var files = new TransactionalFileDeploymentService();

        var run = await CancelAtWritePhase.RunAsync(ct => files.ReplaceBatchAsync(pairs, ct), [dir]);

        Assert.True(run.Result.Success, run.Result.Error);
        Assert.All(pairs, pair => Assert.Equal(File.ReadAllBytes(pair.Source), File.ReadAllBytes(pair.Target)));
        Assert.All(pairs, pair => Assert.True(File.Exists(pair.Target + TransactionalFileDeploymentService.BackupExtension)));
    }

    [Fact]
    public async Task JournaledOperation_TrackingAFile_EndsTheCancellablePhase()
    {
        var journal = new DeploymentJournal(Path.Combine(_root, "journal"));
        var target = Path.Combine(_root, "tracked.dll");
        File.WriteAllBytes(target, [1, 2, 3]);
        using var cancel = new OperationCancellation();
        await using var operation = await journal.BeginAsync("test", cancellationToken: cancel.Token);
        Assert.False(cancel.WritesStarted); // opening the journal entry changes nothing in the game

        await operation.TrackFileAsync(target, cancel.Token);

        Assert.True(cancel.WritesStarted);
        Assert.False(cancel.TryCancel());
        Assert.False(cancel.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task UpdateAll_CancelDuringAUnitsWrites_FinishesThatUnit_ThenStopsBeforeTheNext()
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "update-all.json"));
        var first = Game("First");
        var second = Game("Second");
        foreach (var game in new[] { first, second })
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
                BinaryDirectory = game.BinaryDirectory!, Version = "1"
            });
        var backend = new WritingBackend();
        var service = new LinuxUpdateAllService(records, backend);
        using var outer = new OperationCancellation();
        var accepted = new List<bool>();
        OperationCancellation.WritePhaseObserverForTesting = _ => accepted.Add(outer.TryCancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync([first, second], _ => [], LinuxUpdateRunMode.Manual, outer.Token));

        Assert.Equal([true], accepted);
        Assert.Equal(["First"], backend.Updated);
        Assert.Equal([false], backend.UnitTokenCancelledAfterWrites);
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        return new DetectedGame { Name = name, InstallPath = dir, BinaryDirectory = dir, Source = "Test", Runner = "Wine" };
    }

    /// <summary>An update that enters its write phase, as every real deployment service does before its first change.</summary>
    private sealed class WritingBackend : ILinuxManagedUpdateBackend
    {
        public List<string> Updated { get; } = [];
        public List<bool> UnitTokenCancelledAfterWrites { get; } = [];

        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagedUpdateCheck(true, true, "update available"));

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
        {
            OperationCancellation.BeginWrites(cancellationToken);
            UnitTokenCancelledAfterWrites.Add(cancellationToken.IsCancellationRequested);
            Updated.Add(game.Name);
            return Task.FromResult(ComponentOperationResult.Ok("updated"));
        }
    }
}
