using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w10 follow-up (reviews/w10-followup-findings.md D6–D9). RR1, RR1b, RR3 are the re-review's probes
// (reviews/w10-rereview-journal-holds-probes.cs.txt), adopted with the end state asserted.
public sealed partial class DeploymentScopeCommitPointTests
{
    // D6 / RR1: the game's folder is only unavailable for a while (its drive not mounted yet at this start). RHI's copy is
    // still there, so this is not "copy gone": the game is held with the restart text, nothing is closed, and the start
    // after the folder is back finishes the undo from that copy.
    [PermissionDenialFact]
    public async Task RR1_FolderTemporarilyUnavailable_IsNotTakenForGone_TheStartAfterItIsBackFinishesTheUndo()
    {
        try { await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        Assert.Single(Snapshots());
        Directory.Move(P("game"), P("game.offline")); // "not mounted"

        try
        {
            var (_, journal, startup) = await RestartAsync();
            var entry = Assert.Single(startup.Recovery!.Entries);
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
            Assert.Empty(entry.CopiesGone);
            Assert.Contains("is not there (is its drive mounted?)", Assert.Single(entry.Problems), StringComparison.Ordinal);
            Assert.Single(JournalEntries());
            var summary = LinuxDeploymentRecovery.Record(startup, P("recovery.log"));
            Assert.Empty(summary.CopiesGone);
            Assert.Equal(1, summary.RollbackFailed);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await RefusedRetryAsync(journal)); // held, restart text
            Assert.Single(JournalEntries());
        }
        finally
        {
            Directory.Move(P("game.offline"), P("game")); // "mounted again"
        }

        var (_, journal2, again) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(again.Recovery!.Entries).Outcome);
        Assert.Equal(Before, File.ReadAllBytes(Payload));
        Assert.Empty(Snapshots());
        Assert.Empty(JournalEntries());
        Assert.Null(await SameGameChangeAsync(journal2));
    }

    // D6 / RR1b: same in session — the held game's drive is unplugged and another game's change settles every waiting
    // entry: the held one is not closed and nobody is told its copy is gone; it is still held, and finished once back.
    [PermissionDenialFact]
    public async Task RR1b_InSession_HeldGameFolderUnplugged_AnotherGamesChange_DoesNotCloseIt()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        var told = new List<JournalRecoveryEntry>();
        journal.CopiesGoneInSession += told.Add;
        Directory.Move(P("game"), P("game.offline"));
        try
        {
            await InstallOtherGameAsync(records, journal);
            Assert.Empty(told);
            Assert.Single(JournalEntries());
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await RefusedRetryAsync(journal));
        }
        finally
        {
            Directory.Move(P("game.offline"), P("game"));
        }

        var (retry, _) = await InstallAsync(records, journal); // back: the undo is finished first, then the retry commits
        Assert.Null(retry);
        Assert.Empty(told);
        Assert.Empty(JournalEntries());
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
    }

    // D7 / RR3 (coordinator decision): an entry kept for a newer build's step is narrowed to that step once its file
    // rollback is final, so a later same-game install of the same bytes is never undone at a later start.
    [Fact]
    public async Task RR3_UnknownStepEntry_IsNarrowedToItsStep_SoALaterInstallIsKeptAtTheNextStart()
    {
        var created = P("game", "new.dll");
        var records0 = new LinuxDeploymentRecordStore(Ledger);
        var j0 = LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), JournalDir, _ => false, records: records0);
        var op = await j0.BeginAsync("future", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await op.TrackFileAsync(created);
        await op.RecordWriteAsync(created, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(created, After);
        await op.AddCompensationAsync("a-step-from-a-newer-build", "{}");
        op.AbandonAsIfCrashed();

        var (records, journal, s1) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.UnknownStep, Assert.Single(s1.Recovery!.Entries).Outcome);
        Assert.False(File.Exists(created));
        using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Assert.Single(JournalEntries()))))
        {
            Assert.Equal(0, document.RootElement.GetProperty("Files").GetArrayLength()); // the files are final: dropped
            Assert.Equal("a-step-from-a-newer-build", Assert.Single(document.RootElement.GetProperty("Compensations").EnumerateArray()).GetProperty("Kind").GetString());
        }
        var narrowed = File.ReadAllBytes(Assert.Single(JournalEntries()));

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "later install", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(created, After);
            records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(Game), GameName = "Example", Component = ManagedComponentKind.ReShade,
                BinaryDirectory = P("game"), Files = [new LinuxDeployedFile { Path = created, Sha256 = FileHash.Sha256HexOf(After) }]
            }));
            await scope.CommitAsync();
        }
        Assert.True(File.Exists(created));

        var (_, _, s2) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.UnknownStep, Assert.Single(s2.Recovery!.Entries).Outcome);
        Assert.True(File.Exists(created), "the next start deleted the later install's file (record still says installed)");
        Assert.Equal(After, File.ReadAllBytes(created));
        Assert.Equal(narrowed, File.ReadAllBytes(Assert.Single(JournalEntries()))); // kept for the newer build, as narrowed
    }

    // D8: a held game's retry cannot open RHI's lock file of the entry for a reason other than another process holding it
    // (here a link loop, as a read-only file system or an I/O error would): refused with the true reason — RHI's own note
    // of the change — never the ledger text ("another process works on it" is only EWOULDBLOCK).
    [PermissionDenialFact]
    public async Task D8_Held_TheEntrysLockFailsToOpenForAnotherReason_IsRefusedWithTheTrueReason_NotTheLedger()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        var lockFile = Assert.Single(Directory.GetFiles(JournalDir, "*.lock"));
        File.Delete(lockFile);
        File.CreateSymbolicLink(lockFile, lockFile); // ELOOP on open: an IOException that is not a held lock
        try
        {
            Assert.Equal(DeploymentJournal.EarlierChangeNotSettledMessage, await RefusedRetryAsync(journal));
            Assert.Equal(After, File.ReadAllBytes(Payload)); // nothing was tried
        }
        finally
        {
            File.Delete(lockFile);
        }

        var (retry, _) = await InstallAsync(records, journal);
        Assert.Null(retry);
        Assert.Empty(journal.WaitingForTesting);
        Assert.Empty(JournalEntries());
    }

    // D8, the other side: a lock another open file holds is still "another process works on it now" (undecided).
    [Fact]
    public void D8_ALockHeldByAnotherOpenFile_IsToldApartFromAnyOtherIOError()
    {
        Directory.CreateDirectory(JournalDir);
        var lockFile = P("journal", "held.lock");
        using var holder = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var held = Assert.Throws<IOException>(() => new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        Assert.True(DeploymentJournal.IsHeldByAnother(held), $"HResult {held.HResult}: {held.Message}");
        var loop = P("journal", "loop.lock");
        File.CreateSymbolicLink(loop, loop);
        var other = Assert.Throws<IOException>(() => new FileStream(loop, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        Assert.False(DeploymentJournal.IsHeldByAnother(other), $"HResult {other.HResult}: {other.Message}");
    }

    // D9: an entry that is held (a restore a retry can finish) and also has a copy that is gone names that file once — at
    // the start that closes it — not at every start while it is held.
    [PermissionDenialFact]
    public async Task D9_AHeldEntryWithAGoneCopy_NamesTheFileOnce_WhenItCloses_NotAtEveryStartWhileHeld()
    {
        Rig();
        var side = P("game", "side", "x.dll");
        Directory.CreateDirectory(P("game", "side"));
        File.WriteAllBytes(side, Before);
        var j0 = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var op = await j0.BeginAsync("gone and held", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await op.TrackFileAsync(Payload);
        await op.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(Payload, After);
        await op.TrackFileAsync(side);
        await op.RecordWriteAsync(side, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(side, After);
        foreach (var snapshot in Snapshots()) File.Delete(snapshot); // the payload's copy only (the side file's is in side/)
        op.AbandonAsIfCrashed();

        PermissionDenial.MakeReadOnly(P("game", "side"));
        try
        {
            for (var start = 0; start < 2; start++)
            {
                var (_, _, startup) = await RestartAsync();
                var entry = Assert.Single(startup.Recovery!.Entries);
                Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
                Assert.Equal([Payload], entry.CopiesGone);
                var summary = LinuxDeploymentRecovery.Record(startup, P("recovery.log"));
                Assert.Empty(summary.CopiesGone); // held: told it will be finished, not yet that a copy is gone
                Assert.Equal(1, summary.RollbackFailed);
            }
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game", "side"));
        }

        var (_, _, closing) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, Assert.Single(closing.Recovery!.Entries).Outcome);
        Assert.Equal([Payload], LinuxDeploymentRecovery.Record(closing, P("recovery.log")).CopiesGone);
        Assert.Equal(Before, File.ReadAllBytes(side));
        Assert.Empty(JournalEntries());
        var (_, _, after) = await RestartAsync();
        Assert.Empty(after.Recovery!.Entries);
    }
}
