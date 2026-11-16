using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #256 (upstream 2.8.8 <c>54ec8f2</c>): "Available HDR mods" becomes "Available mods": an Ultra+ column joined by the
/// dialog's normalised name (Ultra+-only games get a row), every ✓/🔨 opens that mod's page (the Download column is gone),
/// per-mod counts, and a leading renodx / luma / ultra+ filters by mod. #257: Source engine games in the RenoDX column.
/// </summary>
public sealed class AvailableModsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-available-mods-" + Guid.NewGuid().ToString("N"));

    public AvailableModsTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static RenoDxSourceDbEntry Source(string name, string? status = "Done") => new() { Name = name, Status = status };

    [Fact]
    public void UltraPlus_JoinsByNormalisedName_AndAnUltraPlusOnlyGameGetsARow()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [new RenoDxModEntry("Cyberpunk 2077™", "https://github.com/rhi-tests/cp.addon64", null, null)],
            [new LumaCatalogEntry("Dead Space", "Filoppi", "https://github.com/rhi-tests/ds.zip", "✅", null, null)],
            ultraPlusGames:
            [
                new GameLinkEntry("CYBERPUNK 2077", "https://theultraplace.com/games/cyberpunk2077/"),
                new GameLinkEntry("Kingdom Come: Deliverance II", "https://theultraplace.com/games/kcd2/"),
                new GameLinkEntry("Elsewhere", "https://evil.example/games/elsewhere/"),   // not the Ultra+ site: dropped
                new GameLinkEntry("Cyberpunk 2077", "https://theultraplace.com/games/other/") // the first entry wins
            ]).ToDictionary(entry => entry.Name);

        Assert.Equal(["Cyberpunk 2077™", "Dead Space", "Kingdom Come: Deliverance II"], entries.Keys.Order());
        var cyberpunk = entries["Cyberpunk 2077™"];
        Assert.Equal("https://theultraplace.com/games/cyberpunk2077/", cyberpunk.UltraPlusUrl);
        Assert.True(cyberpunk.HasRenoDx && cyberpunk.HasUltraPlus && !cyberpunk.HasLuma);
        var kcd = entries["Kingdom Come: Deliverance II"];
        Assert.Equal((false, false, true), (kcd.HasRenoDx, kcd.HasLuma, kcd.HasUltraPlus));
        Assert.Null(kcd.RenoDxLink);
        Assert.False(entries["Dead Space"].HasUltraPlus);
    }

    [Fact]
    public void SourceGames_JoinTheRenoDxColumn_AfterUnity_NeverOverANamedMod_AndLinkTheWiki()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [new RenoDxModEntry("Portal 2", "https://github.com/rhi-tests/portal2.addon64", null, null)],
            [new LumaCatalogEntry("Half Life 2", "Someone", "https://github.com/rhi-tests/hl2.zip", "✅", null, null)],
            [new RenoDxUnityDbEntry { Name = "Black Mesa", Status = "Done" }],
            sourceEntries: [Source("Black Mesa", "WIP"), Source("Portal 2", "WIP"), Source("Half-Life 2"), Source("Left 4 Dead", "WIP")])
            .ToDictionary(entry => entry.Name);

        Assert.Equal(["Black Mesa", "Half-Life 2", "Left 4 Dead", "Portal 2"], entries.Keys.Order());
        Assert.Equal(HdrModAvailability.Available, entries["Black Mesa"].RenoDx); // the Unity row came first (upstream's order)
        Assert.Equal("https://github.com/rhi-tests/portal2.addon64", entries["Portal 2"].RenoDxLink); // the named mod's row
        Assert.Equal(HdrModAvailability.Available, entries["Portal 2"].RenoDx);
        Assert.Equal(HdrModAvailability.WorkInProgress, entries["Left 4 Dead"].RenoDx);
        Assert.Equal(AvailableHdrModsCatalog.RenoDxWikiModsUrl, entries["Left 4 Dead"].RenoDxLink);
        Assert.Equal(HdrModAvailability.Available, entries["Half-Life 2"].Luma); // Luma merges into the Source row by name
    }

    [Fact]
    public void Snapshot_CountsEachMod()
    {
        var snapshot = new AvailableHdrModsSnapshot(
        [
            new HdrModEntry("A", HdrModAvailability.Available, null, HdrModAvailability.WorkInProgress, null, "https://theultraplace.com/games/a/"),
            new HdrModEntry("B", HdrModAvailability.WorkInProgress, null, HdrModAvailability.None, null),
            new HdrModEntry("C", HdrModAvailability.None, null, HdrModAvailability.None, null, "https://theultraplace.com/games/c/")
        ], false, false);

        Assert.Equal((2, 1, 2), (snapshot.RenoDxCount, snapshot.LumaCount, snapshot.UltraPlusCount));
    }

    [Theory]
    [InlineData("luma dead", AvailableModKind.Luma, "dead")]
    [InlineData("  LUMA   Dead Space ", AvailableModKind.Luma, "Dead Space")]
    [InlineData("RenoDX", AvailableModKind.RenoDx, "")]
    [InlineData("ultra+ cyber", AvailableModKind.UltraPlus, "cyber")]
    [InlineData("Ultra", AvailableModKind.UltraPlus, "")]
    [InlineData("ultraplus kingdom", AvailableModKind.UltraPlus, "kingdom")]
    [InlineData("lumafoo", null, "lumafoo")]
    [InlineData("dead luma", null, "dead luma")]
    [InlineData("hdr dead", null, "hdr dead")]
    [InlineData(null, null, "")]
    public void ParseFilter_ALeadingModWordFilters_TheRestIsTheName(string? query, AvailableModKind? mod, string name)
        => Assert.Equal((mod, name), AvailableHdrModsCatalog.ParseFilter(query));

    [Theory]
    [InlineData("luma", new[] { "Dead Space", "Dead Island 2" })]
    [InlineData("luma dead", new[] { "Dead Space", "Dead Island 2" })]
    [InlineData("luma island", new[] { "Dead Island 2" })]
    [InlineData("renodx", new[] { "Dead Space", "Elden Ring" })]
    [InlineData("ultra+", new[] { "Elden Ring" })]
    [InlineData("ultra elden", new[] { "Elden Ring" })]
    [InlineData("dead", new[] { "Dead Space", "Dead Island 2" })]
    [InlineData("nothing", new string[0])]
    public void Matches_FiltersByModThenByName(string query, string[] expected)
    {
        HdrModEntry[] entries =
        [
            new("Dead Space", HdrModAvailability.Available, null, HdrModAvailability.Available, null),
            new("Dead Island 2", HdrModAvailability.None, null, HdrModAvailability.WorkInProgress, null),
            new("Elden Ring", HdrModAvailability.WorkInProgress, null, HdrModAvailability.None, null, "https://theultraplace.com/games/eldenring/")
        ];

        Assert.Equal(expected, entries.Where(entry => AvailableHdrModsCatalog.Matches(entry, query)).Select(entry => entry.Name));
    }

    [Fact]
    public async Task LoadAsync_AddsSourceAndUltraPlus_AndSaysWhichCouldNotBeLoaded()
    {
        const string lumaWiki = "https://example.test/luma-wiki";
        var sourceUp = true;
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            RenoDxCatalogService.StructuredDbUrl => Json("""[{ "name": "Hades II", "snapshotUrl": "https://cdn.test/hades2.addon64" }]"""),
            RenoDxSourceDatabaseService.LiveUrl when sourceUp => Json("""[{ "Name": "Portal", "Status": "Done" }]"""),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        }));
        IReadOnlyList<GameLinkEntry>? ultraPlus = [new GameLinkEntry("Hades II", "https://theultraplace.com/games/hades2/")];
        var catalog = new AvailableHdrModsCatalog(new RenoDxCatalogService(http), new LumaCatalogService(http, wikiUrl: lumaWiki),
            source: new RenoDxSourceDatabaseService(http, Path.Combine(_root, "source.json"), fallbackUrl: null, freshness: TimeSpan.Zero),
            ultraPlus: _ => Task.FromResult<IReadOnlyList<GameLinkEntry>?>(ultraPlus));

        var loaded = await catalog.LoadAsync();

        Assert.Equal(["Hades II", "Portal"], loaded.Entries.Select(entry => entry.Name));
        Assert.Equal("https://theultraplace.com/games/hades2/", loaded.Entries[0].UltraPlusUrl);
        Assert.Equal((false, true, false, false), (loaded.RenoDxUnavailable, loaded.LumaUnavailable, loaded.UltraPlusUnavailable, loaded.SourceUnavailable));

        sourceUp = false;
        ultraPlus = null; // the Ultra+ list has not loaded
        File.Delete(Path.Combine(_root, "source.json"));
        var catalog2 = new AvailableHdrModsCatalog(new RenoDxCatalogService(http), new LumaCatalogService(http, wikiUrl: lumaWiki),
            source: new RenoDxSourceDatabaseService(http, Path.Combine(_root, "source2.json"), fallbackUrl: null, freshness: TimeSpan.Zero),
            ultraPlus: _ => Task.FromResult<IReadOnlyList<GameLinkEntry>?>(ultraPlus));
        var degraded = await catalog2.LoadAsync();

        Assert.Equal("Hades II", Assert.Single(degraded.Entries).Name); // the dialog still opens with what loaded
        Assert.Equal((true, true), (degraded.UltraPlusUnavailable, degraded.SourceUnavailable));
        var line = MainWindow.AvailableModsCountLine(degraded);
        Assert.Contains("1 RenoDX mods, 0 Luma mods, 0 Ultra+ mods", line);
        Assert.Contains("Source", line);
        Assert.Contains("Ultra+ list", line);
    }

    [Fact]
    public async Task UltraPlusGames_WaitsForThatList_AndTheManifestOverrideWinsOnItsSite()
    {
        var policy = new UpstreamLibraryPolicySnapshot
        {
            UltraPlusUrlOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cyberpunk 2077"] = "https://theultraplace.com/games/cp2077-override/",
                ["CODE VEIN II"] = "https://evil.example/codevein2/" // not the Ultra+ site: the list's page stays
            }
        };
        var links = new GameLinkCatalogue(new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri == GameLinkCatalogue.UltraPlusGamesUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GameLinkCatalogueTests.UltraPlusHtml, Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.NotFound))),
            Path.Combine(_root, "links"), policy: _ => Task.FromResult<UpstreamLibraryPolicySnapshot?>(policy));

        var games = (await links.UltraPlusGamesAsync())!.ToDictionary(game => game.Name, game => game.Url);

        Assert.Equal("https://theultraplace.com/games/cp2077-override/", games["Cyberpunk 2077"]);
        Assert.Equal("https://theultraplace.com/games/codevein2/", games["CODE VEIN II"]);

        var offline = new GameLinkCatalogue(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), Path.Combine(_root, "none"));
        Assert.Null(await offline.UltraPlusGamesAsync());
    }

    [Fact]
    public void Dialog_HasGameRenoDxLumaUltraPlusColumns_AndNoDownloadColumn()
    {
        var header = MainWindow.BuildAvailableModsHeader();

        var texts = header.Children.OfType<TextBlock>().OrderBy(Grid.GetColumn).Select(block => block.Text).ToArray();
        Assert.Equal(4, texts.Length);
        Assert.Equal(new string?[] { "RenoDX", "Luma", "Ultra+" }, texts[1..]);
        Assert.DoesNotContain("Download", texts);
        Assert.Equal(4, header.ColumnDefinitions.Count);
    }

    [Fact]
    public void Dialog_EachTickWithAPageOpensIt_AndACrossIsNotALink()
    {
        var opened = new List<string>();
        var entry = new HdrModEntry("Left 4 Dead", HdrModAvailability.WorkInProgress, null, HdrModAvailability.None, null,
            "https://theultraplace.com/games/l4d/");

        var row = MainWindow.BuildAvailableModRow(entry, opened.Add);

        Control Cell(int column) => row.Children.Single(child => Grid.GetColumn(child) == column);
        var renoDx = Assert.IsType<Button>(Cell(1));
        Assert.Equal("🔨", Assert.IsType<TextBlock>(renoDx.Content).Text);
        var luma = Assert.IsType<TextBlock>(Cell(2));
        Assert.Equal("✗", luma.Text);
        var ultraPlus = Assert.IsType<Button>(Cell(3));
        Assert.Equal("✓", Assert.IsType<TextBlock>(ultraPlus.Content).Text);

        renoDx.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ultraPlus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([AvailableHdrModsCatalog.RenoDxWikiModsUrl, "https://theultraplace.com/games/l4d/"], opened);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
