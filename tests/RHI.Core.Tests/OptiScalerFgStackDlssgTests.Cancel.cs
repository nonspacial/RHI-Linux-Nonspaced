using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w2/vmsplit review finding 1: Cancel is honoured only before the first write; the FG stack deploy and its teardown
// never stop half-way.
public sealed partial class OptiScalerFgStackDlssgTests
{
    [Fact]
    public async Task EnableFgStack_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        var fx = CreateFixture("cancel-enable");

        var run = await CancelAtWritePhase.RunAsync(
            ct => fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true", ct), [fx.Bin]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll")));
        Assert.Contains(DlssgRelative, OwnedStackFiles(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!));
    }

    [Fact]
    public async Task DisableFgStack_CancelAfterTheFirstWrite_NeverStopsHalfWay()
    {
        var fx = CreateFixture("cancel-disable");
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);

        var run = await CancelAtWritePhase.RunAsync(
            ct => fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false", ct), [fx.Bin]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.False(Directory.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline")));
        Assert.False(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!.Metadata.ContainsKey(OptiScalerDeploymentService.FgStackFilesMetadataKey));
    }
}
