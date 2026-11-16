using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// #258: upstream 2.8.8's manifest-only commits reach Linux through the fetched manifest — Portal's install path
/// (<c>e40845a</c>) and the DAEMON X MACHINA / Phantom Blade Zero engine versions (<c>2281a09</c>, <c>d4dba12</c>). The
/// entries are copied from upstream's <c>manifest.json</c> at tag <c>RHI-2.8.8</c>, together with the NVIDIA profile
/// override (<c>fc63285</c>), which the fork does not read (no driver profile under Wine/Proton) and must not trip over.
/// </summary>
public sealed class UpstreamManifest288ReleaseTests : IDisposable
{
    private const string Manifest288 = """
        {
          "version": 3,
          "installPathOverrides": { "Outlast": "Binaries\\Win64", "Portal": "bin" },
          "engineHintOverrides": {
            "DAEMON X MACHINA": "Unreal Engine 4.20.3",
            "Phantom Blade Zero": "Unreal Engine 5.7.4"
          },
          "profileNameOverrides": {
            "Dead Space": "Dead Space (Remake)",
            "Lords of the Fallen": "Lords of the Fallen (2023)",
            "Metro 2033 Redux": "Metro Redux"
          }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-manifest-288-" + Guid.NewGuid().ToString("N"));

    public UpstreamManifest288ReleaseTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static UpstreamRhiManifestService Manifest()
        => new(new HttpClient(new JsonHandler(Manifest288)), "https://manifest.test/manifest.json");

    [Fact]
    public async Task Portal_InstallPathOverride_PointsModsAtBin()
    {
        var root = Path.Combine(_root, "steamapps", "common", "Portal");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var game = new DetectedGame { Name = "Portal", InstallPath = root, Source = "Steam", Runner = "Proton" };

        var applied = Assert.Single(await new UpstreamGameLibraryPolicyService(Manifest()).ApplyAsync([game]));

        Assert.Equal(bin, applied.InstallPath);
        Assert.Equal(root, applied.ManifestInstallRoot);
    }

    [Theory]
    [InlineData("DAEMON X MACHINA", "Unreal Engine 4.20.3")]
    [InlineData("Phantom Blade Zero", "Unreal Engine 5.7.4")]
    public async Task EngineVersions_ReachTheEngineHint(string name, string engine)
    {
        var overrides = await Manifest().FetchGameOverridesAsync();

        Assert.Equal(engine, overrides[name].EngineHint);
        // The engine hint drives UE rules such as the DOF Fix range (UE 5.0–5.6): neither of these is eligible.
        Assert.False(DofFixDeploymentService.IsGameEligible(new DetectedGame { Name = name, InstallPath = "/g", EngineHint = engine }));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
