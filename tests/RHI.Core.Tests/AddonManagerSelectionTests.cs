using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #28 (DEP-09): the Addon Manager's selection change runs as one owned-file transaction: a later failure (a payload that
/// cannot be placed, or the state save) puts every earlier add-on back from verified copies and leaves no copy behind.
/// </summary>
public sealed class AddonManagerSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-addon-manager-" + Guid.NewGuid().ToString("N"));

    public AddonManagerSelectionTests() => Directory.CreateDirectory(_root);

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private (ReShadeAddonManagerService Service, DetectedGame Game, string StateDir) Fixture()
    {
        var game = P("game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "ReShade by crosire");
        PeArchitectureDetectorTests.WritePe(Path.Combine(game, "Game.exe"), 0x8664, 64 * 1024);
        var custom = P("custom");
        Directory.CreateDirectory(custom);
        byte marker = 1;
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            File.WriteAllBytes(Path.Combine(custom, name + ".addon64"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, marker++));
        var stateDir = P("state");
        Directory.CreateDirectory(stateDir);
        var http = new HttpClient(new RouteHandler(req => req.RequestUri?.AbsoluteUri == ReShadeAddonManagerService.AddonsIniUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[01]\nPackageName=Generic\n", Encoding.UTF8) }
            : req.RequestUri?.AbsoluteUri == "https://manifest.test/manifest.json"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":2}", Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, P("manifest-cache.json"));
        var service = new ReShadeAddonManagerService(http, manifest, P("addon-cache"), custom, Path.Combine(stateDir, "addon-manager.json"));
        return (service, new DetectedGame
        {
            Name = "Addon Game", Source = "Steam", SteamAppId = 4242, InstallPath = game, BinaryDirectory = game,
            ExecutablePath = Path.Combine(game, "Game.exe"), Runner = "Proton"
        }, stateDir);
    }

    [Fact]
    public async Task ASecondPayloadFailing_PutsTheEarlierAddonsBack_AndKeepsTheSelection()
    {
        var (service, game, _) = Fixture();
        Assert.True((await service.ApplySelectionAsync(game, ["custom-Alpha"])).Success);
        var alpha = Path.Combine(game.BinaryDirectory!, "Alpha.addon64");
        var alphaBytes = File.ReadAllBytes(alpha);
        // Gamma cannot be placed: a folder holds its name.
        Directory.CreateDirectory(Path.Combine(game.BinaryDirectory!, "Gamma.addon64", "blocker"));

        var apply = await service.ApplySelectionAsync(game, ["custom-Beta", "custom-Gamma"]);

        Assert.False(apply.Success);
        Assert.Equal(alphaBytes, File.ReadAllBytes(alpha)); // deleted before the new ones, then put back
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Beta.addon64")));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*"));
        Assert.Equal(["custom-Alpha"], (await service.GetSnapshotAsync(game)).SelectedIds);
    }

    [Fact]
    public async Task TheStateSaveFailing_PutsEveryAddonBack()
    {
        var (service, game, stateDir) = Fixture();
        Assert.True((await service.ApplySelectionAsync(game, ["custom-Alpha"])).Success);
        var alpha = Path.Combine(game.BinaryDirectory!, "Alpha.addon64");
        var alphaBytes = File.ReadAllBytes(alpha);
        var mode = File.GetUnixFileMode(stateDir);
        File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // the save cannot write its temp
        ComponentOperationResult apply;
        try
        {
            apply = await service.ApplySelectionAsync(game, ["custom-Beta"]);
        }
        finally
        {
            File.SetUnixFileMode(stateDir, mode);
        }

        Assert.False(apply.Success);
        Assert.Equal(alphaBytes, File.ReadAllBytes(alpha));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "Beta.addon64")));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*"));
    }

    [Fact]
    public async Task ImportedCustomAddon_IsAVerifiedCopy_AndNeverReplacesADifferentFile()
    {
        var (service, _, _) = Fixture();
        var source = P("Delta.addon64");
        File.WriteAllBytes(source, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 9));

        Assert.True(service.ImportCustomAddonFile(source).Success);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(service.CustomRoot, "Delta.addon64")));

        File.WriteAllBytes(source, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 10));
        Assert.False(service.ImportCustomAddonFile(source).Success);
        Assert.Empty(Directory.GetFiles(service.CustomRoot, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(service.CustomRoot, "*.rhi-*"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}
