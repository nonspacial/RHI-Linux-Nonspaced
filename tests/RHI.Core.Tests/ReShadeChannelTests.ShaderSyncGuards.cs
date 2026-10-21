using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w7 follow-up (shader-sets re-review findings 1 and 3, adopted reviewer probes RR1–RR4, RR8, RR9): the shaders-only sync's
/// own guards — crash recovery when it first brings packs (compiler override), Cancel at its write phase (its own
/// BeginWrites), cancel with a forced rollback then retry and restart, Off taking RHI's search paths out (its
/// CleanupManagedIni), and keys other than the search paths left as the user set them (defaults only on a recreated file).
/// </summary>
public sealed partial class ReShadeChannelTests
{
    private sealed record RrFx(DetectedGame Game, LinuxDeploymentRecordStore Store, string Ledger, string JournalDir,
        DeploymentJournal Journal, ReShadeDeploymentService Service, GlobalShaderAddonStore Sets, string Cache);

    private RrFx RrFixture(string name)
    {
        var (game, store) = SetGame(name);
        var cache = Path.Combine(_root, name + "-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, name);
        var journalDir = Path.Combine(_root, name + "-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var http = new HttpClient(new MutableHandler());
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides, new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")) { Sets = sets },
            journal: journal);
        return new(game, store, Path.Combine(_root, name, "records.json"), journalDir, journal, service, sets, cache);
    }

    private static string UserReg(DetectedGame game) => File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));

    // RR1: crash at the sync's commit point when the sync is the first to bring packs (it sets the compiler override):
    // recovery must put the game folder AND the prefix's override back.
    [Fact]
    public async Task SyncGuard_CrashAtCommit_FirstPacks_RecoveryRestoresFolderAndCompilerOverride()
    {
        var fx = RrFixture("rr1");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var before = CancelAtWritePhase.Snapshot(fx.Game.BinaryDirectory!);
        var reg = UserReg(fx.Game);
        var recordBefore = System.Text.Json.JsonSerializer.Serialize(fx.Store.Find(fx.Game, ManagedComponentKind.ReShade));
        using (var death = new SimulatedDeath(fx.Journal))
        {
            fx.Store.SaveFaultForTesting = death.Die;
            var (died, run) = await death.RunUntilDeathAsync(() => fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]));
            Assert.True(died, run.IsCompleted ? (await run).Message : "running");
            Assert.True(File.Exists(PackFile(fx.Game)));
            Assert.Contains("d3dcompiler_47", UserReg(fx.Game), StringComparison.OrdinalIgnoreCase);
            var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.Ledger);
            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            CancelAtWritePhase.AssertUnchanged(before, fx.Game.BinaryDirectory!);
            Assert.Equal(reg, UserReg(fx.Game));
            Assert.Equal(recordBefore, System.Text.Json.JsonSerializer.Serialize(new LinuxDeploymentRecordStore(fx.Ledger).Find(fx.Game, ManagedComponentKind.ReShade)));
        }
    }

    // RR2: crash inside the registry lock (the override write, after the INI and old packs were cleaned).
    [Fact]
    public async Task SyncGuard_CrashAtCommit_Off_RecoveryRestoresEverything()
    {
        // (renamed in spirit: packs present, the sync takes them all out — Off — and dies at its commit point)
        var fx = RrFixture("rr2");
        fx.Sets.SetShaderMode(fx.Game, ShaderAddonMode.Select, ["CrosireMaster"]);
        var inst = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true, ShaderPackIds = ["CrosireMaster"] });
        Assert.True(inst.Success, inst.Message);
        Assert.True(File.Exists(PackFile(fx.Game)));
        var before = CancelAtWritePhase.Snapshot(fx.Game.BinaryDirectory!);
        var reg = UserReg(fx.Game);
        using (var death = new SimulatedDeath(fx.Journal))
        {
            fx.Store.SaveFaultForTesting = death.Die;
            var (died, run) = await death.RunUntilDeathAsync(() => fx.Service.SyncShaderPacksAsync(fx.Game, []));
            Assert.True(died, run.IsCompleted ? (await run).Message : "running");
            Assert.False(File.Exists(PackFile(fx.Game)));
            var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.Ledger);
            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            CancelAtWritePhase.AssertUnchanged(before, fx.Game.BinaryDirectory!);
            Assert.Equal(reg, UserReg(fx.Game));
        }
    }

    // RR3: Cancel at the write phase is not honoured (runs to completion); version kept.
    [Fact]
    public async Task SyncGuard_CancelAtWritePhase_RunsToCompletion()
    {
        var fx = RrFixture("rr3");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var outcome = await CancelAtWritePhase.RunAsync(ct => fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"], ct),
            [fx.Game.BinaryDirectory!, fx.Game.PrefixPath!]);
        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.True(File.Exists(PackFile(fx.Game)));
        Assert.Equal(["CrosireMaster"], ReShadeDeploymentService.RecordedShaderPacks(fx.Store.Find(fx.Game, ManagedComponentKind.ReShade)));
    }

    // RR4: Cancel after the first write with a forced rollback, then retry, then restart: consistent at every step.
    [Fact]
    public async Task SyncGuard_CancelThenRollback_Retry_Restart_StaysConsistent()
    {
        var fx = RrFixture("rr4");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var managed = ManagedComponents(fx.Store, fx.Service);
        managed.ShaderSets = fx.Sets;
        fx.Sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        Assert.True(managed.ShaderSetBehind(fx.Game));
        var reg = UserReg(fx.Game);
        var outcome = await CancelAtWritePhase.RunAsync(ct => managed.ApplyShaderSetAsync(fx.Game, ct),
            [fx.Game.BinaryDirectory!], atWritePhase: () => fx.Store.SaveFaultForTesting = () => new IOException("disk full (rr4)"));
        fx.Store.SaveFaultForTesting = null;
        Assert.Equal(ShaderSetOutcome.Failed, outcome.Result.Outcome);
        CancelAtWritePhase.AssertUnchanged(outcome.Before, fx.Game.BinaryDirectory!);
        Assert.Equal(reg, UserReg(fx.Game));
        Assert.Empty(Directory.GetFiles(fx.JournalDir));
        Assert.True(managed.ShaderSetBehind(fx.Game));

        var retry = await managed.ApplyShaderSetAsync(fx.Game);
        Assert.Equal(ShaderSetOutcome.Changed, retry.Outcome);
        Assert.True(File.Exists(PackFile(fx.Game)));
        Assert.False(managed.ShaderSetBehind(fx.Game));

        // restart: fresh stores, recovery, then the same answer.
        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.Ledger);
        Assert.True(report is null || report.Entries.Count == 0);
        var store2 = new LinuxDeploymentRecordStore(fx.Ledger);
        var sets2 = new GlobalShaderAddonStore(store2, Path.Combine(_root, "rr4", "shader-addon-sets.json"));
        var service2 = SetService(store2, fx.Cache, sets2);
        var managed2 = ManagedComponents(store2, service2);
        managed2.ShaderSets = sets2;
        Assert.False(managed2.ShaderSetBehind(fx.Game));
        Assert.Equal(ShaderSetOutcome.Unchanged, (await managed2.ApplyShaderSetAsync(fx.Game)).Outcome);
        Assert.Equal(ShaderAddonMode.Global, sets2.ChoiceFor(fx.Game)!.ShaderMode);
    }

    // RR9: Off after packs takes RHI's search paths back out of ReShade.ini (CleanupManagedIni in the sync).
    [Fact]
    public async Task SyncGuard_OffAfterPacks_RemovesRhisSearchPaths()
    {
        var fx = RrFixture("rr9");
        fx.Sets.SetShaderMode(fx.Game, ShaderAddonMode.Select, ["CrosireMaster"]);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var ini = ReShadeIniManager.ResolveIniPath(fx.Game.BinaryDirectory!);
        File.AppendAllText(ini, "\n[OVERLAY]\nTutorialProgress=4\n"); // the user's own key: RHI's file is no longer byte-identical
        Assert.Contains("RHI", File.ReadAllText(ini));
        var result = await fx.Service.SyncShaderPacksAsync(fx.Game, []);
        Assert.True(result.Success, result.Message);
        var lines = File.ReadAllLines(ini).Where(l => l.Contains("SearchPaths", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.DoesNotContain(lines, l => l.Contains("RHI", StringComparison.Ordinal));
    }

    // RR8: does a shaders-only sync rewrite ReShade.ini keys other than the search paths (Settings hotkey)?
    [Fact]
    public async Task SyncGuard_LeavesKeysOtherThanTheSearchPathsAlone()
    {
        var fx = RrFixture("rr8");
        fx.Service.IniDefaults = (_, _) => new ReShadeIniDefaults { OverlayHotkey = "36,0,0,0" };
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var ini = ReShadeIniManager.ResolveIniPath(fx.Game.BinaryDirectory!);
        var text = File.ReadAllText(ini).Replace("KeyOverlay=36,0,0,0", "KeyOverlay=45,0,0,0"); // the user changed it in game
        File.WriteAllText(ini, text);
        var result = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);
        Assert.True(result.Success, result.Message);
        Assert.Contains("KeyOverlay=45,0,0,0", File.ReadAllText(ini));
    }

    // B12 other half: a ReShade.ini the sync recreates (RHI's own file, unchanged, is taken out by the cleanup) gets the
    // Settings defaults again, as ReShade's install writes them.
    [Fact]
    public async Task SyncGuard_RecreatedIni_GetsTheSettingsDefaults()
    {
        var fx = RrFixture("rr8b");
        fx.Service.IniDefaults = (_, _) => new ReShadeIniDefaults { OverlayHotkey = "36,0,0,0" };
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var ini = ReShadeIniManager.ResolveIniPath(fx.Game.BinaryDirectory!);
        Assert.Contains("KeyOverlay=36,0,0,0", File.ReadAllText(ini));

        var result = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.True(result.Success, result.Message);
        Assert.Contains("KeyOverlay=36,0,0,0", File.ReadAllText(ini));
        Assert.Contains("CrosireMaster", File.ReadAllText(ini));
    }

    // B14 (shader-sets re-review nit 4): with the ledger unreadable, start-up adoption adopts nothing and saves nothing.
    [Fact]
    public async Task AdoptFromLedger_UnreadableLedger_AdoptsNothing()
    {
        var fx = RrFixture("b14");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        File.WriteAllText(fx.Ledger, "{ not json");
        var setsPath = Path.Combine(_root, "b14", "adopt-sets.json");
        var sets = new GlobalShaderAddonStore(new LinuxDeploymentRecordStore(fx.Ledger), setsPath);

        Assert.Empty(sets.AdoptFromLedger());
        Assert.False(File.Exists(setsPath));
    }

    // B15 (shader-sets re-review nit 5): the "not up to date" lines name the real causes (a change that did not finish, a
    // game left out of Update all, no native compiler for shaders), never only "cancelled or skipped"; and the missing-
    // compiler note says a DLL picked with Browse takes effect through the profile's Apply.
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void BehindLines_NameTheRealCauses(string language)
    {
        var shaders = RHI.Desktop.Localization.UiLocalizationService.TextFor(language, "Sets.ShadersBehind");
        var addons = RHI.Desktop.Localization.UiLocalizationService.TextFor(language, "Sets.AddonsBehind");
        var updateAll = language == "en-US" ? "Update all" : "Atualizar tudo";
        Assert.Contains(updateAll, shaders);
        Assert.Contains(updateAll, addons);
        Assert.Contains("D3DCompiler 47", shaders);
        Assert.DoesNotContain("D3DCompiler", addons);
        Assert.Contains("Browse", LinuxManagedComponentService.ShaderPacksNotInstalledNote);
    }
}
