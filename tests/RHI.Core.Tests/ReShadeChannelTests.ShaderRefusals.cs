using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders correction (review F3/F4): a sync refused for a file in RHI's shader folder says what happened, then the
/// way out, the path last — in English and Portuguese — and never changes the user's file or writes through a link.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    // Review F3: a pack file the user changed refuses the sync (never discarded); the message names the way out.
    [Fact]
    public async Task Refusal_UserChangedPackFile_SaysHowToGoOn_PathLast_InBothLanguages()
    {
        var fx = await TwoPackGameAsync("refuse-changed");
        File.WriteAllText(PackFile(fx.Game), "// the user's own tweak of Test.fx");
        var before = CancelAtWritePhase.Snapshot(fx.Game.BinaryDirectory!);

        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.False(sync.Success);
        Assert.Equal(ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.ProtectedShaderFilePrefix + PackFile(fx.Game), sync.Message);
        Assert.Contains("so RHI left it alone. Move or delete it, or untick its pack in Shaders/Add-ons, then try again: ", sync.Message, StringComparison.Ordinal);
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", sync.Message);
        Assert.Contains("desmarque o pacote dele em Shaders/Add-ons", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("Move or delete", pt, StringComparison.Ordinal);
        Assert.EndsWith(PackFile(fx.Game), pt, StringComparison.Ordinal);
        Assert.Equal("// the user's own tweak of Test.fx", File.ReadAllText(PackFile(fx.Game)));
        CancelAtWritePhase.AssertUnchanged(before, fx.Game.BinaryDirectory!);
    }

    // Review F4: a pack FILE replaced by a link is refused with a message that says a file can be the link too.
    [Fact]
    public async Task Refusal_LinkedFileInASelectedPack_SaysFolderOrFile_AndNeverWritesThroughIt()
    {
        var fx = await TwoPackGameAsync("refuse-linked-file");
        var target = Path.Combine(_root, "refuse-linked-file", "outside.fx");
        File.Copy(PackFile(fx.Game), target);
        File.Delete(PackFile(fx.Game));
        File.CreateSymbolicLink(PackFile(fx.Game), target);
        var extracted = Path.Combine(fx.Cache, "shader-packs", "CrosireMaster", "extracted");
        File.WriteAllText(Path.Combine(extracted, "Shaders", "Test.fx"), "float4 PS() : SV_Target { return 0; }");
        const string url = "https://github.com/crosire/reshade-shaders/archive/refs/heads/master.zip";
        CacheStamp.Write(extracted, url, url);
        var outside = File.ReadAllText(target);

        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.False(sync.Success);
        Assert.Equal(ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.LinkedShaderFolderPrefix + PackFile(fx.Game), sync.Message);
        Assert.Contains("A folder or file inside RHI's shader folder is a link", sync.Message, StringComparison.Ordinal);
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", sync.Message);
        Assert.Contains("Uma pasta ou um arquivo", pt, StringComparison.Ordinal);
        Assert.Equal(outside, File.ReadAllText(target));
        Assert.NotNull(new FileInfo(PackFile(fx.Game)).LinkTarget);
    }
}
