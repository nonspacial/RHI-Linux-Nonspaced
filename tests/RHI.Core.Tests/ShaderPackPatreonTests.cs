using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #265 (upstream 2.9.4 manifest: "Zenteon+ Shaders by Zenteon", <c>"kind": "DirectUrl"</c>, a free Patreon file): the
/// link is <c>https://www.patreon.com/file?h=…&amp;m=…</c>, which answers 302 to a signed link on
/// <c>c&lt;digits&gt;.patreonusercontent.com</c> serving the zip (checked by hand 2026-11-17: 302, then a 2,054,621-byte zip,
/// no login). Recorded answers only, no network: the pack installs; every redirect is checked before it is followed; no
/// cookie or token is sent; the size cap holds; the signed query string never reaches a message or the log.
/// </summary>
public sealed class ShaderPackPatreonTests : IDisposable
{
    private const string ManifestUrl = "https://example.test/manifest.json";
    private const string FileUrl = "https://www.patreon.com/file?h=145859259&m=580770455";
    private const string Secret = "token-hash=S3cr3tSignature";
    private const string SignedUrl = "https://c10.patreonusercontent.com/4/patreon-media/p/post/145859259/abc123/eyJhIjoxfQ%3D%3D/1.zip?" + Secret + "&token-time=1763337600";

    private const string Manifest = """
        { "version": 2, "shaderPacks": { "ZenteonPlus": {
            "displayName": "Zenteon+ Shaders by Zenteon", "kind": "DirectUrl",
            "url": "https://www.patreon.com/file?h=145859259&m=580770455", "category": "Extra" } } }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shader-patreon-" + Guid.NewGuid().ToString("N"));
    private readonly Remote _remote = new();
    private readonly HttpClient _http;
    private readonly HttpClient _patreonHttp;
    private readonly ReShadeShaderPackService _service;

    public ShaderPackPatreonTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_remote);
        _patreonHttp = new HttpClient(_remote);
        _service = new ReShadeShaderPackService(_http, Path.Combine(_root, "cache"), new UpstreamRhiManifestService(_http, ManifestUrl),
            Path.Combine(_root, "custom")) { PatreonHttp = _patreonHttp };
    }

    public void Dispose()
    {
        _http.Dispose();
        _patreonHttp.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Game()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        return game;
    }

    [Fact]
    public async Task TheFreePatreonPack_Installs_ThroughTheCheckedRedirect_WithNoCookieOrToken()
    {
        var game = Game();

        await _service.SyncAsync(game, ["ZenteonPlus"]);

        Assert.True(File.Exists(Path.Combine(game, "reshade-shaders", "RHI", "ZenteonPlus", "Shaders", "ZN_GI.fx")));
        var patreon = _remote.Seen.Where(r => r.Url.Contains("patreon", StringComparison.Ordinal)).ToList();
        Assert.Equal([("GET", FileUrl), ("GET", SignedUrl)], patreon.Select(r => (r.Method, r.Url)));
        Assert.All(patreon, r => Assert.False(r.HadCookieOrAuthorization));
    }

    [Theory]
    [InlineData("https://evil.example/1.zip?" + Secret)] // another host
    [InlineData("http://c10.patreonusercontent.com/1.zip?" + Secret)] // not HTTPS
    [InlineData("https://c10.patreonusercontent.com.evil.example/1.zip?" + Secret)] // a look-alike
    [InlineData("https://www.patreon.com/posts/123?" + Secret)] // Patreon, but not /file
    public async Task ARedirectOffTheAllowedHosts_IsNeverFollowed_AndItsSignatureIsNeverShown(string location)
    {
        _remote.Location = location;
        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        using var flow = AppLog.UseInThisFlow(log);

        var refused = await Assert.ThrowsAsync<UntrustedDownloadException>(() => _service.SyncAsync(Game(), ["ZenteonPlus"]));

        Assert.StartsWith(DownloadSources.ShaderPackPatreon.RefusalPrefix, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_remote.Seen, r => r.Url.StartsWith(location.Split('?')[0], StringComparison.Ordinal) && r.Url != FileUrl);
        Assert.False(File.Exists(log.FilePath) && File.ReadAllText(log.FilePath).Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileOverTheCap_IsRefusedBeforeItsBodyIsRead()
    {
        _remote.AnnouncedLength = 64L * 1024 * 1024 + 1;

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => _service.SyncAsync(Game(), ["ZenteonPlus"]));

        Assert.StartsWith(TrustedDownload.SizeLimitPrefix + (64L * 1024 * 1024), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndlessRedirects_StopAtTheLimit()
    {
        _remote.Location = FileUrl; // /file redirecting to itself

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.SyncAsync(Game(), ["ZenteonPlus"]));

        Assert.Equal(TrustedDownload.MaxCheckedRedirects + 1, _remote.Seen.Count(r => r.Url == FileUrl));
    }

    [Theory]
    [InlineData(FileUrl, true)]
    [InlineData(SignedUrl, true)]
    [InlineData("https://c3.patreonusercontent.com/x.zip", true)]
    [InlineData("https://www.patreon.com/posts/zenteon-123", false)] // only /file
    [InlineData("https://www.patreon.com/file/../posts", false)]
    [InlineData("https://patreon.com/file?h=1&m=2", false)] // not www
    [InlineData("http://www.patreon.com/file?h=1&m=2", false)]
    [InlineData("https://cx.patreonusercontent.com/x.zip", false)]
    [InlineData("https://c10.patreonusercontent.com.evil.example/x.zip", false)]
    [InlineData("https://www.patreon.com:8443/file?h=1", false)]
    public void ThePatreonPolicy(string url, bool allowed)
        => Assert.Equal(allowed, DownloadSources.ShaderPackPatreon.IsTrusted(url));

    [Fact]
    public void ThePatreonClient_FollowsNoRedirectItself_AndKeepsNoCookies()
    {
        using var handler = ReShadeShaderPackService.PatreonHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void OtherShaderPacks_StillCannotReachPatreon()
    {
        Assert.False(DownloadSources.ShaderPacks.IsTrusted(FileUrl));
        Assert.False(DownloadSources.ShaderPacks.IsTrusted(SignedUrl));
        Assert.Equal(64L * 1024 * 1024, DownloadSources.ShaderPackPatreon.MaxBytes);
    }

    private sealed class Remote : HttpMessageHandler
    {
        public List<(string Method, string Url, bool HadCookieOrAuthorization)> Seen { get; } = [];
        public string Location { get; set; } = SignedUrl;
        public long? AnnouncedLength { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            var url2 = request.RequestUri!.AbsoluteUri;
            if (url2 == ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") });
            lock (Seen) Seen.Add((request.Method.Method, url, request.Headers.Contains("Cookie") || request.Headers.Authorization is not null));

            if (url == FileUrl)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(Location);
                redirect.Headers.Add("Set-Cookie", "session_id=abc; Domain=.patreon.com; Secure; HttpOnly");
                return Task.FromResult(redirect);
            }
            if (url == SignedUrl)
            {
                var content = new ByteArrayContent(Zip(("Zenteon+/Shaders/ZN_GI.fx", "// Zenteon+ " + new string('z', 900))));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                if (AnnouncedLength is { } length) content.Headers.ContentLength = length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
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
