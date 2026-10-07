using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>Only an explicit full refresh revalidates the upstream manifest; partial refreshes stay offline (Refs #61).</summary>
public sealed class ManifestRefreshPolicyTests : IDisposable
{
    private const string Json = """{ "version": 3, "blacklist": ["Blocked Tool"] }""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-refresh-" + Guid.NewGuid().ToString("N"));

    public ManifestRefreshPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PartialScan_DoesNotRefetchTheManifest_EvenWhenTheNetworkHangs()
    {
        var handler = new ScriptedHandler(_ => Ok(Json), hangAfterFirst: true);
        var scanner = Scanner(handler, cachePath: null);

        await scanner.ScanAsync();
        var timer = Stopwatch.StartNew();
        var partial = await scanner.ScanAsync(new HashSet<string> { "Manual" });
        timer.Stop();

        Assert.Equal(1, handler.Requests);
        Assert.True(timer.ElapsedMilliseconds < 1000, $"partial scan took {timer.ElapsedMilliseconds} ms");
        Assert.DoesNotContain(partial, g => g.Name == "Blocked Tool");
    }

    [Fact]
    public async Task FullScan_RevalidatesWithTheETag_AndAn304ReusesTheCachedCopy()
    {
        var handler = new ScriptedHandler(request =>
            request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\"")
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : Ok(Json, "\"v1\""));
        var scanner = Scanner(handler, Path.Combine(_root, "manifest.json"));

        await scanner.ScanAsync();
        var second = await scanner.ScanAsync();

        Assert.Equal(2, handler.Requests);
        Assert.Equal("\"v1\"", handler.LastIfNoneMatch);
        Assert.DoesNotContain(second, g => g.Name == "Blocked Tool");
    }

    [Fact]
    public async Task TotalFailure_IsRemembered_UntilTheNextExplicitFullRefresh()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var scanner = Scanner(handler, cachePath: null);

        var first = await scanner.ScanAsync();
        await scanner.ScanAsync(new HashSet<string> { "Manual" });
        Assert.Equal(1, handler.Requests);
        Assert.Contains(first, g => g.Name == "Blocked Tool"); // fail-open: local library unchanged

        await scanner.ScanAsync();
        Assert.Equal(2, handler.Requests);
    }

    private GameLibraryScanner Scanner(ScriptedHandler handler, string? cachePath)
    {
        var manifest = new UpstreamRhiManifestService(new HttpClient(handler), "https://manifest.test/manifest.json", cachePath: cachePath);
        var steamDir = Path.Combine(_root, "steam-game");
        var manualDir = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(steamDir);
        Directory.CreateDirectory(manualDir);
        return new GameLibraryScanner(
            [
                new StaticDetector("Steam", new DetectedGame { Name = "Steam Game", InstallPath = steamDir, Source = "Steam" }),
                new StaticDetector("Manual", new DetectedGame { Name = "Blocked Tool", InstallPath = manualDir, Source = "Manual" })
            ],
            [new UpstreamGameLibraryPolicyService(manifest)]);
    }

    private static HttpResponseMessage Ok(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool hangAfterFirst = false) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string? LastIfNoneMatch { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _requests);
            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            if (hangAfterFirst && n > 1) await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            return respond(request);
        }
    }

    private sealed class StaticDetector(string name, params DetectedGame[] games) : IGameDetector
    {
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games.Select(g => g.ShallowClone()).ToArray());
    }
}
