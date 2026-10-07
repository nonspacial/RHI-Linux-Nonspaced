using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>pcgw_data.json is revalidated on an explicit full Refresh only (Refs #104).</summary>
public sealed class PcgwCentralDataRefreshTests : IDisposable
{
    private const string Fixture = """{ "version": 1, "games": { "Game": { "dx11": true } } }""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pcgw-refresh-" + Guid.NewGuid().ToString("N"));

    public PcgwCentralDataRefreshTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Invalidate_RevalidatesAFreshCopyWithItsETag()
    {
        var handler = new DataHandler();
        var cache = Path.Combine(_root, "pcgw_data.json");
        await new PcgwCentralDataService(new HttpClient(handler), cache, "https://example.test/pcgw_data.json", null).GetAsync();
        var service = new PcgwCentralDataService(new HttpClient(handler), cache, "https://example.test/pcgw_data.json", null);

        Assert.NotNull(await service.GetAsync());
        Assert.Equal(1, handler.Requests); // fresh disk copy, no request

        service.Invalidate();
        Assert.NotNull(await service.GetAsync());

        Assert.Equal(2, handler.Requests);
        Assert.Equal("\"v1\"", handler.LastIfNoneMatch);
    }

    [Fact]
    public async Task FullLibraryRefresh_RevalidatesInTheBackground_PartialRefreshDoesNot()
    {
        var handler = new DataHandler();
        using var http = new HttpClient(handler);
        var data = new PcgwCentralDataService(http, Path.Combine(_root, "pcgw_data.json"), "https://example.test/pcgw_data.json", null);
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var scanner = new GameLibraryScanner(
            [new Fixed("Manual", new DetectedGame { Name = "Game", InstallPath = gameDir, Source = "Manual" })],
            [new UpstreamGameLibraryPolicyService(manifest, data)]);

        await scanner.ScanAsync();
        await WaitFor(() => handler.DataRequests == 1);
        await scanner.ScanAsync(new HashSet<string> { "Manual" });
        await Task.Delay(200);
        Assert.Equal(1, handler.DataRequests);

        await scanner.ScanAsync();
        await WaitFor(() => handler.DataRequests == 2);
        Assert.Equal("\"v1\"", handler.LastIfNoneMatch);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Fixed(string name, params DetectedGame[] games) : IGameDetector
    {
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games.Select(g => g.ShallowClone()).ToArray());
    }

    private sealed class DataHandler : HttpMessageHandler
    {
        private int _requests;
        private int _dataRequests;
        public int Requests => Volatile.Read(ref _requests);
        public int DataRequests => Volatile.Read(ref _dataRequests);
        public string? LastIfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":3}", Encoding.UTF8, "application/json") });
            var etag = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            LastIfNoneMatch = etag;
            Interlocked.Increment(ref _dataRequests);
            if (etag == "\"v1\"") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
