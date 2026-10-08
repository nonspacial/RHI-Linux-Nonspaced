using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #24 (DEP-05): ReShade refuses install, update and removal while Wine owns the game's prefix, before any change.
public sealed partial class ReShadeChannelTests
{
    private ReShadeDeploymentService ServiceWithWineRunning(LinuxDeploymentRecordStore store, MutableHandler handler, string cache)
    {
        var http = new HttpClient(handler);
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store,
            new WineDllOverrideService(new WineRegistryEditor(_ => true)), new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")));
    }

    [Fact]
    public async Task InstallAndRemove_WhileWineRunsInThePrefix_AreRefusedBeforeAnyChange()
    {
        var (game, store) = CreateGame("wine-running");
        var cache = Path.Combine(_root, "wine-running-cache");
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var registryBefore = File.ReadAllText(userReg);

        var install = await ServiceWithWineRunning(store, new MutableHandler(), cache).InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(install.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, install.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal(registryBefore, File.ReadAllText(userReg));
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));

        var installed = await NewService(store, new MutableHandler(), cache).InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(installed.Success, installed.Message);
        var proxy = Path.Combine(game.BinaryDirectory!, "dxgi.dll");

        var remove = await ServiceWithWineRunning(store, new MutableHandler(), cache).RemoveAsync(game);

        Assert.False(remove.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, remove.Message);
        Assert.True(File.Exists(proxy));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task Remove_AfterTheUserChangedTheProxyOverride_KeepsTheirValue()
    {
        var (game, store) = CreateGame("user-override");
        var cache = Path.Combine(_root, "user-override-cache");
        var service = NewService(store, new MutableHandler(), cache);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        File.WriteAllText(userReg, WineDllOverrideService.SetOverride(File.ReadAllText(userReg), "dxgi", "builtin", "Game.exe"));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(userReg), "dxgi", "Game.exe"));
        Assert.Contains(WineDllOverrideService.KeptUserValueNotePrefix + "dxgi = 'builtin'.", remove.Message, StringComparison.Ordinal);
    }
}
