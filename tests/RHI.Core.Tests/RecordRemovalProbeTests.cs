using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #23 (w3/journal): the "record removed" commit probe. A journaled removal stamps its id on the record it takes away;
/// start-up recovery rolls the removal back while a record still carries the stamp and keeps it once none does.
/// </summary>
public sealed class RecordRemovalProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-removal-probe-tests-" + Guid.NewGuid().ToString("N"));

    public RecordRemovalProbeTests() => Directory.CreateDirectory(Path.Combine(_root, "game"));

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private DetectedGame Game => new() { Name = "Example", Source = "Steam", SteamAppId = 4711, InstallPath = P("game"), BinaryDirectory = P("game") };

    private LinuxDeploymentRecordStore InstalledReShade(string gameKey = "steam:4711")
    {
        File.WriteAllText(P("game", "dxgi.dll"), "reshade payload");
        var records = new LinuxDeploymentRecordStore(P("deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = gameKey, GameName = "Example", Component = ManagedComponentKind.ReShade, BinaryDirectory = P("game"),
            Files = [new LinuxDeployedFile { Path = P("game", "dxgi.dll") }]
        });
        return records;
    }

    private async Task<DeploymentOperation> RemoveFileThenCrashAsync(DeploymentJournal journal, LinuxDeploymentRecordStore records, Action? beforeCrash = null)
    {
        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await operation!.TrackFileAsync(P("game", "dxgi.dll"));
        File.Delete(P("game", "dxgi.dll"));
        beforeCrash?.Invoke();
        operation.AbandonAsIfCrashed();
        return operation;
    }

    [Fact]
    public async Task CrashBeforeTheRecordWasRemoved_RollsTheRemovalBack()
    {
        var records = InstalledReShade();
        await RemoveFileThenCrashAsync(new DeploymentJournal(P("journal")), records);

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal("reshade payload", File.ReadAllText(P("game", "dxgi.dll")));
        Assert.NotNull(records.Find(Game, ManagedComponentKind.ReShade));
        Assert.Empty(Directory.GetFiles(P("journal")));
    }

    [Fact]
    public async Task CrashAfterTheRecordWasRemoved_KeepsTheRemoval()
    {
        var records = InstalledReShade();
        await RemoveFileThenCrashAsync(new DeploymentJournal(P("journal")), records,
            beforeCrash: () => records.Remove(Game, ManagedComponentKind.ReShade));

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(P("game", "dxgi.dll")));
        Assert.DoesNotContain(Directory.GetFiles(P("game")), path => path.Contains(".rhi-", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(P("journal")));
    }

    [Fact]
    public async Task NarrowedRecordSavedThroughEndRemoval_IsTheCommitPoint()
    {
        var records = InstalledReShade();
        await RemoveFileThenCrashAsync(new DeploymentJournal(P("journal")), records, beforeCrash: () =>
        {
            var narrowed = records.Find(Game, ManagedComponentKind.ReShade)!;
            narrowed.Files.Clear();
            records.Upsert(LinuxDeploymentRecovery.EndRemoval(narrowed));
        });

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(P("game", "dxgi.dll")));
    }

    [Fact]
    public async Task RecordReKeyedAfterTheCrash_StillRollsTheRemovalBack()
    {
        // An older build keyed Heroic records by install path; the scan re-keys them (#38) before or after recovery.
        var heroic = new DetectedGame { Name = "Hades", Source = "Heroic", HeroicAppName = "Min", InstallPath = P("game"), BinaryDirectory = P("game") };
        var records = InstalledReShade(gameKey: P("game"));
        var journal = new DeploymentJournal(P("journal"));
        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            journal, records, "ReShade removal: Hades", heroic, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        await operation!.TrackFileAsync(P("game", "dxgi.dll"));
        File.Delete(P("game", "dxgi.dll"));
        operation.AbandonAsIfCrashed();
        Assert.Equal(1, records.MigrateGameKeys([heroic]));

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal("reshade payload", File.ReadAllText(P("game", "dxgi.dll")));
    }

    [Fact]
    public async Task StampingTheRemoval_KeepsTheRecordsLastSaveTime()
    {
        // The Wine recovery's "later record" check reads SavedAtUtc: a removal installs nothing.
        var records = InstalledReShade();
        var savedBefore = records.Find(Game, ManagedComponentKind.ReShade)!.SavedAtUtc;
        await Task.Delay(20);

        var (operation, _) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            new DeploymentJournal(P("journal")), records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);

        var stamped = records.Find(Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(operation!.Id, stamped.Metadata[LinuxDeploymentRecovery.RemovalOperationIdMetadataKey]);
        Assert.Equal(savedBefore, stamped.SavedAtUtc);
        Assert.Equal(LinuxDeploymentRecovery.RecordRemovalProbe(Game, ManagedComponentKind.ReShade), operation.CommitProbe);
        Assert.Equal("steam:4711", LinuxDeploymentRecovery.GameKeyOfCommitProbe(operation.CommitProbe));
        await operation.RollbackAsync();
    }

    [Fact]
    public async Task UnreadableLedger_RefusesBeforeAnythingChanges()
    {
        var records = InstalledReShade();
        File.WriteAllText(P("deployments.json"), "{ not json");
        var journal = new DeploymentJournal(P("journal"));

        var (operation, refusal) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);

        Assert.Null(operation);
        Assert.StartsWith(LinuxDeploymentRecovery.RemovalUnmarkedPrefix, refusal, StringComparison.Ordinal);
        Assert.EndsWith("); nothing was changed.", refusal, StringComparison.Ordinal);
        Assert.Empty(journal.ReferencedOperationIds());
        Assert.Equal("reshade payload", File.ReadAllText(P("game", "dxgi.dll")));
    }

    [Fact]
    public async Task StaleStampOfARolledBackRemoval_DoesNotHoldBackTheNextOne()
    {
        var records = InstalledReShade();
        var journal = new DeploymentJournal(P("journal"));
        var (first, _) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        await first!.RollbackAsync(); // e.g. a refused step: the stamp stays on the record, inert
        await RemoveFileThenCrashAsync(journal, records, beforeCrash: () => records.Remove(Game, ManagedComponentKind.ReShade));

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(P("game", "dxgi.dll")));
    }

    [Fact]
    public async Task RollbackRemoval_Unjournaled_PutsAVanishedRecordBackWithoutAStamp_AndLeavesAPresentOneAlone()
    {
        var records = InstalledReShade();
        var record = records.Find(Game, ManagedComponentKind.ReShade)!;
        record.Metadata[LinuxDeploymentRecovery.RemovalOperationIdMetadataKey] = "dead-op";
        var present = records.Find(Game, ManagedComponentKind.ReShade)!.SavedAtUtc;

        await using (var scope = DeploymentScope.Unjournaled())
            Assert.True((await scope.RollbackRemovalAsync(records, Game, record, "test")).Success);
        Assert.Equal(present, records.Find(Game, ManagedComponentKind.ReShade)!.SavedAtUtc); // untouched

        records.Remove(Game, ManagedComponentKind.ReShade);
        await using (var scope = DeploymentScope.Unjournaled())
            await scope.RollbackRemovalAsync(records, Game, record, "test");

        var back = records.Find(Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(P("game", "dxgi.dll"), Assert.Single(back.Files).Path);
        Assert.False(back.Metadata.ContainsKey(LinuxDeploymentRecovery.RemovalOperationIdMetadataKey));
    }

    // Review w3-journal #2: the record of a failed removal goes back STAMPED and BEFORE the rollback. Here the save that
    // took it away replaced the ledger and then failed, and the rollback cannot finish (the folder is read-only): the
    // journal entry stays for the next start, which must put the file back, not count the removal committed and
    // release the copy the user was told was kept.
    [Fact]
    public async Task RollbackRemoval_RecordGoneAndRollbackUnfinished_NextStartStillPutsTheFileBack()
    {
        var records = InstalledReShade();
        var record = records.Find(Game, ManagedComponentKind.ReShade)!;
        var (opened, refusal) = await DeploymentScope.BeginRemovalAsync(
            new DeploymentJournal(P("journal")), records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        Assert.Null(refusal);
        var scope = opened!;
        await scope.DeleteAsync(P("game", "dxgi.dll"));
        records.Remove(Game, ManagedComponentKind.ReShade); // written, then the save "failed"
        File.SetUnixFileMode(P("game"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        OwnedFileSetRollback rollback;
        try { rollback = await scope.RollbackRemovalAsync(records, Game, record, "test"); }
        finally { File.SetUnixFileMode(P("game"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.False(rollback.Success);
        Assert.False(File.Exists(P("game", "dxgi.dll")));
        var back = records.Find(Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(scope.Operation!.Id, back.Metadata[LinuxDeploymentRecovery.RemovalOperationIdMetadataKey]);
        scope.Operation.AbandonAsIfCrashed();

        var report = await LinuxDeploymentRecovery.RecoverAsync(new DeploymentJournal(P("journal")), records);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal("reshade payload", File.ReadAllText(P("game", "dxgi.dll")));
        Assert.NotNull(records.Find(Game, ManagedComponentKind.ReShade));
    }

    // Review w3-journal #2: a crash between the rollback and putting the record back used to leave the restored file
    // with no record. Put back first, a crash there leaves the removal as it was committed: file and record both gone.
    [Fact]
    public async Task RollbackRemoval_CrashWhilePuttingTheRecordBack_NeverLeavesARestoredFileWithoutItsRecord()
    {
        var records = InstalledReShade();
        var record = records.Find(Game, ManagedComponentKind.ReShade)!;
        var journal = new DeploymentJournal(P("journal"));
        using var death = new SimulatedDeath(journal);
        var (opened, _) = await DeploymentScope.BeginRemovalAsync(
            journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        await opened!.DeleteAsync(P("game", "dxgi.dll"));
        records.Remove(Game, ManagedComponentKind.ReShade); // written, then the save "failed"
        records.SaveFaultForTesting = death.Die;            // the next ledger write dies

        var (died, _) = await death.RunUntilDeathAsync(() => opened.RollbackRemovalAsync(records, Game, record, "test"));
        Assert.True(died);
        await SimulatedDeath.RecoverAsync(P("journal"), P("deployments.json"));

        var after = new LinuxDeploymentRecordStore(P("deployments.json")).Find(Game, ManagedComponentKind.ReShade);
        Assert.Equal(File.Exists(P("game", "dxgi.dll")), after is not null);
    }

    private string Prefix()
    {
        var prefix = P("pfx");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return prefix;
    }

    private static string? Override(string prefix) => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe");

    [Fact]
    public async Task RolledBackRemoval_PutsTheRestoredOverrideBack_AndCommittedOneLeavesItRestored()
    {
        foreach (var committed in new[] { false, true })
        {
            if (Directory.Exists(P("pfx"))) Directory.Delete(P("pfx"), recursive: true);
            if (Directory.Exists(P("journal"))) Directory.Delete(P("journal"), recursive: true);
            var prefix = Prefix();
            var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
            var owned = overrides.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
            var records = InstalledReShade();
            var journal = LinuxDeploymentRecovery.CreateJournal(overrides, P("journal"), _ => false, records);
            var (operation, _) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
                journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
            Assert.Equal(WineOverrideRestoreOutcome.Restored, (await overrides.RestoreAsync(operation, prefix, owned)).Outcome);
            Assert.Null(Override(prefix));
            if (committed) records.Remove(Game, ManagedComponentKind.ReShade);
            operation!.AbandonAsIfCrashed();

            var report = await LinuxDeploymentRecovery.RecoverAsync(
                LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), P("journal"), _ => false, records), records);

            Assert.Equal(committed ? JournalRecoveryOutcome.AlreadyCommitted : JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(committed ? null : "native,builtin", Override(prefix));
        }
    }

    [Fact]
    public async Task RolledBackRemoval_KeepsAValueSetAfterTheCrash_AndWaitsWhileWineRuns()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var owned = overrides.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        var records = InstalledReShade();
        var (operation, _) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            LinuxDeploymentRecovery.CreateJournal(overrides, P("journal"), _ => false, records), records,
            "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);
        await overrides.RestoreAsync(operation, prefix, owned);
        operation!.AbandonAsIfCrashed();

        var running = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(overrides, P("journal"), _ => true, records), records);
        Assert.Equal(JournalRecoveryOutcome.Deferred, Assert.Single(running!.Entries).Outcome);
        Assert.StartsWith(WineOverrideReapplyCompensator.DeferredPrefix, Assert.Single(running.Entries[0].Problems), StringComparison.Ordinal);
        Assert.Null(Override(prefix));

        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "dxgi", "builtin", "Game.exe"));
        var later = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(overrides, P("journal"), _ => false, records), records);

        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, Assert.Single(later!.Entries).Outcome);
        Assert.Equal("builtin", Override(prefix));
    }

    [Fact]
    public async Task RestoreAsync_JournalsOnlyAnOverrideRhiStillHolds()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var owned = overrides.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "dxgi", "native", "Game.exe"));
        var records = InstalledReShade();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, P("journal"), _ => false, records);
        var (operation, _) = await LinuxDeploymentRecovery.BeginRecordRemovalAsync(
            journal, records, "ReShade removal: Example", Game, ManagedComponentKind.ReShade, CancellationToken.None);

        Assert.Equal(WineOverrideRestoreOutcome.KeptLaterValue, (await overrides.RestoreAsync(operation, prefix, owned)).Outcome);
        Assert.DoesNotContain(WineOverrideReapplyCompensator.KindName, File.ReadAllText(Path.Combine(P("journal"), operation!.Id + ".json")));
        await operation.RollbackAsync();
        Assert.Equal("native", Override(prefix));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
