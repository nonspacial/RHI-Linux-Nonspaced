using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// Wine registry safety through a real deployment service (UAL: proxy DLL + per-game override).
// #24: install/remove refused while Wine owns the prefix, before any change. #30: removal keeps a value the user set.
// #23: the override is journaled before it is written, so a crash before the record is saved is rolled back.
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    private static string UserReg(GameFixture fixture) => File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg"));

    private static WineDllOverrideService WineRunning() => new(new WineRegistryEditor(_ => true));

    [Fact]
    public async Task Install_WhileWineRunsInThePrefix_IsRefusedBeforeAnyChange()
    {
        using var fixture = CreateGame("wine-running-install", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x71), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var userRegBefore = UserReg(fixture);
        var service = new UltimateAsiLoaderDeploymentService(http, records, WineRunning(), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(install.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, install.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Equal(userRegBefore, UserReg(fixture));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "cache"))); // refused before the download
    }

    [Fact]
    public async Task Remove_WhileWineRunsInThePrefix_IsRefusedBeforeTeardown()
    {
        using var fixture = CreateGame("wine-running-remove", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x72), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var install = await new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"))
            .InstallAsync(fixture.Game, "version.dll");
        Assert.True(install.Success, install.Message);
        var proxy = Path.Combine(fixture.Bin, "version.dll");
        var proxyBytes = File.ReadAllBytes(proxy);

        var remove = await new UltimateAsiLoaderDeploymentService(http, records, WineRunning(), Path.Combine(fixture.Root, "cache"))
            .RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, remove.Message);
        Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task Remove_AfterTheUserChangedTheOverride_KeepsTheirValueAndSaysSo()
    {
        using var fixture = CreateGame("user-value", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x73), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        var reg = Path.Combine(fixture.Prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "version", "builtin", "Game.exe"));

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
        Assert.EndsWith(WineDllOverrideService.KeptUserValueNotePrefix + "version = 'builtin'.", remove.Message, StringComparison.Ordinal);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task CrashAfterTheOverride_BeforeTheRecord_IsRolledBackOnNextStart()
    {
        using var fixture = CreateGame("crash", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x74), "9.7.4"));
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        DeploymentOperation? started = null;
        journal.OperationStartedForTesting = operation => started = operation;
        using var processDied = new ManualResetEventSlim();
        using var reachedRecord = new ManualResetEventSlim();
        // The process dies while saving the record: the override and proxy are in place, the record is not.
        records.SaveFaultForTesting = () =>
        {
            started!.AbandonAsIfCrashed();
            reachedRecord.Set();
            processDied.Wait(TimeSpan.FromMinutes(2));
            return new IOException("process gone");
        };
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);

        var install = Task.Run(() => service.InstallAsync(fixture.Game, "version.dll"));
        try
        {
            Assert.True(reachedRecord.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));

            // Next start: a fresh process reads the ledger (no UAL record) and recovers the journal.
            var report = await LinuxDeploymentRecovery.RecoverAsync(
                LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, isPrefixInUse: _ => false),
                new LinuxDeploymentRecordStore(ledger));

            Assert.Equal(1, report!.RolledBack);
            Assert.Null(WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
            Assert.Empty(Directory.GetFiles(journalDir));
        }
        finally
        {
            processDied.Set();
            records.SaveFaultForTesting = null;
            await install.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task JournaledInstall_RecordCarriesTheOperationId_AndLeavesNoEntry()
    {
        using var fixture = CreateGame("journaled", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x75), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var journalDir = Path.Combine(fixture.Root, "journal");
        var overrides = new WineDllOverrideService();
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir);
        string? operationId = null;
        journal.OperationStartedForTesting = operation => operationId = operation.Id;
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(install.Success, install.Message);
        var record = records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!;
        Assert.Equal(operationId, record.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey]);
        Assert.Equal(WineDllOverrideService.RhiValue, record.WineOverride!.WrittenValue);
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    // w2 wine review, major 1 (reviewer's repro): Wine starts after the preflight, before the journaled override write.
    // The edit is refused; the user closes the game and installs again. The next start must not undo that install.
    [Fact]
    public async Task RefusedOverrideWrite_LeavesNoStepThatUndoesTheNextSuccessfulInstall()
    {
        using var fixture = CreateGame("review-stale", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x7a), "9.7.4"));
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var running = false;
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => Volatile.Read(ref running)));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir);
        journal.OperationStartedForTesting = _ => Volatile.Write(ref running, true); // the user launches the game
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);

        var first = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.False(first.Success);
        Assert.Contains(WineRegistryEditor.PrefixInUseRefusalPrefix, first.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(journalDir, "*.json"));

        Volatile.Write(ref running, false); // game closed
        journal.OperationStartedForTesting = null;
        var second = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.True(second.Success, second.Message);

        // Next start.
        var report = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, isPrefixInUse: _ => false),
            new LinuxDeploymentRecordStore(ledger));

        Assert.Equal(0, report!.RolledBack);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
        Assert.True(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.WineOverride!.ChangedByRhi);
    }

    // w2 wine review, major 1 (rarer variant): the override is written, the install fails and its in-process undo is
    // deferred because Wine started; the user installs again (finds RHI's value, so the new record does not own it).
    // The next start keeps the override the new install depends on and hands it the ownership, so Remove still
    // restores the value the override had before either install.
    [Fact]
    public async Task DeferredUndo_ThenAReinstall_KeepsTheOverrideAndTheReinstallTakesItsOwnership()
    {
        using var fixture = CreateGame("deferred-reinstall", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x7b), "9.7.4"));
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var running = false;
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => Volatile.Read(ref running)));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, records: records);
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);
        records.SaveFaultForTesting = () =>
        {
            Volatile.Write(ref running, true); // the game starts after the override was written
            return new IOException("ledger write failed");
        };

        var first = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.False(first.Success);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
        Assert.Single(Directory.GetFiles(journalDir, "*.json")); // the deferred undo step

        records.SaveFaultForTesting = null;
        Volatile.Write(ref running, false);
        var second = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.True(second.Success, second.Message);
        // #220: the kept entry waits, so the reinstall first finishes the deferred undo (Wine has stopped) and then sets the
        // override itself: the new record owns it straight away (before #220 it found RHI's value and the next start
        // handed it the ownership; the end state below is the same).
        Assert.True(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.WineOverride!.ChangedByRhi);

        // Next start.
        var restarted = new LinuxDeploymentRecordStore(ledger);
        var report = await LinuxDeploymentRecovery.RecoverAsync(
            LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, _ => false, restarted),
            restarted);

        Assert.Equal(0, report!.Failed);
        Assert.Empty(Directory.GetFiles(journalDir, "*.json"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
        var owned = restarted.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!.WineOverride!;
        Assert.True(owned.ChangedByRhi);
        Assert.Null(owned.PreviousValue);
        Assert.Equal(WineDllOverrideService.RhiValue, owned.WrittenValue);

        var remove = await new UltimateAsiLoaderDeploymentService(http, restarted, new WineDllOverrideService(new WineRegistryEditor(_ => false)),
            Path.Combine(fixture.Root, "cache")).RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.Null(WineDllOverrideService.TryReadOverride(UserReg(fixture), "version", "Game.exe"));
    }

    [Fact]
    public async Task InstallFailingAfterTheOverride_UndoesItInProcess()
    {
        using var fixture = CreateGame("ledger-fails", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x76), "9.7.4"));
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var userRegBefore = UserReg(fixture);
        var overrides = new WineDllOverrideService();
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"),
            LinuxDeploymentRecovery.CreateJournal(overrides, Path.Combine(fixture.Root, "journal")));

        ComponentOperationResult install;
        using (LedgerWriteBlocker.Block(records, ledger))
            install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(install.Success);
        Assert.Equal(userRegBefore, UserReg(fixture));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "journal")));
    }
}
