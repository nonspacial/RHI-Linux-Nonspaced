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

    // w8 integration (#204): RenoDX Upgrade's install and removal with their commit write landing and then throwing are
    // finished as committed in both seam shapes (the add-on stays with its record / stays gone with no record).
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task InstallAndRemoval_CommitWriteLandsThenThrows_AreKept(LandedShape shape)
    {
        var game = CreateGame(0x8664, withReShade: true);
        var ledger = Path.Combine(_root, "landed-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new LinuxDeploymentRecordStore(ledger);
        using var http = NewInstallHttp(CreatePe(0x8664, 96 * 1024, 0x97));
        var journalDir = Path.Combine(_root, "journal-" + Guid.NewGuid().ToString("N"));
        var (journal, _) = SimulatedDeath.NewJournal(journalDir, store);
        var service = new RenoDxUpgradeDeploymentService(http, store, new UpstreamRhiManifestService(http, "https://example.test/manifest.json"),
            Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")), journal);

        await LandedRow.RunAsync(store, shape, at: 1, () => service.InstallAsync(game));
        var target = new LinuxDeploymentRecordStore(ledger).Find(game, ManagedComponentKind.RenoDXUpgrade)!.Files.Single().Path;
        Assert.True(File.Exists(target));

        await LandedRow.RunAsync(store, shape, at: 2, () => service.RemoveAsync(game), removal: true); // 1 = the removal's stamp
        Assert.Null(new LinuxDeploymentRecordStore(ledger).Find(game, ManagedComponentKind.RenoDXUpgrade));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(journalDir));
    }
}
