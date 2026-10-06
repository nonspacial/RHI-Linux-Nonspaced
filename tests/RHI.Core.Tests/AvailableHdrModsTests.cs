using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class AvailableHdrModsTests
{
    [Fact]
    public void StructuredDatabase_WipStatusSurvivesFallbackMerge()
    {
        const string json = """
        [
          { "name": "Done Game", "snapshotUrl": "https://cdn.test/done.addon64", "status": "Done" },
          { "name": "Wip Game", "snapshotUrl": "https://cdn.test/wip.addon64", "status": "WIP" }
        ]
        """;
        var parsed = RenoDxCatalogService.ParseStructuredDatabase(json);
        var merged = RenoDxCatalogService.MergeCatalogs(parsed,
            [new RenoDxModEntry("Wip Game", null, "https://cdn.test/wip.addon32", "Wiki")]);

        Assert.False(merged.Single(x => x.Name == "Done Game").IsWorkInProgress);
        Assert.True(merged.Single(x => x.Name == "Wip Game").IsWorkInProgress);
    }

    [Fact]
    public void Merge_JoinsCataloguesByNormalisedNameAndSortsByName()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [
                new RenoDxModEntry("Street Fighter™ 6", "https://cdn.test/sf6.addon64", null, null),
                new RenoDxModEntry("Alan Wake 2", null, "https://cdn.test/aw2.addon32", null, IsWorkInProgress: true),
                new RenoDxModEntry("Generic Unreal", RenoDxCatalogService.GenericUnrealUrl, null, null, IsGeneric: true)
            ],
            [
                new LumaCatalogEntry("Street Fighter 6", "Filoppi", "https://github.com/luma/sf6.zip", "✅", null, null),
                new LumaCatalogEntry("Batman Arkham Knight", "Filoppi", null, "🚧 WIP", null, null)
            ]);

        Assert.Equal(["Alan Wake 2", "Batman Arkham Knight", "Street Fighter™ 6"], entries.Select(x => x.Name));

        var alanWake = entries[0];
        Assert.Equal(HdrModAvailability.WorkInProgress, alanWake.RenoDx);
        Assert.Equal(HdrModAvailability.None, alanWake.Luma);
        Assert.Equal("https://cdn.test/aw2.addon32", alanWake.DownloadUrl);

        var batman = entries[1];
        Assert.Equal(HdrModAvailability.None, batman.RenoDx);
        Assert.Equal(HdrModAvailability.WorkInProgress, batman.Luma);
        Assert.Null(batman.DownloadUrl);

        var streetFighter = entries[2];
        Assert.Equal(HdrModAvailability.Available, streetFighter.RenoDx);
        Assert.Equal(HdrModAvailability.Available, streetFighter.Luma);
        Assert.Equal("https://github.com/luma/sf6.zip", streetFighter.LumaUrl);
        Assert.Equal("https://cdn.test/sf6.addon64", streetFighter.DownloadUrl);
    }

    [Fact]
    public void Merge_DropsLinksTheDesktopOpenerMustNotReceive()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [new RenoDxModEntry("Local Game", "file:///etc/passwd.addon64", null, null)],
            [new LumaCatalogEntry("Local Game", "Someone", "https://github.com/luma/local.zip", "✅", null, null),
             new LumaCatalogEntry("Script Game", "Someone", "javascript:alert(1)", "✅", null, null)]);

        Assert.Equal("https://github.com/luma/local.zip", entries.Single(x => x.Name == "Local Game").DownloadUrl);
        Assert.Null(entries.Single(x => x.Name == "Script Game").DownloadUrl);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("elden", true)]
    [InlineData(" RING ", true)]
    [InlineData("sekiro", false)]
    public void Matches_IsCaseInsensitiveNameSubstring(string? query, bool expected)
    {
        var entry = new HdrModEntry("Elden Ring", HdrModAvailability.Available, null, HdrModAvailability.None, null);
        Assert.Equal(expected, AvailableHdrModsCatalog.Matches(entry, query));
    }

    [Fact]
    public async Task LoadAsync_KeepsRenoDxRowsWhenLumaWikiFails()
    {
        const string lumaWiki = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            RenoDxCatalogService.StructuredDbUrl => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{ "name": "Hades II", "snapshotUrl": "https://cdn.test/hades2.addon64" }]""", Encoding.UTF8, "application/json")
            },
            lumaWiki => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var catalog = new AvailableHdrModsCatalog(new RenoDxCatalogService(http), new LumaCatalogService(http, wikiUrl: lumaWiki));

        var snapshot = await catalog.LoadAsync();

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("Hades II", entry.Name);
        Assert.False(snapshot.RenoDxUnavailable);
        Assert.True(snapshot.LumaUnavailable);
    }

    [Fact]
    public void ComponentsSummary_ListsInstalledComponentsWithVersionsOnly()
    {
        ComponentRowViewModel Row(ManagedComponentKind kind, string name, ComponentAvailability availability, string? version = null)
            => new(new ManagedComponent { Kind = kind, DisplayName = name, Availability = availability, InstalledVersion = version }, null!);

        var items = DetailSectionSummaryPresenter.ManagedComponents(
        [
            Row(ManagedComponentKind.ReShade, "ReShade", ComponentAvailability.Installed, "6.8.0"),
            Row(ManagedComponentKind.RenoDX, "RenoDX", ComponentAvailability.UpdateAvailable, " "),
            Row(ManagedComponentKind.OptiScaler, "OptiScaler", ComponentAvailability.Available),
            Row(ManagedComponentKind.Dlss, "DLSS", ComponentAvailability.Installed, "310.2"),
            Row(ManagedComponentKind.Streamline, "Streamline", ComponentAvailability.Installed)
        ]);

        Assert.Equal("ReShade 6.8.0  ·  RenoDX", DetailSectionSummaryPresenter.Text(items));
    }

    [Fact]
    public void ExtrasAndRuntimeSummaries_FollowUpstreamLabels()
    {
        Assert.Equal("ASI Loader  ·  ShortFuse 1.2.0",
            DetailSectionSummaryPresenter.Text(DetailSectionSummaryPresenter.Extras(true, true, "1.2.0")));
        Assert.Empty(DetailSectionSummaryPresenter.Extras(false, false, "1.2.0"));

        var game = new DetectedGame { Name = "Game", InstallPath = "/games/game" };
        Assert.Empty(DetailSectionSummaryPresenter.Runtime(game));
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;
        Assert.Equal("API: DX11", DetailSectionSummaryPresenter.Text(DetailSectionSummaryPresenter.Runtime(game)));
        Assert.Empty(DetailSectionSummaryPresenter.Runtime(null));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
