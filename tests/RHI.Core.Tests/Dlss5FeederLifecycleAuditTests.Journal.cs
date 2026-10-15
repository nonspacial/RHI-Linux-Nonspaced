using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #169: the DLSS5 Feeder Full install and removal are one journaled operation each (files, ReShade.ini, the DX9
// translation and the Wine overrides); a crash at any ledger write is undone at the next start, or kept when it committed.
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    private static string UserReg(FeederFixture f) => File.ReadAllText(Path.Combine(f.Game.PrefixPath!, "user.reg"));

    [Fact]
    public async Task Install_CrashAtTheRecordSave_NextStartLeavesTheGameAsBefore_AndItInstallsAgain()
    {
        using var f = new FeederFixture(journaled: true);
        var before = CancelAtWritePhase.Snapshot(f.Bin);
        var userReg = UserReg(f);
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(f.Journal!))
        {
            f.Records.SaveFaultForTesting = death.Die; // the install's only ledger write is its commit point
            (var died, run) = await death.RunUntilDeathAsync(() => f.Install(Dlss5FeederConsumer.RenoDxDlss5));
            Assert.True(died);
            Assert.True(File.Exists(Path.Combine(f.Bin, "dlss5-feed.addon64")));
            Assert.Contains("d3dcompiler_47", UserReg(f));

            var report = await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        }
        await run; // the dead thread, let go, finds its operation abandoned and changes nothing
        CancelAtWritePhase.AssertUnchanged(before, f.Bin);
        Assert.Equal(userReg, UserReg(f));
        var fresh = new LinuxDeploymentRecordStore(f.RecordsPath);
        Assert.Null(fresh.Find(f.Game, ManagedComponentKind.Dlss5Feeder));

        // The empty folders a rolled-back install leaves never block the next one.
        f.Records.SaveFaultForTesting = null;
        var again = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);
        Assert.True(again.Success, again.Message);
        f.AssertInstalled(Dlss5FeederConsumer.RenoDxDlss5, expectedEarlyLoad: "0");
    }

    // Checked before the dead thread is let go: in a real crash nothing runs after the commit write.
    [Fact]
    public async Task Install_CrashRightAfterTheRecordSave_IsKept()
    {
        using var f = new FeederFixture(journaled: true);
        using var death = new SimulatedDeath(f.Journal!);
        f.Records.SaveWrittenFaultForTesting = death.Die;
        var (died, _) = await death.RunUntilDeathAsync(() => f.Install(Dlss5FeederConsumer.DeepFriedChicken));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);

        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
        var record = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.All(record.Files, file => Assert.Equal(file.Sha256, FileHash.Sha256Hex(file.Path), ignoreCase: true));
        Assert.Contains("deep-fried-chicken.addon64", File.ReadAllText(f.IniPath), StringComparison.Ordinal);
        Assert.Contains("d3dcompiler_47", UserReg(f));
    }

    // The DX9 translation (ReShade d3d9.dll -> dxgi.dll, dgVoodoo2 at d3d9.dll) runs inside the Feeder's operation and
    // ReShade's changed record is saved in the Feeder's commit write: a crash at any ledger write of the install leaves
    // ReShade's record where its file is (the migration used to commit on its own, so a crash before the Feeder's record
    // left ReShade's record at dxgi.dll over a d3d9.dll put back by recovery).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dx9Install_CrashAtAnyLedgerWrite_LeavesReShadesRecordWhereItsFileIs(int crashAtSave)
    {
        using var f = new FeederFixture(dx9: true, journaled: true);
        var before = CancelAtWritePhase.Snapshot(f.Bin);
        var reShadeBefore = f.Records.Find(f.Game, ManagedComponentKind.ReShade)!;
        bool died;
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(f.Journal!))
        {
            var saves = 0;
            f.Records.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null;
            (died, run) = await death.RunUntilDeathAsync(() => f.Install(Dlss5FeederConsumer.RenoDxDlss5));
            if (died)
            {
                Assert.True(File.Exists(Path.Combine(f.Bin, "dgVoodoo.conf")));
                var report = await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);
                Assert.Contains(report!.Entries, entry => entry.Outcome == JournalRecoveryOutcome.RolledBack);
                var fresh = new LinuxDeploymentRecordStore(f.RecordsPath);
                var reShade = fresh.Find(f.Game, ManagedComponentKind.ReShade)!;
                Assert.All(reShade.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                Assert.Null(fresh.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
                CancelAtWritePhase.AssertUnchanged(before, f.Bin);
                Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(reShade.Files).Path);
                Assert.Equal(reShadeBefore.WineOverride!.DllName, reShade.WineOverride!.DllName);
            }
        }
        var result = await run;
        f.Records.SaveFaultForTesting = null;
        if (!died)
        {
            Assert.True(result.Success, result.Message);
            var reShade = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.ReShade)!;
            Assert.Equal(Path.Combine(f.Bin, "dxgi.dll"), Assert.Single(reShade.Files).Path);
        }
    }

    // Every ledger write of a removal is a crash point: whichever it is, the next start leaves records that describe the
    // files on disk — either the Feeder still installed (and removable) or gone, with ReShade's record where its file is.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task Remove_CrashAtAnyLedgerWrite_LeavesRecordsThatMatchTheFiles(bool dx9, int crashAtSave)
    {
        using var f = new FeederFixture(dx9: dx9, journaled: true);
        Assert.True((await f.Install(Dlss5FeederConsumer.RenoDxDlss5)).Success);
        var installed = CancelAtWritePhase.Snapshot(f.Bin);
        var stillInstalled = false;
        Task<ComponentOperationResult> run;
        using (var death = new SimulatedDeath(f.Journal!))
        {
            var saves = 0;
            f.Records.SaveFaultForTesting = () => ++saves == crashAtSave ? death.Die() : null; // 1 = the removal's stamp
            (var died, run) = await death.RunUntilDeathAsync(() => f.Service.RemoveAsync(f.Game));
            if (died)
            {
                await SimulatedDeath.RecoverAsync(f.JournalDirectory, f.RecordsPath);
                // The next start, before anything else runs (in a real crash nothing runs after the fault).
                var fresh = new LinuxDeploymentRecordStore(f.RecordsPath);
                var reShade = fresh.Find(f.Game, ManagedComponentKind.ReShade);
                if (reShade is not null)
                    Assert.All(reShade.Files, file => Assert.True(File.Exists(file.Path), file.Path));
                stillInstalled = fresh.Find(f.Game, ManagedComponentKind.Dlss5Feeder) is not null;
                if (stillInstalled)
                {
                    CancelAtWritePhase.AssertUnchanged(installed, f.Bin); // rolled back: every file as installed
                    Assert.Contains("d3dcompiler_47", UserReg(f));        // and the compiler override RHI set
                }
            }
        }
        await run;
        f.Records.SaveFaultForTesting = null;
        if (stillInstalled)
        {
            var again = await f.Service.RemoveAsync(f.Game);
            Assert.True(again.Success, again.Message);
        }
        if (!dx9) f.AssertClean();
        else
        {
            Assert.Null(new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.Dlss5Feeder));
            var restored = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.ReShade)!;
            Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(restored.Files).Path);
            Assert.False(File.Exists(Path.Combine(f.Bin, "dgVoodoo.conf")));
        }
    }

    // A rolled-back install leaves the folders it created (the journal tracks files): empty ones never block the next
    // install, a file in them still does.
    [Fact]
    public async Task Install_EmptyFoldersLeftByARolledBackInstall_DoNotBlock_AFileInThemDoes()
    {
        using var f = new FeederFixture();
        var tree = Path.Combine(f.Bin, "reshade-shaders", "RHI", "DLSS5FeederFull");
        Directory.CreateDirectory(Path.Combine(tree, "Shaders", "include"));
        Directory.CreateDirectory(Path.Combine(tree, "Textures"));
        File.WriteAllText(Path.Combine(tree, "Textures", "someone-else.png"), "x");

        var blocked = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);
        Assert.False(blocked.Success);
        Assert.Contains("External or previous shader tree detected", blocked.Message, StringComparison.Ordinal);

        File.Delete(Path.Combine(tree, "Textures", "someone-else.png"));
        var installed = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);
        Assert.True(installed.Success, installed.Message);
    }
}
