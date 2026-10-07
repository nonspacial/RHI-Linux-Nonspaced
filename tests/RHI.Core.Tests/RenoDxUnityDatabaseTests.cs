using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxUnityDatabaseTests : IDisposable
{
    private const string Fixture = """
    [
      { "Name": "Outer Wilds", "Status": "Done",
        "Upgrades": "`Upgrade_R11G11B10_FLOAT` `Output Size` `Use_Swapchain_Proxy` `1`", "Comments": null },
      { "Name": "Hollow Knight", "Status": "Done", "Upgrades": null, "Comments": "No upgrades" }
    ]
    """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-unity-db-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxUnityDatabaseTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void ParsedUpgrades_FollowsUpstreamTokenRulesAndDropsUnsafeKeys()
    {
        var entry = new RenoDxUnityDbEntry
        {
            Upgrades = "`Upgrade_R8G8B8A8_TYPELESS` `Output Ratio` `Upgrade_CopyDestinations` `Swapchain_Encoding` `0` " +
                       "`Upgrade_R10G10B10A2_TYPELESS` `Any Size` `Bad]\n[Key` `1` `Use_Swapchain_Proxy` `2`"
        };

        Assert.Equal(
            [
                new("Upgrade_R8G8B8A8_TYPELESS", "2"),
                new("Upgrade_CopyDestinations", ""),
                new("Swapchain_Encoding", "0"),
                new("Upgrade_R10G10B10A2_TYPELESS", "3"),
                new("Use_Swapchain_Proxy", "2")
            ],
            entry.ParsedUpgrades());
        Assert.Empty(new RenoDxUnityDbEntry { Upgrades = null }.ParsedUpgrades());
    }

    [Fact]
    public void Find_MatchesNameCaseInsensitivelyThenWithoutTrademarks()
    {
        var db = RenoDxUnityDatabase.Parse(Encoding.UTF8.GetBytes(Fixture))!;

        Assert.Equal(2, db.Count);
        Assert.Equal("Outer Wilds", db.Find("outer wilds")?.Name);
        Assert.Equal("Outer Wilds", db.Find("Outer Wilds™")?.Name);
        Assert.Null(db.Find("Outer Wilds 2"));
        Assert.Null(RenoDxUnityDatabase.Parse("[]"u8));
    }

    [Fact]
    public async Task Loader_PersistsWithEtagRevalidatesAndKeepsLastGoodCopy()
    {
        var cache = Path.Combine(_root, "unity.json");
        var seenEtags = new List<string?>();
        var liveBroken = false;
        using var http = new HttpClient(new Handler(request =>
        {
            var etag = request.Headers.TryGetValues("If-None-Match", out var values) ? values.Single() : null;
            seenEtags.Add(etag);
            if (liveBroken) return Text("{ not json");
            if (etag == "\"v1\"") return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = Text(Fixture);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        }));

        Assert.Equal(2, (await Service(http, cache).GetAsync())?.Count);
        Assert.Equal("\"v1\"", File.ReadAllText(cache + ".etag"));
        Assert.Equal(2, (await Service(http, cache).GetAsync())?.Count);
        liveBroken = true;
        Assert.Equal(2, (await Service(http, cache).GetAsync())?.Count);

        Assert.Equal(new string?[] { null, "\"v1\"", "\"v1\"" }, seenEtags);
        Assert.Equal(Fixture, File.ReadAllText(cache));
    }

    [Fact]
    public async Task Loader_UsesPinnedCopyWhenLiveIsDownAndRejectsNonGitHubSources()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri == RenoDxUnityDatabaseService.PinnedUrl
            ? Text(Fixture)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var pinned = await new RenoDxUnityDatabaseService(http, Path.Combine(_root, "unity.json"), freshness: TimeSpan.Zero).GetAsync();

        Assert.Equal(2, pinned?.Count);
        Assert.False(File.Exists(Path.Combine(_root, "unity.json.etag")));
        Assert.Throws<ArgumentException>(() => new RenoDxUnityDatabaseService(http, Path.Combine(_root, "rejected.json"), liveUrl: "http://raw.githubusercontent.com/x.json"));
        Assert.Throws<ArgumentException>(() => new RenoDxUnityDatabaseService(http, Path.Combine(_root, "rejected.json"), liveUrl: "https://example.test/x.json"));
    }

    [Fact]
    public void ManifestOverridesWinOverUnityDatabaseKeys()
    {
        var merged = RenoDxDeploymentService.MergeIniOverrides(
            [new("Use_Swapchain_Proxy", "1"), new("Upgrade_R11G11B10_FLOAT", "1")],
            new Dictionary<string, string> { ["use_swapchain_proxy"] = "0" });

        Assert.Equal("0", merged["Use_Swapchain_Proxy"]);
        Assert.Equal("1", merged["Upgrade_R11G11B10_FLOAT"]);
    }

    [Fact]
    public async Task UnityInstall_WritesDatabaseKeysAndRemoveRestoresPriorIni()
    {
        var (game, files, ini) = CreateGame(unity: true);
        const string original = "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n\n[renodx]\nUse_Swapchain_Proxy=0\n";
        File.WriteAllText(ini, original);
        var (service, _) = CreateService();

        var install = await service.InstallAsync(game, files);

        Assert.True(install.Success, install.Message);
        var section = RenoDxIniOverrideManager.ReadRenoDxSection(game.BinaryDirectory!)!;
        Assert.Equal("1", section["Use_Swapchain_Proxy"]);
        Assert.Equal("1", section["Upgrade_R11G11B10_FLOAT"]);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(original, File.ReadAllText(ini).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task UnityUpdate_PreservesUserEditOfDatabaseKey()
    {
        var (game, files, _) = CreateGame(unity: true);
        var (service, _) = CreateService();
        Assert.True((await service.InstallAsync(game, files)).Success);
        RenoDxIniOverrideManager.SetUserValue(game.BinaryDirectory!, "Upgrade_R11G11B10_FLOAT", "3", out _);

        var update = await service.UpdateAsync(game, files);

        Assert.True(update.Success, update.Message);
        var section = RenoDxIniOverrideManager.ReadRenoDxSection(game.BinaryDirectory!)!;
        Assert.Equal("3", section["Upgrade_R11G11B10_FLOAT"]);
        Assert.Equal("1", section["Use_Swapchain_Proxy"]);
    }

    [Fact]
    public async Task NonUnityGame_ListedInDatabaseGetsNoDatabaseKeys()
    {
        var (game, files, ini) = CreateGame(unity: false);
        var (service, handler) = CreateService(namedCatalogue: true);

        var install = await service.InstallAsync(game, files);

        Assert.True(install.Success, install.Message);
        Assert.Null(RenoDxIniOverrideManager.ReadRenoDxSection(game.BinaryDirectory!));
        Assert.Equal("[GENERAL]\n", File.ReadAllText(ini).Replace("\r\n", "\n"));
        Assert.Equal(0, handler.UnityDbRequests);
    }

    private static RenoDxUnityDatabaseService Service(HttpClient http, string cache)
        => new(http, cache, fallbackUrl: null, freshness: TimeSpan.Zero);

    private (RenoDxDeploymentService Service, InstallHandler Handler) CreateService(bool namedCatalogue = false)
    {
        var handler = new InstallHandler(namedCatalogue);
        var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var unityDb = new RenoDxUnityDatabaseService(http, Path.Combine(_root, "cache", "unity.json"), fallbackUrl: null);
        return (new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"),
            unityDatabase: unityDb), handler);
    }

    private (DetectedGame Game, string[] Files, string Ini) CreateGame(bool unity)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "OuterWilds.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var ini = Path.Combine(bin, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\n");
        var files = new List<string> { exe, Path.Combine(bin, "ReShade64.dll") };
        if (unity)
        {
            File.WriteAllText(Path.Combine(bin, "UnityPlayer.dll"), "MZ");
            files.Add(Path.Combine(bin, "UnityPlayer.dll"));
        }
        var game = new DetectedGame
        {
            Name = "Outer Wilds", InstallPath = _root, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 753640
        };
        return (game, files.ToArray(), ini);
    }

    private static HttpResponseMessage Text(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // Serves the Unity database, optionally a named catalogue entry (non-Unity case) and any add-on.
    private sealed class InstallHandler(bool namedCatalogue) : HttpMessageHandler
    {
        public int UnityDbRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == RenoDxUnityDatabaseService.LiveUrl)
            {
                Interlocked.Increment(ref UnityDbRequests);
                return Task.FromResult(Text(Fixture));
            }
            if (namedCatalogue && url == RenoDxCatalogService.StructuredDbUrl)
                return Task.FromResult(Text("""[ { "name": "Outer Wilds", "snapshotUrl": "https://clshortfuse.github.io/renodx/renodx-outerwilds.addon64" } ]"""));
            if (!url.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
