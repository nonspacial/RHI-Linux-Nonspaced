using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed partial class OptiScalerDeploymentServiceTests
{
    private (DetectedGame Game, LinuxDeploymentRecordStore Store) EncryptedXboxGame(string name, PeArchitecture declared)
    {
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        File.WriteAllBytes(game.ExecutablePath!, GameArchitectureTests.EncryptedExe);
        game.XboxPackage = new XboxPackageInfo("9ENC", "Xodus", null, null, Architecture: declared);
        return (game, store);
    }

    // #167: the encrypted exe of a Game Pass title used to fail "64-bit only"; the package's x64 now lets it through.
    [Fact]
    public async Task Install_OnAnEncryptedX64XboxExe_Deploys()
    {
        var (game, store) = EncryptedXboxGame("xbox-x64", PeArchitecture.X64);
        var package = CreatePackage("xbox-x64-package") with { Download = new VerifiedDownload("/cache/OptiScaler.7z", new string('C', 64), PublishedSha256: null) };
        var service = NewService(store, package);

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal(GameArchitectureTests.EncryptedExe, File.ReadAllBytes(game.ExecutablePath!));
    }

    [Fact]
    public async Task Install_OnAnEncryptedX86XboxExe_IsRefusedBeforeRequestingPackage()
    {
        var (game, store) = EncryptedXboxGame("xbox-x86", PeArchitecture.X86);
        var provider = new FakePackageProvider(CreatePackage("xbox-x86-package"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.Contains("64-bit", result.Message);
        Assert.Equal(0, provider.CallCount);
    }
}
