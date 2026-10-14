using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #23/#31: the in-place swap (ShortFuse/Feeder add-on, NR DLL) places verified copies and, journaled, is an operation
/// whose commit point is the persisted record: a crash before it is saved puts the previous file back at the next start.
/// </summary>
public sealed class ManagedFileSwapJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-swap-journal-" + Guid.NewGuid().ToString("N"));

    public ManagedFileSwapJournalTests() => Directory.CreateDirectory(Path.Combine(_root, "game"));

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private (DetectedGame Game, LinuxDeploymentRecordStore Records, LinuxDeploymentRecord Record, string Target, string Source) Fixture()
    {
        var game = new DetectedGame { Name = "Swap", Source = "Steam", SteamAppId = 77, InstallPath = P("game"), BinaryDirectory = P("game") };
        var target = P("game", "renodx-dlss.addon64");
        File.WriteAllBytes(target, [1, 2, 3]);
        var source = P("new.addon64");
        File.WriteAllBytes(source, [9, 9, 9]);
        var records = new LinuxDeploymentRecordStore(P("deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), Component = ManagedComponentKind.ShortFuse, BinaryDirectory = P("game"),
            Version = "OLD", Files = [new LinuxDeployedFile { Path = target, Sha256 = FileHash.Sha256Hex(target) }]
        });
        return (game, records, records.Find(game, ManagedComponentKind.ShortFuse)!, target, source);
    }

    [Fact]
    public async Task JournaledSwap_CrashWhileSavingTheRecord_NextStartPutsThePreviousFileBack()
    {
        var (game, records, record, target, source) = Fixture();
        var (journal, _) = SimulatedDeath.NewJournal(P("journal"), records);
        using var death = new SimulatedDeath(journal);

        var (died, _) = await death.RunUntilDeathAsync(() => ManagedFileSwap.ReplaceAsync(record, [(target, source)], _ => throw death.Die(),
            journal: journal, game: game));
        Assert.True(died);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(target));

        var report = await SimulatedDeath.RecoverAsync(P("journal"), P("deployments.json"));

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(P("game"), "*.rhi-*"));
    }

    [Fact]
    public async Task JournaledSwap_SavedRecordCarriesTheOperation_AndLeavesNoEntryOrCopy()
    {
        var (game, records, record, target, source) = Fixture();
        var (journal, _) = SimulatedDeath.NewJournal(P("journal"), records);

        var changed = await ManagedFileSwap.ReplaceAsync(record, [(target, source)], updated => records.Upsert(updated), journal: journal, game: game);

        Assert.Equal(1, changed);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(target));
        var saved = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.True(saved.Metadata.ContainsKey(LinuxDeploymentRecovery.OperationIdMetadataKey));
        Assert.Equal(FileHash.Sha256Hex(target), saved.Files.Single().Sha256, ignoreCase: true);
        Assert.Empty(Directory.GetFiles(P("journal"), "*.json"));
        Assert.Empty(Directory.GetFiles(P("game"), "*.rhi-*"));
    }

    [Fact]
    public async Task IdenticalBytes_StillPersistTheRecord_AndChangeNoFile()
    {
        var (game, records, record, target, _) = Fixture();
        var same = P("same.addon64");
        File.WriteAllBytes(same, [1, 2, 3]);
        var persisted = 0;

        var changed = await ManagedFileSwap.ReplaceAsync(record, [(target, same)], updated => { persisted++; updated.Version = "NEW"; records.Upsert(updated); });

        Assert.Equal(0, changed);
        Assert.Equal(1, persisted); // a pin/version change with the same bytes is still saved
        Assert.Equal("NEW", records.Find(game, ManagedComponentKind.ShortFuse)!.Version);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
