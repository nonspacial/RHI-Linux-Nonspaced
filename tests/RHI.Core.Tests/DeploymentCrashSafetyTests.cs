using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLogCollection
{
    public const string Name = "AppLog.Current";
}

/// <summary>
/// #23 review corrections: crash recovery never loses a file changed after the crash (finding 1), the
/// start-up sweep cannot act on a running operation (finding 2), set-aside copies stay on the same
/// file system and moves are durable (finding 3), plus the minor findings' regressions. The first
/// four tests are the reviewer's probes (reviews/w1-core-probes) turned into the required behaviour.
/// </summary>
[Collection(AppLogCollection.Name)]
public sealed class DeploymentCrashSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-crash-safety-" + Guid.NewGuid().ToString("N"));

    public DeploymentCrashSafetyTests() => Directory.CreateDirectory(Path.Combine(_root, "game"));

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private string[] AllFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories);

    // ---- finding 1: recovery keeps what changed after the crash ------------------------------------

    [Fact]
    public async Task Probe1_UserCreatedReShadeIniAfterCrash_IsKept()
    {
        var ini = P("game", "ReShade.ini");
        var journal = new DeploymentJournal(P("journal"));
        var op = await journal.BeginAsync("ReShade install", "record|ReShade|steam:1");
        await op.TrackFileAsync(ini, default, includeBackup: false); // no declared write, as before the fix
        File.WriteAllText(ini, "written by the install");
        op.AbandonAsIfCrashed();
        File.WriteAllText(ini, "user tuned this in the overlay after the crash");

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Equal("user tuned this in the overlay after the crash", File.ReadAllText(ini));
        Assert.Empty(Directory.GetFiles(P("journal"))); // final, reported once
    }

    [Fact]
    public async Task Probe2_UserEditedOptiScalerIniAfterCrash_IsKept_AndRhisCopyOfTheOldContentSetAside()
    {
        var ini = P("game", "OptiScaler.ini");
        File.WriteAllText(ini, "before");
        var journal = new DeploymentJournal(P("journal"));
        var op = await journal.BeginAsync("ReShade install", "record|ReShade|steam:1");
        await op.TrackFileAsync(ini, default, includeBackup: false);
        File.WriteAllText(ini, "written by the install");
        await op.RecordWrittenContentAsync(ini);
        op.AbandonAsIfCrashed();
        File.WriteAllText(ini, "user edit after crash");

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, Assert.Single(report.Entries).Outcome);
        Assert.Equal("user edit after crash", File.ReadAllText(ini));
        var copy = Assert.Single(Directory.GetFiles(P("game", RecoveredCopies.FolderName), "*", SearchOption.AllDirectories));
        Assert.Equal("OptiScaler.ini" + RecoveredCopies.PreOperationSuffix, Path.GetFileName(copy));
        Assert.Equal("before", File.ReadAllText(copy));
        Assert.DoesNotContain(Directory.GetFiles(P("game")), path => path.Contains(".rhi-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclaredWriteStillInPlace_IsRolledBack()
    {
        var ini = P("game", "OptiScaler.ini");
        File.WriteAllText(ini, "before");
        var journal = new DeploymentJournal(P("journal"));
        var op = await journal.BeginAsync("x");
        await op.TrackFileAsync(ini, default, includeBackup: false);
        File.WriteAllText(ini, "written by the install");
        await op.RecordWrittenContentAsync(ini);
        op.AbandonAsIfCrashed();

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        Assert.Equal(1, report.RolledBack);
        Assert.Equal("before", File.ReadAllText(ini));
        Assert.False(Directory.Exists(P("game", RecoveredCopies.FolderName)));
    }

    [Fact]
    public async Task OriginalCreatedByTheUserAfterTheCrash_IsKept()
    {
        var dll = P("game", "dxgi.dll");
        var journal = new DeploymentJournal(P("journal"));
        var op = await journal.BeginAsync("x");
        await op.TrackFileAsync(dll);
        File.WriteAllText(dll, "rhi proxy");
        await op.RecordWrittenContentAsync(dll);
        op.AbandonAsIfCrashed();
        File.WriteAllText(dll + ".original", "user's own backup");

        var report = await new DeploymentJournal(P("journal")).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, Assert.Single(report.Entries).Outcome);
        Assert.False(File.Exists(dll)); // RHI's file removed
        Assert.Equal("user's own backup", File.ReadAllText(dll + ".original"));
    }

    [Fact]
    public async Task InProcessRollback_StillRestoresTheOperationsOwnUndeclaredWrites()
    {
        var ini = P("game", "ReShade.ini");
        File.WriteAllText(ini, "before");
        var op = await new DeploymentJournal(P("journal")).BeginAsync("x");
        await op.TrackFileAsync(ini, default, includeBackup: false);
        File.WriteAllText(ini, "half-applied profile");

        var rollback = await op.RollbackAsync();

        Assert.True(rollback.Success, string.Join("; ", rollback.Failures));
        Assert.Equal("before", File.ReadAllText(ini));
    }

    // ---- finding 2: the sweep cannot hit a running operation -----------------------------------------

    [Fact]
    public void Probe3_RenamedAsideCopyOfALiveOperation_IsNotSwept_DespiteAnOldModificationTime()
    {
        var target = P("game", "nvngx_dlss.dll");
        File.WriteAllText(target, "previous managed build");
        File.WriteAllText(target + ".original", "game original");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-30));
        var aside = target + OwnedFileSet.SnapshotMarker + Guid.NewGuid().ToString("N");
        File.Move(target, aside); // a live operation moves the old content aside by rename
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(aside) > TimeSpan.FromHours(6));

        var report = DeploymentLeftoverSweeper.Sweep([P("game")], _ => false, TimeSpan.FromHours(6));

        Assert.Empty(report.Quarantined);
        Assert.Empty(report.Deleted);
        Assert.Equal("previous managed build", File.ReadAllText(aside));
    }

    [Fact]
    public void Probe4_FreshCopyThatKeptItsSourcesModificationTime_IsNotSwept()
    {
        var dll = P("game", "dinput8.dll");
        File.WriteAllText(dll, "v1");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddDays(-10));
        var backup = dll + ".rhi-update-backup-" + Guid.NewGuid().ToString("N");
        File.Copy(dll, backup); // legacy REFramework update, still running
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(backup) > TimeSpan.FromHours(6));

        var report = DeploymentLeftoverSweeper.Sweep([P("game")], _ => false, TimeSpan.FromHours(6));

        Assert.Empty(report.Deleted);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void FileChangeTime_IsTheInodeChangeTime_WhichSettingTheModificationTimeCannotMoveBack()
    {
        if (!OperatingSystem.IsLinux()) return; // statx is Linux-only; elsewhere the sweep falls back to mtime/creation time
        var file = P("game", "probe.bin");
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));

        var ctime = FileChangeTime.TryGetUtc(file);

        Assert.NotNull(ctime);
        Assert.InRange(DateTime.UtcNow - ctime!.Value, TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(5));
        Assert.Null(FileChangeTime.TryGetUtc(P("game", "missing.bin")));
    }

    [Fact]
    public async Task Sweep_AsksTheJournalForEveryFile_AndSparesLiveAndUnreadableEntries()
    {
        var journal = new DeploymentJournal(P("journal"));
        await using var live = await journal.BeginAsync("running");
        var unreadableId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(P("journal"), unreadableId + ".json.unreadable"), "{");
        var liveLeftover = P("game", "a.dll" + OwnedFileSet.SnapshotMarker + live.Id);
        var unreadableLeftover = P("game", "b.dll" + OwnedFileSet.SnapshotMarker + unreadableId);
        File.WriteAllText(liveLeftover, "only copy");
        File.WriteAllText(unreadableLeftover, "only copy");
        var asked = new List<string>();

        var report = DeploymentLeftoverSweeper.Sweep([P("game")], id => { asked.Add(id); return journal.IsReferenced(id); },
            TimeSpan.FromHours(6), DateTime.UtcNow.AddDays(1));

        Assert.True(report.IsEmpty);
        Assert.Equal(new[] { live.Id, unreadableId }.Order(), asked.Order());
        Assert.True(File.Exists(liveLeftover));
        Assert.True(File.Exists(unreadableLeftover));
    }

    [Fact]
    public async Task StartupSweep_IsSkippedWhileAnotherInstanceRuns_AndRunsOnceItHasExited()
    {
        var (journal, records, leftover) = RecordedGameWithRenamedAsideLeftover();
        var other = DeploymentInstanceGate.Register(P("instances"));
        using var self = DeploymentInstanceGate.Register(P("instances"));

        var skipped = await LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, self, sweepMinimumAge: TimeSpan.Zero);

        Assert.NotNull(skipped.Sweep!.SkippedReason);
        Assert.Equal("old managed build", File.ReadAllText(leftover));

        other.Dispose();
        var swept = await LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, self, sweepMinimumAge: TimeSpan.Zero);

        Assert.Null(swept.Sweep!.SkippedReason);
        Assert.False(File.Exists(leftover));
        Assert.Equal("old managed build", File.ReadAllText(Assert.Single(swept.Sweep.Quarantined).To));
    }

    [Fact]
    public async Task StartupSweep_WaitsForAnotherInstancesStartup_AndSkipsWhenItDoesNotFinish()
    {
        var (journal, records, leftover) = RecordedGameWithRenamedAsideLeftover();
        using var other = DeploymentInstanceGate.Register(P("instances"));
        using var otherStartup = other.AcquireStartupLock(TimeSpan.Zero);
        Assert.NotNull(otherStartup);
        using var self = DeploymentInstanceGate.Register(P("instances"));

        var report = await LinuxDeploymentRecovery.RunAtStartupAsync(journal, records, self,
            startupLockTimeout: TimeSpan.FromMilliseconds(200), sweepMinimumAge: TimeSpan.Zero);

        Assert.Contains("starting up", report.Sweep!.SkippedReason);
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public void DeadInstanceRegistration_DoesNotCount_AndIsRemoved()
    {
        Directory.CreateDirectory(P("instances"));
        var stale = P("instances", "12345-" + Guid.NewGuid().ToString("N") + ".instance");
        File.WriteAllText(stale, "");
        using var self = DeploymentInstanceGate.Register(P("instances"));

        Assert.False(self.OtherInstanceRunning());
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(self.InstancePath));
    }

    [Fact]
    public async Task Startup_NeverThrows_OnAMalformedRecordedPath()
    {
        var records = new LinuxDeploymentRecordStore(P("records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:9", Component = ManagedComponentKind.ReShade, BinaryDirectory = "bad\0dir",
            Files = [new LinuxDeployedFile { Path = "bad\0dir/dxgi.dll" }]
        });
        using var self = DeploymentInstanceGate.Register(P("instances"));

        var report = await LinuxDeploymentRecovery.RunAtStartupAsync(new DeploymentJournal(P("journal")), records, self);
        var summary = LinuxDeploymentRecovery.Record(report, P("state", "recovery.log"));

        Assert.NotEmpty(report.Sweep!.Problems);
        Assert.Empty(report.Errors);
        Assert.Equal(P("state", "recovery.log"), summary.LogPath);
        Assert.Contains("Problem: Sweep:", File.ReadAllText(P("state", "recovery.log")));
    }

    [Fact]
    public async Task JournalTemporaries_AreSwept()
    {
        var journal = new DeploymentJournal(P("journal"));
        await (await journal.BeginAsync("x")).CommitAsync();
        var temp = AtomicFile.TempPathFor(Path.Combine(P("journal"), Guid.NewGuid().ToString("N") + ".json"));
        File.WriteAllText(temp, "{ half");

        var report = LinuxDeploymentRecovery.Sweep(journal, new LinuxDeploymentRecordStore(P("records.json")), nowUtc: DateTime.UtcNow.AddDays(1));

        Assert.Equal([temp], report.Deleted);
    }

    // ---- finding 3: set-aside copies stay on the same file system; moves are durable -----------------

    [Fact]
    public void MoveDurable_AcrossFileSystems_PlacesAVerifiedCopyBeforeDeletingTheSource()
    {
        File.WriteAllText(P("game", "a.bin"), "only copy");
        AtomicFile.SimulateCrossDeviceForTesting.Value = true;
        try
        {
            AtomicFile.MoveDurable(P("game", "a.bin"), P("elsewhere-a.bin"));
        }
        finally
        {
            AtomicFile.SimulateCrossDeviceForTesting.Value = false;
        }

        Assert.False(File.Exists(P("game", "a.bin")));
        Assert.Equal("only copy", File.ReadAllText(P("elsewhere-a.bin")));
        Assert.DoesNotContain(AllFiles(), path => path.Contains(AtomicFile.TempMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void MoveDurable_AcrossFileSystems_KeepsTheSourceWhenItFailsAfterTheCopy()
    {
        File.WriteAllText(P("game", "b.bin"), "only copy");
        AtomicFile.SimulateCrossDeviceForTesting.Value = true;
        AtomicFile.CopyCompletedForTesting.Value = _ => throw new IOException("power cut");
        try
        {
            Assert.Throws<IOException>(() => AtomicFile.MoveDurable(P("game", "b.bin"), P("elsewhere-b.bin")));
        }
        finally
        {
            AtomicFile.SimulateCrossDeviceForTesting.Value = false;
            AtomicFile.CopyCompletedForTesting.Value = null;
        }

        Assert.Equal("only copy", File.ReadAllText(P("game", "b.bin")));
        Assert.Equal("only copy", File.ReadAllText(P("elsewhere-b.bin")));
    }

    [Fact]
    public void MoveDurable_NeverReplacesAnExistingFile()
    {
        File.WriteAllText(P("game", "c.bin"), "mine");
        File.WriteAllText(P("game", "d.bin"), "theirs");

        Assert.Throws<IOException>(() => AtomicFile.MoveDurable(P("game", "c.bin"), P("game", "d.bin")));
        Assert.Equal("mine", File.ReadAllText(P("game", "c.bin")));
        Assert.Equal("theirs", File.ReadAllText(P("game", "d.bin")));
    }

    [Fact]
    public void RecoveredCopies_StayBesideTheFile_AndNeverOverwriteEachOther()
    {
        var now = DateTime.UtcNow;
        File.WriteAllText(P("game", "x.rhi-rollback-1"), "first");
        File.WriteAllText(P("game", "y.rhi-rollback-2"), "second");

        var first = RecoveredCopies.MoveAside(P("game", "x.rhi-rollback-1"), now, "same-name");
        var second = RecoveredCopies.MoveAside(P("game", "y.rhi-rollback-2"), now, "same-name");

        Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        Assert.StartsWith(P("game", RecoveredCopies.FolderName), first, StringComparison.Ordinal);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    // ---- finding 11: the sweep never touches anything that is not an RHI leftover ------------------

    [Fact]
    public void Sweep_LeavesSymlinksNearMissNamesUserFilesAndNestedFoldersAlone()
    {
        var hex = Guid.NewGuid().ToString("N");
        var untouched = new List<string>();
        string Make(string relative, string content = "user data")
        {
            var path = P(["game", .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            untouched.Add(path);
            return path;
        }
        File.WriteAllText(P("game", "game.dll"), "game");
        Make("game.dll.rhi-tmp-" + hex[..31]);                  // 31 hex digits
        Make("game.dll.rhi-tmp-" + hex + "a");                  // 33 hex digits
        Make("game.dll.rhi-tmp-" + hex.ToUpperInvariant());     // upper-case hex
        Make("game.dll.rhi-" + hex);                            // no tag
        Make("notes.rhi-backup.txt");                           // a user file that merely contains ".rhi-"
        Make("sub/game.dll.rhi-tmp-" + hex);                    // nested: never walked
        var linkTarget = Make("real-target.bin", "linked content");
        var link = P("game", "game.dll.rhi-rollback-" + hex);
        File.CreateSymbolicLink(link, linkTarget);

        var report = DeploymentLeftoverSweeper.Sweep([P("game")], _ => false, TimeSpan.Zero, DateTime.UtcNow.AddDays(1));

        Assert.True(report.IsEmpty, string.Join("; ", report.Deleted.Concat(report.Problems)));
        Assert.All(untouched, path => Assert.True(File.Exists(path), path));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("linked content", File.ReadAllText(linkTarget));
    }

    // ---- finding 5: Wine override undo respects later changes and a running wineserver --------------

    [Fact]
    public async Task OverrideChangedByTheUserAfterTheCrash_IsKept()
    {
        var (prefix, userReg, step) = PrefixWithRhiOverride();
        var overrides = new WineDllOverrideService();
        overrides.EnsureNativeBuiltin(prefix, "dxgi", "Game.exe");
        File.WriteAllText(userReg, File.ReadAllText(userReg).Replace("native,builtin", "builtin", StringComparison.Ordinal));
        var journal = new DeploymentJournal(P("journal"), [new WineOverrideCompensator(overrides, _ => false)]);
        var op = await journal.BeginAsync("x");
        await op.AddCompensationAsync(WineOverrideCompensator.KindName, step);
        op.AbandonAsIfCrashed();

        var report = await journal.RecoverAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task OverrideUndo_IsDeferredWhileWineRunsInThePrefix_ThenDoneAtTheNextStart()
    {
        var (prefix, userReg, step) = PrefixWithRhiOverride();
        var overrides = new WineDllOverrideService();
        var dll = P("game", "dxgi.dll");
        var running = true;
        var journal = new DeploymentJournal(P("journal"), [new WineOverrideCompensator(overrides, _ => running)]);
        var op = await journal.BeginAsync("x");
        await op.TrackFileAsync(dll);
        await op.AddCompensationAsync(WineOverrideCompensator.KindName, step);
        File.WriteAllText(dll, "proxy");
        await op.RecordWrittenContentAsync(dll);
        overrides.EnsureNativeBuiltin(prefix, "dxgi", "Game.exe");
        op.AbandonAsIfCrashed();

        var first = await journal.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.Deferred, Assert.Single(first.Entries).Outcome);
        Assert.Equal(1, first.Deferred);
        Assert.False(File.Exists(dll)); // files are done
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Contains(op.Id, journal.ReferencedOperationIds());

        File.WriteAllText(dll, "the user's own dxgi.dll, placed later"); // must not be mistaken for an RHI file
        running = false;
        var second = await journal.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(second.Entries).Outcome);
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Equal("the user's own dxgi.dll, placed later", File.ReadAllText(dll));
        Assert.Empty(journal.ReferencedOperationIds());
    }

    [Fact]
    public void WineServerProbe_MatchesTheWineprefixOfARunningWineserver()
    {
        var proc = P("proc");
        void Process(int pid, string comm, params string[] environment)
        {
            Directory.CreateDirectory(Path.Combine(proc, pid.ToString()));
            File.WriteAllText(Path.Combine(proc, pid.ToString(), "comm"), comm + "\n");
            File.WriteAllText(Path.Combine(proc, pid.ToString(), "environ"), string.Join('\0', environment) + "\0");
        }
        Directory.CreateDirectory(P("pfx"));
        Process(10, "wineserver", "HOME=/home/x", "WINEPREFIX=" + P("pfx") + "/");
        Process(11, "bash", "WINEPREFIX=" + P("other"));
        Directory.CreateDirectory(Path.Combine(proc, "self"));

        Assert.True(WineServerProbe.IsRunningFor(P("pfx"), proc));
        Assert.False(WineServerProbe.IsRunningFor(P("other"), proc));
        Assert.False(WineServerProbe.IsRunningFor(P("pfx"), P("no-proc")));
    }

    // ---- findings 8 and 14 ------------------------------------------------------------------------------

    [Fact]
    public void RecoveryLog_LivesInTheStateDirectory_RotatesAndReportsProblemsToAppLog()
    {
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME")!, "rhi-linux", "logs", "recovery.log"),
            LinuxDeploymentRecovery.LogPath);
        var log = P("state", "logs", "recovery.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, new string('x', 1024 * 1024 + 1));
        var appLog = new AppLog(P("state", "rhi.log"));
        var previous = AppLog.Current;
        AppLog.Current = appLog;
        try
        {
            var failed = new JournalRecoveryReport([new("id", "ReShade install: Game", JournalRecoveryOutcome.RollbackFailed, ["no surviving copy"], [])]);
            LinuxDeploymentRecovery.Record(new DeploymentStartupReport(failed, 0, null, []), log);
        }
        finally
        {
            AppLog.Current = previous;
        }

        Assert.True(File.Exists(log + ".1"));
        Assert.Contains("RollbackFailed: ReShade install: Game", File.ReadAllText(log));
        Assert.Contains("no surviving copy", File.ReadAllText(appLog.FilePath));
    }

    [Fact]
    public async Task EntryRecoveredByAnotherInstanceMeanwhile_IsNotReportedAsFailed()
    {
        var journal = new DeploymentJournal(P("journal"));
        var op = await journal.BeginAsync("x");
        op.AbandonAsIfCrashed();
        var recovering = new DeploymentJournal(P("journal"))
        {
            BeforeEntryLockForTesting = id => File.Delete(Path.Combine(P("journal"), id + ".json")) // the other instance finished it
        };

        var report = await recovering.RecoverAsync();

        Assert.Empty(report.Entries);
        Assert.Empty(Directory.GetFiles(P("journal")));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private (DeploymentJournal Journal, LinuxDeploymentRecordStore Records, string Leftover) RecordedGameWithRenamedAsideLeftover()
    {
        var dll = P("game", "nvngx_dlss.dll");
        File.WriteAllText(dll, "old managed build");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddDays(-30));
        var leftover = dll + OwnedFileSet.SnapshotMarker + Guid.NewGuid().ToString("N");
        File.Move(dll, leftover);
        var records = new LinuxDeploymentRecordStore(P("records.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:1", Component = ManagedComponentKind.ReShade, BinaryDirectory = P("game"),
            Files = [new LinuxDeployedFile { Path = dll }]
        });
        return (new DeploymentJournal(P("journal")), records, leftover);
    }

    private (string Prefix, string UserReg, string Step) PrefixWithRhiOverride()
    {
        var prefix = P("pfx");
        Directory.CreateDirectory(prefix);
        var userReg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(userReg, "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var step = WineOverrideCompensator.Describe(prefix, new WineDllOverrideService().PlanNativeBuiltin(prefix, "dxgi", "Game.exe"));
        return (prefix, userReg, step);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
