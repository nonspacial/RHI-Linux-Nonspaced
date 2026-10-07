using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>Manifest titles are recovered only from the game's own folder names, never from a parent folder (Refs #62).</summary>
public sealed class CanonicalPathIdentityTests : IDisposable
{
    private const string Manifest = """
        {
          "version": 3,
          "installPathOverrides": { "Hades": "x64" },
          "thirtyTwoBitGames": ["Hades"],
          "engineOverrides": { "Hades": "Custom" },
          "lumaNameOverrides": { "Hades": "Hades Luma" },
          "dllNameOverrides": { "Hades": { "reshade": "d3d11.dll" } }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-canon-" + Guid.NewGuid().ToString("N"));

    public CanonicalPathIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GameInsideAFolderNamedAfterAnotherTitle_KeepsItsOwnIdentity()
    {
        var install = Dir("Games", "Hades", "shipwright");
        Dir("Games", "Hades", "shipwright", "x64");
        var game = new DetectedGame { Name = "Ship of Harkinian", InstallPath = install, Source = "Lutris", Runner = "Wine" };
        var manifest = Service();

        var applied = Assert.Single(await new UpstreamGameLibraryPolicyService(manifest).ApplyAsync([game]));

        Assert.Equal("Ship of Harkinian", applied.CanonicalManifestName);
        Assert.Equal(install, applied.InstallPath);
        Assert.Null(applied.ReShadeDllNameOverride);
        Assert.Null(await manifest.ResolveGameOverrideAsync(applied));
        Assert.Null(await manifest.ResolveLumaNameOverrideAsync(applied));
    }

    [Fact]
    public async Task GameFolderOrExecutableNamedAfterTheTitle_StillResolves()
    {
        var folder = Dir("Games", "Hades");
        var byFolder = new DetectedGame { Name = "hades-gog", InstallPath = folder, Source = "Heroic", Runner = "Wine" };
        var other = Dir("Games", "supergiant");
        var exe = Path.Combine(other, "Hades.exe");
        await File.WriteAllBytesAsync(exe, [0x4d, 0x5a]);
        var byExe = new DetectedGame { Name = "sg", InstallPath = other, ExecutablePath = exe, Source = "Lutris", Runner = "Wine" };
        var manifest = Service();

        var forFolder = await manifest.ResolveGameOverrideAsync(byFolder);
        var forExe = await manifest.ResolveGameOverrideAsync(byExe);

        Assert.Equal(PeArchitecture.X86, forFolder?.ForcedArchitecture);
        Assert.Equal(PeArchitecture.X86, forExe?.ForcedArchitecture);
        Assert.Equal("Hades Luma", await manifest.ResolveLumaNameOverrideAsync(byFolder));
    }

    private UpstreamRhiManifestService Service()
        => new(new HttpClient(new JsonHandler(Manifest)), "https://manifest.test/manifest.json");

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
