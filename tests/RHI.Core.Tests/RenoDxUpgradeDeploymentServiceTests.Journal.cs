using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: RenoDX Upgrade journals its add-on (#23); a failure after the copy no longer leaves it without a record (#31).
public sealed partial class RenoDxUpgradeDeploymentServiceTests
{
    [Fact]
    public async Task Install_RecordSaveFails_RemovesTheAddonAgain()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var store = NewStore();
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x95));
        store.SaveFaultForTesting = () => new IOException("disk full");

        var install = await NewService(http, store).InstallAsync(game);

        Assert.False(install.Success);
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "renodx-upgrade.*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheAddonBack()
    {
        var game = CreateGame(0x8664, withReShade: true);
        var ledger = Path.Combine(_root, "crash-remove-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new LinuxDeploymentRecordStore(ledger);
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x96));
        var journalDir = Path.Combine(_root, "journal-" + Guid.NewGuid().ToString("N"));
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, store);
        var service = new RenoDxUpgradeDeploymentService(http, store, new UpstreamRhiManifestService(http, "https://example.test/manifest.json"),
            Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")), journal);
        Assert.True((await service.InstallAsync(game)).Success);
        var target = store.Find(game, ManagedComponentKind.RenoDXUpgrade)!.Files.Single().Path;
        var bytes = File.ReadAllBytes(target);
        using var death = new SimulatedDeath(journal);
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null; // 1 = the removal's stamp

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(game));
        Assert.True(died);
        Assert.False(File.Exists(target));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }
}
