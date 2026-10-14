using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class ReShadeDeploymentServiceTests
{
    // #167: a Game Pass exe is encrypted on disk; ReShade takes the bitness its package declares and adds its proxy
    // beside it (an added file, nothing the package ships is replaced).
    [Fact]
    public async Task Install_OnAnEncryptedXboxExe_UsesThePackagesArchitecture()
    {
        var game = GameArchitectureTests.EncryptedXboxGame(Path.Combine(_root, "xbox"), PeArchitecture.X64);
        var prefix = Directory.CreateDirectory(Path.Combine(_root, "xbox", "pfx")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        game.PrefixPath = prefix;
        game.GraphicsApi = GraphicsApi.DirectX12;
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "xbox", "records.json"));
        var service = NewService(store, Path.Combine(_root, "xbox", "cache"));

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal(GameArchitectureTests.EncryptedExe, File.ReadAllBytes(game.ExecutablePath!));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
    }
}
