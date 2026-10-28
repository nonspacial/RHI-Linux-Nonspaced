using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12 follow-up (re-review shaders M1, N2): when <c>reshade-shaders/RHI</c> or <c>reshade-shaders</c> is a link, the
/// empty-folder clean-up never reaches into the link's target (the user's own place), neither on ReShade's removal nor on
/// an Off sync; a changed core header in <c>_Core</c> is refused with a line that names only the way out that works there.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    [Theory]
    [InlineData("RHI", "removal")]
    [InlineData("RHI", "off")]
    [InlineData("reshade-shaders", "removal")]
    [InlineData("reshade-shaders", "off")]
    public async Task Prune_LinkedRhiRootOrShaderFolder_NeverRemovesAnEmptyFolderInTheLinksTarget(string linked, string how)
    {
        var name = "followup-link-" + linked + "-" + how;
        var fx = await TwoPackGameAsync(name);
        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, [])).Success);
        Assert.False(Directory.Exists(RhiDir(fx.Game)));
        var shaders = Path.Combine(fx.Game.BinaryDirectory!, "reshade-shaders");
        var elsewhere = Path.Combine(_root, name, "users-shader-library");
        // The user's own library: an empty folder named after a catalogue pack, beside a file of theirs.
        var inTarget = linked == "RHI" ? elsewhere : Path.Combine(elsewhere, "RHI");
        Directory.CreateDirectory(Path.Combine(inTarget, "Prod80", "Shaders"));
        File.WriteAllText(Path.Combine(inTarget, "keep.txt"), "the user's");
        if (linked == "RHI") Directory.CreateSymbolicLink(RhiDir(fx.Game), elsewhere);
        else
        {
            Directory.Delete(shaders, recursive: true);
            Directory.CreateSymbolicLink(shaders, elsewhere);
        }

        var result = how == "removal" ? await fx.Service.RemoveAsync(fx.Game) : await fx.Service.SyncShaderPacksAsync(fx.Game, []);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(Path.Combine(inTarget, "Prod80", "Shaders")), "an empty folder in the link's target was removed");
        Assert.Equal("the user's", File.ReadAllText(Path.Combine(inTarget, "keep.txt")));
        Assert.NotNull(new DirectoryInfo(linked == "RHI" ? RhiDir(fx.Game) : shaders).LinkTarget); // the link stays
    }

    // Re-review N2: _Core goes with every pack, so "untick its pack" cannot clear a changed header; the line says move or
    // delete it only, path last, EN + PT-BR. The user's header is kept.
    [Fact]
    public async Task Refusal_UserChangedCoreHeader_SaysMoveOrDeleteOnly_PathLast_InBothLanguages()
    {
        var fx = await TwoPackGameAsync("followup-core-header");
        var header = RhiDir(fx.Game, "_Core", "Shaders", "ReShade.fxh");
        Assert.True(File.Exists(header));
        File.WriteAllText(header, "// the user's header tweak");

        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.False(sync.Success);
        Assert.Equal(ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.ProtectedCoreHeaderPrefix + header, sync.Message);
        Assert.DoesNotContain("untick", sync.Message, StringComparison.Ordinal);
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", sync.Message);
        Assert.DoesNotContain("desmarque", pt, StringComparison.Ordinal);
        Assert.Contains("Mova ou apague o arquivo e tente de novo: ", pt, StringComparison.Ordinal);
        Assert.EndsWith(header, pt, StringComparison.Ordinal);
        Assert.Equal("// the user's header tweak", File.ReadAllText(header));
    }

    // A pack file the user changed still reads the pack line (its pack can be unticked).
    [Fact]
    public async Task Refusal_UserChangedPackFile_KeepsThePackLine()
    {
        var fx = await TwoPackGameAsync("followup-pack-line");
        File.WriteAllText(PackFile(fx.Game), "// the user's own tweak");

        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.Equal(ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.ProtectedShaderFilePrefix + PackFile(fx.Game), sync.Message);
    }
}

/// <summary>
/// w12 follow-up (re-review shaders N3): two catalogue pack ids that give the same folder name would place the same file;
/// that is RHI's own clash, refused with its own line (never "changed or added outside RHI"), before anything is written.
/// </summary>
public sealed class ShaderPackFolderClashTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-pack-clash-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task TwoPackIdsWithOneFolderName_AreRhisClash_NeverTheUsersFile_AndNothingIsWritten()
    {
        string Staged(string name)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "staged", name, "Shaders")).FullName;
            File.WriteAllText(Path.Combine(dir, "Same.fx"), "// " + name);
            return Path.GetDirectoryName(dir)!;
        }
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var root = Path.Combine(game, "reshade-shaders", "RHI");
        var service = new ReShadeShaderPackService(new HttpClient(), Path.Combine(_root, "cache"), customRoot: Path.Combine(_root, "custom"))
        {
            // w12 final review nit 4: the clash names both packs as Shaders/Add-ons lists them.
            CatalogForTesting =
            [
                new("Pack/One", "Pack One (slash build)", "", ReShadeShaderPackSourceKind.DirectUrl, "https://github.com/rhi-tests/a.zip"),
                new("Pack_One", "Pack One (underscore build)", "", ReShadeShaderPackSourceKind.DirectUrl, "https://github.com/rhi-tests/b.zip"),
            ]
        };
        var prepared = new ReShadeShaderPackService.PreparedShaderSync(root, Path.Combine(root, ".rhi-manifest.json"),
            new ReShadeShaderPackService.Manifest(), ["Pack/One", "Pack_One"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Pack/One"] = Staged("a"), ["Pack_One"] = Staged("b") },
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["Pack/One"] = [], ["Pack_One"] = [] },
            NeedsCoreHeaders: false, CoreHeadersStaged: null);

        var refusal = await Assert.ThrowsAsync<IOException>(() => service.ApplySyncAsync(prepared, null));

        Assert.Equal(ReShadeShaderPackService.PackFolderClashPrefix + "Pack One (slash build), Pack One (underscore build) — "
                     + Path.Combine(root, "Pack_One", "Shaders", "Same.fx"), refusal.Message);
        Assert.DoesNotContain("outside RHI", refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", refusal.Message);
        Assert.StartsWith("Dois pacotes de shaders do catálogo do RHI", pt, StringComparison.Ordinal);
        Assert.Contains("Pack One (slash build), Pack One (underscore build) — ", pt, StringComparison.Ordinal);
    }

    // Nit 4: a pack whose folder is _Core clashes with ReShade's core headers; the headers' placer is named "_Core".
    [Fact]
    public async Task APackPlacingACoreHeader_NamesThePackAndCore()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game-core")).FullName;
        var root = Path.Combine(game, "reshade-shaders", "RHI");
        var staged = Directory.CreateDirectory(Path.Combine(_root, "staged-core", "pack", "Shaders")).FullName;
        File.WriteAllText(Path.Combine(staged, "ReShade.fxh"), "// pack copy");
        var core = Directory.CreateDirectory(Path.Combine(_root, "staged-core", "core", "Shaders")).FullName;
        foreach (var header in new[] { "ReShade.fxh", "ReShadeUI.fxh" }) File.WriteAllText(Path.Combine(core, header), "// " + header);
        var service = new ReShadeShaderPackService(new HttpClient(), Path.Combine(_root, "cache"), customRoot: Path.Combine(_root, "custom"))
        {
            CatalogForTesting = [new("_Core", "Core lookalike", "", ReShadeShaderPackSourceKind.DirectUrl, "https://github.com/rhi-tests/c.zip")]
        };
        var prepared = new ReShadeShaderPackService.PreparedShaderSync(root, Path.Combine(root, ".rhi-manifest.json"),
            new ReShadeShaderPackService.Manifest(), ["_Core"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["_Core"] = Path.GetDirectoryName(staged)! },
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) { ["_Core"] = [] },
            NeedsCoreHeaders: true, CoreHeadersStaged: Path.GetDirectoryName(core)!);

        var refusal = await Assert.ThrowsAsync<IOException>(() => service.ApplySyncAsync(prepared, null));

        Assert.Equal(ReShadeShaderPackService.PackFolderClashPrefix + "Core lookalike, _Core — "
                     + Path.Combine(root, "_Core", "Shaders", "ReShade.fxh"), refusal.Message);
    }
}
