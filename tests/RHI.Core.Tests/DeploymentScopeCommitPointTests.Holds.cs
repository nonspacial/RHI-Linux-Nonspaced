using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #220: a game whose undo could not be finished is held — in process (an operation that failed and could not roll back)
// and across a restart (start-up recovery's RollbackFailed) — so its next change is refused before anything changes until
// the undo succeeds; other games carry on. Without the hold a same-game retry commits and the start that finally finishes
// the old undo puts the files back under the retry's record (the w8 re-review's probe B).
public sealed partial class DeploymentScopeCommitPointTests
{
    private DetectedGame OtherGame => new() { Name = "Other", Source = "Steam", SteamAppId = 4712, InstallPath = P("other"), BinaryDirectory = P("other") };

    /// <summary>A restart: a fresh ledger store and journal, then start-up recovery as the app runs it.</summary>
    private async Task<(LinuxDeploymentRecordStore Records, DeploymentJournal Journal, DeploymentStartupReport Startup)> RestartAsync()
    {
        var records = new LinuxDeploymentRecordStore(Ledger);
        var journal = SimulatedDeath.NewJournal(JournalDir, records).Journal;
        return (records, journal, await LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, gate: null));
    }

    /// <summary>The other game's install: its payload placed, its stamped record saved and committed.</summary>
    private async Task InstallOtherGameAsync(LinuxDeploymentRecordStore records, DeploymentJournal journal)
    {
        Directory.CreateDirectory(P("other"));
        var payload = P("other", "dxgi.dll");
        File.WriteAllBytes(payload, Before);
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "other install", OtherGame, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(payload, After);
            records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(OtherGame), GameName = "Other", Component = ManagedComponentKind.ReShade,
                BinaryDirectory = P("other"), Files = [new LinuxDeployedFile { Path = payload, Sha256 = FileHash.Sha256HexOf(After) }]
            }));
            await scope.CommitAsync();
        }
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(OtherGame, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(payload));
    }

    /// <summary>A same-game change that must be refused before it changes anything; returns the refusal.</summary>
    private async Task<string?> RefusedRetryAsync(DeploymentJournal journal)
    {
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "retry", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        if (opened is not null) await opened.RollbackAsync(); // not refused: nothing placed, so nothing to undo
        Assert.Null(opened);
        return refusal;
    }

    // Probe B: undecided → the ledger removed → restart with the game folder read-only → start-up RollbackFailed. The same
    // game's next change is refused with the failed-undo text (the start-up hands the kept entry over; the session's retry
    // of the undo fails again), another game goes ahead; once the folder is writable, a restart finishes the undo, the
    // retry commits and the next start keeps it: record and payload agree.
    [PermissionDenialFact]
    public async Task ProbeB_StartupRollbackFailed_TheGameIsHeld_UntilARestartFinishesTheUndo_ThenTheRetryIsKept()
    {
        await UndecidedInstallAsync();
        File.Delete(Ledger); // reads again (empty): not committed, so the next start rolls the entry back
        PermissionDenial.MakeReadOnly(P("game"));
        try
        {
            var (records, journal, startup) = await RestartAsync();
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, Assert.Single(startup.Recovery!.Entries).Outcome);

            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await RefusedRetryAsync(journal));
            var (removal, removalRefusal) = await DeploymentScope.BeginRemovalAsync(
                journal, records, "removal", Game, ManagedComponentKind.OptiScaler, CancellationToken.None);
            Assert.Null(removal);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, removalRefusal);
            Assert.Equal(After, File.ReadAllBytes(Payload));
            Assert.Single(JournalEntries());

            await InstallOtherGameAsync(records, journal); // another game is not held up
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game"));
        }

        var (records2, journal2, restarted) = await RestartAsync();
        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(restarted.Recovery!.Entries).Outcome);
        Assert.Equal(Before, File.ReadAllBytes(Payload));

        var (retry, _) = await InstallAsync(records2, journal2);

        Assert.Null(retry); // committed
        Assert.Empty(JournalEntries());
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);
        Assert.Empty(report!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }

    // Probe B as the reviewer ran it: after the start-up RollbackFailed the folder is writable again and the user retries in
    // the same session. The handed-over entry is settled first (its undo now succeeds, so the hold lifts without a
    // restart); the retry commits and the next start keeps it (it used to roll the old entry back over the retry).
    [PermissionDenialFact]
    public async Task ProbeB_StartupRollbackFailed_ThenTheFolderIsWritable_TheUndoIsFinishedBeforeTheRetry_AndTheNextStartKeepsIt()
    {
        await UndecidedInstallAsync();
        File.Delete(Ledger);
        PermissionDenial.MakeReadOnly(P("game"));
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try
        {
            (records, journal, var startup) = await RestartAsync();
            Assert.Equal(JournalRecoveryOutcome.RollbackFailed, Assert.Single(startup.Recovery!.Entries).Outcome);
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game"));
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

    /// <summary>
    /// An install whose commit write fails before landing (not committed) while the game folder turns read-only, so the
    /// in-process rollback fails: the entry is kept for the next start.
    /// </summary>
    private async Task<(LinuxDeploymentRecordStore Records, DeploymentJournal Journal)> InstallWhoseUndoFailsAsync()
    {
        var (records, journal) = Rig();
        records.SaveFaultForTesting = () =>
        {
            PermissionDenial.MakeReadOnly(P("game"));
            return new IOException("Simulated write failure: deployments.json");
        };
        var (rollback, _) = await InstallAsync(records, journal);
        records.SaveFaultForTesting = null;
        Assert.Equal(CommitDecision.NotCommitted, rollback!.Decision);
        Assert.False(rollback.Success);
        Assert.Equal(After, File.ReadAllBytes(Payload));
        Assert.Single(JournalEntries());
        return (records, journal);
    }

    // In process: a not-committed operation whose rollback failed holds its game — the same game's install and removal are
    // refused before anything changes — while another game's change goes through. Once the folder is writable the hold
    // lifts at the game's next change (the undo is retried first and succeeds), the retry commits and the next start keeps it.
    [PermissionDenialFact]
    public async Task InProcess_TheUndoFails_TheGameIsHeld_AnotherGameGoesThrough_AndTheHoldLiftsOnceTheUndoSucceeds()
    {
        LinuxDeploymentRecordStore records;
        DeploymentJournal journal;
        try
        {
            (records, journal) = await InstallWhoseUndoFailsAsync();

            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, await RefusedRetryAsync(journal));
            var (removal, removalRefusal) = await DeploymentScope.BeginRemovalAsync(
                journal, records, "removal", Game, ManagedComponentKind.OptiScaler, CancellationToken.None);
            Assert.Null(removal);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, removalRefusal);
            Assert.Equal(After, File.ReadAllBytes(Payload));
            Assert.Single(JournalEntries());

            await InstallOtherGameAsync(records, journal);
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game"));
        }

        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry);
        Assert.Empty(JournalEntries());
        Assert.Empty(Snapshots());
        Assert.Empty(journal.WaitingForTesting);
        var report = await SimulatedDeath.RecoverAsync(JournalDir, Ledger);
        Assert.Empty(report!.Entries);
        Assert.NotNull(new LinuxDeploymentRecordStore(Ledger).Find(Game, ManagedComponentKind.ReShade));
        Assert.Equal(After, File.ReadAllBytes(Payload));
    }

    // A plain file transaction (no record probe: no game to tell) whose undo fails keeps its entry for the next start but
    // holds up no game: the hold stays per game, never every game.
    [PermissionDenialFact]
    public async Task PlainFileTransaction_TheUndoFails_HoldsUpNoGame()
    {
        var (records, journal) = Rig();
        var operation = await journal.BeginAsync("plain file transaction");
        await operation.TrackFileAsync(Payload);
        await operation.RecordWriteAsync(Payload, FileHash.Sha256HexOf(After));
        File.WriteAllBytes(Payload, After);
        OwnedFileSetRollback rollback;
        PermissionDenial.MakeReadOnly(P("game"));
        try
        {
            rollback = await operation.RollbackAsync();
            Assert.False(rollback.Success);
            Assert.Single(JournalEntries());

            // Its undo still fails (the folder is still read-only): another game's change goes ahead all the same.
            await InstallOtherGameAsync(records, journal);
        }
        finally
        {
            PermissionDenial.MakeWritable(P("game"));
        }

        var (retry, _) = await InstallAsync(records, journal);

        Assert.Null(retry);
        Assert.Empty(journal.WaitingForTesting);
    }

    // The start-up hand-over, entry by entry: a failed undo that names a game holds it; a plain file transaction's holds up
    // no game; one whose entry cannot be read now holds up every game (fail closed); an entry that did not fail is not held.
    [Fact]
    public void TheStartupHandOver_HoldsEachFailedUndoByItsOwnProbe()
    {
        var (_, journal) = Rig();
        Directory.CreateDirectory(JournalDir);
        void Entry(string id, string? probe) => File.WriteAllText(Path.Combine(JournalDir, id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { Format = 1, Id = id, Description = id, CommitProbe = probe }));
        Entry("game", LinuxDeploymentRecovery.RecordCommitProbe(Game, ManagedComponentKind.ReShade));
        Entry("plain", null);
        Entry("deferred", LinuxDeploymentRecovery.RecordCommitProbe(OtherGame, ManagedComponentKind.ReShade));
        File.WriteAllText(Path.Combine(JournalDir, "garbled.json"), "{ not json");
        JournalRecoveryEntry Failed(string id) => new(id, id, JournalRecoveryOutcome.RollbackFailed, ["could not undo"], []);

        journal.HoldFailedUndos(new JournalRecoveryReport([
            Failed("game"), Failed("plain"), Failed("garbled"), Failed("gone"),
            new("deferred", "deferred", JournalRecoveryOutcome.Deferred, ["Wine is running"], [])]));

        Assert.Equal(["game", "garbled"], journal.WaitingForTesting.Order(StringComparer.Ordinal));
    }

    // The text: it says plainly to restart RHI to finish the undo (and what to check first), keeps "Nothing was changed",
    // and no longer promises an in-session retry; PT-BR alike.
    [Fact]
    public void TheFailedUndoRefusal_SaysPlainlyToRestartRHI()
    {
        var en = DeploymentJournal.EarlierUndoFailedMessage;
        var pt = UiLocalizationService.PresentFor("pt-BR", en);

        Assert.StartsWith("RHI could not finish undoing an earlier change to this game", en, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed. Restart RHI to finish it", en, StringComparison.Ordinal);
        Assert.Contains("rhi.log names the file that failed", en, StringComparison.Ordinal);
        Assert.DoesNotContain("RHI tries the undo again first", en, StringComparison.Ordinal);
        Assert.Contains("Nada foi alterado. Reinicie o RHI para concluir", pt, StringComparison.Ordinal);
        Assert.Contains("o rhi.log indica o arquivo que falhou", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart RHI", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("o RHI tenta desfazer a alteração primeiro", pt, StringComparison.Ordinal);
    }
}
