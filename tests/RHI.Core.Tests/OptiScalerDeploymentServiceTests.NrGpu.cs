using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #229: OptiScaler DLSS NR / F5 stage "Latest" made for the card (PrepareDlssNrRuntimeAsync) and their update check
/// compares the installed runtime with that same "Latest".
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Theory]
    [InlineData(OptiScalerVariant.F5)]
    [InlineData(OptiScalerVariant.DlssNr)]
    public async Task NrGpu_Install_StagesLatestForThisCard_AndRecordsIt(OptiScalerVariant variant)
    {
        var (game, store) = CreateGame("nrgpu-" + variant, GraphicsApi.DirectX12);
        var server = new Server();
        using var http = new HttpClient(server);
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new FakePackageProvider(CreateF5Package("nrgpu-package-" + variant)), Dlss(http, Path.Combine(_root, "nrgpu-cache-" + variant), RtxGeneration.Rtx40));

        var installed = await service.InstallAsync(game, variant);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll")));
        Assert.Equal(ForAll, store.Find(game, ManagedComponentKind.OptiScaler)!.Metadata[OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey]);
        Assert.Equal(0, server.DownloadsOf(For50));
    }

    [Theory]
    [InlineData(RtxGeneration.Rtx40, ForAll, false)]
    [InlineData(RtxGeneration.Rtx40, For50, true)]
    [InlineData(RtxGeneration.Rtx50, For50, false)]
    [InlineData(RtxGeneration.Unknown, For50, false)]
    public async Task NrGpu_CheckUpdate_ComparesTheRuntimeWithLatestForThisCard(RtxGeneration card, string installed, bool update)
    {
        var (game, store) = CreateGame($"nrgpu-check-{card}-{update}", GraphicsApi.DirectX12);
        using var http = new HttpClient(new Server());
        var package = CreateF5Package($"nrgpu-check-package-{card}-{update}");
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new FakePackageProvider(package), Dlss(http, Path.Combine(_root, $"nrgpu-check-cache-{card}-{update}"), card));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.F5)).Success);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        record.Metadata[OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey] = installed;
        store.Upsert(record);

        var check = await service.CheckUpdateAsync(game);

        Assert.False(check.CouldNotCheck, check.Message);
        Assert.Equal(update, check.UpdateAvailable);
    }
}
