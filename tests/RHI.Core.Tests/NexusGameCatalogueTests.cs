using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #174: the card menu's "Nexus Mods page" for every game Nexus has a page for — manifest first, then RHI's built-in
/// table of store names Nexus spells differently, then Nexus's public game list (upstream's games.json, 24 h cache).
/// </summary>
public sealed class NexusGameCatalogueTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-nexus-games-" + Guid.NewGuid().ToString("N"))).FullName;
    private string CachePath => Path.Combine(_root, "cache", "nexus_games.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string List = """
        [
          {"id": 3333, "name": "Cyberpunk 2077", "domain_name": "cyberpunk2077", "nexusmods_url": "https://www.nexusmods.com/cyberpunk2077"},
          {"id": 9001, "name": "Broken Sword 3 - the Sleeping Dragon", "domain_name": "brokensword3thesleepingdragon", "nexusmods_url": "https://www.nexusmods.com/brokensword3thesleepingdragon"},
          {"id": 9002, "name": "Plain Http", "domain_name": "plainhttp", "nexusmods_url": "http://www.nexusmods.com/plainhttp"},
          {"id": 9003, "name": "Other Host", "domain_name": "otherhost", "nexusmods_url": "https://nexusmods.com.evil.example/otherhost"},
          {"id": 9004, "name": "Deep Link", "domain_name": "deeplink", "nexusmods_url": "https://www.nexusmods.com/deeplink/mods/1"},
          {"id": 9005, "name": "Cyberpunk 2077", "domain_name": "second", "nexusmods_url": "https://www.nexusmods.com/second"}
        ]
        """;

    private static NexusApiClientTests.FakeNexus Serving(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static DetectedGame Named(string name, int? steamAppId = null) => new() { Name = name, SteamAppId = steamAppId };

    [Theory]
    [InlineData("The Elder Scrolls V: Skyrim Special Edition", null, "skyrimspecialedition")]
    [InlineData("The Elder Scrolls V: Skyrim Anniversary Edition", null, "skyrimspecialedition")]
    [InlineData("Some localised name", 489830, "skyrimspecialedition")]
    [InlineData("The Elder Scrolls V: Skyrim", null, "skyrim")]
    [InlineData("RESIDENT EVIL 2", 883710, "residentevil22019")]
    [InlineData("Metro Exodus Enhanced Edition", null, "metroexodus")]
    [InlineData("DARK SOULS™ III", null, "darksouls3")]
    public void BuiltInTable_AnswersWithoutTheList_ForStoreNamesNexusSpellsDifferently(string name, int? appId, string domain)
    {
        Assert.Equal("https://www.nexusmods.com/" + domain, GameLinksPresenter.PageFor(Named(name, appId)));
        Assert.Equal("https://www.nexusmods.com/" + domain, GameLinksPresenter.PageFor(Named(name, appId), new NexusGameCatalogue(new HttpClient(Serving("[]")), CachePath)));
    }

    [Fact]
    public void Manifest_StillWins_OverTheBuiltInTable()
    {
        var game = Named("The Elder Scrolls V: Skyrim Special Edition", 489830);
        game.NexusUrlOverride = "https://www.nexusmods.com/games/override";
        Assert.Equal("https://www.nexusmods.com/games/override", GameLinksPresenter.PageFor(game));
    }

    [Fact]
    public async Task List_AnswersEveryOtherGame_ByNormalisedName_KeepingOnlyNexusGamePages()
    {
        var fake = Serving(List);
        var games = new NexusGameCatalogue(new HttpClient(fake), CachePath);
        Assert.Null(GameLinksPresenter.PageFor(Named("Cyberpunk 2077"), games)); // not loaded yet: never blocks

        await games.LoadAsync();

        Assert.True(games.IsLoaded);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://data.nexusmods.com/file/nexus-data/games.json", request.RequestUri!.AbsoluteUri);
        Assert.Equal("https://www.nexusmods.com/cyberpunk2077", GameLinksPresenter.PageFor(Named("Cyberpunk 2077™"), games)); // first entry wins
        Assert.Equal("https://www.nexusmods.com/brokensword3thesleepingdragon",
            GameLinksPresenter.PageFor(Named("Broken Sword 3 - the Sleeping Dragon (2003)"), games)); // Steam's trailing year
        Assert.Null(GameLinksPresenter.PageFor(Named("Plain Http"), games));
        Assert.Null(GameLinksPresenter.PageFor(Named("Other Host"), games));
        Assert.Null(GameLinksPresenter.PageFor(Named("Deep Link"), games));
        Assert.Null(GameLinksPresenter.PageFor(Named("Not On Nexus"), games));
        Assert.Equal("cyberpunk2077", GameLinksPresenter.GameDomainOf(GameLinksPresenter.PageFor(Named("Cyberpunk 2077"), games))); // nxm matching
    }

    [Fact]
    public async Task List_IsCachedForADay_AndTheLastGoodCopyServesWhenNexusIsUnreachable()
    {
        var first = new NexusGameCatalogue(new HttpClient(Serving(List)), CachePath);
        await first.LoadAsync();
        Assert.True(File.Exists(CachePath));

        var fresh = Serving("[]");
        var second = new NexusGameCatalogue(new HttpClient(fresh), CachePath);
        await second.LoadAsync();
        Assert.Empty(fresh.Requests); // a copy younger than 24 h is used without asking
        Assert.Equal("https://www.nexusmods.com/cyberpunk2077", second.PageFor("Cyberpunk 2077", null));

        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddDays(-2));
        var offline = new NexusApiClientTests.FakeNexus(_ => throw new HttpRequestException("offline"));
        var third = new NexusGameCatalogue(new HttpClient(offline), CachePath);
        await third.LoadAsync();
        Assert.Single(offline.Requests);
        Assert.Equal("https://www.nexusmods.com/cyberpunk2077", third.PageFor("Cyberpunk 2077", null));
    }

    [Fact]
    public async Task EmptyOrBrokenAnswer_NeverReplacesTheLastGoodCopy()
    {
        await new NexusGameCatalogue(new HttpClient(Serving(List)), CachePath).LoadAsync();
        var good = File.ReadAllBytes(CachePath);
        File.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddDays(-2));

        foreach (var body in new[] { "[]", "{\"not\": \"a list\"}", "<html>" })
        {
            var games = new NexusGameCatalogue(new HttpClient(Serving(body)), CachePath);
            await games.LoadAsync();
            Assert.Equal("https://www.nexusmods.com/cyberpunk2077", games.PageFor("Cyberpunk 2077", null));
            Assert.Equal(good, File.ReadAllBytes(CachePath));
        }
    }

    [Fact]
    public async Task FailedLoad_WithNoCopy_IsRetriedOnlyAfterAPause()
    {
        var time = new ManualClock(DateTimeOffset.UtcNow);
        var offline = new NexusApiClientTests.FakeNexus(_ => throw new HttpRequestException("offline"));
        var games = new NexusGameCatalogue(new HttpClient(offline), CachePath, timeProvider: time);

        await games.LoadAsync();
        await games.LoadAsync(); // e.g. every card menu opened meanwhile
        Assert.False(games.IsLoaded);
        Assert.Single(offline.Requests);

        time.Now += TimeSpan.FromMinutes(6);
        await games.LoadAsync();
        Assert.Equal(2, offline.Requests.Count);
    }

    [Fact]
    public void Policy_AllowsOnlyNexusDataHost()
    {
        Assert.Equal("data.nexusmods.com", DownloadSources.NexusGameList.Validate(NexusGameCatalogue.GamesListUrl).Host);
        var refused = Assert.Throws<UntrustedDownloadException>(() => DownloadSources.NexusGameList.Validate("https://api.nexusmods.com/file/nexus-data/games.json"));
        Assert.StartsWith(DownloadSources.NexusGameList.RefusalPrefix, refused.Message, StringComparison.Ordinal);
        Assert.Equal(16L * 1024 * 1024, DownloadSources.NexusGameList.MaxBytes);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
