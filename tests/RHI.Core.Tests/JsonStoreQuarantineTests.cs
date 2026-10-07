using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// DET-18 (Refs #57): every JSON store keeps an unreadable file. It is copied aside with its
/// original bytes, user data is never saved over, and the user is told once.
/// </summary>
public sealed class JsonStoreQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-json-store-" + Guid.NewGuid().ToString("N"));

    public JsonStoreQuarantineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Garbage(string tag) => Encoding.UTF8.GetBytes("{ \"truncated\": [1, 2, " + tag + " é");

    private static string[] Quarantined(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");

    private static DetectedGame Game(string root) => new()
    {
        Name = "Store Game",
        InstallPath = root,
        BinaryDirectory = root,
        Source = "Steam",
        SteamAppId = 4242,
        Runner = "Proton",
        ForcedArchitecture = PeArchitecture.X64
    };

    // ---- Shared helper ---------------------------------------------------------------------

    [Fact]
    public void Helper_MissingOrBlankFile_IsMissingAndWritable()
    {
        var path = Path.Combine(_root, "blank.json");
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        Assert.Equal(JsonStoreLoadStatus.Missing, store.Load().Status);

        File.WriteAllText(path, "  \n");
        Assert.Equal(JsonStoreLoadStatus.Missing, store.Load().Status);
        store.Save(new() { ["a"] = "b" });

        Assert.Equal("b", store.Load().Value!["a"]);
        Assert.Empty(Quarantined(path));
    }

    [Fact]
    public void Helper_GarbageFile_IsCopiedAsideOnceAndNeverOverwritten()
    {
        var path = Path.Combine(_root, "garbage.json");
        var original = Garbage("helper");
        File.WriteAllBytes(path, original);
        var notices = new List<JsonStoreQuarantineNotice>();
        using var _ = JsonStoreQuarantine.Listen(n => { if (n.FilePath == Path.GetFullPath(path)) lock (notices) notices.Add(n); });
        var store = new JsonFileStore<Dictionary<string, string>>(path);

        var first = store.Load();
        var second = new JsonFileStore<Dictionary<string, string>>(path).Load();
        var refused = Assert.Throws<JsonStoreUnavailableException>(() => store.Save(new() { ["a"] = "b" }));

        Assert.Equal(JsonStoreLoadStatus.Unreadable, first.Status);
        Assert.Equal(first.QuarantinePath, second.QuarantinePath);
        Assert.Equal(first.QuarantinePath, refused.QuarantinePath);
        var copy = Assert.Single(Quarantined(path));
        Assert.Equal(copy, first.QuarantinePath);
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(original, File.ReadAllBytes(path));
        var notice = Assert.Single(notices);
        Assert.True(notice.WritesRefused);
        Assert.Equal(copy, notice.QuarantinePath);
    }

    [Fact]
    public void Helper_FixedFile_IsWritableAgain_AndANewCorruptionIsANewEvent()
    {
        var path = Path.Combine(_root, "fixed.json");
        File.WriteAllBytes(path, Garbage("one"));
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        Assert.True(store.Load().IsUnreadable);

        File.WriteAllText(path, "{\"k\":\"v\"}");
        store.Save(new() { ["k"] = "w" });
        Assert.Equal("w", store.Load().Value!["k"]);

        File.WriteAllBytes(path, Garbage("two-longer"));
        Assert.True(store.Load().IsUnreadable);
        Assert.Equal(2, Quarantined(path).Length);
    }

    [Fact]
    public void Helper_NullLiteral_IsUnreadable()
    {
        var path = Path.Combine(_root, "null.json");
        File.WriteAllText(path, "null");
        var store = new JsonFileStore<Dictionary<string, string>>(path);

        Assert.True(store.Load().IsUnreadable);
        Assert.Throws<JsonStoreUnavailableException>(() => store.Save([]));
    }

    [Fact]
    public void Helper_CacheKind_CopiesAsideThenRebuilds()
    {
        var path = Path.Combine(_root, "cache.json");
        var original = Garbage("cache");
        File.WriteAllBytes(path, original);
        var store = new JsonFileStore<Dictionary<string, string>>(path, kind: JsonStoreKind.Cache);

        Assert.True(store.Load().IsUnreadable);
        store.Save(new() { ["x"] = "y" });

        Assert.Equal("y", store.Load().Value!["x"]);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
    }

    [Fact]
    public void Helper_Save_LeavesNoTempFiles()
    {
        var path = Path.Combine(_root, "atomic", "store.json");
        var store = new JsonFileStore<Dictionary<string, string>>(path);
        for (var i = 0; i < 3; i++) store.Save(new() { ["i"] = i.ToString() });

        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Equal("2", store.Load().Value!["i"]);
    }

    [Fact]
    public void Helper_StatusTextNamesTheFileAndItsCopy()
    {
        var text = MainWindowViewModel.StoreQuarantinedText(
            new JsonStoreQuarantineNotice("/x/game-library.json", "/x/game-library.json.corrupt-20260101-000000", WritesRefused: true));
        var refused = MainWindowViewModel.StoreSaveFailedText(new JsonStoreUnavailableException("/x/settings.json", "/x/settings.json.corrupt-1"));

        Assert.Contains("game-library.json", text);
        Assert.Contains("/x/game-library.json.corrupt-20260101-000000", text);
        Assert.Contains("settings.json", refused);
        Assert.Contains("/x/settings.json.corrupt-1", refused);
    }

    // ---- Every store -------------------------------------------------------------------------

    public static TheoryData<string> Stores => new()
    {
        "game-library", "settings", "api-overrides", "optiscaler-variants", "luma-mode", "neural-pins",
        "dlssg2030", "rtx40-mfg", "dlss-enabler", "shortfuse", "ui-settings", "addon-manager",
        "shader-manifest", "shader-pack-settings"
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Store_GarbageFile_LoadThenSave_KeepsOriginalBytesAndRefusesTheWrite(string store)
    {
        var dir = Path.Combine(_root, store);
        Directory.CreateDirectory(dir);
        var game = Game(dir);
        var (path, read, write) = Scenario(store, dir, game);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Garbage(store);
        File.WriteAllBytes(path, original);

        read();
        var saved = await write();

        Assert.False(saved);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
    }

    [Fact]
    public async Task PcgwCache_GarbageFile_IsCopiedAsideAndRebuilt()
    {
        var path = Path.Combine(_root, "pcgw", "pcgw_graphics_api_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Garbage("pcgw");
        File.WriteAllBytes(path, original);
        const string html = """<table id="table-api"><tr><th scope="row"><abbr title="Windows">Direct3D 11</abbr></th><td>11</td></tr></table>""";
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));

        await new PcGamingWikiGraphicsApiService(http, path).FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");

        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Quarantined(path))));
        Assert.Contains("pcgamingwiki.com", File.ReadAllText(path));
    }

    private (string Path, Action Read, Func<Task<bool>> Write) Scenario(string store, string dir, DetectedGame game)
    {
        static Task<bool> Refused(Action save)
        {
            try { save(); return Task.FromResult(true); }
            catch (JsonStoreUnavailableException) { return Task.FromResult(false); }
        }

        switch (store)
        {
            case "game-library":
            {
                var s = new LinuxGameLibraryStore(Path.Combine(dir, "game-library.json"));
                return (s.LibraryPath, () => Assert.Empty(s.Load().ManualGames), () => Refused(() => s.Save(s.Load())));
            }
            case "settings":
            {
                var s = new LinuxSettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "cache"), Path.Combine(dir, "data"));
                return (s.SettingsPath, () => s.Load(), () => Refused(() => s.Save(s.Load())));
            }
            case "api-overrides":
            {
                var path = Path.Combine(dir, "game-api-overrides.json");
                var s = new GraphicsApiOverrideStore(path);
                return (path, () => Assert.Null(s.Get(game)), () => Refused(() => s.Set(game, GraphicsApi.Vulkan)));
            }
            case "optiscaler-variants":
            {
                var path = Path.Combine(dir, "optiscaler-variants.json");
                var s = new OptiScalerVariantPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, OptiScalerVariant.Nightly)));
            }
            case "luma-mode":
            {
                var path = Path.Combine(dir, "luma-mode.json");
                var s = new LumaModePreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, LumaModePreference.Enabled)));
            }
            case "neural-pins":
            {
                var path = Path.Combine(dir, "neural-addon-version-pins.json");
                var s = new NeuralAddonVersionPreferenceStore(path);
                return (path, () => s.Get(game, ManagedComponentKind.ShortFuse), () => Refused(() => s.Set(game, ManagedComponentKind.ShortFuse, "v1.0")));
            }
            case "dlssg2030":
            {
                var path = Path.Combine(dir, "dlssg2030-preferences.json");
                var s = new Dlssg2030PreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new Dlssg2030Preferences())));
            }
            case "rtx40-mfg":
            {
                var path = Path.Combine(dir, "rtx40-mfg-standalone-preferences.json");
                var s = new Rtx40MfgPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new Rtx40MfgPreferences())));
            }
            case "dlss-enabler":
            {
                var path = Path.Combine(dir, "dlss-enabler-standalone-preferences.json");
                var s = new StandaloneDlssEnablerPreferenceStore(path);
                return (path, () => s.Get(game), () => Refused(() => s.Set(game, new StandaloneDlssEnablerPreferences())));
            }
            case "shortfuse":
            {
                var path = Path.Combine(dir, "shortfuse-settings.json");
                var s = new ShortFuseSettingsStore(path);
                return (path, () => Assert.False(s.IsAutoConfigEnabled(game)), () => Refused(() => s.SetAutoConfigEnabled(game, true)));
            }
            case "ui-settings":
            {
                var s = new DetailSectionLayoutStore(Path.Combine(dir, "ui-settings.json"));
                return (s.Path, () => s.Load(), () => Refused(() => s.Save(s.Load())));
            }
            case "addon-manager":
            {
                // The Addon Manager refuses before touching the game: no add-on file is deployed.
                File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "ReShade by crosire");
                var custom = Path.Combine(dir, "custom");
                Directory.CreateDirectory(custom);
                File.WriteAllBytes(Path.Combine(custom, "MyLocal.addon64"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 1));
                var statePath = Path.Combine(dir, "addon-manager.json");
                var http = new HttpClient(new RouteHandler(req => req.RequestUri?.AbsoluteUri == ReShadeAddonManagerService.AddonsIniUrl
                    ? Text("[01]\nPackageName=Generic\nDownloadUrl64=https://example.test/generic.addon64\n")
                    : req.RequestUri?.AbsoluteUri == "https://manifest.test/manifest.json"
                        ? Text("{\"version\":2}")
                        : new HttpResponseMessage(HttpStatusCode.NotFound)));
                var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(dir, "manifest-cache.json"));
                var s = new ReShadeAddonManagerService(http, manifest, Path.Combine(dir, "addon-cache"), custom, statePath);
                return (statePath, () => { }, async () =>
                {
                    var result = await s.ApplySelectionAsync(game, ["custom-MyLocal"]);
                    Assert.Empty(Directory.EnumerateFiles(dir, "*.addon64", SearchOption.TopDirectoryOnly));
                    return result.Success;
                });
            }
            case "shader-manifest":
            {
                var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ShaderZip()) }));
                var s = new ReShadeShaderPackService(http, Path.Combine(dir, "shader-cache"), customRoot: Path.Combine(dir, "custom-shaders"));
                var path = Path.Combine(dir, "reshade-shaders", "RHI", ".rhi-manifest.json");
                return (path, () => { }, async () =>
                {
                    try { await s.SyncAsync(dir, ["CrosireMaster"]); return true; }
                    catch (JsonStoreUnavailableException) { }
                    try { s.Cleanup(dir); return true; }
                    catch (JsonStoreUnavailableException) { return false; }
                });
            }
            case "shader-pack-settings":
            {
                var cache = Path.Combine(dir, "shader-cache");
                var s = new ReShadeShaderPackService(new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                    cache, customRoot: Path.Combine(dir, "custom-shaders"));
                return (Path.Combine(cache, ".pack-settings.json"), () => Assert.Empty(s.GetExcludedFiles("CrosireMaster")),
                    () => Refused(() => s.SetExcludedFiles("CrosireMaster", ["Test.fx"])));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(store), store, null);
        }
    }

    private static HttpResponseMessage Text(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };

    private static byte[] ShaderZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("reshade-shaders-master/Shaders/Test.fx").Open());
            writer.Write("float4 main() : SV_Target { return 1; }");
        }
        return memory.ToArray();
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
