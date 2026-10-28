using RHI.Core.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #240 (review F5): <see cref="DeploymentOperation.AfterCommit"/> work runs once the operation committed and
/// its snapshots are gone, but while it still holds its lock — no other operation's settle or recovery acts on its entry
/// in between — and never after a rollback.
/// </summary>
public sealed class DeploymentJournalAfterCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-after-commit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AfterCommit_RunsWhileTheLockIsStillHeld_ThenTheLockGoes()
    {
        var journal = new DeploymentJournal(Path.Combine(_root, "journal"));
        var operation = await journal.BeginAsync("after-commit");
        var lockPath = journal.LockPath(operation.Id);
        bool? heldDuring = null;
        operation.AfterCommit("observe the lock", () => heldDuring = LockIsHeld(lockPath));

        await operation.CommitAsync();

        Assert.True(heldDuring);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task AfterCommit_NeverRunsAfterARollback()
    {
        var journal = new DeploymentJournal(Path.Combine(_root, "journal"));
        var operation = await journal.BeginAsync("rolled back");
        var ran = false;
        operation.AfterCommit("must not run", () => ran = true);

        await operation.RollbackAsync();

        Assert.False(ran);
    }

    private static bool LockIsHeld(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
