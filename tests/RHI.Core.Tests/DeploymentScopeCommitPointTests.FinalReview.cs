using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w10 final review (reviews/w10-final-review.md findings 3 and 4).
public sealed partial class DeploymentScopeCommitPointTests
{
    // Finding 3 (coordinator decision: a game is never held for good by something a restart cannot fix). The review's
    // probe: a crashed change edited game/sub/ReShade.ini, then game/sub was deleted while game stays. RHI's copy went with
    // the sub-folder, and the game folder is there (its drive is mounted), so the first start closes the change and names
    // the file; the game is not refused again. While the GAME folder itself is not there (drive offline) it is still held.
    [PermissionDenialFact]
    public async Task F3_ASubFolderDeletedInAGameFolderThatIsThere_ClosesTheChange_ButAMissingGameFolderStillHolds()
    {
        Rig();
        var sub = P("game", "sub");
        var ini = Path.Combine(sub, "ReShade.ini");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(ini, Before);
        var j0 = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var (op, refusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(j0, "edit the sub-folder's ReShade.ini", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await op!.TrackFileAsync(ini);
        File.WriteAllBytes(ini, After);
        await op.RecordWrittenContentAsync(ini);
        op.AbandonAsIfCrashed(); // the process dies here
        Assert.Single(Directory.GetFiles(sub, "*" + OwnedFileSet.SnapshotMarker + "*")); // RHI's copy is in the sub-folder

        // The game's drive is not mounted at this start: nothing proves the copy gone; held with the restart text.
        Directory.Move(P("game"), P("game.offline"));
        try
        {
            var (_, journal, offline) = await RestartAsync();
            var entry = Assert.Single(offline.Recovery!.Entries);
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
            Assert.Empty(entry.CopiesGone);
            Assert.Empty(LinuxDeploymentRecovery.Record(offline, P("recovery.log")).CopiesGone);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await RefusedRetryAsync(journal));
            Assert.Single(JournalEntries());
        }
        finally
        {
            Directory.Move(P("game.offline"), P("game"));
        }

        // Mounted again, but the sub-folder (and RHI's copy in it) was deleted: following "restart RHI" ends it.
        Directory.Delete(sub, recursive: true);
        var (_, journal2, startup) = await RestartAsync();
        var closed = Assert.Single(startup.Recovery!.Entries);
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, closed.Outcome);
        Assert.Equal([ini], closed.CopiesGone);
        Assert.Empty(JournalEntries());
        var summary = LinuxDeploymentRecovery.Record(startup, P("recovery.log"));
        Assert.True(summary.NeedsNotice);
        Assert.Equal([ini], summary.CopiesGone);
        Assert.Equal(0, summary.RollbackFailed);
        Assert.Null(await SameGameChangeAsync(journal2));
        for (var start = 0; start < 2; start++)
        {
            var (_, journal3, later) = await RestartAsync();
            Assert.Empty(later.Recovery!.Entries);
            Assert.Null(await SameGameChangeAsync(journal3));
        }
        Assert.False(Directory.Exists(sub)); // nothing re-created
    }

    // Finding 3, the unit: a missing folder is climbed only inside a game folder, and only while that game folder is there.
    [Fact]
    public void F3_ProvedAbsent_ClimbsOnlyInsideAGameFolderThatIsThere()
    {
        Directory.CreateDirectory(P("game"));
        var deep = P("game", "a", "b", "copy.rhi-rollback-x");
        Assert.False(OwnedFileSet.ProvedAbsent(deep)); // no game folder known: the copy's own folder must list (D6)
        Assert.True(OwnedFileSet.ProvedAbsent(deep, [P("game")])); // game/a missing, game lists without it
        Assert.True(OwnedFileSet.ProvedAbsent(deep, [P("game") + Path.DirectorySeparatorChar])); // spelled with a slash
        Assert.False(OwnedFileSet.ProvedAbsent(P("elsewhere", "x", "copy"), [P("game")])); // outside every game folder
        Directory.CreateDirectory(P("gamex"));
        Assert.False(OwnedFileSet.ProvedAbsent(P("gamex", "a", "copy"), [P("game")])); // a sibling sharing the prefix
        Directory.Move(P("game"), P("game.offline"));
        Assert.False(OwnedFileSet.ProvedAbsent(deep, [P("game")])); // the game folder itself is not there: offline
        Assert.False(OwnedFileSet.ProvedAbsent(P("game", "copy"), [P("game")]));
    }

    // Re-review 1, the unit: an install folder inside the Wine prefix is a game folder too. While it is missing (its drive
    // offline behind a mount point in the prefix) nothing below it is proved gone, though the prefix lists without it.
    [Fact]
    public void FRR1_ProvedAbsent_StopsAtAMissingInstallFolderNestedInThePrefix()
    {
        var pfx = P("pfx");
        var foo = P("pfx", "drive_c", "Games", "Foo");
        var deep = P("pfx", "drive_c", "Games", "Foo", "sub", "copy.rhi-rollback-x");
        Directory.CreateDirectory(Path.Combine(foo, "sub"));
        File.WriteAllBytes(deep, Before);
        Assert.False(OwnedFileSet.ProvedAbsent(deep, [foo, pfx])); // there
        File.Delete(deep);
        Assert.True(OwnedFileSet.ProvedAbsent(deep, [foo, pfx])); // its own folder lists without it
        Directory.Delete(Path.Combine(foo, "sub"));
        Assert.True(OwnedFileSet.ProvedAbsent(deep, [foo, pfx])); // sub deleted inside Foo, which is there
        Directory.Move(foo, P("Foo.away"));
        Assert.False(OwnedFileSet.ProvedAbsent(deep, [foo, pfx])); // Foo itself missing: held
        Assert.False(OwnedFileSet.ProvedAbsent(deep, [pfx, foo])); // in either order
        Assert.False(OwnedFileSet.ProvedAbsent(deep, [foo + Path.DirectorySeparatorChar, pfx])); // spelled with a slash
        Assert.False(OwnedFileSet.ProvedAbsent(Path.Combine(foo, "copy"), [foo, pfx]));
        Assert.True(OwnedFileSet.ProvedAbsent(deep, [pfx])); // only the prefix known (older shape): climbs to it
    }

    // Re-review 1, the restart flow: a crashed edit of Foo/sub/ReShade.ini where Foo (the install folder) sits inside the
    // prefix. Foo moved away for one start: held, entry kept, RHI's copy kept. Foo back: the undo finishes and the file is
    // restored.
    [Fact]
    public async Task FRR1_AMissingInstallFolderInsideThePrefix_Holds_ThenTheUndoFinishesOnceItIsBack()
    {
        Rig();
        var foo = P("pfx", "drive_c", "Games", "Foo");
        var nested = new DetectedGame
        {
            Name = "Foo", Source = "Lutris", InstallPath = foo, BinaryDirectory = foo, PrefixPath = P("pfx")
        };
        Assert.Equal([foo, P("pfx")], LinuxDeploymentRecovery.GameFolders(nested));
        var sub = Path.Combine(foo, "sub");
        var ini = Path.Combine(sub, "ReShade.ini");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(ini, Before);
        var j0 = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var (op, refusal) = await LinuxDeploymentRecovery.BeginRecordOperationAsync(j0, "edit Foo's ReShade.ini", nested, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await op!.TrackFileAsync(ini);
        File.WriteAllBytes(ini, After);
        await op.RecordWrittenContentAsync(ini);
        op.AbandonAsIfCrashed(); // the process dies here
        Assert.Single(Directory.GetFiles(sub, "*" + OwnedFileSet.SnapshotMarker + "*"));

        async Task<string?> NestedChangeAsync(DeploymentJournal journal)
        {
            var (opened, refused) = await DeploymentScope.BeginAsync(journal, "retry", nested, ManagedComponentKind.ReShade, CancellationToken.None);
            if (opened is not null) await opened.RollbackAsync();
            return refused;
        }

        // Foo's drive is not mounted at this start (the prefix is there and lists without Foo): held, nothing named.
        var away = P("Foo.away");
        Directory.Move(foo, away);
        try
        {
            var (_, journal, offline) = await RestartAsync();
            var entry = Assert.Single(offline.Recovery!.Entries);
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
            Assert.Empty(entry.CopiesGone);
            Assert.Empty(LinuxDeploymentRecovery.Record(offline, P("recovery.log")).CopiesGone);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await NestedChangeAsync(journal));
            Assert.Single(JournalEntries());
            Assert.Single(Directory.GetFiles(Path.Combine(away, "sub"), "*" + OwnedFileSet.SnapshotMarker + "*")); // RHI's copy kept
        }
        finally
        {
            Directory.Move(away, foo);
        }

        // Foo is back: the undo finishes and the file is RHI's no longer.
        var (_, journal2, back) = await RestartAsync();
        var done = Assert.Single(back.Recovery!.Entries);
        Assert.Equal(JournalRecoveryOutcome.RolledBack, done.Outcome);
        Assert.Empty(done.CopiesGone);
        Assert.Equal(Before, File.ReadAllBytes(ini));
        Assert.Empty(Directory.GetFiles(sub, "*" + OwnedFileSet.SnapshotMarker + "*"));
        Assert.Empty(JournalEntries());
        Assert.Null(await NestedChangeAsync(journal2));
    }

    // Finding 4: an entry kept for a newer build's step whose copy is gone names that file in the notice once — at the
    // start that drops the entry's files (narrowed to the step) — and never again.
    [Fact]
    public async Task F4_AGoneCopyInAnUnknownStepEntry_IsNamedInTheNoticeOnce()
    {
        Rig();
        var j0 = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var op = await j0.BeginAsync("future", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await op.TrackFileAsync(Payload);
        await op.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(Payload, After);
        await op.AddCompensationAsync("a-step-from-a-newer-build", "{}");
        foreach (var snapshot in Snapshots()) File.Delete(snapshot);
        op.AbandonAsIfCrashed();

        var (_, _, first) = await RestartAsync();
        var entry = Assert.Single(first.Recovery!.Entries);
        Assert.Equal(JournalRecoveryOutcome.UnknownStep, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        var summary = LinuxDeploymentRecovery.Record(first, P("recovery.log"));
        Assert.Equal([Payload], summary.CopiesGone);
        Assert.True(summary.NeedsNotice);

        for (var start = 0; start < 2; start++)
        {
            var (_, _, later) = await RestartAsync();
            Assert.Equal(JournalRecoveryOutcome.UnknownStep, Assert.Single(later.Recovery!.Entries).Outcome);
            Assert.Empty(LinuxDeploymentRecovery.Record(later, P("recovery.log")).CopiesGone); // named once
        }
        Assert.Single(JournalEntries()); // still kept for the newer build
    }

    // Finding 4, in session: the same entry handed over by a start that could not run recovery (WaitOnKeptEntries) is
    // settled before the game's next change; its gone copy is named then (the status-line notice), and not at a later start.
    [Fact]
    public async Task F4_AGoneCopyInAnUnknownStepEntry_SettledInSession_IsNamedOnce()
    {
        Rig();
        var j0 = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var op = await j0.BeginAsync("future", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await op.TrackFileAsync(Payload);
        await op.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(Payload, After);
        await op.AddCompensationAsync("a-step-from-a-newer-build", "{}");
        foreach (var snapshot in Snapshots()) File.Delete(snapshot);
        op.AbandonAsIfCrashed();

        var journal = SimulatedDeath.NewJournal(JournalDir, new LinuxDeploymentRecordStore(Ledger)).Journal;
        var told = new List<JournalRecoveryEntry>();
        journal.CopiesGoneInSession += told.Add;
        journal.WaitOnKeptEntries(); // as after a start that could not read the ledger

        Assert.Null(await SameGameChangeAsync(journal)); // an unknown step holds nothing

        var entry = Assert.Single(told);
        Assert.Equal(JournalRecoveryOutcome.UnknownStep, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        Assert.Equal([Payload], LinuxDeploymentRecovery.RecordSettled(entry, P("recovery.log")).CopiesGone);
        Assert.Null(await SameGameChangeAsync(journal));
        Assert.Single(told);
        var (_, _, later) = await RestartAsync();
        Assert.Empty(LinuxDeploymentRecovery.Record(later, P("recovery.log")).CopiesGone);
        Assert.Single(JournalEntries());
    }
}
