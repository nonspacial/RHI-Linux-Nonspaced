using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxUnityCacheTests : IDisposable
{
    private const string PagesUnityUrl = "https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-unity-cache-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxUnityCacheTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UnityAddonUrls_ResolveToHttpsGitHubReleasesWhateverTheSource()
    {
        var entry = new RenoDxModEntry("Outer Wilds", PagesUnityUrl, "https://clshortfuse.github.io/renodx/renodx-unityengine.addon32", null);

        Assert.Equal(RenoDxCatalogService.GenericUnityUrl64, entry.UrlFor(PeArchitecture.X64));
        Assert.Equal(RenoDxCatalogService.GenericUnityUrl32, entry.UrlFor(PeArchitecture.X86));
        Assert.Equal("https://cdn.test/renodx-sf6.addon64", RenoDxCatalogService.ResolveAddonUrl("https://cdn.test/renodx-sf6.addon64"));
        Assert.All([RenoDxCatalogService.GenericUnityUrl64, RenoDxCatalogService.GenericUnityUrl32],
            url => Assert.StartsWith("https://github.com/NotVoosh/renodx-unity/releases/download/", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestPagesUnityUrl_InstallsTwiceWithOneDownloadFromGitHubReleases()
    {
        var (game, files) = CreateUnityGame();
        var handler = new CountingHandler(manifestUnityUrl: PagesUnityUrl);
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        var cacheDir = Path.Combine(_root, "cache");
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), cacheDir, manifest);

        var first = await service.InstallAsync(game, files);
        var second = await service.InstallAsync(game, files);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, handler.Gets(RenoDxCatalogService.GenericUnityUrl64));
        Assert.Equal(0, handler.Gets(PagesUnityUrl));
        Assert.Equal(RenoDxCatalogService.GenericUnityUrl64, store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.True(File.Exists(Path.Combine(cacheDir, "renodx-unityengine.addon64")));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-unityengine.addon64")));
    }

    [Fact]
    public async Task GenericUnity_RemoteSizeChangeForcesFreshDownload()
    {
        var (game, files) = CreateUnityGame();
        var handler = new CountingHandler(manifestUnityUrl: null);
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"));

        Assert.True((await service.InstallAsync(game, files)).Success);
        handler.AddonSize += 1024;
        var update = await service.InstallAsync(game, files);

        Assert.True(update.Success, update.Message);
        Assert.Equal(2, handler.Gets(RenoDxCatalogService.GenericUnityUrl64));
        Assert.Equal(handler.AddonSize, new FileInfo(Path.Combine(game.BinaryDirectory!, "renodx-unityengine.addon64")).Length);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (DetectedGame Game, string[] Files) CreateUnityGame()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "OuterWilds.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        File.WriteAllText(Path.Combine(bin, "UnityPlayer.dll"), "MZ");
        var game = new DetectedGame
        {
            Name = "Outer Wilds", InstallPath = _root, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 753640
        };
        return (game, [exe, Path.Combine(bin, "ReShade64.dll"), Path.Combine(bin, "UnityPlayer.dll")]);
    }

    private sealed class CountingHandler(string? manifestUnityUrl) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);
        public int AddonSize { get; set; } = 120 * 1024;

        public int Gets(string url) { lock (_gets) return _gets.GetValueOrDefault(url); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://example.test/manifest.json" && manifestUnityUrl is not null)
            {
                var json = $$"""{ "version": 2, "snapshotOverrides": { "Outer Wilds": "{{manifestUnityUrl}}" } }""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            if (!url.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (request.Method == HttpMethod.Get)
                lock (_gets) _gets[url] = _gets.GetValueOrDefault(url) + 1;
            var bytes = new byte[AddonSize];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
