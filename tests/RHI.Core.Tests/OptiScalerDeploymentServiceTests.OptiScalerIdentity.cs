using RHI.Core.Models;

namespace RHI.Core.Tests;

/// <summary>#154: a tool that only mentions OptiScaler is no longer taken for OptiScaler, and it stays protected.</summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Fact]
    public async Task Install_OverAToolThatMentionsOptiScaler_IsStillRefusedAsForeign()
    {
        var (game, store) = CreateGame("encore", GraphicsApi.DirectX12);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var bytes = OptiScalerIdentityTests.RtxEncoreLikeDll();
        File.WriteAllBytes(dxgi, bytes);
        var service = NewService(store, CreatePackage("encore-package"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("Foreign DLL protection", result.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
    }
}
