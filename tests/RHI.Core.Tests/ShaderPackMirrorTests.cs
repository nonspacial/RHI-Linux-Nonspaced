using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #241 (upstream f32704c, <c>ShaderPackService.Mirror</c>): a sync mirrors RHI's shader folder file by file
/// — unchanged files (same size and SHA-256 as the staged copy) are not touched and not journaled, changed ones are
/// replaced, new ones placed, ones no longer wanted deleted; the manifest lists exactly what is deployed. Before, every
/// sync deleted every file and copied everything back (a running game lost its shaders on every Apply / Update All).
/// </summary>
public sealed class ShaderPackMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-w12-mirror-" + Guid.NewGuid().ToString("N"));

    public ShaderPackMirrorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Fx(ReShadeShaderPackService Service, string Game, string Cache, ShaderPackCoreHeadersTests.PackServer Server);

    private Fx Fixture(string name)
    {
        var server = new ShaderPackCoreHeadersTests.PackServer();
        var cache = Path.Combine(_root, name + "-cache");
        var service = new ReShadeShaderPackService(new HttpClient(server), cache, customRoot: Path.Combine(_root, name + "-custom"));
        return new(service, Directory.CreateDirectory(Path.Combine(_root, name, "game")).FullName, cache, server);
    }

    private static string Rhi(string game, params string[] parts) => Path.Combine([game, "reshade-shaders", "RHI", .. parts]);

    /// <summary>Every file and folder under RHI's folder with its inode and modification time: equal = nothing was written.</summary>
    internal static IReadOnlyDictionary<string, string> Fingerprint(string rhiRoot)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(rhiRoot).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(new DirectoryInfo(rhiRoot)))
            map[Path.GetRelativePath(rhiRoot, entry.FullName)] = $"{FileIdentity.TryGet(entry.FullName)?.Inode} {entry.LastWriteTimeUtc.Ticks}";
        return map;
    }

    private static IReadOnlyList<string> ManifestPaths(string game)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Rhi(game, ".rhi-manifest.json")));
        return doc.RootElement.GetProperty("Files").EnumerateArray().Select(file => file.GetProperty("RelativePath").GetString()!).ToArray();
    }

    private static IReadOnlyList<string> DeployedFiles(string game)
        => Directory.EnumerateFiles(Rhi(game), "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Rhi(game), file))
            .Where(file => file != ".rhi-manifest.json").Order(StringComparer.Ordinal).ToArray();

    private static async Task<(ReShadeShaderSyncResult Result, int Tracked)> JournaledSyncAsync(Fx fx, string journalDir, params string[] packs)
    {
        var journal = new DeploymentJournal(journalDir);
        var prepared = await fx.Service.PrepareSyncAsync(fx.Game, packs);
        await using var operation = await journal.BeginAsync("mirror test");
        var result = await fx.Service.ApplySyncAsync(prepared, operation);
        var tracked = operation.Files.Files.Count;
        await operation.CommitAsync();
        return (result, tracked);
    }

    // Prove-first (red on 8f15e44): the second identical sync deletes and re-copies every file.
    [Fact]
    public async Task SecondIdenticalSync_WritesNothing_AndJournalsNothing()
    {
        var fx = Fixture("same");
        var first = await JournaledSyncAsync(fx, Path.Combine(_root, "same-journal"), "Prod80", "CrosireMaster");
        Assert.True(first.Tracked > 0);
        var before = Fingerprint(Rhi(fx.Game));

        var second = await JournaledSyncAsync(fx, Path.Combine(_root, "same-journal"), "Prod80", "CrosireMaster");

        Assert.Equal(0, second.Tracked);
        Assert.Equal(before, Fingerprint(Rhi(fx.Game)));
        Assert.Equal(first.Result.EffectSearchPaths, second.Result.EffectSearchPaths);
        Assert.Equal(first.Result.TextureSearchPaths, second.Result.TextureSearchPaths);
    }

    [Fact]
    public async Task ChangedAddedAndRemovedFiles_AreMirrored_UnchangedOnesKeptInPlace_ManifestListsExactlyTheFolder()
    {
        var fx = Fixture("mirror");
        const string a = "float4 PS() : SV_Target { return 1; } // AAAA";
        const string b = "float4 PS() : SV_Target { return 0; } // BBBB"; // same length: size alone cannot tell them apart
        Assert.Equal(a.Length, b.Length);
        fx.Server.Packs["prod80"] = () => ShaderPackCoreHeadersTests.PackServer.Zip(
            ("p-master/Shaders/Effect.fx", a), ("p-master/Shaders/Old.fx", "old effect " + new string('o', 40)),
            ("p-master/Textures/Lut.png", new string('t', 900)));
        await fx.Service.SyncAsync(fx.Game, ["Prod80"]);
        var before = Fingerprint(Rhi(fx.Game));

        fx.Server.Packs["prod80"] = () => ShaderPackCoreHeadersTests.PackServer.Zip(
            ("p-master/Shaders/Effect.fx", b), ("p-master/Shaders/Sub/New.fx", "new effect " + new string('n', 40)),
            ("p-master/Textures/Lut.png", new string('t', 900)));
        Directory.Delete(Path.Combine(fx.Cache, "Prod80"), true); // the pack moved on upstream: staged again
        await fx.Service.SyncAsync(fx.Game, ["Prod80"]);
        var after = Fingerprint(Rhi(fx.Game));

        Assert.Equal(b, File.ReadAllText(Rhi(fx.Game, "Prod80", "Shaders", "Effect.fx")));
        Assert.True(File.Exists(Rhi(fx.Game, "Prod80", "Shaders", "Sub", "New.fx")));
        Assert.False(File.Exists(Rhi(fx.Game, "Prod80", "Shaders", "Old.fx")));
        foreach (var unchanged in new[] { "Prod80/Textures/Lut.png", "_Core/Shaders/ReShade.fxh", "_Core/Shaders/ReShadeUI.fxh" })
            Assert.Equal(before[unchanged], after[unchanged]);
        Assert.Equal(DeployedFiles(fx.Game), ManifestPaths(fx.Game).Order(StringComparer.Ordinal));
    }

    // Headers that cannot be staged this time (offline, cache gone) keep the copy RHI placed before, still owned.
    [Fact]
    public async Task HeadersUnavailableNow_KeepTheCopyAPreviousSyncPlaced()
    {
        var fx = Fixture("keep-headers");
        await fx.Service.SyncAsync(fx.Game, ["Prod80"]);
        var header = Rhi(fx.Game, "_Core", "Shaders", "ReShade.fxh");
        var identity = FileIdentity.TryGet(header);
        fx.Server.HeadersMissing = true;
        Directory.Delete(Path.Combine(fx.Cache, "ReShadeCoreHeaders"), true);

        var result = await fx.Service.SyncAsync(fx.Game, ["Prod80"]);

        Assert.Equal(identity, FileIdentity.TryGet(header));
        Assert.Contains(ShaderPackCoreHeadersTests.CoreSearchPath, result.EffectSearchPaths);
        Assert.Contains("_Core/Shaders/ReShade.fxh", ManifestPaths(fx.Game));

        var none = await fx.Service.SyncAsync(fx.Game, []);
        Assert.Empty(none.EffectSearchPaths);
        Assert.False(File.Exists(header)); // still RHI's: it goes with the last pack
    }

    // A pack folder the user turned into a link: a sync of that pack is refused before anything changes and never
    // writes through it.
    [Fact]
    public async Task SelectedPackFolderIsALink_IsRefusedBeforeAnyChange()
    {
        var fx = Fixture("link");
        fx.Server.Packs["prod80"] = () => ShaderPackCoreHeadersTests.PackServer.Zip(("p-master/Shaders/Effect.fx", "v1 " + new string('1', 900)));
        await fx.Service.SyncAsync(fx.Game, ["Prod80", "CrosireMaster"]);
        var linked = Rhi(fx.Game, "Prod80", "Shaders");
        var elsewhere = Path.Combine(_root, "link", "elsewhere");
        Directory.Move(linked, elsewhere);
        Directory.CreateSymbolicLink(linked, elsewhere);
        fx.Server.Packs["prod80"] = () => ShaderPackCoreHeadersTests.PackServer.Zip(("p-master/Shaders/Effect.fx", "v2 " + new string('2', 900)));
        Directory.Delete(Path.Combine(fx.Cache, "Prod80"), true);
        var before = CancelAtWritePhase.Snapshot(fx.Game, elsewhere);

        // CrosireMaster goes too: refused before its files are removed, not half-way.
        var error = await Assert.ThrowsAsync<IOException>(() => fx.Service.SyncAsync(fx.Game, ["Prod80"]));

        Assert.StartsWith(ReShadeShaderPackService.LinkedShaderFolderPrefix, error.Message, StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, fx.Game, elsewhere);
        Assert.Equal("v1 " + new string('1', 900), File.ReadAllText(Path.Combine(elsewhere, "Effect.fx")));
    }

    // The messages a refused sync shows (behind "The shader packs were not changed: ") read in Portuguese.
    public static TheoryData<string, string> RefusalTexts => new()
    {
        { ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.LinkedShaderFolderPrefix + "/g/reshade-shaders/RHI/Prod80/Shaders/a.fx", "is a link" },
        { ReShadeDeploymentService.ShaderSyncFailedPrefix + ReShadeShaderPackService.ProtectedShaderFilePrefix + "/g/reshade-shaders/RHI/Prod80/Shaders/a.fx", "externally owned" },
    };

    [Theory]
    [MemberData(nameof(RefusalTexts))]
    public void RefusalMessage_LeavesNoEnglishFixedText_InPortuguese(string message, string englishFixedText)
    {
        var pt = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", message);
        Assert.DoesNotContain(englishFixedText, pt, StringComparison.Ordinal);
        Assert.NotEqual(RHI.Desktop.Localization.UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        Assert.Equal(message, RHI.Desktop.Localization.UiLocalizationService.PresentFor("en-US", message));
    }
}
