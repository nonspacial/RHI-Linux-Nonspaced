using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 commit-point review, findings 1 and 2: an unreadable ledger that no save of the operation got past rolls back at
// once whatever made it unreadable, and an operation left undecided is settled (or the next operation on its game
// refused) before anything else changes that game in the same session.
public sealed partial class DeploymentScopeCommitPointTests
{
    /// <summary>Makes the ledger unreadable by permission; false when this process reads it anyway (root).</summary>
    private bool LockLedger()
    {
        File.SetUnixFileMode(Ledger, UnixFileMode.None);
        try
        {
            File.ReadAllBytes(Ledger);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void UnlockLedger() => File.SetUnixFileMode(Ledger, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    // Finding 1: a ledger that cannot be READ (EACCES here, EIO alike) when the commit write comes: the store refuses the
    // save (it never read the file: unreadable, writes refused) or the save fails before its rename (a store that had read
    // it earlier writes a temp it cannot verify). Either way nothing of the operation landed: rolled back at once — whether
    // the ledger was unreadable when the operation began or became so before its commit write (it used to stay
    // half-installed until the ledger was repaired).
    public enum Unreadable { AtBeginFreshStore, AtBeginStoreReadItBefore, BeforeTheCommitWrite }

    [Theory]
    [InlineData(Unreadable.AtBeginFreshStore)]
    [InlineData(Unreadable.AtBeginStoreReadItBefore)]
    [InlineData(Unreadable.BeforeTheCommitWrite)]
    public async Task Install_LedgerUnreadableByPermission_NoSaveGotThrough_RollsBackAtOnce(Unreadable when)
    {
        var (records, journal) = Rig();
        records.Upsert(Record(ManagedComponentKind.OptiScaler));
        if (when != Unreadable.BeforeTheCommitWrite && !LockLedger()) return;
        if (when == Unreadable.AtBeginFreshStore)
        {
            records = new LinuxDeploymentRecordStore(Ledger);
            journal = SimulatedDeath.NewJournal(JournalDir, records).Journal;
        }

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "test install", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        OwnedFileSetRollback rollback;
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(Payload, After);
            if (when == Unreadable.BeforeTheCommitWrite && !LockLedger()) return;
            var failure = Xunit.Record.Exception(() => records.Upsert(scope.Stamp(Record())));
            Assert.True(failure is InvalidOperationException or UnauthorizedAccessException or IOException, failure?.ToString());
            if (when == Unreadable.AtBeginFreshStore) Assert.IsType<InvalidOperationException>(failure); // refused by the store
            rollback = await scope.RollbackAsync();
        }

        Assert.Equal(CommitDecision.NotCommitted, rollback.Decision);
        Assert.True(rollback.RolledBack);
        Assert.True(rollback.Success);
        Assert.Equal(Before, File.ReadAllBytes(Payload));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
    }

    // The other side of the same rule: a save that got past the ledger's checks and then left it unreadable (here by
    // permission) may have landed, so nothing is undone and the next start decides.
    [Fact]
    public async Task Install_LedgerUnreadableByPermissionAfterTheWrite_UndoesNothing()
    {
        var (records, journal) = Rig();
        var locked = true;
        LandedLedgerWrite.Set(records, LandedShape.InsideWrite, () =>
        {
            locked = LockLedger();
            return new IOException(LandedLedgerWrite.FaultMessage);
        });

        var (rollback, _) = await InstallAsync(records, journal);
        LandedLedgerWrite.Disarm(records);
        if (!locked) return;

        Assert.Equal(CommitDecision.Undecided, rollback!.Decision);
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.Single(JournalEntries());
        UnlockLedger();
    }

    /// <summary>An install left undecided: its commit write landed (<paramref name="landed"/>), then the ledger was damaged.</summary>
    private async Task<(LinuxDeploymentRecordStore Records, DeploymentJournal Journal, byte[] Landed)> UndecidedInstallAsync()
    {
        var (records, journal) = Rig();
        byte[]? landed = null;
        LandedLedgerWrite.Set(records, LandedShape.InsideWrite, () =>
        {
            landed = File.ReadAllBytes(Ledger);
            File.WriteAllText(Ledger, "{ not json");
            return new IOException(LandedLedgerWrite.FaultMessage);
        });
        var (rollback, _) = await InstallAsync(records, journal);
        LandedLedgerWrite.Disarm(records);
        Assert.Equal(CommitDecision.Undecided, rollback!.Decision);
        Assert.Single(JournalEntries());
        return (records, journal, landed!);
    }

    // Finding 2: the ledger reads again in the same session (the user removed it as the store's text says, or the landed
    // bytes came back) and the user retries the same install. The undecided entry is settled first, as the next start would
    // settle it; the retry then commits, and the next start keeps the retry's files and record (it used to roll the
    // entry back over the retry: record installed, file back at its pre-install bytes).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Undecided_ThenTheLedgerReadsAgain_ARetryCommits_AndTheNextStartKeepsIt(bool removeLedger)
    {
        var (records, journal, landed) = await UndecidedInstallAsync();
        if (removeLedger) File.Delete(Ledger);
        else File.WriteAllBytes(Ledger, landed);

        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry); // committed
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());

        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);

        Assert.Empty(report!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }

    // The ledger still cannot be read: the next install or removal of the same game is refused with a plain message before
    // anything changes; another game is not held up. Once the ledger reads, the next start decides as before.
    [Fact]
    public async Task Undecided_LedgerStillUnreadable_TheNextOperationOnTheGameIsRefused_NothingChanges()
    {
        var (records, journal, landed) = await UndecidedInstallAsync();

        var (install, installRefusal) = await DeploymentScope.BeginAsync(journal, "retry", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        var (removal, removalRefusal) = await DeploymentScope.BeginRemovalAsync(
            journal, records, "removal", Game, ManagedComponentKind.OptiScaler, CancellationToken.None);

        Assert.Null(install);
        Assert.Equal(DeploymentJournal.EarlierChangeUndecidedMessage, installRefusal);
        Assert.Null(removal);
        Assert.Equal(DeploymentJournal.EarlierChangeUndecidedMessage, removalRefusal);
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.Single(JournalEntries());
        Assert.Single(Snapshots());

        var otherRoot = P("other");
        Directory.CreateDirectory(otherRoot);
        var other = new DetectedGame { Name = "Other", Source = "Steam", SteamAppId = 4712, InstallPath = otherRoot, BinaryDirectory = otherRoot };
        var (otherScope, otherRefusal) = await DeploymentScope.BeginAsync(journal, "other", other, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(otherRefusal);
        await otherScope!.RollbackAsync();

        File.WriteAllBytes(Ledger, landed);
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }

    // The same hole across a restart: start-up recovery is deferred while the ledger cannot be read, so the kept entry
    // waits on disk. The session settles it before the game's next operation once the ledger reads again.
    [Fact]
    public async Task StartupDeferred_ThenTheLedgerReadsAgain_TheKeptEntryIsSettledBeforeTheRetry()
    {
        var (_, _, landed) = await UndecidedInstallAsync();

        // Restart: fresh store and journal; the ledger is still damaged, so recovery touches nothing.
        var records = new LinuxDeploymentRecordStore(Ledger);
        var journal = SimulatedDeath.NewJournal(JournalDir, records).Journal;
        var startup = await LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, gate: null);
        Assert.Null(startup.Recovery);
        Assert.Equal(1, startup.DeferredEntries);

        var (refused, refusal) = await DeploymentScope.BeginAsync(journal, "retry", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refused);
        Assert.Equal(DeploymentJournal.EarlierChangeUndecidedMessage, refusal);

        File.WriteAllBytes(Ledger, landed);
        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry);
        Assert.Empty(JournalEntries());
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);
        Assert.Empty(report!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }
    // A settle whose rollback fails keeps its entry for the next start and keeps holding up the game; once the rollback can
    // run, the next attempt settles it and the retry goes ahead.
    [Fact]
    public async Task Undecided_TheSettlingRollbackFails_TheGameStaysHeldUp_UntilItCanRollBack()
    {
        var (records, journal, _) = await UndecidedInstallAsync();
        File.Delete(Ledger); // reads again: not committed, so the settle rolls back
        var folder = P("game");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.WriteAllBytes(P("game", "probe"), []);
                return; // the folder is writable anyway (root): nothing to prove here
            }
            catch (UnauthorizedAccessException)
            {
            }
            var (refused, refusal) = await DeploymentScope.BeginAsync(journal, "retry", Game, ManagedComponentKind.ReShade, CancellationToken.None);

            Assert.Null(refused);
            Assert.Equal(DeploymentJournal.EarlierChangeUndecidedMessage, refusal);
            Assert.Single(JournalEntries());
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry);
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);
        Assert.Empty(report!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }
}
