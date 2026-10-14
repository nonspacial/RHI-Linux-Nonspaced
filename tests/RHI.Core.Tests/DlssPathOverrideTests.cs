using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// #155 (upstream 2.8.6 881a98c, cac10d7, 7d500b0): manifest <c>dlssPathOverrides</c>/<c>streamlinePathOverrides</c>.
/// Layout copied from upstream's manifest entry for The Sinking City 2: DLSS and Streamline 9 folders below the root,
/// past the depth-8 walk; <c>nvngx_dlssg.dll</c> sits beside Streamline.
/// </summary>
public sealed class DlssPathOverrideTests : IDisposable
{
    internal const string DlssRel = @"Engine\Plugins\FrogwaresPlugins\ThirdPartyPlugins\Nvidia\DLSS\Binaries\ThirdParty\Win64";
    internal const string StreamlineRel = @"Engine\Plugins\FrogwaresPlugins\ThirdPartyPlugins\Nvidia\StreamlineCore\Binaries\ThirdParty\Win64";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-path-override-" + Guid.NewGuid().ToString("N"));

    public DlssPathOverrideTests() => Directory.CreateDirectory(_root);

    internal static (string Dlss, string Streamline) SinkingCityLayout(string gameRoot)
    {
        var dlss = Path.Combine(gameRoot, DlssRel.Replace('\\', '/'));
        var streamline = Path.Combine(gameRoot, StreamlineRel.Replace('\\', '/'));
        Directory.CreateDirectory(dlss);
        Directory.CreateDirectory(streamline);
        File.WriteAllBytes(Path.Combine(dlss, "nvngx_dlss.dll"), [0x4d, 0x5a, 1]);
        File.WriteAllBytes(Path.Combine(dlss, "nvngx_dlssd.dll"), [0x4d, 0x5a, 2]);
        File.WriteAllBytes(Path.Combine(streamline, "sl.interposer.dll"), [0x4d, 0x5a, 3]);
        File.WriteAllBytes(Path.Combine(streamline, "nvngx_dlssg.dll"), [0x4d, 0x5a, 4]);
        return (dlss, streamline);
    }

    private DetectedGame Game(string name, string? dlssOverride = DlssRel, string? streamlineOverride = StreamlineRel)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        return new DetectedGame
        {
            Name = "The Sinking City 2", InstallPath = root, BinaryDirectory = root, Source = "Test", Runner = "Wine",
            DlssPathOverride = dlssOverride, StreamlinePathOverride = streamlineOverride
        };
    }

    private static DlssManagerService Manager(string cache) => new(new HttpClient(new NoNetwork()), new TransactionalFileDeploymentService(), cache);

    [Fact]
    public void Detect_ManifestFolders_FindDllsPastTheDepth8Walk_AndDlssgBesideStreamline()
    {
        var game = Game("sinking");
        var (dlss, streamline) = SinkingCityLayout(game.InstallPath);
        var index = GameFileIndexer.Build(game.InstallPath, 8);
        var manager = Manager(Path.Combine(_root, "cache"));

        var walked = manager.Detect(game.InstallPath, null, index);
        var detected = manager.Detect(game, index, protectedFiles: null);

        Assert.False(walked.HasAny); // the depth-8 walk alone misses them (why the manifest names the folder)
        Assert.Equal([Path.Combine(dlss, "nvngx_dlss.dll")], detected.DlssPaths);
        Assert.Equal([Path.Combine(dlss, "nvngx_dlssd.dll")], detected.DlssdPaths);
        Assert.Equal([Path.Combine(streamline, "nvngx_dlssg.dll")], detected.DlssgPaths);
        Assert.Equal([streamline], detected.StreamlineFolders);
        Assert.Empty(detected.DlssnrPaths);
    }

    [Fact]
    public void Detect_WithoutStreamlineOverride_ReadsStreamlineFromTheDlssFolder()
    {
        var game = Game("dlss-only", streamlineOverride: null);
        var (dlss, _) = SinkingCityLayout(game.InstallPath);
        File.WriteAllBytes(Path.Combine(dlss, "sl.common.dll"), [0x4d, 0x5a]);

        var detected = Manager(Path.Combine(_root, "cache")).Detect(game, null, null);

        Assert.Equal([dlss], detected.StreamlineFolders);
        Assert.Empty(detected.DlssgPaths); // dlssg lives in the (unnamed) Streamline folder: not looked for elsewhere
    }

    [Fact]
    public void Detect_ManifestFolder_MatchesFileNamesIgnoringCase_AndDropsProtectedFiles()
    {
        var game = Game("case");
        var (dlss, _) = SinkingCityLayout(game.InstallPath);
        File.Move(Path.Combine(dlss, "nvngx_dlss.dll"), Path.Combine(dlss, "NVNGX_DLSS.DLL"));

        var detected = Manager(Path.Combine(_root, "cache")).Detect(game, null, protectedFiles: [Path.Combine(dlss, "nvngx_dlssd.dll")]);

        Assert.Equal([Path.Combine(dlss, "NVNGX_DLSS.DLL")], detected.DlssPaths);
        Assert.Empty(detected.DlssdPaths);
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData("/etc")]
    public void Detect_OverrideLeavingTheGameRoot_IsIgnored_AndTheNormalWalkRuns(string escape)
    {
        var game = Game("escape-" + escape.GetHashCode().ToString("x"), dlssOverride: escape);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "nvngx_dlss.dll"), [0x4d, 0x5a]);
        var shallow = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        File.WriteAllBytes(shallow, [0x4d, 0x5a]);

        var detected = Manager(Path.Combine(_root, "cache")).Detect(game, null, null);

        Assert.Equal([shallow], detected.DlssPaths);
    }

    [Fact]
    public void Detect_OverrideThroughASymlinkPointingOut_IsIgnored()
    {
        var game = Game("symlink", dlssOverride: "linked", streamlineOverride: null);
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "nvngx_dlss.dll"), [0x4d, 0x5a]);
        Directory.CreateSymbolicLink(Path.Combine(game.InstallPath, "linked"), outside);

        Assert.Null(DlssManagerService.ResolveManifestFolder(game.InstallPath, "linked"));
        Assert.False(Manager(Path.Combine(_root, "cache")).Detect(game, null, null).HasAny);
    }

    [Fact]
    public void Detect_MissingOverrideFolder_FallsBackToTheNormalWalk()
    {
        var game = Game("missing");
        var shallow = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        File.WriteAllBytes(shallow, [0x4d, 0x5a]);

        Assert.Equal([shallow], Manager(Path.Combine(_root, "cache")).Detect(game, null, null).DlssPaths);
    }

    [Fact]
    public async Task Policy_BindsBothOverridesToTheGame_AsTheManifestSpellsThem()
    {
        var json = """
        {
          "version": 4,
          "dlssPathOverrides": { "The Sinking City 2": "Engine\\Plugins\\FrogwaresPlugins\\ThirdPartyPlugins\\Nvidia\\DLSS\\Binaries\\ThirdParty\\Win64" },
          "streamlinePathOverrides": { "The Sinking City 2": "Engine\\Plugins\\FrogwaresPlugins\\ThirdPartyPlugins\\Nvidia\\StreamlineCore\\Binaries\\ThirdParty\\Win64" }
        }
        """;
        using var http = new HttpClient(new JsonHandler(json));
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json",
            cachePath: Path.Combine(_root, "manifest-cache.json")));
        var root = Path.Combine(_root, "The Sinking City 2");
        Directory.CreateDirectory(root);
        var other = Path.Combine(_root, "Other Game");
        Directory.CreateDirectory(other);

        var applied = await policy.ApplyAsync([
            new DetectedGame { Name = "The Sinking City 2", InstallPath = root, Source = "Test", Runner = "Wine" },
            new DetectedGame { Name = "Other Game", InstallPath = other, Source = "Test", Runner = "Wine" }]);

        var sinking = applied.Single(g => g.Name == "The Sinking City 2");
        Assert.Equal(DlssRel, sinking.DlssPathOverride);
        Assert.Equal(StreamlineRel, sinking.StreamlinePathOverride);
        var unrelated = applied.Single(g => g.Name == "Other Game");
        Assert.Null(unrelated.DlssPathOverride);
        Assert.Null(unrelated.StreamlinePathOverride);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("detection must not touch the network");
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
