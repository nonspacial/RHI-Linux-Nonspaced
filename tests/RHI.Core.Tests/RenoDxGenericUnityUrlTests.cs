using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #268 (upstream 2.9.5 beta a593a09): the generic Unity add-on from its new location, overridable by manifest componentUrls.
public sealed class RenoDxGenericUnityUrlTests : IDisposable
{
    private const string OldRelease64 = "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64";
    private const string NewPages64 = "https://notvoosh.github.io/renodx/renodx-unityengine.addon64";
    private const string Release32 = "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon32";
    private const string ManifestUrl = "https://example.test/manifest.json";

    private static readonly string ManifestFixture =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rhi-manifest-componentUrls-7e234dc.json"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-unity-url-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxGenericUnityUrlTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void BuiltInLinks_AreUpstreams_NewPages64_AndRelease32()
    {
        Assert.Equal(NewPages64, GenericUnityAddonUrls.Default.Url64);
        Assert.Equal(Release32, GenericUnityAddonUrls.Default.Url32);
        // Every older link of the add-on (release, old Pages, ShortFuse's Pages) follows the current one.
        Assert.Equal(NewPages64, RenoDxCatalogService.ResolveAddonUrl(OldRelease64));
        Assert.Equal(NewPages64, RenoDxCatalogService.ResolveAddonUrl("https://notvoosh.github.io/renodx-unity/renodx-unityengine.addon64"));
        Assert.Equal(Release32, RenoDxCatalogService.ResolveAddonUrl("https://clshortfuse.github.io/renodx/renodx-unityengine.addon32"));
    }

    [Fact]
    public void ManifestComponentUrls_PinnedUpstreamManifest_GivesBothLinks()
    {
        var urls = GenericUnityAddonUrls.FromComponentUrls(new Dictionary<string, string>
        {
            ["dc"] = "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
            ["unity64"] = NewPages64,
            ["unity32"] = Release32,
        });
        Assert.Equal(new GenericUnityAddonUrls(NewPages64, Release32), urls);
    }

    [Fact]
    public void ManifestComponentUrls_OverrideEachLink_AndInvalidLinksKeepTheBuiltInOne()
    {
        var moved = GenericUnityAddonUrls.FromComponentUrls(new Dictionary<string, string>
        {
            ["Unity64"] = " https://voosh.github.io/x/renodx-unityengine.addon64 ",
            ["unity32"] = "not a link",
        });
        Assert.Equal("https://voosh.github.io/x/renodx-unityengine.addon64", moved.Url64);
        Assert.Equal(Release32, moved.Url32);

        // Linux: http, an untrusted host or the wrong bitness keep the built-in link (the install would refuse them).
        foreach (var bad in new[] { "http://notvoosh.github.io/renodx/renodx-unityengine.addon64", "https://evil.example/renodx-unityengine.addon64",
                     "https://notvoosh.github.io/renodx/renodx-unityengine.addon32" })
            Assert.Equal(NewPages64, GenericUnityAddonUrls.FromComponentUrls(new Dictionary<string, string> { ["unity64"] = bad }).Url64);
        Assert.Equal(GenericUnityAddonUrls.Default, GenericUnityAddonUrls.FromComponentUrls(null));
    }

    [Fact]
    public async Task Catalog_GenericUnityEntry_FollowsTheManifestLink()
    {
        var handler = new Handler(manifest: """{ "version": 2, "componentUrls": { "unity64": "https://voosh.github.io/x/renodx-unityengine.addon64" } }""");
        using var http = new HttpClient(handler);
        var catalog = new RenoDxCatalogService(http, new UpstreamRhiManifestService(http, ManifestUrl));
        var game = new DetectedGame { Name = "Unknown Unity Game", InstallPath = "/tmp/game" };

        var entry = await catalog.ResolveAsync(game, ["/tmp/game/UnityPlayer.dll"]);

        Assert.NotNull(entry);
        Assert.Equal("https://voosh.github.io/x/renodx-unityengine.addon64", entry!.UrlFor(PeArchitecture.X64));
        Assert.Equal(Release32, entry.UrlFor(PeArchitecture.X86));
    }

    [Fact]
    public async Task InstalledFromTheOldReleaseLink_ShowsAnUpdate_ThatInstallsFromTheNewLink()
    {
        var (game, files) = CreateUnityGame();
        var handler = new Handler(manifest: """{ "version": 2, "componentUrls": { "unity64": "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64" } }""");
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, ManifestUrl);
        var oldService = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest);
        Assert.True((await oldService.InstallAsync(game, files)).Success);
        Assert.Equal(OldRelease64, store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);

        handler.Manifest = ManifestFixture; // the manifest now moves unity64 to the new Pages link
        var manifestNow = new UpstreamRhiManifestService(http, ManifestUrl);
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifestNow), Path.Combine(_root, "cache"), manifestNow);
        var check = await service.CheckUpdateAsync(game, files);
        var update = await service.UpdateAsync(game, files);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.True(update.Success, update.Message);
        Assert.Equal(NewPages64, store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.Equal(1, handler.Gets(NewPages64));
    }

    [Fact]
    public async Task UnreachableNewLink_FallsBackToTheOldRelease_AndKeepsTheCatalogueLinkOnTheRecord()
    {
        var (game, files) = CreateUnityGame();
        var handler = new Handler(manifest: null) { Unreachable = { NewPages64 } };
        using var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var service = new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http), Path.Combine(_root, "cache"));

        var result = await service.InstallAsync(game, files);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, handler.Gets(OldRelease64));
        Assert.Equal(NewPages64, store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-unityengine.addon64")));
    }

    [Fact]
    public async Task EveryUnityLinkUnreachable_NamesEachLinkTried()
    {
        var (game, files) = CreateUnityGame();
        var handler = new Handler(manifest: null) { AllUnreachable = true };
        using var http = new HttpClient(handler);
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")),
            new RenoDxCatalogService(http), Path.Combine(_root, "cache"));

        var result = await service.InstallAsync(game, files);

        Assert.False(result.Success);
        Assert.Contains(NewPages64, result.Message);
        Assert.Contains(OldRelease64, result.Message);
        Assert.Contains("https://clshortfuse.github.io/renodx/renodx-unityengine.addon64", result.Message);
    }

    private (DetectedGame Game, string[] Files) CreateUnityGame()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        File.WriteAllText(Path.Combine(bin, "UnityPlayer.dll"), "MZ");
        var game = new DetectedGame { Name = "Some Unity Game", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton" };
        return (game, [exe, Path.Combine(bin, "ReShade64.dll"), Path.Combine(bin, "UnityPlayer.dll")]);
    }

    private sealed class Handler(string? manifest) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);
        public string? Manifest { get; set; } = manifest;
        public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);
        public bool AllUnreachable { get; set; }

        public int Gets(string url) { lock (_gets) return _gets.GetValueOrDefault(url); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ManifestUrl)
                return Task.FromResult(Manifest is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") });
            if (!url.Contains(".addon", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (AllUnreachable || Unreachable.Contains(url))
                throw new HttpRequestException("Name or service not known");
            if (request.Method == HttpMethod.Get)
                lock (_gets) _gets[url] = _gets.GetValueOrDefault(url) + 1;
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + url.GetHashCode().ToString("x") + "\"");
            return Task.FromResult(response);
        }
    }
}
