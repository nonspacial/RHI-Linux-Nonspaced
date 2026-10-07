using System.Diagnostics;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>The live PCGamingWiki fallback: native/ambiguous results stay local, misses are cached, selection never waits on it (Refs #54).</summary>
public sealed class PcgwFallbackPolicyTests : IDisposable
{
    private const string VulkanTable = """<table id="table-api"><tr><th scope="row"><abbr title="Windows">Vulkan</abbr></th><td>1.3</td></tr></table>""";
    private const string NoResults = """["Unknown Game",[],[],[]]""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pcgw-policy-" + Guid.NewGuid().ToString("N"));

    public PcgwFallbackPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NativeFnaGame_NeverContactsPcgw_AndKeepsItsNativeCandidates()
    {
        var dir = Dir("broken-sword");
        var exe = Path.Combine(dir, "BS2");
        File.WriteAllBytes(exe, [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]);
        File.WriteAllText(Path.Combine(dir, "FNA.dll"), "managed");
        var game = new DetectedGame { Name = "Broken Sword 2", InstallPath = dir, ExecutablePath = exe, Runner = "Native Linux", SteamAppId = 35100 };
        var handler = new CountingHandler(_ => Html(VulkanTable));

        await Probe(handler).ProbeAsync(game, GameFileIndexer.Build(dir));

        Assert.Equal(0, handler.Requests);
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);
        Assert.Equal([GraphicsApi.Vulkan, GraphicsApi.OpenGL], game.GraphicsApis);
    }

    [Fact]
    public async Task AmbiguousLocalCandidates_AreNotReplacedByPcgw()
    {
        var dir = Dir("ambiguous");
        var exe = Path.Combine(dir, "Game.exe");
        File.WriteAllText(exe, "MZ no renderer import");
        File.WriteAllText(Path.Combine(dir, "engine.dll"), "d3d12.dll d3d11.dll d3d9.dll vulkan-1.dll");
        var game = new DetectedGame { Name = "Ambiguous", InstallPath = dir, ExecutablePath = exe, Runner = "Proton", SteamAppId = 1 };
        var handler = new CountingHandler(_ => Html(VulkanTable));

        await Probe(handler).ProbeAsync(game, GameFileIndexer.Build(dir));

        Assert.Equal(0, handler.Requests);
        Assert.True(game.GraphicsApis.Count >= 3);
    }

    [Fact]
    public async Task DefiniteMiss_IsCachedUntilItsTtlExpires()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-07T12:00:00Z"));
        var handler = new CountingHandler(_ => Json(NoResults));
        var service = new PcGamingWikiGraphicsApiService(new HttpClient(handler), Path.Combine(_root, "cache.json"), time, TimeSpan.Zero, missTtl: TimeSpan.FromDays(7));

        await service.ResolveAndFetchAsync("Unknown Game", null);
        await service.ResolveAndFetchAsync("Unknown Game", null);
        Assert.Equal(1, handler.Requests);

        // A new session reads the miss from disk.
        var reloaded = new PcGamingWikiGraphicsApiService(new HttpClient(handler), Path.Combine(_root, "cache.json"), time, TimeSpan.Zero, missTtl: TimeSpan.FromDays(7));
        await reloaded.ResolveAndFetchAsync("Unknown Game", null);
        Assert.Equal(1, handler.Requests);

        time.Now += TimeSpan.FromDays(8);
        await reloaded.ResolveAndFetchAsync("Unknown Game", null);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task NotFoundAppIdPage_IsACachedMiss_ButAServerErrorIsNot()
    {
        var status = HttpStatusCode.NotFound;
        var handler = new CountingHandler(_ => new HttpResponseMessage(status));
        var service = new PcGamingWikiGraphicsApiService(new HttpClient(handler), Path.Combine(_root, "cache.json"), minRequestGap: TimeSpan.Zero);

        await service.ResolveAndFetchAsync("Game", 77);
        await service.ResolveAndFetchAsync("Game", 77);
        Assert.Equal(1, handler.Requests);

        status = HttpStatusCode.InternalServerError;
        await service.ResolveAndFetchAsync("Other", 78);
        await service.ResolveAndFetchAsync("Other", 78);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task SlowLookup_DoesNotHoldUpTheSelection_AndArrivesLater()
    {
        var release = new TaskCompletionSource();
        var handler = new CountingHandler(_ => Html(VulkanTable), release.Task);
        var probe = Probe(handler, budget: TimeSpan.FromMilliseconds(100));
        var late = new TaskCompletionSource<DetectedGame>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.LateMetadataAvailable += game => late.TrySetResult(game);
        var dir = Dir("slow");
        var exe = Path.Combine(dir, "Game.exe");
        File.WriteAllText(exe, "MZ no renderer import");
        var game = new DetectedGame { Name = "Slow Game", InstallPath = dir, ExecutablePath = exe, Runner = "Proton", SteamAppId = 5 };

        var timer = Stopwatch.StartNew();
        await probe.ProbeAsync(game, GameFileIndexer.Build(dir));
        timer.Stop();

        Assert.True(timer.ElapsedMilliseconds < 2000, $"selection waited {timer.ElapsedMilliseconds} ms");
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);
        Assert.Contains("PCGamingWiki lookup still running", game.GraphicsApiEvidence);

        release.SetResult();
        Assert.Same(game, await late.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var again = new DetectedGame { Name = "Slow Game", InstallPath = dir, ExecutablePath = exe, Runner = "Proton", SteamAppId = 5 };
        await probe.ProbeAsync(again, GameFileIndexer.Build(dir));
        Assert.Equal(GraphicsApi.Vulkan, again.GraphicsApi);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void DefaultCache_LivesInTheRhiLinuxCacheFolder()
    {
        var service = new PcGamingWikiGraphicsApiService(new HttpClient(new CountingHandler(_ => Json(NoResults))));

        Assert.Equal(Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "pcgw_graphics_api_cache.json"), service.CachePath);
    }

    private LinuxGameRuntimeProbe Probe(CountingHandler handler, TimeSpan? budget = null)
        => new(pcGamingWiki: new PcGamingWikiGraphicsApiService(new HttpClient(handler), Path.Combine(_root, "probe-cache.json"), minRequestGap: TimeSpan.Zero),
            pcgwSelectionBudget: budget ?? TimeSpan.FromSeconds(10));

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, Task? gate = null) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            return respond(request);
        }
    }
}
