using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179 correction round: the independent review's probes (reviews/w7-review-shader-sets-probes.cs.txt), adopted, and the
/// tests for each finding's fix.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    // Review major 1: the ReShade profile (and the Shaders/Add-ons dialog) start from what the profile shows; for a game
    // without RHI's ReShade that used to be the record's packs (none), and an untouched Apply then saved "Select []".
    [Fact]
    public async Task Review_R1_FirstInstallFromProfile_UntouchedPacks_StaysGlobal()
    {
        var (game, store) = SetGame("r1");
        var cache = Path.Combine(_root, "r1-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r1");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var shown = managed.GetReShadeOptions(game); // what the profile panel shows for a game without ReShade

        var result = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = shown.ShaderPackIds });

        Assert.True(result.Success, result.Message);
        var choice = sets.ChoiceFor(game)!;
        Assert.True(choice.ShaderMode == ShaderAddonMode.Global, $"mode {choice.ShaderMode} [{string.Join(",", choice.ShaderPackIds)}]");
        Assert.True(File.Exists(PackFile(game)), "global set not deployed");
    }

    // Review major 1, second half: a Global game whose files lag (a batch skipped it while it ran) keeps Global on a
    // channel-only profile Apply, and that Apply brings its packs up to the global set.
    [Fact]
    public async Task Review_R1b_LaggingGlobalGame_ChannelOnlyApply_StaysGlobal()
    {
        var (game, store) = SetGame("r1b");
        var cache = Path.Combine(_root, "r1b-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r1b");
        var service = SetService(store, cache, sets);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]); // the batch never reached it
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;

        var profile = managed.GetReShadeOptions(game);
        var result = await managed.ApplyReShadeAsync(game, profile);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ShaderAddonMode.Global, sets.ChoiceFor(game)!.ShaderMode);
        Assert.True(File.Exists(PackFile(game)));
    }
}
