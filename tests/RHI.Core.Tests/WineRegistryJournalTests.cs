using RHI.Core.Deployment;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Journal undo of a Wine override (#23 registry remainder) under the editor's rules (#24, #30), and the PT-BR
/// presentation of every message the registry work added.
/// </summary>
public sealed class WineRegistryJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-wine-journal-" + Guid.NewGuid().ToString("N"));

    public WineRegistryJournalTests() => Directory.CreateDirectory(_root);

    private string Prefix()
    {
        var prefix = Path.Combine(_root, "pfx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return prefix;
    }

    [Fact]
    public async Task InterruptedOperation_WhileWineRunsInThePrefix_IsDeferredAndUndoneAtTheNextStart()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("UAL install: Game");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "winmm.dll", "Game.exe");
        operation.AbandonAsIfCrashed();

        // Next start, the game is running: the editor sees wineserver even where the compensator's own check did not.
        var running = new WineDllOverrideService(new WineRegistryEditor(_ => true));
        var deferred = await new DeploymentJournal(journalDir, [new WineOverrideCompensator(running, _ => false)]).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.Deferred, Assert.Single(deferred.Entries).Outcome);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));

        var later = await LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, _ => false).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(later.Entries).Outcome);
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    [Fact]
    public async Task InterruptedOperation_ThenTheUserSetTheirOwnValue_KeepsIt()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-user");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("OptiScaler install: Game");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "dxgi.dll", "Game.exe");
        operation.AbandonAsIfCrashed();
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "dxgi", "native", "Game.exe"));

        var report = await LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), journalDir, _ => false).RecoverAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(JournalRecoveryOutcome.KeptLaterChanges, entry.Outcome);
        Assert.Equal("native", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task EnsureWithAnOperation_JournalsTheUndoStepBeforeWriting()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-order");
        var editor = new WineRegistryEditor(_ => false);
        var overrides = new WineDllOverrideService(editor);
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        await using var operation = await journal.BeginAsync("Test: Game");
        string? journaledBeforeWrite = null;
        editor.InsideLockForTesting = _ => journaledBeforeWrite = File.ReadAllText(Path.Combine(journalDir, operation.Id + ".json"));

        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "d3d9.dll", "Game.exe");

        Assert.Contains(WineOverrideCompensator.KindName, journaledBeforeWrite);
        Assert.Contains("d3d9", journaledBeforeWrite);
    }

    // w2 wine review, major 1: a refused write never leaves an undo step behind.
    [Fact]
    public async Task EnsureWhileWineRuns_IsRefusedBeforeTheStepIsJournaled()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-refused");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => true));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("UAL install: Game");

        var refused = await Assert.ThrowsAsync<WineRegistryEditRefusedException>(
            () => overrides.EnsureNativeBuiltinAsync(operation, prefix, "winmm.dll", "Game.exe"));

        Assert.True(refused.PrefixInUse);
        Assert.DoesNotContain(WineOverrideCompensator.KindName, File.ReadAllText(Path.Combine(journalDir, operation.Id + ".json")));
        operation.AbandonAsIfCrashed();
    }

    // w2 wine review, major 1: a step whose override is still at its earlier value was never written (or is undone):
    // done at once, even while Wine runs, so it never stays to undo a later install.
    [Fact]
    public void Compensator_StepNeverWritten_IsDoneEvenWhileWineRuns()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => true));
        var step = WineOverrideCompensator.Describe(prefix, overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe"));

        var result = new WineOverrideCompensator(overrides, _ => true).Compensate(step);

        Assert.Equal(CompensationStatus.Done, result.Status);
    }

    // w2 wine review, major 1: a step whose override now belongs to a record of the same game installed after it was
    // journaled is left alone; a record of another game, or one installed before the step, does not count.
    [Theory]
    [InlineData("steam:1", 1, false)]  // later record of the step's game: kept
    [InlineData("steam:2", 1, true)]   // another game's record: undone
    [InlineData("steam:1", -1, true)]  // a record from before the step: undone
    public void Compensator_OverrideALaterRecordOfTheGameOwns_IsKept(string recordGameKey, int recordAgeSign, bool undone)
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        // The record is saved now; the step was journaled a minute before (a later record) or after (an earlier one).
        var journaled = DateTime.UtcNow.AddMinutes(-recordAgeSign);
        var step = WineOverrideCompensator.Describe(prefix, overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe"), "steam:1", journaled);
        var owned = overrides.EnsureNativeBuiltin(prefix, "winmm.dll", "Game.exe"); // the later install wrote it itself
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "ledger-" + Guid.NewGuid().ToString("N") + ".json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = recordGameKey, Component = RHI.Core.Models.ManagedComponentKind.UltimateAsiLoader,
            InstalledAtUtc = journaled.AddMinutes(recordAgeSign), WineOverride = owned
        });

        var result = new WineOverrideCompensator(overrides, _ => false, records).Compensate(step);

        Assert.Equal(CompensationStatus.Done, result.Status);
        var after = WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe");
        Assert.Equal(undone ? null : "native,builtin", after);
    }

    // w2 wine re-review, finding 1: "later" is when the record was last saved, not its install time. An operation that
    // updates a record in place (the DX9 migration keeps InstalledAtUtc) after the step was journaled still counts.
    [Fact]
    public void Compensator_RecordInstalledBeforeTheStepButSavedAfterIt_KeepsTheOverride()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journaled = DateTime.UtcNow.AddMinutes(-1);
        var step = WineOverrideCompensator.Describe(prefix, overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe"), "steam:1", journaled);
        var owned = overrides.EnsureNativeBuiltin(prefix, "winmm.dll", "Game.exe");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "ledger-" + Guid.NewGuid().ToString("N") + ".json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:1", Component = RHI.Core.Models.ManagedComponentKind.ReShade,
            InstalledAtUtc = journaled.AddHours(-1), WineOverride = owned
        });

        var result = new WineOverrideCompensator(overrides, _ => false, records).Compensate(step);

        Assert.Equal(CompensationStatus.Done, result.Status);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
    }

    // w2 wine re-review, nit 2: an unreadable ledger cannot say whether a later record depends on the override, so the
    // step is kept (an I/O failure the journal retries at the next start) and the override left in place.
    [Fact]
    public void Compensator_WithAnUnreadableLedger_KeepsTheStep_AndTheOverride()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var step = WineOverrideCompensator.Describe(prefix, overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe"), "steam:1", DateTime.UtcNow);
        overrides.EnsureNativeBuiltin(prefix, "winmm.dll", "Game.exe");
        var ledger = Path.Combine(_root, "ledger-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(ledger, "{ not a ledger");
        var records = new LinuxDeploymentRecordStore(ledger);
        Assert.Empty(records.All());
        Assert.NotNull(records.CorruptLedgerPath);

        var refused = Assert.Throws<IOException>(() => new WineOverrideCompensator(overrides, _ => false, records).Compensate(step));

        Assert.StartsWith(WineOverrideCompensator.UnreadableLedgerPrefix, refused.Message, StringComparison.Ordinal);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
    }

    // w2 wine re-review, nit 5: with several later records that found RHI's value already there, the earliest installed
    // takes the step's ownership (what an uninterrupted first install would have owned), whatever the ledger order.
    [Fact]
    public void OwnershipHandOver_GoesToTheEarliestInstalledLaterRecord()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journaled = DateTime.UtcNow.AddMinutes(-10);
        var plan = overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe");
        var step = WineOverrideCompensator.Describe(prefix, plan, "steam:1", journaled);
        overrides.EnsureNativeBuiltin(prefix, "winmm.dll", "Game.exe");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "ledger-" + Guid.NewGuid().ToString("N") + ".json"));
        WineOverrideRecord Found() => new() { DllName = "winmm", ApplicationName = "Game.exe", PreviousValue = "native,builtin", ChangedByRhi = false };
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:1", Component = RHI.Core.Models.ManagedComponentKind.ShortFuse, InstalledAtUtc = journaled.AddMinutes(5), WineOverride = Found()
        });
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:1", Component = RHI.Core.Models.ManagedComponentKind.UltimateAsiLoader, InstalledAtUtc = journaled.AddMinutes(1), WineOverride = Found()
        });

        Assert.Equal(CompensationStatus.Done, new WineOverrideCompensator(overrides, _ => false, records).Compensate(step).Status);

        var owner = records.All().Single(r => r.WineOverride!.ChangedByRhi);
        Assert.Equal(RHI.Core.Models.ManagedComponentKind.UltimateAsiLoader, owner.Component);
        Assert.Null(owner.WineOverride!.PreviousValue);
    }

    // A ledger that cannot be written while the step hands its ownership over keeps the step for the next start.
    [Fact]
    public async Task OwnershipHandOver_ToAnUnwritableLedger_KeepsTheStep()
    {
        var prefix = Prefix();
        var journalDir = Path.Combine(_root, "journal-handover");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("UAL install: Game", "record|UltimateAsiLoader|steam:7");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "winmm.dll", "Game.exe");
        operation.AbandonAsIfCrashed();
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "ledger-handover.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:7", Component = RHI.Core.Models.ManagedComponentKind.UltimateAsiLoader, InstalledAtUtc = DateTime.UtcNow.AddMinutes(1),
            WineOverride = new WineOverrideRecord { DllName = "winmm", ApplicationName = "Game.exe", PreviousValue = "native,builtin", ChangedByRhi = false }
        });
        records.SaveFaultForTesting = () => new InvalidOperationException("The RHI deployment ledger is unreadable.");

        var report = await LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, _ => false, records).RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RollbackFailed, Assert.Single(report.Entries).Outcome);
        Assert.Single(Directory.GetFiles(journalDir, "*.json"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
    }

    // A value set since RHI's write is kept, and saying so needs no stopped Wine (read only).
    [Fact]
    public void Compensator_ALaterValue_IsKeptWithoutWaitingForWine()
    {
        var prefix = Prefix();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var step = WineOverrideCompensator.Describe(prefix, overrides.PlanNativeBuiltin(prefix, "winmm.dll", "Game.exe"));
        var reg = Path.Combine(prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "winmm", "builtin", "Game.exe"));

        var result = new WineOverrideCompensator(new WineDllOverrideService(new WineRegistryEditor(_ => true)), _ => true).Compensate(step);

        Assert.Equal(CompensationStatus.KeptLaterChange, result.Status);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(reg), "winmm", "Game.exe"));
    }

    // w2 wine review, major 1: the app's journal sees the ledger, or a deferred undo cannot tell a later install apart.
    // The app's composition (what App builds), not a source pattern (w2 wine re-review, nit 4): its journal sees the
    // ledger, so an interrupted install's undo leaves the override a later install of the game depends on.
    [Fact]
    public async Task App_BindsTheRecoveryJournalToTheLedger()
    {
        var prefix = Prefix();
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "app-ledger.json"));
        var journalDir = Path.Combine(_root, "app-journal");
        var (overrides, journal) = RHI.Desktop.AppComposition.CreateWineRecovery(records, isUiThread: () => false,
            journalDirectory: journalDir, isPrefixInUse: _ => false);
        var operation = await journal.BeginAsync("UAL install: Game", "record|UltimateAsiLoader|steam:7");
        await overrides.EnsureNativeBuiltinAsync(operation, prefix, "winmm.dll", "Game.exe");
        operation.AbandonAsIfCrashed();
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = "steam:7", Component = RHI.Core.Models.ManagedComponentKind.ReShade,
            WineOverride = new WineOverrideRecord { DllName = "winmm", ApplicationName = "Game.exe", PreviousValue = "native,builtin", ChangedByRhi = false }
        });

        await LinuxDeploymentRecovery.RecoverAsync(journal, records);

        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.True(records.All().Single().WineOverride!.ChangedByRhi);
    }

    public static TheoryData<string> Messages() => new()
    {
        WineRegistryEditor.PrefixInUseRefusalPrefix + "/jogos/prefixo",
        WineRegistryEditor.BusyRefusalPrefix + "/jogos/prefixo",
        WineRegistryEditor.MissingHiveMessage,
        D3DCompilerToolInstaller.OverrideNotSetWineRunningPrefix + "/jogos/prefixo",
        LinuxDeploymentRecovery.JournalUnavailablePrefix + "E/S negada); nothing was changed.",
        // The kept-value note rides on a removal message (here one the platform writes in Portuguese).
        "Universal MFG removido e o override Wine anterior foi restaurado."
            + WineDllOverrideService.KeptValueNotes(new WineOverrideRestoreResult(WineOverrideRestoreOutcome.KeptLaterValue, "dxgi", "builtin")),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void RegistryMessage_HasAPortuguesePresentation(string message)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);

        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        var english = UiLocalizationService.EnglishMarkersIn(pt).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {pt}");
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("en-US"), UiLocalizationService.PresentFor("en-US", message));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
