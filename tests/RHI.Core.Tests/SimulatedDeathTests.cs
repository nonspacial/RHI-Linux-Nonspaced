namespace RHI.Core.Tests;

/// <summary>
/// The crash-test helper's own guarantee (w7 follow-up: a gate run went red when the let-go thread wrote into a fixture
/// folder being deleted): once <see cref="SimulatedDeath"/> is disposed, the run it parked has finished, so nothing the
/// test does next (assertions, fixture clean-up) races it.
/// </summary>
public sealed class SimulatedDeathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-simulated-death-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WaitsForTheLetGoRunToFinish(bool runThrows)
    {
        var records = new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var (journal, _) = SimulatedDeath.NewJournal(Path.Combine(_root, "journal"), records);
        var finished = false;
        Task<int> run;
        using (var death = new SimulatedDeath(journal))
        {
            (var died, run) = await death.RunUntilDeathAsync(async () =>
            {
                var error = death.Die();          // parked here until the test lets go
                await Task.Delay(300);            // the let-go thread's catch/rollback still working
                finished = true;
                if (runThrows) throw error;
                return 1;
            });
            Assert.True(died);
        }

        Assert.True(finished, "the let-go run was still working after SimulatedDeath was disposed");
        Assert.True(run.IsCompleted);
    }
}
