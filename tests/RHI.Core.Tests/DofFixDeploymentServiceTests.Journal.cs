using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: DOF Fix journals its add-on (#23); a failure after the copy no longer leaves it without a record (#31).
public sealed partial class DofFixDeploymentServiceTests
{
    [Fact]
    public async Task Install_RecordSaveFails_RemovesTheAddonAgain()
    {
        var (service, records, game, http) = CancelFixture("save-fails");
        using var _ = http;
        records.SaveFaultForTesting = () => new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(game));

        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName)));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*"));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheAddonBack()
    {
        var (unjournaled, records, game, http) = CancelFixture("crash-remove");
        using var _ = http;
        Assert.True((await unjournaled.InstallAsync(game)).Success);
        var addon = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);
        var bytes = File.ReadAllBytes(addon);
        var ledger = Path.Combine(_root, "crash-remove-records.json");
        var journalDir = Path.Combine(_root, "crash-remove-journal");
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, records);
        var service = new DofFixDeploymentService(http, records, new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json"),
            Path.Combine(_root, "cache-crash-remove"), journal);
        using var death = new SimulatedDeath(journal);
        var saves = 0;
        records.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null; // 1 = the removal's stamp

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(game));
        Assert.True(died);
        Assert.False(File.Exists(addon));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(addon));
    }
}
