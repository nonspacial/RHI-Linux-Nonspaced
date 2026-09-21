using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ShaderAddonManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shader-addon-test-" + Guid.NewGuid().ToString("N"));

    public ShaderAddonManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ShaderCatalog_MirrorsBundledUpstreamSourceCount()
    {
        Assert.Equal(49, ReShadeShaderPackCatalog.DefaultPacks.Count);
        Assert.Equal(49, ReShadeShaderPackCatalog.DefaultPacks.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(ReShadeShaderPackCatalog.DefaultPacks, x => x.Id == "Lilium" && x.Category == ReShadeShaderPackCategory.Essential);
        Assert.Contains(ReShadeShaderPackCatalog.DefaultPacks, x => x.Id == "RenoFXHDRToolkit" && x.Category == ReShadeShaderPackCategory.Recommended);
    }

    [Fact]
    public void ShaderCatalog_AzenCarriesUpstreamDependency()
    {
        var azen = Assert.Single(ReShadeShaderPackCatalog.DefaultPacks, x => x.Id == "Azen");
        Assert.Contains("SmolbbsoopShaders", azen.Requires!);

        var expanded = ReShadeShaderPackService.ExpandDependencies(["Azen"], ReShadeShaderPackCatalog.DefaultPacks);
        Assert.Equal(["SmolbbsoopShaders", "Azen"], expanded);
    }

    [Fact]
    public void ShaderService_CustomSentinelUsesInjectedXdgRoot()
    {
        var custom = Path.Combine(_root, "custom-shaders");
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, ""));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "cache"), customRoot: custom);

        var pack = Assert.Single(service.AvailablePacks, x => x.Id == ReShadeShaderPackCatalog.CustomPackId);
        Assert.True(pack.IsCustom);
        Assert.Contains(custom, pack.Description, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(custom, "Shaders")));
        Assert.True(Directory.Exists(Path.Combine(custom, "Textures")));
    }

    [Fact]
    public void ShaderManifestOverrides_CanDisableOverrideAndAdd()
    {
        var overrides = new Dictionary<string, UpstreamShaderPackDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["CrosireLegacy"] = new("CrosireLegacy", null, null, null, null, null, null, null, [], true),
            ["qUINT"] = new("qUINT", "qUINT Remote", null, "https://example.test/quint.zip", null, null, "remote description", "Recommended", [], false),
            ["RemotePack"] = new("RemotePack", "Remote Pack", "DirectUrl", "https://example.test/remote.zip", false, null, "OTA pack", "Extra", ["CrosireMaster"], false),
        };

        var merged = ReShadeShaderPackService.MergeManifestOverridesForTest(overrides);

        Assert.DoesNotContain(merged, x => x.Id == "CrosireLegacy");
        var quint = Assert.Single(merged, x => x.Id == "qUINT");
        Assert.Equal("qUINT Remote", quint.DisplayName);
        Assert.Equal(ReShadeShaderPackCategory.Recommended, quint.Category);
        var remote = Assert.Single(merged, x => x.Id == "RemotePack");
        Assert.Contains("CrosireMaster", remote.Requires!);
    }

    [Fact]
    public async Task ShaderSync_CustomPackDeploysLocalShadersWithoutNetwork()
    {
        var custom = Path.Combine(_root, "custom-deploy");
        Directory.CreateDirectory(Path.Combine(custom, "Shaders"));
        Directory.CreateDirectory(Path.Combine(custom, "Textures"));
        File.WriteAllText(Path.Combine(custom, "Shaders", "Local.fx"), "float4 main() : SV_Target { return 1; }");
        File.WriteAllText(Path.Combine(custom, "Textures", "Local.png"), new string('T', 1200));

        using var http = new HttpClient(new ThrowingHandler());
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "custom-cache"), customRoot: custom);
        var game = Path.Combine(_root, "custom-game");
        Directory.CreateDirectory(game);

        var result = await service.SyncAsync(game, [ReShadeShaderPackCatalog.CustomPackId]);

        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "__custom__", "Shaders", "Local.fx")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "__custom__", "Textures", "Local.png")));
        Assert.Contains(ReShadeShaderPackCatalog.CustomPackId, result.PackIds);
    }

    [Fact]
    public async Task ShaderSync_ExcludesKnownBrokenUpstreamShaderFiles()
    {
        using var http = new HttpClient(new StaticBytesHandler(CreateShaderZipWithBrokenFile()));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "excluded-cache"));
        var game = Path.Combine(_root, "excluded-game");
        Directory.CreateDirectory(game);

        await service.SyncAsync(game, ["CrosireMaster"]);

        var shaders = Path.Combine(game, "reshade-shaders", "RHI", "CrosireMaster", "Shaders");
        Assert.True(File.Exists(Path.Combine(shaders, "Good.fx")));
        Assert.False(File.Exists(Path.Combine(shaders, "BX_XIV_ChromakeyPlus.fx")));
    }

    [Fact]
    public void AddonsIniParser_MatchesUpstreamCommentAndExclusionRules()
    {
        var ini = """
                  [00]
                  PackageName=Excluded Zero

                  # [01]
                  # PackageName=Commented

                  [02]
                  PackageName=Working Addon
                  PackageDescription=Works
                  DownloadUrl64=https://example.test/working.addon64

                  ;[03]
                  ;PackageName=Also Commented

                  [21]
                  PackageName=Excluded Twenty One

                  [04]
                  PackageDescription=Missing name
                  """;

        var parsed = ReShadeAddonManagerService.ParseAddonsIni(ini);

        var addon = Assert.Single(parsed);
        Assert.Equal("02", addon.Id);
        Assert.Equal("Working Addon", addon.DisplayName);
        Assert.Equal("https://example.test/working.addon64", addon.DownloadUrl64);
    }

    [Fact]
    public void AddonManifestOverrides_CanDisableOverrideAndAdd()
    {
        var catalog = ReShadeAddonManagerService.ParseAddonsIni("""
            [01]
            PackageName=One
            DownloadUrl64=https://example.test/one.addon64

            [02]
            PackageName=Two
            DownloadUrl64=https://example.test/two.addon64
            """);
        var overrides = new Dictionary<string, UpstreamAddonPackDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["01"] = new("01", "One Remote", "updated", null, null, "https://example.test/one-new.addon64", null, null, null, null, false),
            ["02"] = new("02", "Two", null, null, null, null, null, null, null, null, true),
            ["custom-remote"] = new("custom-remote", "Remote Addon", "new", null, null, "https://example.test/remote.addon64", null, null, "remote", null, false),
        };

        ReShadeAddonManagerService.MergeManifest(catalog, overrides);

        Assert.DoesNotContain(catalog, x => x.Id == "02");
        Assert.Equal("One Remote", Assert.Single(catalog, x => x.Id == "01").DisplayName);
        Assert.Equal("remote", Assert.Single(catalog, x => x.Id == "custom-remote").DeployFileName);
    }

    [Fact]
    public void AddonManager_DedicatedComponentsAreProtectedFromDuplicateOwnership()
    {
        var upgrade = new ReShadeAddonDefinition("renodx-upgrade", "RenoDX Upgrade", "", null, null, "https://example.test/u.addon64", null, null, null, null);
        var generic = new ReShadeAddonDefinition("42", "Generic Addon", "", null, null, "https://example.test/g.addon64", null, null, null, null);

        Assert.True(ReShadeAddonManagerService.IsComponentManaged(upgrade));
        Assert.False(ReShadeAddonManagerService.IsComponentManaged(generic));
    }

    [Fact]
    public void AddonManager_DirectPayloadArchitectureMismatchIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            ReShadeAddonManagerService.ValidateDirectUrlArchitecture("https://example.test/mod.addon64", ".addon32"));
        Assert.Throws<InvalidDataException>(() =>
            ReShadeAddonManagerService.ValidateDirectUrlArchitecture("https://example.test/mod.addon32", ".addon64"));

        ReShadeAddonManagerService.ValidateDirectUrlArchitecture("https://example.test/mod.zip", ".addon64");
        ReShadeAddonManagerService.ValidateDirectUrlArchitecture("https://example.test/mod.addon64", ".addon64");
    }

    [Fact]
    public async Task AddonCatalog_IncludesLocalCustomArchitectureWithoutAdoptingIt()
    {
        var custom = Path.Combine(_root, "custom-addons");
        Directory.CreateDirectory(custom);
        File.WriteAllBytes(Path.Combine(custom, "MyLocal.addon64"), new byte[2048]);
        var cache = Path.Combine(_root, "addon-cache");
        var manifestCache = Path.Combine(_root, "manifest-cache.json");

        using var http = new HttpClient(new RouteHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri == ReShadeAddonManagerService.AddonsIniUrl)
                return Text(HttpStatusCode.OK, "[01]\nPackageName=Generic\nDownloadUrl64=https://example.test/generic.addon64\n");
            if (req.RequestUri?.AbsoluteUri == "https://manifest.test/manifest.json")
                return Text(HttpStatusCode.OK, "{\"version\":2}");
            return Text(HttpStatusCode.NotFound, "");
        }));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, manifestCache);
        var manager = new ReShadeAddonManagerService(http, manifest, cache, custom, Path.Combine(_root, "addon-state.json"));

        var catalog = await manager.RefreshCatalogItemsAsync();

        var local = Assert.Single(catalog, x => x.Id == "custom-MyLocal");
        Assert.True(local.IsCustom);
        Assert.False(local.Supports32Bit);
        Assert.True(local.Supports64Bit);
        Assert.False(local.ManagedByComponent);
    }

    [Fact]
    public async Task UpstreamManifest_ParsesShaderPackContract()
    {
        var json = """
            {
              "version": 2,
              "shaderPacks": {
                "RemoteShader": {
                  "displayName": "Remote Shader",
                  "kind": "DirectUrl",
                  "url": "https://example.test/shader.zip",
                  "isMinimum": false,
                  "description": "desc",
                  "category": "Recommended",
                  "requires": ["CrosireMaster"]
                },
                "DisabledShader": { "disabled": true }
              }
            }
            """;
        var cache = Path.Combine(_root, "shader-manifest.json");
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, json));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/shaders.json", null, cache);

        var packs = await manifest.FetchShaderPacksAsync();

        var remote = packs["RemoteShader"];
        Assert.Equal("DirectUrl", remote.Kind);
        Assert.Equal("Recommended", remote.Category);
        Assert.Contains("CrosireMaster", remote.Requires);
        Assert.True(packs["DisabledShader"].Disabled);
    }

    private static byte[] CreateShaderZipWithBrokenFile()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(archive, "repo/Shaders/Good.fx", new string('G', 1200));
            Write(archive, "repo/Shaders/BX_XIV_ChromakeyPlus.fx", new string('B', 1200));
        }
        return memory.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StaticHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Text(status, content));
    }

    private sealed class StaticBytesHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network should not be used for Custom Shaders.");
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
