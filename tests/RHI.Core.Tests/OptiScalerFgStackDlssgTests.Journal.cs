using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/dllnames phase B (#23, #31): the FG stack's deploy and disable are journaled. A crash before the record is saved is
// rolled back at the next start: the stack files go, a game file the stack replaced is back at its name with its
// .original gone, and the record still describes the files on disk.
public sealed partial class OptiScalerFgStackDlssgTests
{
    [Fact]
    public async Task EnableFgStack_CrashWhileSavingTheRecord_NextStartRemovesTheStackAndPutsTheGameFileBack()
    {
        var fx = CreateFixture("journal-enable", journaled: true);
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x6A)); // a file of the game's own at a stack target: kept as .original
        var recordBefore = File.ReadAllText(fx.RecordsPath);
        using var death = new SimulatedDeath(fx.Journal!);
        fx.Store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => fx.Service.SetAdvancedSettingAsync(
            fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile)));

        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.RecordsPath);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(Dlssg(0x6A), File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(dlssg + ".original"));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile)));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll")));
        Assert.Equal(recordBefore, File.ReadAllText(fx.RecordsPath));
        Assert.Empty(Directory.GetFiles(fx.Bin, "*.rhi-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DisableFgStack_CrashWhileSavingTheRecord_NextStartPutsTheWholeStackBack()
    {
        var fx = CreateFixture("journal-disable", journaled: true);
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        File.WriteAllBytes(dlssg, Dlssg(0x6B));
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);
        var deployed = File.ReadAllBytes(dlssg);
        var enabler = Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile);
        var enablerBytes = File.ReadAllBytes(enabler);
        using var death = new SimulatedDeath(fx.Journal!);
        fx.Store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => fx.Service.SetAdvancedSettingAsync(
            fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false"));
        Assert.True(died);
        Assert.False(File.Exists(enabler));

        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.RecordsPath);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(enablerBytes, File.ReadAllBytes(enabler));
        Assert.Equal(deployed, File.ReadAllBytes(dlssg));
        Assert.Equal(Dlssg(0x6B), File.ReadAllBytes(dlssg + ".original"));
        var record = new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("1", record.Metadata[OptiScalerDeploymentService.FgStackEnabledMetadataKey]);
        Assert.Contains(DlssgRelative, OwnedStackFiles(record));
    }
}
