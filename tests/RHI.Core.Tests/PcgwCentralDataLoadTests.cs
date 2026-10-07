using System.Diagnostics;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>pcgw_data.json loading: shared background load (Refs #102) and recorded failures (Refs #103).</summary>
public sealed class PcgwCentralDataLoadTests : IDisposable
{
    private const string Fixture = """
        { "version": 1, "games": { "Mass Effect: Andromeda": { "steam_appid": 1238000, "dx11": true, "engine": "Frostbite 3" } } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pcgw-load-" + Guid.NewGuid().ToString("N"));

    public PcgwCentralDataLoadTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CancelledCaller_DoesNotCancelTheSharedLoad_AndTheNextCallerReusesIt()
    {
        var release = new TaskCompletionSource();
        var handler = new GatedHandler(_ => Json(Fixture), release.Task);
        var service = Service(handler);

        using var selection = new CancellationTokenSource();
        var first = service.GetAsync(selection.Token);
        await handler.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        selection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = service.GetAsync();
        release.SetResult();

        Assert.Equal(1, (await second.WaitAsync(TimeSpan.FromSeconds(10)))?.Count);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task SlowFirstDownload_DoesNotHoldUpTheSelection_NorTriggerPerGameRequests()
    {
        var release = new TaskCompletionSource();
        var handler = new GatedHandler(request =>
        {
            if (request.RequestUri!.Host.EndsWith("pcgamingwiki.com", StringComparison.Ordinal))
                throw new InvalidOperationException("No per-game PCGW request while the data file is loading.");
            return Json(Fixture);
        }, release.Task);
        using var http = new HttpClient(handler);
        var probe = new LinuxGameRuntimeProbe(
            pcGamingWiki: new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "api.json"), minRequestGap: TimeSpan.Zero),
            pcgwData: new PcgwCentralDataService(http, Path.Combine(_root, "pcgw_data.json"), "https://example.test/pcgw_data.json", fallbackUrl: null),
            pcgwDataSelectionBudget: TimeSpan.FromMilliseconds(100));
        var late = new TaskCompletionSource<DetectedGame>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.LateMetadataAvailable += g => late.TrySetResult(g);
        var game = Game("Mass Effect: Andromeda");

        var timer = Stopwatch.StartNew();
        await probe.ProbeAsync(game, GameFileIndexer.Build(game.InstallPath));
        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < 2000, $"selection waited {timer.ElapsedMilliseconds} ms");
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);

        release.SetResult();
        Assert.Same(game, await late.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var again = Game("Mass Effect: Andromeda");
        await probe.ProbeAsync(again, GameFileIndexer.Build(again.InstallPath));
        Assert.Equal(GraphicsApi.DirectX11, again.GraphicsApi);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task EverySourceFailing_IsRecorded_AndLogged()
    {
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        var handler = new GatedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new PcgwCentralDataService(new HttpClient(handler), Path.Combine(_root, "pcgw_data.json"),
            "https://example.test/live.json", "https://example.test/pinned.json");

        var flowLog = AppLog.UseInThisFlow(log);
        PcgwCentralData? data;
        try { data = await service.GetAsync(); }
        finally { flowLog.Dispose(); }

        Assert.Null(data);
        Assert.Contains("pinned download failed", service.LastError);
        var text = await File.ReadAllTextAsync(log.FilePath);
        Assert.Contains("live download failed", text);
        Assert.Contains("500", text);
    }

    [Fact]
    public async Task UnusableLiveCopy_IsRecordedWithTheParseError()
    {
        var service = Service(new GatedHandler(_ => Json("{ \"games\": ")));

        Assert.Null(await service.GetAsync());
        Assert.Contains("live copy unusable", service.LastError);
    }

    private PcgwCentralDataService Service(GatedHandler handler)
        => new(new HttpClient(handler), Path.Combine(_root, "pcgw_data.json"), "https://example.test/pcgw_data.json", fallbackUrl: null);

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var exe = Path.Combine(dir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, Runner = "Proton" };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class GatedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, Task? gate = null) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public Task FirstRequest => _first.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            _first.TrySetResult();
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            return respond(request);
        }
    }
}
