using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #239 through ReShade itself: every path that deploys packs (the shaders-only sync, ReShade's install with
/// packs, the Vulkan layer) puts ReShade's core headers in RHI's <c>_Core</c> folder and its search path in ReShade.ini, so
/// a pack whose effects include <c>ReShade.fxh</c> compiles; Off takes both out again.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    private static string RhiDir(DetectedGame game, params string[] parts)
        => Path.Combine([game.BinaryDirectory!, "reshade-shaders", "RHI", .. parts]);

    private static string[] EffectSearchPaths(DetectedGame game)
        => File.ReadAllLines(ReShadeIniManager.ResolveIniPath(game.BinaryDirectory!))
            .Where(line => line.StartsWith("EffectSearchPaths=", StringComparison.Ordinal))
            .SelectMany(line => line["EffectSearchPaths=".Length..].Split(','))
            .ToArray();

    [Fact]
    public async Task CoreHeaders_ShadersOnlySync_PutsTheHeadersOnReShadesSearchPath_AndOffTakesThemOut()
    {
        var fx = RrFixture("core-sync");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);

        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.True(sync.Success, sync.Message);
        Assert.True(File.Exists(RhiDir(fx.Game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.True(File.Exists(RhiDir(fx.Game, "_Core", "Shaders", "ReShadeUI.fxh")));
        Assert.Contains(ShaderPackCoreHeadersTests.CoreSearchPath, EffectSearchPaths(fx.Game));

        var off = await fx.Service.SyncShaderPacksAsync(fx.Game, []);
        Assert.True(off.Success, off.Message);
        Assert.False(File.Exists(RhiDir(fx.Game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.DoesNotContain(ShaderPackCoreHeadersTests.CoreSearchPath, EffectSearchPaths(fx.Game));
    }

    [Fact]
    public async Task CoreHeaders_ReShadeInstallWithPacks_DeploysTheHeadersOnItsSearchPath()
    {
        var fx = RrFixture("core-install");
        fx.Sets.SetShaderMode(fx.Game, ShaderAddonMode.Select, ["CrosireMaster"]);

        var install = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true, ShaderPackIds = ["CrosireMaster"] });

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(RhiDir(fx.Game, "_Core", "Shaders", "ReShade.fxh")));
        Assert.Contains(ShaderPackCoreHeadersTests.CoreSearchPath, EffectSearchPaths(fx.Game));
    }
}
