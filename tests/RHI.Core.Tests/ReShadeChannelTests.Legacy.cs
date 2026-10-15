using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

// #186 (upstream legacyReShadeAvailable / legacyReShadeVersions / reshadeGameInfo): the Legacy channel's version list
// comes from the RHI manifest, a manifest pin picks the version of a fresh install, and an installed ReShade never moves.
public sealed partial class ReShadeChannelTests
{
    private const string LegacyManifestUrl = "https://raw.githubusercontent.com/rhi-tests/rhi/main/manifest.json";

    private const string LegacyManifest = """
        {
          "version": 3,
          "legacyReShadeAvailable": ["6.7.3", "6.4.1", "6.4.1", "latest; rm -rf /", " 6.0.0 ", ""],
          "legacyReShadeVersions": { "Max Payne 3": "6.4.1", "Bad Pin": "../../x" },
          "reshadeGameInfo": { "Max Payne 3": { "notes": "ReShade 6.4.1 is required for RenoDX.", "notesUrl": "https://github.com/rhi-tests/notes", "notesUrlLabel": "Guide" } }
        }
        """;

    private static HttpClient ManifestHttp(string json, Func<HttpRequestMessage, HttpResponseMessage?>? other = null)
        => new(new ManifestHandler(json, other));

    [Fact]
    public async Task LegacyList_IsTheManifestsPlainVersionsInOrder_ElseTheDefault()
    {
        using var http = ManifestHttp(LegacyManifest);
        var manifest = new UpstreamRhiManifestService(http, LegacyManifestUrl);
        var cache = Path.Combine(_root, "legacy-list-cache");
        var (_, store) = CreateGame("legacy-list");
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new FakeExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom")),
            upstreamManifest: manifest);

        Assert.Equal(["6.7.3", "6.4.1", "6.0.0"], await service.LegacyVersionsAsync());

        using var empty = ManifestHttp("""{ "version": 3 }""");
        var withoutList = new ReShadeDeploymentService(empty, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new FakeExtractor(), cache, new ReShadeShaderPackService(empty, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom")),
            upstreamManifest: new UpstreamRhiManifestService(empty, LegacyManifestUrl));
        Assert.Equal(["6.4.1"], await withoutList.LegacyVersionsAsync());

        using var down = new HttpClient(new MutableHandler { HomeUnreachable = true });
        var offline = new ReShadeDeploymentService(down, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(),
            new FakeExtractor(), cache, new ReShadeShaderPackService(down, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom")),
            upstreamManifest: new UpstreamRhiManifestService(new HttpClient(new FailingHandler()), LegacyManifestUrl));
        Assert.Equal(["6.4.1"], await offline.LegacyVersionsAsync());
    }

    [Fact]
    public async Task ManifestPins_AndTheReShadeNote_AreBoundToTheGame_AndABadPinIsIgnored()
    {
        using var http = ManifestHttp(LegacyManifest);
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, LegacyManifestUrl));
        var maxPayne = Path.Combine(_root, "pins", "Max Payne 3");
        var bad = Path.Combine(_root, "pins", "Bad Pin");
        Directory.CreateDirectory(maxPayne);
        Directory.CreateDirectory(bad);

        var applied = await policy.ApplyAsync([
            new DetectedGame { Name = "Max Payne 3", InstallPath = maxPayne, Source = "Steam" },
            new DetectedGame { Name = "Bad Pin", InstallPath = bad, Source = "Steam" }]);

        var game = applied.Single(g => g.Name == "Max Payne 3");
        Assert.Equal("6.4.1", game.ManifestLegacyReShadeVersion);
        Assert.Equal("ReShade 6.4.1 is required for RenoDX.", game.ManifestReShadeNote);
        Assert.Equal("https://github.com/rhi-tests/notes", game.ManifestReShadeNoteUrl);
        Assert.Equal("Guide", game.ManifestReShadeNoteUrlLabel);
        Assert.Null(applied.Single(g => g.Name == "Bad Pin").ManifestLegacyReShadeVersion);
    }

    [Fact]
    public async Task ManifestPin_FindsAGameByItsFolderName_WhenTheStoreSpellsTheTitleDifferently()
    {
        // The pin's key joins the manifest's known titles, so a card found by its folder ("Max Payne 3") gets it.
        using var http = ManifestHttp(LegacyManifest);
        var policy = new UpstreamGameLibraryPolicyService(new UpstreamRhiManifestService(http, LegacyManifestUrl));
        var folder = Path.Combine(_root, "pins-folder", "Max Payne 3");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "MaxPayne3.exe"), [0x4d, 0x5a]);

        var applied = Assert.Single(await policy.ApplyAsync([new DetectedGame { Name = "maxpayne3", InstallPath = folder, Source = "Wine" }]));

        Assert.Equal("6.4.1", applied.ManifestLegacyReShadeVersion);
        Assert.Equal("ReShade 6.4.1 is required for RenoDX.", applied.ManifestReShadeNote);
    }

    [Fact]
    public void DefaultOptions_TakeTheManifestPin_OnlyWhenItIsAPlainVersion()
    {
        var game = new DetectedGame { Name = "G", InstallPath = "/g" };
        Assert.Equal(ReShadeChannel.Stable, ReShadeDeploymentService.DefaultOptionsFor(game).Channel);
        game.ManifestLegacyReShadeVersion = "6.4.1";
        var pinned = ReShadeDeploymentService.DefaultOptionsFor(game);
        Assert.Equal(ReShadeChannel.Legacy, pinned.Channel);
        Assert.Equal("6.4.1", pinned.LegacyVersion);
        game.ManifestLegacyReShadeVersion = "6.4.1/../../x";
        Assert.Equal(ReShadeChannel.Stable, ReShadeDeploymentService.DefaultOptionsFor(game).Channel);
    }

    [Fact]
    public async Task PinnedGame_RowAndDependencyInstalls_TakeThePinnedVersion()
    {
        var (game, store) = CreateGame("legacy-pin-install");
        game.ManifestLegacyReShadeVersion = "6.4.1";
        var handler = new MutableHandler();
        var cache = Path.Combine(_root, "legacy-pin-install-cache");

        // A component that needs ReShade installs it through the no-profile path.
        var dependency = await NewService(store, handler, cache).InstallAsync(game);
        Assert.True(dependency.Success, dependency.Message);
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal("6.4.1", record.Version);
        Assert.Equal(ReShadeChannel.Legacy, NewService(store, handler, cache).GetInstalledOptions(game).Channel);
        Assert.EndsWith("ReShade_Setup_6.4.1_Addon.exe", record.SourceUrl, StringComparison.Ordinal);

        Assert.True((await NewService(store, handler, cache).RemoveAsync(game)).Success);
        var row = await ManagedComponents(store, NewService(store, handler, cache)).InstallAsync(game, ManagedComponentKind.ReShade, []);
        Assert.True(row.Success, row.Message);
        Assert.Equal("6.4.1", store.Find(game, ManagedComponentKind.ReShade)!.Version);
    }

    [Fact]
    public async Task APinAddedLater_NeverChangesAnInstalledReShade()
    {
        var (game, store) = CreateGame("legacy-pin-installed");
        var handler = new MutableHandler { StableVersion = "6.8.0" };
        var cache = Path.Combine(_root, "legacy-pin-installed-cache");
        Assert.True((await NewService(store, handler, cache).InstallAsync(game)).Success);
        game.ManifestLegacyReShadeVersion = "6.4.1";

        // The row's Install on a game whose ReShade files went missing re-applies the installed profile, not the pin.
        var reinstall = await ManagedComponents(store, NewService(store, handler, cache)).InstallAsync(game, ManagedComponentKind.ReShade, []);
        var update = await NewService(store, handler, cache).UpdateAsync(game);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.True(update.Success, update.Message);
        Assert.Equal("6.8.0", store.Find(game, ManagedComponentKind.ReShade)!.Version);
        Assert.Equal(ReShadeChannel.Stable, NewService(store, handler, cache).GetInstalledOptions(game).Channel);
    }

    private sealed class ManifestHandler(string json, Func<HttpRequestMessage, HttpResponseMessage?>? other) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            return Task.FromResult(other?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
