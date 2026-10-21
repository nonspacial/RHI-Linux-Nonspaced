using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #204, the one decision point: a failure at or after an operation's commit write is decided by
/// <see cref="DeploymentScope.RollbackAsync"/> / <see cref="DeploymentScope.RollbackRemovalAsync"/> from a FRESH read of the
/// ledger file with start-up recovery's rule. Committed → finished as committed (nothing undone); not committed → rolled
/// back as before; ledger unreadable → nothing undone in process, the next start decides.
/// </summary>
[Trait("Category", "CommitPoint")]
public sealed partial class DeploymentScopeCommitPointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-commit-point-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Before = "pre-operation bytes"u8.ToArray();
    private static readonly byte[] After = "bytes the operation placed"u8.ToArray();

    public DeploymentScopeCommitPointTests() => Directory.CreateDirectory(P("game"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);
    private string Payload => P("game", "dxgi.dll");
    private string Ledger => P("deployments.json");
    private string JournalDir => P("journal");

    private DetectedGame Game => new() { Name = "Example", Source = "Steam", SteamAppId = 4711, InstallPath = P("game"), BinaryDirectory = P("game") };

    private LinuxDeploymentRecord Record(ManagedComponentKind component = ManagedComponentKind.ReShade, string? sha = null) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(Game), GameName = "Example", Component = component, BinaryDirectory = P("game"),
        Files = [new LinuxDeployedFile { Path = Payload, Sha256 = sha ?? FileHash.Sha256HexOf(After) }]
    };

    private (LinuxDeploymentRecordStore Records, DeploymentJournal Journal) Rig(bool withLedgerCheck = true)
    {
        File.WriteAllBytes(Payload, Before);
        var records = new LinuxDeploymentRecordStore(Ledger);
        var journal = withLedgerCheck
            ? SimulatedDeath.NewJournal(JournalDir, records).Journal
            : LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), JournalDir, _ => false);
        return (records, journal);
    }

    /// <summary>An install: the payload replaced, then the stamped record saved (the commit point); the catch rolls back.</summary>
    private async Task<(OwnedFileSetRollback? Rollback, DeploymentScope Scope)> InstallAsync(LinuxDeploymentRecordStore records, DeploymentJournal? journal)
    {
        DeploymentScope scope;
        if (journal is null) scope = DeploymentScope.Unjournaled();
        else
        {
            var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "test install", Game, ManagedComponentKind.ReShade, CancellationToken.None);
            Assert.Null(refusal);
            scope = opened!;
        }
        await using (scope)
        {
            try
            {
                await scope.PlaceBytesAsync(Payload, After);
                records.Upsert(scope.Stamp(Record()));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return (await scope.RollbackAsync(), scope);
            }
            await scope.CommitAsync();
            return (null, scope);
        }
    }

    private string[] JournalEntries() => Directory.Exists(JournalDir) ? Directory.GetFiles(JournalDir, "*.json") : [];
    private string[] Snapshots() => Directory.GetFiles(P("game"), "*" + OwnedFileSet.SnapshotMarker + "*");

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Install_CommitWriteLandsThenThrows_IsFinishedAsCommitted_NothingUndone(LandedShape shape)
    {
        var (records, journal) = Rig();
        LandedLedgerWrite.Arm(records, shape, at: 1);

        var (rollback, _) = await InstallAsync(records, journal);

        Assert.NotNull(rollback);
        Assert.True(rollback!.Committed);
        Assert.False(rollback.RolledBack);
        Assert.True(rollback.Success);
        Assert.Equal("", DeploymentScope.RollbackNote(rollback));
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    [Fact]
    public async Task Install_CommitWriteFailsBeforeLanding_RollsBackAsBefore()
    {
        var (records, journal) = Rig();
        using (LedgerWriteBlocker.Block(records, Ledger))
        {
            var (rollback, _) = await InstallAsync(records, journal);

            Assert.True(rollback!.RolledBack);
            Assert.True(rollback.Success);
        }
        Assert.Equal(Before, File.ReadAllBytes(Payload));
        Assert.Null(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    // The ledger changed during the operation and cannot be read now: nothing is undone in process (the entry and every
    // copy stay) and the result says so; the next start decides from the ledger as it then reads.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Install_LedgerUnreadableAfterTheWrite_UndoesNothing_AndTheNextStartDecides(bool landedLedgerComesBack)
    {
        var (records, journal) = Rig();
        records.Upsert(Record(ManagedComponentKind.OptiScaler)); // an earlier ledger to come back to
        var earlier = File.ReadAllBytes(Ledger);
        byte[]? landed = null;
        LandedLedgerWrite.Set(records, LandedShape.InsideWrite, () =>
        {
            landed = File.ReadAllBytes(Ledger);
            File.WriteAllText(Ledger, "{ not json"); // e.g. damaged by something else right after the write
            return new IOException(LandedLedgerWrite.FaultMessage);
        });

        var (rollback, _) = await InstallAsync(records, journal);
        LandedLedgerWrite.Disarm(records);

        Assert.Equal(CommitDecision.Undecided, rollback!.Decision);
        Assert.False(rollback.RolledBack);
        Assert.False(rollback.Success);
        Assert.Equal(" " + DeploymentOperation.UndecidedNote, DeploymentScope.RollbackNote(rollback));
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.Single(JournalEntries());
        Assert.Single(Snapshots());

        File.WriteAllBytes(Ledger, landedLedgerComesBack ? landed! : earlier);
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);

        Assert.Equal(landedLedgerComesBack ? JournalRecoveryOutcome.AlreadyCommitted : JournalRecoveryOutcome.RolledBack,
            Assert.Single(report!.Entries).Outcome);
        Assert.Equal(landedLedgerComesBack ? After : Before, File.ReadAllBytes(Payload));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    // A ledger that was already unreadable when the operation began refused every save, so no write of it landed: the
    // failure rolls back as before (it must not be left half-installed until the ledger is repaired).
    [Fact]
    public async Task Install_LedgerUnreadableBeforeTheOperation_RollsBackAsBefore()
    {
        var (records, journal) = Rig();
        File.WriteAllText(Ledger, "{ not json");

        var (rollback, _) = await InstallAsync(records, journal);

        Assert.True(rollback!.RolledBack);
        Assert.Equal(Before, File.ReadAllBytes(Payload));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    // No journal (inside-operation helpers, tests) or a journal that does not see the ledger: no decision, rolled back as before.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutALedgerToReadAgain_ALandedWriteIsRolledBackAsBefore(bool journaled)
    {
        var (records, journal) = Rig(withLedgerCheck: false);
        LandedLedgerWrite.Arm(records, LandedShape.InsideWrite, at: 1);

        var (rollback, _) = await InstallAsync(records, journaled ? journal : null);

        Assert.True(rollback!.RolledBack);
        Assert.Equal(Before, File.ReadAllBytes(Payload));
    }

    /// <summary>A removal: the record stamped, the payload deleted, then the record (and <paramref name="alongside"/>) saved without it.</summary>
    private async Task<OwnedFileSetRollback?> RemoveAsync(LinuxDeploymentRecordStore records, DeploymentJournal journal,
        LinuxDeploymentRecord? alongsideAfter = null, LinuxDeploymentRecord? alongsideBefore = null)
    {
        var record = records.Find(Game, ManagedComponentKind.ReShade)!;
        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(journal, records, "test removal", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await using var scope = opened!;
        try
        {
            await scope.DeleteAsync(Payload);
            records.Commit(alongsideAfter is null ? [] : [alongsideAfter], [(Game, ManagedComponentKind.ReShade)]);
        }
        catch (IOException)
        {
            return await scope.RollbackRemovalAsync(records, Game, record, "test", alongside: alongsideBefore is null ? null : [alongsideBefore]);
        }
        await scope.CommitAsync();
        return null;
    }

    private (LinuxDeploymentRecordStore Records, DeploymentJournal Journal) Installed()
    {
        var (records, journal) = Rig();
        File.WriteAllBytes(Payload, After);
        records.Upsert(Record());
        return (records, journal);
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Removal_CommitWriteLandsThenThrows_StaysRemoved_TheRecordIsNotPutBack(LandedShape shape)
    {
        var (records, journal) = Installed();
        LandedLedgerWrite.Arm(records, shape, at: 2); // 1 = the removal's stamp, 2 = the commit point

        var rollback = await RemoveAsync(records, journal);

        Assert.True(rollback!.Committed);
        Assert.False(File.Exists(Payload));
        Assert.Null(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    // Two records at one commit point (the DX9 Feeder/Luma removals, ReShade under OptiScaler, ShortFuse): a landed write
    // keeps BOTH as written — the removed one stays gone and the other is not put back as it was before.
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Removal_TwoRecordCommitLandsThenThrows_KeepsBothRecordsAsWritten(LandedShape shape)
    {
        var (records, journal) = Installed();
        var other = Record(ManagedComponentKind.OptiScaler, sha: "before");
        records.Upsert(other);
        var otherAfter = LinuxDeploymentRecordStore.Clone(other);
        otherAfter.Version = "after";
        LandedLedgerWrite.Arm(records, shape, at: 2);

        var rollback = await RemoveAsync(records, journal, alongsideAfter: otherAfter, alongsideBefore: other);

        Assert.True(rollback!.Committed);
        var fresh = new LinuxDeploymentRecordStore(Ledger);
        Assert.Null(fresh.Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal("after", fresh.Find(Game, ManagedComponentKind.OptiScaler)!.Version);
        Assert.False(File.Exists(Payload));
    }

    [Fact]
    public async Task Removal_CommitWriteFailsBeforeLanding_PutsTheFileBack_AndTheRecordStays()
    {
        var (records, journal) = Installed();
        var saves = 0;
        records.SaveFaultForTesting = () => ++saves == 2 ? new IOException("disk full") : null;

        var rollback = await RemoveAsync(records, journal);
        records.SaveFaultForTesting = null;

        Assert.True(rollback!.RolledBack);
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Empty(JournalEntries());
    }

    // The in-process decision and start-up recovery read the same rule: for the install's and the removal's every write,
    // dying right after it lands and throwing right after it lands leave the same files and records.
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task CrashRecovery_AndTheInProcessOutcome_Agree_AtEveryWrite(LandedShape shape)
    {
        Task<CommitPointCase> Make(string name, bool removal)
        {
            var root = P("agree", name);
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var payload = Path.Combine(game, "dxgi.dll");
            File.WriteAllBytes(payload, removal ? After : Before);
            var ledger = Path.Combine(root, "deployments.json");
            var records = new LinuxDeploymentRecordStore(ledger);
            var detected = new DetectedGame { Name = "Example", Source = "Steam", SteamAppId = 4711, InstallPath = game, BinaryDirectory = game };
            LinuxDeploymentRecord Rec() => new()
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(detected), GameName = "Example", Component = ManagedComponentKind.ReShade,
                BinaryDirectory = game, Files = [new LinuxDeployedFile { Path = payload, Sha256 = FileHash.Sha256HexOf(After) }]
            };
            if (removal) records.Upsert(Rec());
            var journalDir = Path.Combine(root, "journal");
            var journal = SimulatedDeath.NewJournal(journalDir, records).Journal;
            async Task<ComponentOperationResult> Act()
            {
                var (opened, refusal) = removal
                    ? await DeploymentScope.BeginRemovalAsync(journal, records, "removal", detected, ManagedComponentKind.ReShade, CancellationToken.None)
                    : await DeploymentScope.BeginAsync(journal, "install", detected, ManagedComponentKind.ReShade, CancellationToken.None);
                if (refusal is not null) return ComponentOperationResult.Fail(refusal);
                await using var scope = opened!;
                try
                {
                    if (removal)
                    {
                        await scope.DeleteAsync(payload);
                        records.Remove(detected, ManagedComponentKind.ReShade);
                    }
                    else
                    {
                        await scope.PlaceBytesAsync(payload, After);
                        records.Upsert(scope.Stamp(Rec()));
                    }
                }
                catch (IOException ex)
                {
                    var rollback = removal ? await scope.RollbackRemovalAsync(records, detected, Rec(), "test") : await scope.RollbackAsync();
                    return rollback.Committed ? DeploymentScope.Landed("Example", removal) : ComponentOperationResult.Fail(ex.Message);
                }
                await scope.CommitAsync();
                return ComponentOperationResult.Ok("done");
            }
            return Task.FromResult(new CommitPointCase(root, [game], records, ledger, journal, journalDir, Act));
        }

        var install = await CommitPointAgreement.AssertEveryWriteAsync(name => Make(name, removal: false), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(name => Make(name, removal: true), shape);

        Assert.True(Assert.Single(install).Success);            // its only write is the commit point
        Assert.Equal([false, true], removal.Select(r => r.Success)); // the stamp, then the commit point
    }
}
