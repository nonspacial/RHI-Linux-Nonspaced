using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/dllnames phase B (#136, #120): OptiScaler installed under another DLL name than dxgi.dll (Control UE frame
// generation puts it at winmm.dll so ReShade keeps dxgi.dll). The name is recorded; update and removal use it; Wine gets
// the override for that name; ReShade at dxgi.dll is left alone.
public sealed partial class OptiScalerDeploymentServiceTests
{
    private static string? Override(DetectedGame game, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task Install_UnderWinmm_DeploysThereWithItsOverride_UpdateAndRemoveFollowTheRecordedName()
    {
        var (game, store) = CreateGame("proxy-winmm", GraphicsApi.DirectX12);
        var first = CreatePackage("proxy-winmm-1");
        var second = CreatePackage("proxy-winmm-2") with { Version = "9.9.10" };
        File.AppendAllText(Path.Combine(second.RootDirectory, "OptiScaler.dll"), "v2");
        var provider = new SwitchingPackageProvider(first);
        var service = NewService(store, provider);
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");

        var installed = await service.InstallAsync(game, OptiScalerVariant.Nightly, nightlyBuild: null, proxyName: "WINMM.dll");

        Assert.True(installed.Success, installed.Message);
        Assert.EndsWith("winmm.dll.", installed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(winmm));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("native,builtin", Override(game, "winmm"));
        Assert.Null(Override(game, "dxgi"));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(record));
        Assert.Equal("winmm", record.WineOverride!.DllName);

        provider.Package = second;
        var updated = await service.UpdateAsync(game);
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(Sha256(Path.Combine(second.RootDirectory, "OptiScaler.dll")), Sha256(winmm));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));

        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(winmm));
        Assert.Null(Override(game, "winmm"));
    }

    [Fact]
    public async Task Install_UnderWinmm_LeavesRhiReShadeAtDxgiAsItIs()
    {
        var (game, store) = CreateGame("proxy-winmm-reshade", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var reShadeBytes = File.ReadAllBytes(dxgi);
        var service = NewService(store, CreatePackage("proxy-winmm-reshade-package"));

        var installed = await service.InstallAsync(game, OptiScalerVariant.Stable, null, "winmm.dll");

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.DoesNotContain("LoadReshade=true", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(dxgi), Path.GetFullPath(Assert.Single(store.Find(game, ManagedComponentKind.ReShade)!.Files).Path));
        Assert.Equal("native,builtin", Override(game, "dxgi"));
        Assert.Equal("native,builtin", Override(game, "winmm"));

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.Equal("native,builtin", Override(game, "dxgi"));
        Assert.Null(Override(game, "winmm"));
    }

    [Fact]
    public async Task Install_UnderTheNameRhiReShadeHas_ChainsReShade_AndRemovalMovesItBackToThatName()
    {
        var (game, store) = CreateGame("proxy-winmm-chain", GraphicsApi.DirectX12);
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        WriteSignedTextFile(winmm, "ReShade crosire reshade.me");
        var reShadeBytes = File.ReadAllBytes(winmm);
        var reShadeOverride = new WineDllOverrideService().EnsureNativeBuiltin(game.PrefixPath!, "winmm.dll", "Game.exe");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "9.9.9",
            WineOverride = reShadeOverride,
            Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        var service = NewService(store, CreatePackage("proxy-winmm-chain-package"));

        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "winmm.dll")).Success);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(winmm));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(reShadeBytes, File.ReadAllBytes(winmm));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "ReShade64.dll")));
        Assert.Equal(Path.GetFullPath(winmm), Path.GetFullPath(Assert.Single(store.Find(game, ManagedComponentKind.ReShade)!.Files).Path));
        Assert.Equal("native,builtin", Override(game, "winmm"));
    }

    [Fact]
    public async Task Install_UnderANameNotOffered_IsRefusedBeforeThePackageIsRequested()
    {
        var (game, store) = CreateGame("proxy-bad-name", GraphicsApi.DirectX12);
        var provider = new FakePackageProvider(CreatePackage("proxy-bad-name-package"));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, OptiScalerVariant.Stable, null, "d3d9.dll");

        Assert.False(result.Success);
        Assert.Equal(OptiScalerDeploymentService.ProxyNameNotOfferedPrefix + "d3d9.dll", result.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_UnderANameAnotherRhiComponentHolds_IsRefusedNamingIt_AndChangesNothing()
    {
        var (game, store) = CreateGame("proxy-held", GraphicsApi.DirectX12);
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllText(winmm, "MZ ultimate asi loader");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = game.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        var userReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        var service = NewService(store, CreatePackage("proxy-held-package"));

        var result = await service.InstallAsync(game, OptiScalerVariant.Stable, null, "winmm.dll");

        Assert.False(result.Success);
        Assert.Contains(OptiScalerDeploymentService.NameHeldByRhiComponentPrefix + "winmm.dll (", result.Message);
        Assert.Equal("MZ ultimate asi loader", File.ReadAllText(winmm));
        Assert.Equal(userReg, File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    // Review minor 1: an OptiScaler RHI did not install under ANY supported name (the user's own, usually dxgi.dll)
    // refuses every install, whichever name it would take — two OptiScaler proxies would both load. Refused before the
    // package is requested, naming the file, with nothing changed.
    [Theory]
    [InlineData("dxgi.dll", "winmm.dll")]
    [InlineData("DXGI.dll", "winmm.dll")]
    [InlineData("winmm.dll", "dxgi.dll")]
    [InlineData("d3d11.dll", "dxgi.dll")]
    [InlineData("d3d12.dll", "winmm.dll")]
    [InlineData("dbghelp.dll", "dxgi.dll")]
    [InlineData("version.dll", "dxgi.dll")]
    [InlineData("wininet.dll", "dxgi.dll")]
    [InlineData("winhttp.dll", "dxgi.dll")]
    public async Task Install_WithAnOptiScalerRhiDidNotInstallUnderAnySupportedName_IsRefusedNamingIt_BeforeTheDownload(string existing, string chosen)
    {
        var (game, store) = CreateGame("proxy-existing-" + Guid.NewGuid().ToString("N"), GraphicsApi.DirectX12);
        var users = Path.Combine(game.BinaryDirectory!, existing);
        WriteSignedTextFile(users, "optiscaler/OptiScaler the user's own copy");
        var usersBytes = File.ReadAllBytes(users);
        var userReg = File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg"));
        var provider = new FakePackageProvider(CreatePackage("proxy-existing-package-" + Guid.NewGuid().ToString("N")));
        var service = NewService(store, provider);

        var result = await service.InstallAsync(game, OptiScalerVariant.Nightly, null, chosen);

        Assert.False(result.Success);
        Assert.Equal(OptiScalerDeploymentService.OptiScalerAlreadyPresentPrefix + existing, result.Message);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(usersBytes, File.ReadAllBytes(users));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, chosen)));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Equal(userReg, File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.Null(store.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task Install_BesideAnotherProxyThatIsNotOptiScaler_StillInstalls()
    {
        var (game, store) = CreateGame("proxy-existing-other", GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "version.dll"), "MZ some other proxy");
        var service = NewService(store, CreatePackage("proxy-existing-other-package"));

        var installed = await service.InstallAsync(game, OptiScalerVariant.Stable, null, "winmm.dll");

        Assert.True(installed.Success, installed.Message);
    }

    [Fact]
    public void ComponentProbe_RecognisesOptiScalerUnderEveryNameTheServiceInstallsUnder()
        => Assert.Equal(OptiScalerDeploymentService.ProxyNames.Order(StringComparer.Ordinal),
            ComponentProbe.OptiScalerProxyNames.Order(StringComparer.Ordinal));

    [Fact]
    public void GetProxyName_RecordsBeforeTheNameExisted_AreDxgi()
    {
        var record = new LinuxDeploymentRecord { Component = ManagedComponentKind.OptiScaler };
        Assert.Equal("dxgi.dll", OptiScalerDeploymentService.GetProxyName(record));
        record.Metadata[OptiScalerDeploymentService.ProxyNameMetadataKey] = "../evil.dll";
        Assert.Equal("dxgi.dll", OptiScalerDeploymentService.GetProxyName(record));
    }
}
