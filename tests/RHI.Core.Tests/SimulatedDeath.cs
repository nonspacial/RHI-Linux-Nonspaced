using RHI.Core.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Simulates the process dying inside a deployment service (#23 crash tests): <see cref="Die"/> (called from a fault seam
/// such as <c>LinuxDeploymentRecordStore.SaveFaultForTesting</c>) drops the locks of every journaled operation still on
/// disk, as a crash would, and parks the service's thread until the test disposes this, so no in-process rollback runs
/// before the test has recovered the journal as the next start would.
/// </summary>
internal sealed class SimulatedDeath : IDisposable
{
    private readonly DeploymentJournal _journal;
    private readonly List<DeploymentOperation> _started = [];
    private readonly ManualResetEventSlim _dead = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly List<Task> _runs = [];

    public SimulatedDeath(DeploymentJournal journal)
    {
        _journal = journal;
        journal.OperationStartedForTesting = operation => { lock (_started) _started.Add(operation); };
    }

    public bool Died => _dead.IsSet;

    /// <summary>The process dies here; returns the exception the seam throws once the test lets the thread go.</summary>
    public Exception Die()
    {
        lock (_started)
            foreach (var operation in _started.Where(operation => File.Exists(_journal.EntryPath(operation.Id))))
                operation.AbandonAsIfCrashed();
        _dead.Set();
        _release.Wait(TimeSpan.FromMinutes(2));
        return new IOException("process gone");
    }

    /// <summary>Runs <paramref name="act"/> on another thread and waits until it died (true) or finished (false).</summary>
    public async Task<(bool Died, Task<T> Run)> RunUntilDeathAsync<T>(Func<Task<T>> act)
    {
        var run = Task.Run(act);
        lock (_runs) _runs.Add(run);
        var deadline = DateTime.UtcNow.AddSeconds(180); // generous: parallel mutant/suite runs slow a full-stack install down
        while (!_dead.IsSet && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
        return (_dead.IsSet, run);
    }

    /// <summary>
    /// Lets the parked thread go and waits until every run it parked has finished (w7 follow-up gate flake: the let-go
    /// thread's in-process catch wrote into the game/prefix folders while the test's fixture was deleting them —
    /// "Directory not empty" in <c>Dlss5FeederLifecycleAuditTests</c>). Nothing a test does after this races that thread.
    /// The wait is a hang guard only: the runs end within milliseconds once released.
    /// </summary>
    public void Dispose()
    {
        _journal.OperationStartedForTesting = null;
        _release.Set();
        Task[] runs;
        lock (_runs) runs = [.. _runs];
        try
        {
            if (!Task.WaitAll(runs, TimeSpan.FromMinutes(2)))
                throw new TimeoutException("A run let go by SimulatedDeath did not finish within the hang guard.");
        }
        catch (AggregateException)
        {
            // The let-go run failing (the seam's "process gone" IOException) is expected; only its end matters here.
        }
    }

    /// <summary>A journal (and its override service) the way the app builds it, with Wine never running.</summary>
    public static (DeploymentJournal Journal, RHI.Platform.Linux.Deployment.WineDllOverrideService Overrides) NewJournal(
        string directory, RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore records)
    {
        var overrides = new RHI.Platform.Linux.Deployment.WineDllOverrideService(new RHI.Platform.Linux.Deployment.WineRegistryEditor(_ => false));
        return (RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(overrides, directory, _ => false, records), overrides);
    }

    /// <summary>The next start's recovery: a fresh ledger reader (the dead thread still holds the old store's lock).</summary>
    public static Task<JournalRecoveryReport?> RecoverAsync(string journalDirectory, string ledgerPath)
    {
        var ledger = new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(ledgerPath);
        var overrides = new RHI.Platform.Linux.Deployment.WineDllOverrideService(new RHI.Platform.Linux.Deployment.WineRegistryEditor(_ => false));
        return RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.RecoverAsync(
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(overrides, journalDirectory, _ => false, ledger), ledger);
    }
}
