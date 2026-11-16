using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #267 (upstream 2.8.9): packs already downloaded are checked for updates at every start, used by a game or not.
// #265 (upstream 2.9.4): packs the upstream manifest adds (SD Shaders, UltrawideSideGlass) update like the built-in ones.
public sealed class ShaderPackRefreshTests : IDisposable
{
    private const string ManifestUrl = "https://example.test/manifest.json";
    private const string SdArchive = "https://github.com/Starodubysko/sd-reshade-shaders/archive/refs/heads/main.zip";
    private const string GlassApi = "https://api.github.com/repos/NickFirzen/Ultrawide-Side-Glass-Reshade-Shader/releases/latest";

    private static readonly string ManifestFixture =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rhi-manifest-shaderPacks-7e234dc.json"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shader-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly Remote _remote = new();
    private readonly HttpClient _http;
    private readonly ReShadeShaderPackService _service;

    public ShaderPackRefreshTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_remote);
        _service = new ReShadeShaderPackService(_http, Path.Combine(_root, "cache"), new UpstreamRhiManifestService(_http, ManifestUrl),
            Path.Combine(_root, "custom"));
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Staged(string packId, string file)
        => Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache", packId, "extracted"), file, SearchOption.AllDirectories));

    [Fact]
    public async Task ManifestBranchArchive_DownloadedOnce_IsStagedAgainWhenTheServerHasANewBuild()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        await _service.SyncAsync(game, ["SdReshadeShaders"]);
        Assert.Contains("build 1", File.ReadAllText(Staged("SdReshadeShaders", "SD_Density.fx")));

        Assert.Equal(0, await _service.RefreshDownloadedAsync()); // same ETag: nothing downloaded
        Assert.Equal(1, _remote.Gets(SdArchive));

        _remote.SdBuild = 2;
        var refreshed = await _service.RefreshDownloadedAsync();

        Assert.True(refreshed >= 1);
        Assert.Equal(2, _remote.Gets(SdArchive));
        Assert.Contains("build 2", File.ReadAllText(Staged("SdReshadeShaders", "SD_Density.fx")));
    }

    [Fact]
    public async Task ManifestSingleFxRelease_IsStaged_AndStagedAgainForANewRelease()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        await _service.SyncAsync(game, ["UltrawideSideGlass"]);
        Assert.Contains("v1", File.ReadAllText(Staged("UltrawideSideGlass", "UltrawideSideGlass.fx")));
        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "UltrawideSideGlass", "Shaders", "UltrawideSideGlass.fx")));

        _remote.GlassRelease = 2;
        await _service.RefreshDownloadedAsync();

        Assert.Contains("v2", File.ReadAllText(Staged("UltrawideSideGlass", "UltrawideSideGlass.fx")));
    }

    [Fact]
    public async Task PacksNeverDownloaded_StayOnDemand()
    {
        Assert.Equal(0, await _service.RefreshDownloadedAsync());
        Assert.DoesNotContain(_remote.Requests, url => url.EndsWith(".zip", StringComparison.Ordinal) || url.Contains("releases/download", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "SdReshadeShaders")));
    }

    [Fact]
    public async Task ServerUnreachable_KeepsTheStagedCopy()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        await _service.SyncAsync(game, ["SdReshadeShaders"]);
        _remote.Offline = true;

        Assert.Equal(0, await _service.RefreshDownloadedAsync());
        Assert.Contains("build 1", File.ReadAllText(Staged("SdReshadeShaders", "SD_Density.fx")));
        // A sync offline still deploys the staged copy (the archive's server could not be asked).
        var again = Path.Combine(_root, "game2");
        Directory.CreateDirectory(again);
        await _service.SyncAsync(again, ["SdReshadeShaders"]);
        Assert.True(File.Exists(Path.Combine(again, "reshade-shaders", "RHI", "SdReshadeShaders", "Shaders", "SD_Density.fx")));
    }

    private sealed class Remote : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public int SdBuild { get; set; } = 1;
        public int GlassRelease { get; set; } = 1;
        public bool Offline { get; set; }

        public int Gets(string url) { lock (_gets) return _gets.GetValueOrDefault(url); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (url == ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestFixture, Encoding.UTF8, "application/json") });
            if (Offline) throw new HttpRequestException("offline (test)");
            if (request.Method == HttpMethod.Get) lock (_gets) _gets[url] = _gets.GetValueOrDefault(url) + 1;

            if (url == SdArchive)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(Zip(("sd-reshade-shaders-main/Shaders/SD_Density.fx", $"// build {SdBuild} " + new string('x', 700))))
                };
                response.Headers.ETag = new EntityTagHeaderValue($"\"sd-{SdBuild}\"");
                return Task.FromResult(response);
            }
            if (url == GlassApi)
            {
                var tag = "v" + GlassRelease;
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = tag,
                    assets = new[] { new { name = "UltrawideSideGlass.fx", browser_download_url = $"https://github.com/NickFirzen/Ultrawide-Side-Glass-Reshade-Shader/releases/download/{tag}/UltrawideSideGlass.fx" } }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) });
            }
            if (url.StartsWith("https://github.com/NickFirzen/Ultrawide-Side-Glass-Reshade-Shader/releases/download/", StringComparison.Ordinal))
            {
                var release = url.Split('/')[^2];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"// {release} " + new string('g', 700)) });
            }
            if (url.Contains("crosire/reshade-shaders", StringComparison.Ordinal))
            {
                var headers = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(Zip(
                        ("reshade-shaders-slim/Shaders/ReShade.fxh", new string('h', 700)), ("reshade-shaders-slim/Shaders/ReShadeUI.fxh", new string('u', 700))))
                };
                headers.Headers.ETag = new EntityTagHeaderValue("\"core\"");
                return Task.FromResult(headers);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] Zip(params (string Name, string Content)[] files)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in files)
                {
                    using var entry = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                    entry.Write(content);
                }
            return stream.ToArray();
        }
    }
}
