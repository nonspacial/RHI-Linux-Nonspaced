using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w7/reshade (#196): an installed OptiScaler renamed in place — its proxy, override and recorded name move together;
// removal then follows the new name and leaves user.reg as it was; the ReShade chain (ReShade64.dll) keeps the name.
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Fact]
    public async Task Rename_MovesTheProxyOverrideAndRecordedName_AndRemovalFollowsIt()
    {
        var (game, store) = CreateGame("rename-os", GraphicsApi.DirectX12);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var userRegBefore = File.ReadAllBytes(userReg);
        var service = NewService(store, CreatePackage("rename-os-package"));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var bytes = File.ReadAllBytes(dxgi);

        var renamed = await service.RenameProxyAsync(game, "winmm.dll", choice: null);

        Assert.True(renamed.Success, renamed.Message);
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        Assert.False(File.Exists(dxgi));
        Assert.Equal(bytes, File.ReadAllBytes(winmm));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(record));
        Assert.Contains(record.Files, file => file.Path == winmm);
        Assert.DoesNotContain(record.Files, file => file.Path == dxgi);
        Assert.Equal("native,builtin", Override(game, "winmm"));
        Assert.Null(Override(game, "dxgi"));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini"))); // only the DLL moves

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.False(File.Exists(winmm));
        Assert.Equal(userRegBefore, File.ReadAllBytes(userReg));
    }

    [Fact]
    public async Task Managed_RenameToAutomatic_AndReset_ClearTheChoiceAndMoveOptiScalerBack()
    {
        var (game, store) = CreateGame("rename-os-managed", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("rename-os-managed-package"), "rename-os-managed");
        names.SetOptiScaler(game, "version.dll");
        Assert.True((await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null)).Success);
        Assert.True(managed.HasDllNamesToReset(game));

        var renamed = await managed.RenameOptiScalerProxyAsync(game, "winmm.dll");
        Assert.True(renamed.Success, renamed.Message);
        Assert.Equal("winmm.dll", names.GetOptiScaler(game));
        Assert.Equal("winmm.dll", managed.OptiScalerProxyNameFor(game));

        var reset = await managed.ResetDllNamesAsync(game);

        Assert.True(reset.Success, reset.Message);
        Assert.Contains("winmm.dll → dxgi.dll", reset.Message);
        Assert.Null(names.GetOptiScaler(game));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("dxgi.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));
        Assert.False(managed.HasDllNamesToReset(game));
    }

    [Fact]
    public async Task Rename_WhileRhiReShadeIsChainedAsReShade64_IsRefusedAndNothingChanges()
    {
        var (game, store) = CreateGame("rename-os-chain", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var service = NewService(store, CreatePackage("rename-os-chain-package"));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        var watched = new[] { game.BinaryDirectory!, game.PrefixPath!, Path.GetDirectoryName(Path.Combine(_root, "rename-os-chain", "records.json"))! };
        var before = CancelAtWritePhase.Snapshot(watched);

        var renamed = await service.RenameProxyAsync(game, "winmm.dll", choice: null);

        Assert.False(renamed.Success);
        Assert.StartsWith(OptiScalerDeploymentService.RenameChainedPrefix + "dxgi.dll", renamed.Message);
        CancelAtWritePhase.AssertUnchanged(before, watched);
        Assert.Empty(service.RenameOptions(game, OptiScalerDeploymentService.ProxyNames).Targets);
    }

    [Fact]
    public async Task RenameOptions_LeaveOutNamesAnotherFileUses()
    {
        var (game, store) = CreateGame("rename-os-options", GraphicsApi.DirectX12);
        var service = NewService(store, CreatePackage("rename-os-options-package"));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "version.dll"), [0x4D, 0x5A]);

        var (targets, refusal) = service.RenameOptions(game, OptiScalerDeploymentService.ProxyNames);

        Assert.Null(refusal);
        Assert.Contains("winmm.dll", targets);
        Assert.DoesNotContain("version.dll", targets);
        Assert.DoesNotContain("dxgi.dll", targets);
    }
}
