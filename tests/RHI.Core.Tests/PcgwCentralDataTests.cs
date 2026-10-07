using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class PcgwCentralDataTests : IDisposable
{
    private const string Fixture = """
    {
      "version": 1,
      "name_overrides": { "Mass Effect Andromeda": "Mass Effect: Andromeda" },
      "games": {
        "Mass Effect: Andromeda": { "steam_appid": 1238000, "dx11": true, "engine": "Frostbite 3" },
        "Assassin\u2019s Creed Origins": { "dx11": true, "engine": "Engine:AnvilNext_2.0" },
        "Deus Ex: Mankind Divided": { "dx11": true, "dx12": true },
        "CrossCode": { "dx11": true, "opengl": true, "engine": "NW.js" },
        "crosscode": { "dx11": true, "opengl": true, "engine": "NW.js" },
        "Steam Only": { "steam_appid": 4242, "vulkan": true, "dx11": true }
      }
    }
    """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pcgw-data-" + Guid.NewGuid().ToString("N"));

    public PcgwCentralDataTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Lookup_FollowsUpstreamOrderAndNormalisation()
    {
        var data = PcgwCentralData.Parse(Encoding.UTF8.GetBytes(Fixture))!;

        Assert.Equal(5, data.Count); // case-variant duplicate collapsed, not thrown
        Assert.Equal("Mass Effect: Andromeda", data.TryLookup("Mass Effect Andromeda", null)?.PageTitle);
        Assert.Equal("Steam Only", data.TryLookup("Some Store Title", 4242)?.PageTitle);
        Assert.Equal("Assassin\u2019s Creed Origins", data.TryLookup("Assassin's Creed Origins", null)?.PageTitle);
        Assert.Equal("Deus Ex: Mankind Divided", data.TryLookup("Deus Ex: Mankind Divided™", null)?.PageTitle);
        Assert.Null(data.TryLookup("Not A Game", 1));
        Assert.Equal("AnvilNext 2.0", PcgwCentralData.NormaliseEngineName("Engine:AnvilNext_2.0"));
    }

    [Fact]
    public async Task ColdStart_ResolvesApiAndEngineFromDataFileWithoutPcgwRequests()
    {
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame { Name = "Mass Effect Andromeda", InstallPath = gameDir, ExecutablePath = exe, Runner = "Proton" };
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host.EndsWith("pcgamingwiki.com", StringComparison.Ordinal))
                throw new InvalidOperationException("No per-game PCGW request expected.");
            return Json(Fixture);
        }));
        var probe = new LinuxGameRuntimeProbe(
            pcGamingWiki: new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api.json"), minRequestGap: TimeSpan.Zero),
            pcgwData: new PcgwCentralDataService(http, Path.Combine(_root, "pcgw_data.json"), "https://example.test/pcgw_data.json", fallbackUrl: null));

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(GraphicsApi.DirectX11, game.GraphicsApi);
        Assert.Equal(GraphicsApiConfidence.Medium, game.GraphicsApiConfidence);
        Assert.Equal("Frostbite 3", game.EngineHint);
        Assert.Equal("example.test", Assert.Single(requests).Host);
    }

    [Fact]
    public async Task Loader_UsesFreshCacheThenRevalidatesWithEtag()
    {
        var cache = Path.Combine(_root, "pcgw_data.json");
        var seenEtags = new List<string?>();
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var etag = request.Headers.TryGetValues("If-None-Match", out var values) ? values.Single() : null;
            seenEtags.Add(etag);
            if (etag == "\"v1\"") return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = Json(Fixture);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        }
        using var http = new HttpClient(new Handler(Respond));

        Assert.NotNull(await new PcgwCentralDataService(http, cache, "https://example.test/d.json", null).GetAsync());
        Assert.NotNull(await new PcgwCentralDataService(http, cache, "https://example.test/d.json", null).GetAsync());
        var revalidated = await new PcgwCentralDataService(http, cache, "https://example.test/d.json", null, freshness: TimeSpan.Zero).GetAsync();

        Assert.Equal(5, revalidated?.Count);
        Assert.Equal(new string?[] { null, "\"v1\"" }, seenEtags); // second instance hit the fresh cache
    }

    [Fact]
    public async Task Loader_KeepsLastGoodCopyOnBadLiveDataAndUsesPinnedCopyWhenNoCache()
    {
        var cache = Path.Combine(_root, "pcgw_data.json");
        using var good = new HttpClient(new Handler(_ => Json(Fixture)));
        await new PcgwCentralDataService(good, cache, "https://example.test/live.json", null).GetAsync();
        using var broken = new HttpClient(new Handler(_ => Json("{ not json")));

        var stale = await new PcgwCentralDataService(broken, cache, "https://example.test/live.json", null, freshness: TimeSpan.Zero).GetAsync();

        Assert.Equal(5, stale?.Count);
        Assert.Equal(Fixture, File.ReadAllText(cache));

        using var liveDown = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/pinned.json"
            ? Json(Fixture)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var pinned = await new PcgwCentralDataService(liveDown, Path.Combine(_root, "other.json"),
            "https://example.test/live.json", "https://example.test/pinned.json").GetAsync();
        Assert.Equal(5, pinned?.Count);
    }

    [Fact]
    public void CentralData_CorrectsPeScanDx9OnlyForBinarySignatureEvidence()
    {
        var data = PcgwCentralData.Parse(Encoding.UTF8.GetBytes(Fixture))!;
        var (title, entry) = data.TryLookup("CrossCode", null)!.Value;
        DetectedGame Dx9(string evidence) => new()
        {
            Name = "CrossCode", InstallPath = _root, EngineHint = "Unity",
            GraphicsApi = GraphicsApi.DirectX9, GraphicsApis = [GraphicsApi.DirectX9],
            GraphicsApiConfidence = GraphicsApiConfidence.High, GraphicsApiEvidence = evidence
        };

        var peScan = Dx9("Executable signature: d3d9.dll");
        LinuxGameRuntimeProbe.ApplyCentralData(peScan, title, entry);
        var runtimeLog = Dx9("Runtime renderer log: Player.log (d3d9)");
        LinuxGameRuntimeProbe.ApplyCentralData(runtimeLog, title, entry);

        Assert.Equal(GraphicsApi.DirectX11, peScan.GraphicsApi);
        Assert.Equal(new[] { GraphicsApi.DirectX11 }, peScan.GraphicsApis);
        Assert.Equal("Unity", peScan.EngineHint); // local/manifest engine is never replaced
        Assert.Equal(GraphicsApi.DirectX9, runtimeLog.GraphicsApi);
    }

    [Fact]
    public async Task LivePcgw_HonoursRetryAfterThenRecovers()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-06T12:00:00Z"));
        var requests = 0;
        const string html = """<table id="table-api"><tr><th scope="row"><abbr title="Windows">Direct3D 12</abbr></th><td>12</td></tr></table>""";
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++requests == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return limited;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        }));
        var service = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api.json"), clock, TimeSpan.Zero);
        const string url = "https://www.pcgamingwiki.com/wiki/Game";

        var limitedResult = await service.FetchAsync("Game", url);
        clock.Now += TimeSpan.FromSeconds(119);
        var pausedResult = await service.FetchAsync("Game", url);
        var pausedRequests = requests;
        clock.Now += TimeSpan.FromSeconds(2);
        var recovered = await service.FetchAsync("Game", url);

        Assert.Equal(GraphicsApi.Unknown, limitedResult.Primary);
        Assert.Equal(GraphicsApi.Unknown, pausedResult.Primary);
        Assert.Equal(1, pausedRequests);
        Assert.Equal(GraphicsApi.DirectX12, recovered.Primary);
        Assert.False(service.IsPaused);
        Assert.Equal(PcGamingWikiGraphicsApiService.MaxPause, PcGamingWikiGraphicsApiService.ClampPause(TimeSpan.FromDays(1)));
        Assert.Equal(PcGamingWikiGraphicsApiService.MinPause, PcGamingWikiGraphicsApiService.ClampPause(TimeSpan.Zero));
    }

    [Fact]
    public async Task InFileEntryWithoutApiData_SkipsTheLivePcgwLookup()
    {
        // ~85 % of the real file: engine and/or appid only. The entry answers the engine; the API stays
        // for local detection, and no per-game PCGW request is made (Refs #105).
        const string engineOnly = """{ "version": 1, "games": { "Engine Only Game": { "steam_appid": 777, "engine": "Engine:Unreal_Engine_4" } } }""";
        var gameDir = Path.Combine(_root, "engine-only");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame { Name = "Engine Only Game", InstallPath = gameDir, ExecutablePath = exe, Runner = "Proton", SteamAppId = 777 };
        var pcgwRequests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host.EndsWith("pcgamingwiki.com", StringComparison.Ordinal)) pcgwRequests++;
            return Json(engineOnly);
        }));
        var probe = new LinuxGameRuntimeProbe(
            pcGamingWiki: new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api-engine.json"), minRequestGap: TimeSpan.Zero),
            pcgwData: new PcgwCentralDataService(http, Path.Combine(_root, "pcgw_engine.json"), "https://example.test/pcgw_data.json", fallbackUrl: null));

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(0, pcgwRequests);
        Assert.Equal("Unreal Engine 4", game.EngineHint);
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);
    }

    [Fact]
    public async Task LivePcgw_ServiceUnavailableWithHttpDateRetryAfter_PausesUntilThatTime()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-06T12:00:00Z"));
        var requests = 0;
        const string html = """<table id="table-api"><tr><th scope="row"><abbr title="Windows">Direct3D 11</abbr></th><td>11</td></tr></table>""";
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++requests == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(clock.Now + TimeSpan.FromSeconds(90));
                return busy;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        }));
        var service = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api-503.json"), clock, TimeSpan.Zero);
        const string url = "https://www.pcgamingwiki.com/wiki/Game";

        await service.FetchAsync("Game", url);
        clock.Now += TimeSpan.FromSeconds(89);
        Assert.True(service.IsPaused);
        await service.FetchAsync("Game", url);
        Assert.Equal(1, requests);
        clock.Now += TimeSpan.FromSeconds(2);

        Assert.Equal(GraphicsApi.DirectX11, (await service.FetchAsync("Game", url)).Primary);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task LivePcgw_TimeoutPausesFurtherRequests()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-06T12:00:00Z"));
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            throw new TaskCanceledException("simulated request timeout");
        }));
        var service = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api-timeout.json"), clock, TimeSpan.Zero);

        await service.FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");
        await service.FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");

        Assert.True(service.IsPaused);
        Assert.Equal(1, requests);
        clock.Now += PcGamingWikiGraphicsApiService.DefaultPause + TimeSpan.FromSeconds(1);
        Assert.False(service.IsPaused);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
