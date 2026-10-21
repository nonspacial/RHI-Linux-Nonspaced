using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: ShortFuse install and removal are journaled operations (#23): every game file they touch (add-on, DLSS /
// Streamline DLLs and their .original, ReShade's name, ReShade.ini) is tracked before it changes and the ShortFuse record
// (with ReShade's renamed record, in one ledger write) is the commit point. Rollbacks restore from verified copies (#31).
public sealed partial class ShortFuseDeploymentServiceTests
{
    private static string? Override(GameFixture fixture, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), dll, "Game.exe");

    private (Context Ctx, DeploymentJournal Journal, string JournalDir, string Ledger, WineRegistryEditor Editor) JournaledContext(GameFixture fixture)
    {
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var editor = new WineRegistryEditor(_ => false);
        var overrides = new WineDllOverrideService(editor);
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir, _ => false, records);
        return (CreateContext(fixture, fullNetwork: true, wineOverrides: overrides, journal: journal, records: records), journal, journalDir, ledger, editor);
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecords_NextStartPutsReShadeIniAndDlssBack()
    {
        using var fixture = CreateGame("crash-install");
        var (ctx, journal, journalDir, ledger, editor) = JournaledContext(fixture);
        using var _ = ctx;
        var reshadePath = AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var reshadeBytes = File.ReadAllBytes(reshadePath);
        var ini = Path.Combine(fixture.Bin, "ReShade.ini");
        File.WriteAllText(ini, "[INSTALL]\nHookStreamline=0\nOtherSetting=KeepMe\n");
        var iniBytes = File.ReadAllBytes(ini);
        using var death = new SimulatedDeath(journal);
        // Registry edits: 1 = the ASI Loader's override (its own operation, committed), 2 = ReShade's suspension. The
        // next ledger write is ShortFuse's commit: the process dies there.
        var edits = 0;
        editor.InsideLockForTesting = _ => { if (++edits == 2) ctx.Records.SaveFaultForTesting = death.Die; };

        var (died, _) = await death.RunUntilDeathAsync(() => ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName)));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "nvngx_dlssg.dll")));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Contains(report!.Entries, entry => entry.Outcome == JournalRecoveryOutcome.RolledBack);
        Assert.Equal(0, report.Failed);
        Assert.Equal(reshadeBytes, File.ReadAllBytes(reshadePath));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName)));
        Assert.Equal(iniBytes, File.ReadAllBytes(ini));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "nvngx_dlssg.dll")));
        Assert.False(File.Exists(Path.Combine(RenoDxDeploymentService.ResolveAddonDeployPath(fixture.Bin), ShortFuseDeploymentService.AddonFileName)));
        Assert.Equal("native,builtin", Override(fixture, "dxgi")); // the suspension is undone
        var after = new LinuxDeploymentRecordStore(ledger);
        Assert.Null(after.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        Assert.Equal(reshadePath, after.Find(fixture.Game, ManagedComponentKind.ReShade)!.Files.Single().Path);
        // The ASI Loader's own install had committed: it stays installed, owned and removable on its own.
        Assert.NotNull(after.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.Empty(Directory.GetFiles(journalDir, "*.json"));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheShortFuseStateBack()
    {
        using var fixture = CreateGame("crash-remove");
        var (ctx, journal, journalDir, ledger, editor) = JournaledContext(fixture);
        using var _ = ctx;
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var ini = Path.Combine(fixture.Bin, "ReShade.ini");
        File.WriteAllText(ini, "[INSTALL]\nHookStreamline=0\n");
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true)).Success);
        var asi = Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName);
        var installedIni = File.ReadAllBytes(ini);
        var dlssg = File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssg.dll"));
        using var death = new SimulatedDeath(journal);
        // The first registry edit of the removal is ReShade's override re-enable; the next save (the commit) dies.
        editor.InsideLockForTesting = _ => ctx.Records.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => ctx.ShortFuse.RemoveAsync(fixture.Game));
        Assert.True(died);
        Assert.False(File.Exists(asi));
        Assert.Equal("native,builtin", Override(fixture, "dxgi"));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.True(File.Exists(asi));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "dxgi.dll")));
        Assert.Equal(installedIni, File.ReadAllBytes(ini));
        Assert.Equal(dlssg, File.ReadAllBytes(Path.Combine(fixture.Bin, "nvngx_dlssg.dll")));
        Assert.Null(Override(fixture, "dxgi")); // suspended again, as the ShortFuse record says
        var after = new LinuxDeploymentRecordStore(ledger);
        Assert.NotNull(after.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        Assert.Equal(asi, after.Find(fixture.Game, ManagedComponentKind.ReShade)!.Files.Single().Path);
        Assert.Equal("native,builtin", Override(fixture, "winmm")); // the ASI Loader was never touched
    }

    [Fact]
    public async Task Remove_TheAsiLoaderFailingAfterTheCommit_LeavesItInstalledAndSaysSo()
    {
        using var fixture = CreateGame("ual-after");
        var (ctx, _, _, _, editor) = JournaledContext(fixture);
        using var __ = ctx;
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true)).Success);
        // Edits: 1 = ReShade's re-enable, 2 = the ASI Loader's override restore; its record save then fails.
        var edits = 0;
        editor.InsideLockForTesting = _ =>
        {
            if (++edits == 2) ctx.Records.SaveFaultForTesting = () => { ctx.Records.SaveFaultForTesting = null; return new IOException("disk full"); };
        };

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Contains(ShortFuseDeploymentService.UalKeptPrefix, remove.Message, StringComparison.Ordinal);
        // Review w3-journal #8: the success line no longer claims the UAL state was restored when the loader stays.
        Assert.StartsWith(ShortFuseDeploymentService.RemovedUalKeptMessage, remove.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UAL state", remove.Message, StringComparison.Ordinal);
        Assert.Null(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        // ReShade's record follows its file back to its name in the same ledger write as the ShortFuse record's removal.
        Assert.Equal(Path.Combine(fixture.Bin, "dxgi.dll"), ctx.Records.Find(fixture.Game, ManagedComponentKind.ReShade)!.Files.Single().Path);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "dxgi.dll")));
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal("native,builtin", Override(fixture, "winmm"));
    }

    // Journal re-review minor 1 / #204: the commit write replaces the ledger (ShortFuse gone, ReShade renamed back to
    // dxgi.dll) and then reports failure. The removal is finished as committed: ShortFuse stays removed and ReShade's record
    // names dxgi.dll, where its file is (it used to roll the rename back and put both records back as before the write).
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Remove_CommitWriteLandsThenFails_StaysRemoved_ReShadesRecordFollowsItsFile(LandedShape shape)
    {
        using var fixture = CreateGame("commit-written-then-failed-" + shape);
        var (ctx, _, journalDir, _, editor) = JournaledContext(fixture);
        using var _ = ctx;
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true)).Success);
        var asi = Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName);
        var dxgi = Path.Combine(fixture.Bin, "dxgi.dll");
        // The first registry edit of the removal is ReShade's override re-enable; the next ledger save is the commit write.
        var armed = false;
        editor.InsideLockForTesting = _ =>
        {
            if (armed) return;
            armed = true;
            LandedLedgerWrite.Arm(ctx.Records, shape, at: 1);
        };

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        LandedLedgerWrite.Disarm(ctx.Records);
        Assert.True(remove.Success, remove.Message);
        Assert.StartsWith(DeploymentScope.LandedRemovalPrefix, remove.Message);
        Assert.False(File.Exists(asi));
        Assert.True(File.Exists(dxgi));
        var after = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        Assert.Null(after.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        var reshade = after.Find(fixture.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(dxgi, reshade.Files.Single().Path);
        Assert.Equal("native,builtin", Override(fixture, "dxgi"));
        Assert.Empty(Directory.GetFiles(journalDir, "*.json"));
    }

    // Review w3-journal #6: ShortFuse's record goes and ReShade's renamed-back record is saved in ONE ledger write. Split
    // into two writes, a crash between them left ReShade's record naming ReShade64.asi while the file was back at its
    // original name. The process dies at the first ledger write after the commit write here; whatever the next start
    // finds, every record must describe the files on disk.
    [Fact]
    public async Task Remove_CrashRightAfterTheCommitWrite_LeavesEveryRecordDescribingTheFilesOnDisk()
    {
        using var fixture = CreateGame("crash-after-commit");
        var (ctx, journal, journalDir, ledger, editor) = JournaledContext(fixture);
        using var _ = ctx;
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true)).Success);
        var addon = Path.Combine(RenoDxDeploymentService.ResolveAddonDeployPath(fixture.Bin), ShortFuseDeploymentService.AddonFileName);
        using var death = new SimulatedDeath(journal);
        // The first registry edit of the removal is ReShade's override re-enable; the save after it is the commit write,
        // the one after that dies.
        var saves = 0;
        editor.InsideLockForTesting = _ => ctx.Records.SaveFaultForTesting ??= () => ++saves == 2 ? death.Die() : null;

        var (died, _) = await death.RunUntilDeathAsync(() => ctx.ShortFuse.RemoveAsync(fixture.Game));
        Assert.True(died);
        await SimulatedDeath.RecoverAsync(journalDir, ledger);

        var after = new LinuxDeploymentRecordStore(ledger);
        var reshade = after.Find(fixture.Game, ManagedComponentKind.ReShade)!;
        Assert.True(File.Exists(reshade.Files.Single().Path), $"ReShade's record names {reshade.Files.Single().Path}, which is not on disk");
        Assert.Equal(after.Find(fixture.Game, ManagedComponentKind.ShortFuse) is not null, File.Exists(addon));
        // Here the commit write went through: ShortFuse is gone and ReShade is back at its own name.
        Assert.Null(after.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        Assert.Equal(Path.Combine(fixture.Bin, "dxgi.dll"), reshade.Files.Single().Path);
    }
}
