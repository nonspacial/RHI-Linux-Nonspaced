using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #154: which refusal (or coexistence) ReShade picks for an occupied dxgi.dll follows the version-resource identity.
/// Protection is unchanged: every file that is not RHI's stays untouched.
/// </summary>
public sealed partial class ReShadeDeploymentServiceTests
{
    private (DetectedGame Game, string Dxgi) DxgiGame(string name, byte[] dxgiBytes)
    {
        var bin = Path.Combine(_root, name);
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        File.WriteAllBytes(dxgi, dxgiBytes);
        return (new DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Wine", GraphicsApi = GraphicsApi.DirectX12 }, dxgi);
    }

    [Fact]
    public async Task Install_OverAToolThatMentionsOptiScaler_IsTheForeignDllRefusal()
    {
        var bytes = OptiScalerIdentityTests.RtxEncoreLikeDll();
        var (game, dxgi) = DxgiGame("encore", bytes);
        var service = NewService(new LinuxDeploymentRecordStore(Path.Combine(_root, "encore-records.json")), Path.Combine(_root, "encore-cache"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.StartsWith("Foreign DLL protection", result.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
    }

    [Fact]
    public async Task Install_OverAnUnmanagedOptiScalerThatMentionsReShade_IsTheOptiScalerRefusal()
    {
        var bytes = OptiScalerIdentityTests.OptiScalerDll("LoadReshade: load ReShade64.dll");
        var (game, dxgi) = DxgiGame("external-opti", bytes);
        var service = NewService(new LinuxDeploymentRecordStore(Path.Combine(_root, "external-opti-records.json")), Path.Combine(_root, "external-opti-cache"));

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.StartsWith("OptiScaler occupies", result.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
    }

    [Fact]
    public async Task Install_BesideAManagedOptiScalerThatMentionsReShade_UsesReShade64Coexistence()
    {
        var bytes = OptiScalerIdentityTests.OptiScalerDll("LoadReshade: load ReShade64.dll");
        var (game, dxgi) = DxgiGame("managed-opti", bytes);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "managed-opti-records.json"));
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = game.BinaryDirectory!,
            Files =
            [
                new LinuxDeployedFile { Path = dxgi, ExistedBeforeInstall = false, Sha256 = Sha256(dxgi) },
                new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, AllowUserChanges = true, Sha256 = Sha256(ini) }
            ]
        });
        var service = NewService(store, Path.Combine(_root, "managed-opti-cache"));

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
    }
}
