using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w10 review finding 1 (coordinator decision): never hold a game for a failure no retry can fix. RHI's copy of the file's
// earlier content gone (the user deleted RHI's copies, or a store reinstall wiped the folder) → the entry is closed and the
// user told which file (start-up / status notice); a step this build has no handler for (a newer build's entry) → the entry
// stays as it is for that build, logged, and holds nothing. A failure a retry can fix (permission) still holds the game.
// R1, R1c, R2 are the reviewer's probes (reviews/w10-review-journal-holds-probes.cs.txt), adopted with the end state asserted.
public sealed partial class DeploymentScopeCommitPointTests
{
    /// <summary>A same-game change after the restarts; returns its refusal (null: it began, and was undone again at once).</summary>
    private async Task<string?> SameGameChangeAsync(DeploymentJournal journal)
    {
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "retry", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        if (opened is not null) await opened.RollbackAsync();
        return refusal;
    }

    // R1: the undo can never succeed — RHI's copies were deleted. The user follows the message to the letter (folder
    // writable, restart RHI): the first start closes the entry and names the file; the game is not refused again.
    [PermissionDenialFact]
    public async Task R1_UndoImpossible_CopiesDeleted_FollowingTheMessage_LetsTheUserOut()
    {
        try { await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        foreach (var snapshot in Snapshots()) File.Delete(snapshot);

        var (records, journal, startup) = await RestartAsync();

        var entry = Assert.Single(startup.Recovery!.Entries);
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        Assert.Empty(JournalEntries()); // closed: no later start meets it again
        Assert.Empty(journal.WaitingForTesting);
        Assert.Equal(After, File.ReadAllBytes(Payload)); // left as it is: nothing to put back
        var summary = LinuxDeploymentRecovery.Record(startup, P("recovery.log"));
        Assert.True(summary.NeedsNotice);
        Assert.Equal([Payload], summary.CopiesGone);
        Assert.Equal(0, summary.RollbackFailed); // not "will try again at the next start"
        Assert.Null(await SameGameChangeAsync(journal));

        // The user's reinstall then commits and the next start keeps it (record and payload agree).
        var (retry, _) = await InstallAsync(records, journal);
        Assert.Null(retry);
        var (_, _, again) = await RestartAsync();
        Assert.Empty(again.Recovery!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }

    // R1c: the store reinstalls the game (folder wiped and made again; RHI's copy went with it).
    [PermissionDenialFact]
    public async Task R1c_GameReinstalled_FollowingTheMessage_LetsTheUserOut()
    {
        try { await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        Directory.Delete(P("game"), recursive: true);
        Directory.CreateDirectory(P("game"));

        var (_, journal, startup) = await RestartAsync();

        var entry = Assert.Single(startup.Recovery!.Entries);
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        Assert.Empty(JournalEntries());
        Assert.False(File.Exists(Payload));
        Assert.Null(await SameGameChangeAsync(journal));
        var (_, journal2, second) = await RestartAsync();
        Assert.Empty(second.Recovery!.Entries);
        Assert.Null(await SameGameChangeAsync(journal2));
    }

    // R2: an entry with a step no handler of this build knows (a newer build crashed, then this one started): it holds
    // nothing at any start, and its entry is left exactly as the newer build wrote it.
    [Fact]
    public async Task R2_UnknownStep_HoldsNothing_AndTheEntryStaysAsTheNewerBuildWroteIt()
    {
        Rig();
        Directory.CreateDirectory(JournalDir);
        var entryPath = Path.Combine(JournalDir, "future.json");
        File.WriteAllText(entryPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Format = 1, Id = "future", Description = "future step", CommitProbe = LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade),
            Files = Array.Empty<object>(), Compensations = new[] { new { Kind = "a-step-from-a-newer-build", Data = "{}" } }
        }));
        var written = File.ReadAllBytes(entryPath);

        for (var restart = 0; restart < 2; restart++)
        {
            var (_, journal, startup) = await RestartAsync();
            var entry = Assert.Single(startup.Recovery!.Entries);
            Assert.Equal(JournalRecoveryOutcome.UnknownStep, entry.Outcome);
            Assert.Empty(journal.WaitingForTesting);
            Assert.Null(await SameGameChangeAsync(journal));
            Assert.Equal(written, File.ReadAllBytes(entryPath));
            Assert.False(LinuxDeploymentRecovery.Record(startup, P("recovery.log")).NeedsNotice); // logged, no notice
        }
    }

    // A failure a retry can fix is still held: the game folder refuses this process even a look (mode 0000), so RHI cannot
    // tell whether its copy is there — never taken for "gone". Held with the restart text; once the folder opens again a
    // restart finishes the undo from the copy that was there all along.
    [PermissionDenialFact]
    public async Task AFolderThatCannotBeRead_IsNotTakenForAGoneCopy_TheGameStaysHeld_UntilARestartFinishesTheUndo()
    {
        try { await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        Assert.Single(Snapshots());
        File.SetUnixFileMode(P("game"), UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(P("game"))); // precondition
            var (_, journal, startup) = await RestartAsync();
            var entry = Assert.Single(startup.Recovery!.Entries);
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
            Assert.Empty(entry.CopiesGone);
            Assert.Contains("because a folder cannot be read", Assert.Single(entry.Problems), StringComparison.Ordinal);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await SameGameChangeAsync(journal));
            Assert.Single(JournalEntries());
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game"));
        }

        var (_, journal2, restarted) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(restarted.Recovery!.Entries).Outcome);
        Assert.Equal(Before, File.ReadAllBytes(Payload));
        Assert.Null(await SameGameChangeAsync(journal2));
    }

    // In process: the operation's own undo finds its copy gone (deleted while it ran). No retry could do better, so the
    // entry is closed at once, the session is told which file, and the game is not held.
    [Fact]
    public async Task InProcess_TheCopyIsGone_TheEntryIsClosed_TheSessionIsTold_AndTheGameIsNotHeld()
    {
        var (records, journal) = Rig();
        var told = new List<JournalRecoveryEntry>();
        journal.CopiesGoneInSession += told.Add;
        records.SaveFaultForTesting = () =>
        {
            foreach (var snapshot in Snapshots()) File.Delete(snapshot);
            return new IOException("Simulated write failure: deployments.json");
        };
        var (rollback, _) = await InstallAsync(records, journal);
        records.SaveFaultForTesting = null;

        Assert.False(rollback!.Success);
        Assert.Contains("no surviving copy", Assert.Single(rollback.Failures), StringComparison.Ordinal);
        Assert.Empty(JournalEntries());
        Assert.Empty(journal.WaitingForTesting);
        var entry = Assert.Single(told);
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        Assert.Null(await SameGameChangeAsync(journal));
        var summary = LinuxDeploymentRecovery.RecordSettled(entry, P("recovery.log"));
        Assert.Equal([Payload], summary.CopiesGone);
        Assert.True(summary.NeedsNotice);
        Assert.Contains(Payload, File.ReadAllText(P("recovery.log")), StringComparison.Ordinal);
    }

    // In session: the game is held (folder read-only); meanwhile the user deletes RHI's copies and makes the folder
    // writable. The game's next change settles the entry first: closed, the session is told, and the change goes ahead.
    [PermissionDenialFact]
    public async Task Held_ThenTheCopyIsGone_TheNextChangeClosesTheEntry_TellsTheSession_AndGoesAhead()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        Assert.Single(journal.WaitingForTesting);
        var told = new List<JournalRecoveryEntry>();
        journal.CopiesGoneInSession += told.Add;
        foreach (var snapshot in Snapshots()) File.Delete(snapshot);

        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry);
        Assert.Equal([Payload], Assert.Single(told).CopiesGone);
        Assert.Empty(journal.WaitingForTesting);
        Assert.Empty(JournalEntries());
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
    }

    // A copy that is gone with a step deferred (Wine running): the files are final, so the entry is narrowed to the
    // deferred step alone — the next start runs that step and does not name the file again.
    [Fact]
    public async Task ACopyGoneBesideADeferredStep_KeepsOnlyTheStep_AndTheFileIsNamedOnce()
    {
        Rig();
        var deferring = new SwitchableCompensator { Status = CompensationStatus.Deferred };
        var journal = new DeploymentJournal(JournalDir, [deferring]);
        var operation = await journal.BeginAsync("deferred and gone", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await operation.TrackFileAsync(Payload);
        await operation.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        await operation.AddCompensationAsync(SwitchableCompensator.KindName, "{}");
        File.WriteAllBytes(Payload, After);
        foreach (var snapshot in Snapshots()) File.Delete(snapshot);
        operation.AbandonAsIfCrashed();

        var first = await journal.RecoverAsync();

        var entry = Assert.Single(first.Entries);
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, entry.Outcome);
        Assert.Equal([Payload], entry.CopiesGone);
        Assert.Single(JournalEntries());
        deferring.Status = CompensationStatus.Done;
        var second = await journal.RecoverAsync();
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(second.Entries).Outcome);
        Assert.Empty(second.Entries[0].CopiesGone);
        Assert.Empty(JournalEntries());
    }

    // One path's copy is gone while the other path's restore fails for a reason that can be fixed (the folder is read-only):
    // that is a retryable failure — held, entry kept — never taken for "only copies gone" (which would close the entry with
    // the backup still missing). Once the folder is writable the next start puts the backup back and closes the entry.
    [PermissionDenialFact]
    public async Task ACopyGoneBesideARestoreThatCanStillBeFinished_IsRetried_NotClosed()
    {
        Rig();
        var backup = Payload + OwnedFileSet.BackupExtension;
        var backupBefore = "the earlier .original"u8.ToArray();
        File.WriteAllBytes(backup, backupBefore);
        var journal = new DeploymentJournal(JournalDir, []);
        var operation = await journal.BeginAsync("gone and blocked", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        await operation.TrackFileAsync(Payload);
        await operation.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(Payload, After);
        File.Delete(backup);
        var payloadCopy = Payload + OwnedFileSet.SnapshotMarker + operation.Id;
        Assert.True(File.Exists(payloadCopy) && File.Exists(backup + OwnedFileSet.SnapshotMarker + operation.Id));
        File.Delete(payloadCopy); // the payload's earlier content survives nowhere
        operation.AbandonAsIfCrashed();

        JournalRecoveryReport first;
        PermissionDenial.MakeReadOnly(P("game"));
        try { first = await journal.RecoverAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }

        var entry = Assert.Single(first.Entries);
        Assert.Equal(JournalRecoveryOutcome.RollbackFailed, entry.Outcome);
        Assert.Empty(entry.CopiesGone);
        Assert.Single(JournalEntries());
        var second = await journal.RecoverAsync();
        Assert.Equal(JournalRecoveryOutcome.CopiesGone, Assert.Single(second.Entries).Outcome);
        Assert.Equal([Payload], second.Entries[0].CopiesGone);
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));
        Assert.Empty(JournalEntries());
    }

    private sealed class SwitchableCompensator : IDeploymentCompensator
    {
        public const string KindName = "test-switchable";
        public CompensationStatus Status { get; set; }
        public string Kind => KindName;
        public CompensationResult Compensate(string data) => new(Status);
    }

    // ── w10 review finding 2: the refusal gives the true reason ──────────────────────────────────────────────────────

    // A held game's retry finishes the undo but cannot remove the entry (RHI's journal folder turned read-only): refused
    // with the text about RHI's own note of the change — not "the deployment ledger cannot be read" (the ledger is fine).
    [PermissionDenialFact]
    public async Task Held_TheRetryCannotUpdateTheEntry_IsRefusedForThatReason_NotTheLedger()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        PermissionDenial.MakeReadOnly(JournalDir);
        try
        {
            Assert.Equal(DeploymentJournal.EarlierChangeNotSettledMessage, await RefusedRetryAsync(journal));
            Assert.Equal(Before, File.ReadAllBytes(Payload)); // the undo itself went through
        }
        finally
        {
            PermissionDenial.MakeWritable(JournalDir);
        }

        var (retry, _) = await InstallAsync(records, journal);
        Assert.Null(retry);
        Assert.Empty(journal.WaitingForTesting);
        Assert.Empty(JournalEntries());
    }

    // A held game's retry cannot even open RHI's own lock file of the entry (its mode refuses this process): the same true
    // reason, not the ledger and not "another process"; once the file opens again the retry finishes the undo and goes ahead.
    [PermissionDenialFact]
    public async Task Held_TheRetryCannotOpenTheEntrysLock_IsRefusedForThatReason_NotTheLedger()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        var lockFile = Assert.Single(Directory.GetFiles(JournalDir, "*.lock"));
        PermissionDenial.MakeUnreadable(lockFile);
        try
        {
            Assert.Equal(DeploymentJournal.EarlierChangeNotSettledMessage, await RefusedRetryAsync(journal));
            Assert.Equal(After, File.ReadAllBytes(Payload)); // nothing was tried
        }
        finally
        {
            File.SetUnixFileMode(lockFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var (retry, _) = await InstallAsync(records, journal);
        Assert.Null(retry);
        Assert.Empty(journal.WaitingForTesting);
        Assert.Empty(JournalEntries());
    }

    // A game held in this session whose entry then cannot be read at the retry (its mode refuses this process): it holds only
    // that game — known from when it started waiting — with the true reason; another game goes ahead.
    [PermissionDenialFact]
    public async Task Held_TheEntryCannotBeReadAtTheRetry_HoldsOnlyItsGame_WithTheTrueReason()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try { (records, journal) = await InstallWhoseUndoFailsAsync(); }
        finally { PermissionDenial.MakeWritable(P("game")); }
        var entryPath = Assert.Single(JournalEntries());
        PermissionDenial.MakeUnreadable(entryPath);
        try
        {
            Assert.Equal(DeploymentJournal.EarlierChangeNotSettledMessage, await RefusedRetryAsync(journal));
            await InstallOtherGameAsync(records, journal);
        }
        finally
        {
            File.SetUnixFileMode(entryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // An entry that cannot be read when the start hands it over: it holds only its own game (known from recovery), and that
    // game's refusal names the real reason; another game goes ahead.
    [PermissionDenialFact]
    public async Task AnUnreadableEntryAtTheHandOver_HoldsOnlyItsGame_WithTheTrueReason()
    {
        var (_, journal) = Rig();
        Directory.CreateDirectory(JournalDir);
        var probe = LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade);
        var entryPath = Path.Combine(JournalDir, "game.json");
        File.WriteAllText(entryPath, System.Text.Json.JsonSerializer.Serialize(new { Format = 1, Id = "game", Description = "game", CommitProbe = probe }));
        PermissionDenial.MakeUnreadable(entryPath);
        try
        {
            journal.HoldFailedUndos(new JournalRecoveryReport([
                new("game", "game", JournalRecoveryOutcome.RollbackFailed, ["could not undo"], []) { CommitProbe = probe }]));

            var other = await journal.BeginAsync("other game", LinuxDeploymentRecovery.RecordCommitProbe(OtherGame, ManagedComponentKind.ReShade));
            await other.RollbackAsync();
            var refused = await Assert.ThrowsAsync<DeploymentDecisionPendingException>(() => journal.BeginAsync("same game", probe));
            Assert.Equal(DeploymentJournal.EarlierChangeNotSettledMessage, refused.Message);
        }
        finally
        {
            File.SetUnixFileMode(entryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // An entry a deferred start handed over (its game not known then) that cannot be read at the retry: its game is known
    // from nowhere, so it holds up no game (logged) — neither fail-closed for every game nor the ledger's text.
    [PermissionDenialFact]
    public async Task AKeptEntryThatCannotBeReadAtTheRetry_AndWhoseGameIsNotKnown_HoldsUpNoGame()
    {
        var (_, journal) = Rig();
        Directory.CreateDirectory(JournalDir);
        var entryPath = Path.Combine(JournalDir, "kept.json");
        File.WriteAllText(entryPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Format = 1, Id = "kept", Description = "kept", CommitProbe = LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade)
        }));
        journal.WaitOnKeptEntries();
        PermissionDenial.MakeUnreadable(entryPath);
        try
        {
            var other = await journal.BeginAsync("other game", LinuxDeploymentRecovery.RecordCommitProbe(OtherGame, ManagedComponentKind.ReShade));
            await other.RollbackAsync();
            Assert.True(File.Exists(entryPath)); // kept for the next start
        }
        finally
        {
            File.SetUnixFileMode(entryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // Its game known from neither the report nor the entry: it holds up no game at all (and is logged).
    [PermissionDenialFact]
    public async Task AnUnreadableEntryWhoseGameIsNotKnown_HoldsUpNoGame()
    {
        var (_, journal) = Rig();
        Directory.CreateDirectory(JournalDir);
        var entryPath = Path.Combine(JournalDir, "unknown.json");
        File.WriteAllText(entryPath, "{}");
        PermissionDenial.MakeUnreadable(entryPath);
        try
        {
            journal.HoldFailedUndos(new JournalRecoveryReport([new("unknown", "", JournalRecoveryOutcome.RollbackFailed, ["could not undo"], [])]));

            Assert.Empty(journal.WaitingForTesting);
            var change = await journal.BeginAsync("a change", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
            await change.RollbackAsync();
        }
        finally
        {
            File.SetUnixFileMode(entryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void TheNotSettledRefusal_SaysPlainlyToRestartRHI_AndIsNotTheLedgerText()
    {
        var en = DeploymentJournal.EarlierChangeNotSettledMessage;
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", en);

        Assert.Contains("could not read or update its own note of that change. Nothing was changed. Restart RHI to finish it", en, StringComparison.Ordinal);
        Assert.DoesNotContain("ledger", en, StringComparison.Ordinal);
        Assert.Contains("Nada foi alterado. Reinicie o RHI para concluir", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart RHI", pt, StringComparison.Ordinal);
    }
}
