using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>Manifest path policies (installPathOverrides, splitGames, launchExeOverrides) stay inside the game root (Refs #45).</summary>
public sealed class LibraryPolicyContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-contain-" + Guid.NewGuid().ToString("N"));

    public LibraryPolicyContainmentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task InstallPathOverride_ThatClimbsOutOfTheGameRoot_KeepsTheDetectedRoot()
    {
        // Live manifest entry; the Heroic sideload root is ".../Hades II/Content" and has no Ship folder.
        var game = Game("Hades II", Dir("lib", "Hades II", "Content"));
        Dir("lib", "Other Game");

        var applied = Assert.Single(await Policy("""{ "version": 3, "installPathOverrides": { "Hades II": "Ship|..\\.." } }""").ApplyAsync([game]));

        Assert.Equal(Path.Combine(_root, "lib", "Hades II", "Content"), applied.InstallPath);
        Assert.Null(applied.ManifestInstallRoot);
    }

    [Fact]
    public async Task InstallPathOverride_InsideTheRoot_StillApplies()
    {
        var root = Dir("lib", "Hades II");
        var ship = Dir("lib", "Hades II", "Ship");

        var applied = Assert.Single(await Policy("""{ "version": 3, "installPathOverrides": { "Hades II": "Ship|..\\.." } }""").ApplyAsync([Game("Hades II", root)]));

        Assert.Equal(ship, applied.InstallPath);
        Assert.Equal(root, applied.ManifestInstallRoot);
    }

    [Fact]
    public async Task InstallPathOverride_ThroughASymlinkPointingOutOfTheRoot_IsRejected()
    {
        var root = Dir("lib", "Hades II");
        var outside = Dir("lib");
        Directory.CreateSymbolicLink(Path.Combine(root, "Ship"), outside);

        var applied = Assert.Single(await Policy("""{ "version": 3, "installPathOverrides": { "Hades II": "Ship" } }""").ApplyAsync([Game("Hades II", root)]));

        Assert.Equal(root, applied.InstallPath);
    }

    [Fact]
    public async Task InstallPathOverride_AbsolutePath_IsRejected()
    {
        var root = Dir("lib", "Some Game");
        var elsewhere = Dir("elsewhere");

        var applied = Assert.Single(await Policy($$"""{ "version": 3, "installPathOverrides": { "Some Game": "{{elsewhere.Replace("\\", "\\\\")}}" } }""").ApplyAsync([Game("Some Game", root)]));

        Assert.Equal(root, applied.InstallPath);
    }

    [Fact]
    public async Task SplitGames_ChildOutsideTheBundle_IsNotCreated_AndTheBundleStays()
    {
        var bundle = Dir("lib", "Bundle");
        Dir("lib", "Neighbour");

        var result = await Policy("""
            { "version": 3, "splitGames": { "Bundle": [ { "name": "Escaped", "subPath": "..\\Neighbour" } ] } }
            """).ApplyAsync([Game("Bundle", bundle)]);

        var only = Assert.Single(result);
        Assert.Equal("Bundle", only.Name);
        Assert.Equal(bundle, only.InstallPath);
        Assert.False(only.IsManifestSplitChild);
    }

    [Fact]
    public async Task LaunchExeOverride_OutsideTheRoot_DoesNotReplaceTheExecutable()
    {
        var root = Dir("lib", "Some Game");
        var foreign = Path.Combine(Dir("lib", "Other"), "Other.exe");
        await File.WriteAllBytesAsync(foreign, [0x4d, 0x5a]);
        var game = Game("Some Game", root);

        var applied = Assert.Single(await Policy("""{ "version": 3, "launchExeOverrides": { "Some Game": "..\\Other\\Other.exe" } }""").ApplyAsync([game]));

        Assert.Null(applied.ExecutablePath);
    }

    [Fact]
    public void PathIdentity_ResolvesSymlinksAndTrailingSeparators()
    {
        var real = Dir("media", "Games", "X");
        var link = Path.Combine(_root, "home-games");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "media", "Games"));

        Assert.Equal(PathIdentity.Canonicalize(real), PathIdentity.Canonicalize(Path.Combine(link, "X") + "/"));
        Assert.True(PathIdentity.IsSameOrInside(Path.Combine(link, "X", "bin"), real));
        Assert.False(PathIdentity.IsSameOrInside(Path.Combine(_root, "media", "Games", "XY"), real));
        Assert.Equal("/", PathIdentity.Canonicalize("/"));
    }

    private UpstreamGameLibraryPolicyService Policy(string json)
        => new(new UpstreamRhiManifestService(new HttpClient(new JsonHandler(json)), "https://manifest.test/manifest.json"));

    private static DetectedGame Game(string name, string path)
        => new() { Name = name, InstallPath = path, Source = "Heroic", Runner = "Wine" };

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
