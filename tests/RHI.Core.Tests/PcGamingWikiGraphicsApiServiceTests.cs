using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class PcGamingWikiGraphicsApiServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-pcgw-api-" + Guid.NewGuid().ToString("N"));

    public PcGamingWikiGraphicsApiServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Fetch_ParsesOnlyWindowsApiRowsAndCachesResult()
    {
        const string html = """
        <html><body><table id="table-api">
          <tr><th scope="row"><abbr title="Windows">Direct3D 11</abbr></th><td>11</td></tr>
          <tr><th scope="row"><abbr title="Windows">Direct3D 12</abbr></th><td>12</td></tr>
          <tr><th scope="row"><abbr title="Linux">Vulkan</abbr></th><td>1.3</td></tr>
        </table></body></html>
        """;
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        }));
        var service = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "cache.json"));

        var first = await service.FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");
        var second = await service.FetchAsync("Game", "https://www.pcgamingwiki.com/wiki/Game");

        Assert.Equal(GraphicsApi.DirectX12, first.Primary);
        Assert.Contains(GraphicsApi.DirectX11, first.Candidates);
        Assert.DoesNotContain(GraphicsApi.Vulkan, first.Candidates);
        Assert.Equal(GraphicsApiConfidence.Medium, first.Confidence);
        Assert.Contains("PCGamingWiki", first.Evidence);
        Assert.Equal(first.Primary, second.Primary);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Evidence, second.Evidence);
        Assert.Equal(first.Candidates.ToArray(), second.Candidates.ToArray());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RuntimeProbe_UsesPcgwOnlyWhenLocalDetectionIsUnknown()
    {
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame
        {
            Name = "Fallback Game",
            InstallPath = gameDir,
            ExecutablePath = exe,
            Runner = "Proton",
            PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Fallback_Game"
        };
        const string html = """
        <table id="table-api"><tr><th scope="row"><abbr title="Windows">Direct3D 9</abbr></th><td>9.0c</td></tr></table>
        """;
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));
        var pcgw = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "runtime-cache.json"), minRequestGap: TimeSpan.Zero);
        var probe = new LinuxGameRuntimeProbe(pcGamingWiki: pcgw);

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(GraphicsApi.DirectX9, game.GraphicsApi);
        Assert.Equal(GraphicsApiConfidence.Medium, game.GraphicsApiConfidence);
        Assert.Contains("PCGamingWiki", game.GraphicsApiEvidence);
    }


    [Fact]
    public async Task RuntimeProbe_ResolvesPcgwBySteamAppIdWhenLocalDetectionIsUnknown()
    {
        var gameDir = Path.Combine(_root, "steam-fallback");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame
        {
            Name = "Steam Fallback Game",
            InstallPath = gameDir,
            ExecutablePath = exe,
            Runner = "Proton",
            SteamAppId = 424242
        };
        const string html = """
        <table id="table-api"><tr><th scope="row"><abbr title="Windows">Vulkan</abbr></th><td>1.3</td></tr></table>
        """;
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        }));
        var pcgw = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "steam-cache.json"), minRequestGap: TimeSpan.Zero);
        var probe = new LinuxGameRuntimeProbe(pcGamingWiki: pcgw);

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(GraphicsApi.Vulkan, game.GraphicsApi);
        Assert.Single(requests);
        Assert.Equal("www.pcgamingwiki.com", requests[0].Host);
        Assert.Equal("/api/appid.php", requests[0].AbsolutePath);
        Assert.Contains("appid=424242", requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeProbe_UsesPcgwOpenSearchForNonSteamUnknownGame()
    {
        var gameDir = Path.Combine(_root, "search-fallback");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ no renderer imports here");
        var game = new DetectedGame
        {
            Name = "Search Fallback Game",
            InstallPath = gameDir,
            ExecutablePath = exe,
            Runner = "Wine"
        };
        const string html = """
        <table id="table-api"><tr><th scope="row"><abbr title="Windows">OpenGL</abbr></th><td>4.6</td></tr></table>
        """;
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.Equals("/w/api.php", StringComparison.Ordinal))
            {
                const string json = "[\"Search Fallback Game\",[\"Search Fallback Game\"],[\"\"],[\"https://www.pcgamingwiki.com/wiki/Search_Fallback_Game\"]]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        }));
        var pcgw = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "search-cache.json"), minRequestGap: TimeSpan.Zero);
        var probe = new LinuxGameRuntimeProbe(pcGamingWiki: pcgw);

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(GraphicsApi.OpenGL, game.GraphicsApi);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/w/api.php", requests[0].AbsolutePath);
        Assert.Equal("/wiki/Search_Fallback_Game", requests[1].AbsolutePath);
    }

    [Fact]
    public async Task RuntimeProbe_PreservesStrongLocalEvidenceWithoutContactingPcgw()
    {
        var gameDir = Path.Combine(_root, "local");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ d3d12.dll");
        var game = new DetectedGame
        {
            Name = "Local Game",
            InstallPath = gameDir,
            ExecutablePath = exe,
            Runner = "Proton",
            PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Local_Game"
        };
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("PCGW must not be contacted.")));
        var pcgw = new PcGamingWikiGraphicsApiService(http, Path.Combine(_root, "local-cache.json"));
        var probe = new LinuxGameRuntimeProbe(pcGamingWiki: pcgw);

        await probe.ProbeAsync(game, GameFileIndexer.Build(gameDir));

        Assert.Equal(GraphicsApi.DirectX12, game.GraphicsApi);
        Assert.Equal(GraphicsApiConfidence.High, game.GraphicsApiConfidence);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
