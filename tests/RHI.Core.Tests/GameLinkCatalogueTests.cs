using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #183: the card menu's web pages — PCGamingWiki, Ultra+, an ultrawide fix, the HDR Gaming Database and the RenoDX mod's
/// discussion page — each only where the game has one, the manifest's <c>*UrlOverrides</c> first, lists kept 24 h and never
/// waited for.
/// </summary>
public sealed class GameLinkCatalogueTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-game-links-" + Guid.NewGuid().ToString("N"))).FullName;
    private string CacheDir => Path.Combine(_root, "links");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal const string UltraPlusHtml = """
        <div class="grid">
          <a href="/games/cyberpunk2077/" class="game-card" data-x="1"><img src="a.png"><h3>Cyberpunk 2077</h3></a>
          <a href="/games/codevein2/" class="game-card"><div><h3>CODE VEIN II</h3></div></a>
          <a href="/games/../../evil/" class="game-card"><h3>Escape Attempt</h3></a>
          <a href="/about/" class="nav"><h3>Not a game</h3></a>
          <a href="/games/kcd2/" class="game-card"><h3>Kingdom Come: Deliverance II &amp; DLC</h3></a>
        </div>
        """;

    private const string LyallJson = """
        [
          {"name": "DQ7RFix", "description": "An ASI plugin for **DRAGON QUEST VII Reimagined** that adds ultrawide support.", "html_url": "https://codeberg.org/Lyall/DQ7RFix", "has_releases": true},
          {"name": "ShiftFix", "description": "A fix for Shift Game (and Shift Game Deluxe) that removes pillarboxing.", "html_url": "https://codeberg.org/Lyall/ShiftFix", "has_releases": true},
          {"name": "NoReleases", "description": "A fix for Unreleased Game that does things.", "html_url": "https://codeberg.org/Lyall/NoReleases", "has_releases": false},
          {"name": "UltrawidePatches", "description": "A fix for Everything that patches.", "html_url": "https://codeberg.org/Lyall/UltrawidePatches", "has_releases": true},
          {"name": "Elsewhere", "description": "A fix for Elsewhere Game that moved.", "html_url": "https://evil.example/Lyall/Elsewhere", "has_releases": true},
          {"name": "Shared", "description": "A fix for Shared Game that is Lyall's.", "html_url": "https://codeberg.org/Lyall/Shared", "has_releases": true}
        ]
        """;

    private const string RoseMarkdown = """
        # Alan Wake 2 ultrawide and black bars removal
        Some text
        [Download](https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/alanwake2)
        # Other solutions
        ## Remote Game
        [Download page and instructions](https://www.nexusmods.com/remotegame/mods/1)
        ## Sibling Repo Game widescreen fix
        [Download page and instructions](/../../../SiblingRepo#readme)
        # Shared Game ultrawide
        [Download](https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/shared)
        """;

    private const string P1xelMarkdown = """
        ## [Pixel Game](https://github.com/p1xel8ted/UltrawideFixes/releases/tag/PixelGame)
        ## [Shared Game](https://github.com/p1xel8ted/UltrawideFixes/releases/tag/Shared)
        ## [Wrong Repo](https://github.com/someone/else/releases/tag/x)
        """;

    private const string HdrMarkdown = """
        # HDR Gaming Database
        * [Cyberpunk 2077™](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/12) ⭐
          * [Assassin’s Creed Shadows](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/34)
        * [Elsewhere](https://evil.example/discussions/1)
        """;

    private static HttpResponseMessage Text(string body, string type = "text/plain")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    /// <summary>Serves every list; counts requests per host.</summary>
    private static Fake Lists(Func<HttpRequestMessage, HttpResponseMessage?>? fallback = null) => new(request =>
        request.RequestUri!.AbsoluteUri switch
        {
            GameLinkCatalogue.UltraPlusGamesUrl => Text(UltraPlusHtml, "text/html"),
            GameLinkCatalogue.LyallReposUrl => Text(LyallJson, "application/json"),
            GameLinkCatalogue.RoseReadmeUrl => Text(RoseMarkdown),
            GameLinkCatalogue.P1xelReadmeUrl => Text(P1xelMarkdown),
            GameLinkCatalogue.HdrDatabaseReadmeUrl => Text(HdrMarkdown),
            _ => fallback?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound)
        });

    private static DetectedGame Named(string name) => new() { Name = name, InstallPath = "/games/" + name };

    [Fact]
    public void Parsers_ReadEachList_AndKeepOnlyHttpsPagesOnTheirOwnHosts()
    {
        Assert.Equal(
        [
            new GameLinkEntry("Cyberpunk 2077", "https://theultraplace.com/games/cyberpunk2077/"),
            new GameLinkEntry("CODE VEIN II", "https://theultraplace.com/games/codevein2/"),
            new GameLinkEntry("Kingdom Come: Deliverance II & DLC", "https://theultraplace.com/games/kcd2/")
        ], GameLinkCatalogue.ParseUltraPlus(UltraPlusHtml)); // the "/games/../.." card is dropped

        Assert.Equal(
        [
            new GameLinkEntry("DRAGON QUEST VII Reimagined", "https://codeberg.org/Lyall/DQ7RFix"),
            new GameLinkEntry("Shift Game", "https://codeberg.org/Lyall/ShiftFix"),
            new GameLinkEntry("Shared Game", "https://codeberg.org/Lyall/Shared")
        ], GameLinkCatalogue.ParseLyall(LyallJson)); // no releases, UltrawidePatches and another host are left out

        Assert.Equal(
        [
            new GameLinkEntry("Alan Wake 2", "https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/alanwake2"),
            new GameLinkEntry("Remote Game", "https://www.nexusmods.com/remotegame/mods/1"),
            new GameLinkEntry("Sibling Repo Game", "https://github.com/RoseTheFlower/SiblingRepo"),
            new GameLinkEntry("Shared Game", "https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/shared")
        ], GameLinkCatalogue.ParseRose(RoseMarkdown));

        Assert.Equal(
        [
            new GameLinkEntry("Pixel Game", "https://github.com/p1xel8ted/UltrawideFixes/releases/tag/PixelGame"),
            new GameLinkEntry("Shared Game", "https://github.com/p1xel8ted/UltrawideFixes/releases/tag/Shared")
        ], GameLinkCatalogue.ParseP1xel(P1xelMarkdown));

        Assert.Equal(
        [
            new GameLinkEntry("Cyberpunk 2077", "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/12"),
            new GameLinkEntry("Assassin's Creed Shadows", "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/34")
        ], GameLinkCatalogue.ParseHdrDatabase(HdrMarkdown));
    }

    [Fact]
    public async Task Lookups_PutTheManifestFirst_ThenEachListInUpstreamsOrder_AndNeverWaitForALoad()
    {
        var links = new GameLinkCatalogue(new HttpClient(Lists()), CacheDir);
        var shared = Named("Shared Game");
        Assert.Null(links.UltrawideFixPageFor(shared)); // not loaded yet: never blocks
        Assert.Null(links.UltraPlusPageFor(Named("Cyberpunk 2077")));

        await links.LoadAsync();
        Assert.Equal("https://codeberg.org/Lyall/Shared", links.UltrawideFixPageFor(shared)); // Lyall before Rose and p1xel8ted
        Assert.Equal("https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/alanwake2", links.UltrawideFixPageFor(Named("Alan Wake 2")));
        Assert.Equal("https://github.com/p1xel8ted/UltrawideFixes/releases/tag/PixelGame", links.UltrawideFixPageFor(Named("Pixel Game")));
        Assert.Equal("https://theultraplace.com/games/cyberpunk2077/", links.UltraPlusPageFor(Named("CYBERPUNK 2077™"))); // normalised name
        Assert.Equal("https://github.com/KoKlusz/HDR-Gaming-Database/discussions/12", links.HdrDatabasePageFor(Named("Cyberpunk 2077")));
        Assert.Equal("https://github.com/KoKlusz/HDR-Gaming-Database/discussions/34", links.HdrDatabasePageFor(Named("ASSASSIN'S CREED SHADOWS®")));
        Assert.Null(links.HdrDatabasePageFor(Named("Elsewhere")));
        Assert.Null(links.UltraPlusPageFor(Named("Not On Any List")));

        var overridden = Named("Shared Game");
        overridden.UwFixUrlOverride = "https://www.nexusmods.com/shared/mods/15";
        overridden.UltraPlusUrlOverride = "https://theultraplace.com/games/shared/";
        overridden.PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Shared";
        Assert.Equal("https://www.nexusmods.com/shared/mods/15", links.UltrawideFixPageFor(overridden));
        Assert.Equal("https://theultraplace.com/games/shared/", links.UltraPlusPageFor(overridden));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Shared", links.PcgwPageFor(overridden));

        var plain = Named("Shared Game");
        plain.UwFixUrlOverride = "http://www.nexusmods.com/shared/mods/15"; // not https: never handed to the opener
        plain.PcgwUrlOverride = "https://user:pw@www.pcgamingwiki.com/wiki/Shared";
        Assert.Equal("https://codeberg.org/Lyall/Shared", links.UltrawideFixPageFor(plain));
        Assert.Null(links.PcgwPageFor(plain));
    }

    [Fact]
    public async Task Lists_AreKeptADay_TheLastGoodCopyServesOffline_AndABrokenAnswerNeverReplacesIt()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        await new GameLinkCatalogue(new HttpClient(Lists()), CacheDir, timeProvider: clock).LoadAsync();

        var counting = Lists();
        var second = new GameLinkCatalogue(new HttpClient(counting), CacheDir, timeProvider: clock);
        clock.Now += TimeSpan.FromHours(23);
        await second.LoadAsync();
        Assert.Empty(counting.Requests); // younger than 24 h: no request
        Assert.Equal("https://theultraplace.com/games/codevein2/", second.UltraPlusPageFor(Named("CODE VEIN II")));

        clock.Now += TimeSpan.FromHours(2);
        var broken = new Fake(_ => Text("<html>nothing here</html>", "text/html"));
        var third = new GameLinkCatalogue(new HttpClient(broken), CacheDir, timeProvider: clock);
        await third.LoadAsync();
        Assert.Equal(5, broken.Requests.Count); // stale: every list asked once
        Assert.Equal("https://theultraplace.com/games/codevein2/", third.UltraPlusPageFor(Named("CODE VEIN II")));
        Assert.Equal("https://codeberg.org/Lyall/Shared", third.UltrawideFixPageFor(Named("Shared Game")));

        var offline = new Fake(_ => throw new HttpRequestException("offline"));
        var fourth = new GameLinkCatalogue(new HttpClient(offline), CacheDir, timeProvider: clock);
        await fourth.LoadAsync();
        Assert.Equal("https://theultraplace.com/games/codevein2/", fourth.UltraPlusPageFor(Named("CODE VEIN II")));
        Assert.Equal(3, fourth.UltraPlusGames.Count);
    }

    [Fact]
    public async Task FailedLoad_WithNoCopy_IsRetriedOnlyAfterAPause()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var offline = new Fake(_ => throw new HttpRequestException("offline"));
        var links = new GameLinkCatalogue(new HttpClient(offline), CacheDir, timeProvider: clock);
        await links.LoadAsync();
        await links.LoadAsync(); // every card menu opened meanwhile
        Assert.Equal(5, offline.Requests.Count);
        Assert.False(links.UltraPlusLoaded);

        clock.Now += TimeSpan.FromMinutes(6);
        await links.LoadAsync();
        Assert.Equal(10, offline.Requests.Count);
    }

    [Fact]
    public async Task ARedirectOffTheListsHost_IsRefused()
    {
        var redirected = new Fake(request =>
        {
            var response = Text(UltraPlusHtml, "text/html");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/games/");
            return response;
        });
        var links = new GameLinkCatalogue(new HttpClient(redirected), CacheDir);
        await links.LoadAsync();
        Assert.False(links.UltraPlusLoaded);
        Assert.Null(links.UltraPlusPageFor(Named("Cyberpunk 2077")));
    }

    [Fact]
    public void Policies_AllowOnlyTheirOwnHost()
    {
        Assert.True(DownloadSources.UltraPlusGameList.IsTrusted(GameLinkCatalogue.UltraPlusGamesUrl));
        Assert.False(DownloadSources.UltraPlusGameList.IsTrusted("http://theultraplace.com/games/"));
        Assert.False(DownloadSources.UltraPlusGameList.IsTrusted("https://theultraplace.com.evil.example/games/"));
        Assert.True(DownloadSources.UltrawideFixList.IsTrusted(GameLinkCatalogue.LyallReposUrl));
        Assert.False(DownloadSources.UltrawideFixList.IsTrusted("https://github.com/Lyall"));
        Assert.True(DownloadSources.CommunityGameList.IsTrusted(GameLinkCatalogue.RoseReadmeUrl));
        Assert.True(DownloadSources.CommunityGameList.IsTrusted(GameLinkCatalogue.HdrDatabaseReadmeUrl));
        Assert.False(DownloadSources.CommunityGameList.IsTrusted("https://github.com/KoKlusz/HDR-Gaming-Database"));
        Assert.True(DownloadSources.SteamStoreSearch.IsTrusted("https://store.steampowered.com/api/storesearch/?term=x"));
        Assert.False(DownloadSources.SteamStoreSearch.IsTrusted("https://steamcommunity.com/"));
        Assert.Equal(4L * 1024 * 1024, DownloadSources.UltraPlusGameList.MaxBytes);
        Assert.Equal(4L * 1024 * 1024, DownloadSources.CommunityGameList.MaxBytes);
    }

    [Fact]
    public async Task EachLinkKind_OpensOnlyItsOwnSites_FromTheManifest_TheLists_AndTheCacheFiles()
    {
        foreach (var (hosts, allowed, refused) in new (IReadOnlySet<string>, string, string)[]
                 {
                     (LinkHosts.Pcgw, "https://www.pcgamingwiki.com/wiki/X", "https://theultraplace.com/games/x/"),
                     (LinkHosts.UltraPlus, "https://theultraplace.com/games/x/", "https://www.pcgamingwiki.com/wiki/X"),
                     (LinkHosts.UltrawideFix, "https://codeberg.org/Lyall/XFix", "https://theultraplace.com/games/x/"),
                     (LinkHosts.HdrDatabase, "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/1", "https://www.nexusmods.com/x/mods/1"),
                     (LinkHosts.Discussion, "https://github.com/clshortfuse/renodx/discussions/1", "https://discord.com/channels/1/2"),
                     (LinkHosts.ModPage, "https://gamebanana.com/mods/1", "https://github.com/x/y"),
                     (LinkHosts.Discord, "https://discord.gg/x", "https://www.nexusmods.com/x/mods/1"),
                     (LinkHosts.ManifestNote, "https://store.steampowered.com/app/1", "https://evil.example/x"),
                 })
        {
            Assert.Equal(allowed, GameLinkCatalogue.SafePage(allowed, hosts));
            Assert.Null(GameLinkCatalogue.SafePage(refused, hosts));
            Assert.Null(GameLinkCatalogue.SafePage(allowed.Replace("https://", "http://", StringComparison.Ordinal), hosts));
            var host = new Uri(allowed).Host;
            Assert.Null(GameLinkCatalogue.SafePage(allowed.Replace(host, host + ".evil.example", StringComparison.Ordinal), hosts));
        }

        // The manifest's overrides: another kind's site is not taken.
        var links = new GameLinkCatalogue(new HttpClient(Lists()), CacheDir);
        var crossed = Named("Crossed Game");
        crossed.PcgwUrlOverride = "https://theultraplace.com/games/crossed/";
        crossed.UltraPlusUrlOverride = "https://www.pcgamingwiki.com/wiki/Crossed_Game";
        crossed.UwFixUrlOverride = "https://theultraplace.com/games/crossed/";
        Assert.Null(links.PcgwPageFor(crossed));
        Assert.Null(links.UltraPlusPageFor(crossed));
        Assert.Null(links.UltrawideFixPageFor(crossed));
        var right = Named("Right Game");
        right.PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Right_Game";
        right.UltraPlusUrlOverride = "https://theultraplace.com/games/right/";
        right.UwFixUrlOverride = "https://www.nexusmods.com/rightgame/mods/7";
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Right_Game", links.PcgwPageFor(right));
        Assert.Equal("https://theultraplace.com/games/right/", links.UltraPlusPageFor(right));
        Assert.Equal("https://www.nexusmods.com/rightgame/mods/7", links.UltrawideFixPageFor(right));

        // A README pointing a game at another site gets no entry (the reviewer's repro).
        Assert.Empty(GameLinkCatalogue.ParseRose("# Foo ultrawide\n[Download page and instructions](https://evil.example/x)"));
        Assert.Empty(GameLinkCatalogue.ParseRose("# Foo ultrawide\n[Download page and instructions](https://theultraplace.com/games/foo/)"));

        // Cache files edited to point elsewhere are not used; the good entries beside them are.
        var seeded = Path.Combine(_root, "seeded");
        void Seed(string file, params (string Name, string Url)[] entries)
        {
            Directory.CreateDirectory(seeded);
            File.WriteAllText(Path.Combine(seeded, file), System.Text.Json.JsonSerializer.Serialize(new
            {
                fetchedAtUtc = DateTimeOffset.UtcNow,
                entries = entries.Select(e => new { e.Name, e.Url }).ToArray()
            }));
        }
        Seed("ultraplus-games.json", ("Seeded Game", "https://codeberg.org/x"), ("Good Game", "https://theultraplace.com/games/good/"));
        Seed("uwfix-lyall.json", ("Seeded Game", "https://theultraplace.com/games/seeded/"), ("Good Game", "https://codeberg.org/Lyall/GoodFix"));
        Seed("hdr-gaming-database.json", ("Seeded Game", "https://www.nexusmods.com/x/mods/1"), ("Good Game", "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/5"));
        File.WriteAllText(Path.Combine(seeded, "pcgw-links.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 0,
            pages = new Dictionary<string, string>
            {
                [NexusGameCatalogue.NormalizeName("Seeded Game")] = "https://github.com/x",
                [NexusGameCatalogue.NormalizeName("Good Game")] = "https://www.pcgamingwiki.com/wiki/Good_Game"
            },
            misses = new Dictionary<string, DateTimeOffset>(),
            appIds = new Dictionary<string, int>()
        }));
        var cached = new GameLinkCatalogue(new HttpClient(new Fake(_ => throw new HttpRequestException("offline"))), seeded);
        await cached.LoadAsync();
        var seededGame = Named("Seeded Game");
        Assert.Null(cached.UltraPlusPageFor(seededGame));
        Assert.Null(cached.UltrawideFixPageFor(seededGame));
        Assert.Null(cached.HdrDatabasePageFor(seededGame));
        Assert.Null(cached.PcgwPageFor(seededGame));
        var good = Named("Good Game");
        Assert.Equal("https://theultraplace.com/games/good/", cached.UltraPlusPageFor(good));
        Assert.Equal("https://codeberg.org/Lyall/GoodFix", cached.UltrawideFixPageFor(good));
        Assert.Equal("https://github.com/KoKlusz/HDR-Gaming-Database/discussions/5", cached.HdrDatabasePageFor(good));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Good_Game", cached.PcgwPageFor(good));
    }

    // ── PCGamingWiki ────────────────────────────────────────────────────────────────────────────────────────────

    private const string CentralJson = """
        {"name_overrides": {"AFOP": "Avatar: Frontiers of Pandora"},
         "games": {
           "Avatar: Frontiers of Pandora": {"steam_appid": 2840770},
           "Alan Wake 2": {"steam_appid": 0},
           "Who Is It?": {"steam_appid": 777}
         }}
        """;

    private PcgwCentralDataService Central(out Fake handler)
    {
        handler = new Fake(_ => Text(CentralJson, "application/json"));
        return new PcgwCentralDataService(new HttpClient(handler), Path.Combine(_root, "pcgw_data.json"), fallbackUrl: null);
    }

    private static UpstreamLibraryPolicySnapshot Policy(bool useAppId = false, int cacheVersion = 0)
        => new() { PcgwUseAppId = useAppId, PcgwUrlCacheVersion = cacheVersion };

    [Fact]
    public async Task Pcgw_ComesFromTheManifest_ThenPcgwData_ByNameOrSteamAppId()
    {
        var central = Central(out _);
        await central.GetAsync();
        var links = new GameLinkCatalogue(new HttpClient(Lists()), CacheDir, pcgwData: central);

        Assert.Equal("https://www.pcgamingwiki.com/wiki/Avatar:_Frontiers_of_Pandora", links.PcgwPageFor(Named("AFOP")));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Alan_Wake_2", links.PcgwPageFor(Named("Alan Wake 2")));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Who_Is_It%3F", links.PcgwPageFor(Named("Who Is It?"))); // a ? stays in the title
        var byAppId = Named("Some Localised Title");
        byAppId.SteamAppId = 2840770;
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Avatar:_Frontiers_of_Pandora", links.PcgwPageFor(byAppId));
        var manifestAppId = Named("Another Localised Title");
        manifestAppId.ManifestSteamAppIdOverride = 2840770;
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Avatar:_Frontiers_of_Pandora", links.PcgwPageFor(manifestAppId));
        Assert.Null(links.PcgwPageFor(Named("Unknown Game")));
    }

    [Fact]
    public async Task Pcgw_BackgroundLookup_ReadsSteamAppIdTxt_ThenUsesTheAppIdRedirect_WhenTheManifestSaysItWorks()
    {
        var central = Central(out _);
        var game = Named("Folder Only Game");
        game.InstallPath = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.WriteAllText(Path.Combine(game.InstallPath, "steam_appid.txt"), "2840770\n");
        var http = Lists();
        var links = new GameLinkCatalogue(new HttpClient(http), CacheDir, pcgwData: central, policy: _ => Task.FromResult<UpstreamLibraryPolicySnapshot?>(Policy()));

        Assert.Null(links.PcgwPageFor(game)); // nothing known yet, nothing awaited
        await links.ResolvePcgwAsync(game);
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Avatar:_Frontiers_of_Pandora", links.PcgwPageFor(game)); // AppID found in pcgw_data.json
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Host == "store.steampowered.com");

        var other = Named("Not In Pcgw Data");
        other.SteamAppId = 4242;
        var appIdLinks = new GameLinkCatalogue(new HttpClient(Lists()), Path.Combine(_root, "other"), pcgwData: central,
            policy: _ => Task.FromResult<UpstreamLibraryPolicySnapshot?>(Policy(useAppId: true)));
        await appIdLinks.ResolvePcgwAsync(other);
        Assert.Equal("https://www.pcgamingwiki.com/api/appid.php?appid=4242", appIdLinks.PcgwPageFor(other));

        // Kept on disk: a new session knows the page without asking.
        var again = new GameLinkCatalogue(new HttpClient(new Fake(_ => throw new HttpRequestException("offline"))), Path.Combine(_root, "other"));
        Assert.Equal("https://www.pcgamingwiki.com/api/appid.php?appid=4242", again.PcgwPageFor(other));
    }

    [Fact]
    public async Task Pcgw_WithoutAnAppId_UsesSteamSearch_ThenPcgwsOwnSearch_AndKeepsADefiniteMiss()
    {
        var central = Central(out _);
        var searched = new List<string>();
        var pcgwHttp = new Fake(request =>
        {
            var term = Uri.UnescapeDataString(request.RequestUri!.Query);
            searched.Add(term);
            return term.Contains("Searchable Game")
                ? Text("""["Searchable Game", ["Searchable Game"], [""], ["https://www.pcgamingwiki.com/wiki/Searchable_Game"]]""", "application/json")
                : Text("""["x", [], [], []]""", "application/json");
        });
        var pcgwSearch = new PcGamingWikiGraphicsApiService(new HttpClient(pcgwHttp), Path.Combine(_root, "pcgw-api.json"), minRequestGap: TimeSpan.Zero);
        var steam = Lists(request => request.RequestUri!.Host == "store.steampowered.com"
            ? Text("""{"total": 2, "items": [{"id": 11, "name": "Searchable Game: Remastered"}, {"id": 2840770, "name": "Steam Named Game™"}]}""", "application/json")
            : null);
        var links = new GameLinkCatalogue(new HttpClient(steam), CacheDir, pcgwData: central, pcgwSearch: pcgwSearch,
            policy: _ => Task.FromResult<UpstreamLibraryPolicySnapshot?>(Policy()));

        var steamNamed = Named("Steam Named Game");
        await links.ResolvePcgwAsync(steamNamed);
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Avatar:_Frontiers_of_Pandora", links.PcgwPageFor(steamNamed)); // Steam's AppID → pcgw_data.json
        Assert.Empty(searched);

        await links.ResolvePcgwAsync(Named("Searchable Game"));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Searchable_Game", links.PcgwPageFor(Named("Searchable Game")));

        var missing = Named("No Page Anywhere");
        await links.ResolvePcgwAsync(missing);
        Assert.Null(links.PcgwPageFor(missing));
        var asked = searched.Count;
        await links.ResolvePcgwAsync(missing); // a definite miss is kept
        Assert.Equal(asked, searched.Count);
    }

    [Fact]
    public async Task Pcgw_ANewerManifestCacheVersion_ForgetsFoundPagesAndMisses()
    {
        var game = Named("Not In Pcgw Data");
        game.SteamAppId = 4242;
        var version = 1;
        var links = new GameLinkCatalogue(new HttpClient(Lists()), CacheDir,
            policy: _ => Task.FromResult<UpstreamLibraryPolicySnapshot?>(Policy(useAppId: true, cacheVersion: version)));
        await links.ResolvePcgwAsync(game);
        Assert.NotNull(links.PcgwPageFor(game));

        version = 2;
        var other = Named("Second Game");
        other.SteamAppId = 99;
        await links.ResolvePcgwAsync(other); // the next lookup reads the manifest's new version
        Assert.Null(new GameLinkCatalogue(new HttpClient(Lists()), CacheDir).PcgwPageFor(game));
        Assert.Equal("https://www.pcgamingwiki.com/api/appid.php?appid=99", links.PcgwPageFor(other));
    }

    [Fact]
    public async Task Pcgw_ASearchThatFailedOrWasPaused_IsNotRememberedAsNoPage()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var mode = "offline";
        var searches = 0;
        var pcgwHttp = new Fake(request =>
        {
            Interlocked.Increment(ref searches);
            return mode switch
            {
                "offline" => throw new HttpRequestException("offline"),
                "busy" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => Text("""["Late Game", ["Late Game"], [""], ["https://www.pcgamingwiki.com/wiki/Late_Game"]]""", "application/json")
            };
        });
        var pcgwSearch = new PcGamingWikiGraphicsApiService(new HttpClient(pcgwHttp), Path.Combine(_root, "pcgw-api.json"), clock, minRequestGap: TimeSpan.Zero);
        var steam = Lists(request => request.RequestUri!.Host == "store.steampowered.com" ? Text("""{"total": 0, "items": []}""", "application/json") : null);
        var links = new GameLinkCatalogue(new HttpClient(steam), CacheDir, pcgwSearch: pcgwSearch, timeProvider: clock);
        var game = Named("Late Game");

        await links.ResolvePcgwAsync(game); // PCGW unreachable
        Assert.Equal(1, searches);
        Assert.Null(links.PcgwPageFor(game));

        clock.Now += TimeSpan.FromMinutes(6); // the network pause is over
        mode = "busy";
        await links.ResolvePcgwAsync(game); // PCGW answers 503: paused again
        Assert.Equal(2, searches);
        Assert.True(pcgwSearch.IsPaused);

        await links.ResolvePcgwAsync(game); // still paused: nothing asked, and still not "no page"
        Assert.Equal(2, searches);

        clock.Now += TimeSpan.FromMinutes(16);
        mode = "answers";
        await links.ResolvePcgwAsync(game);
        Assert.Equal(3, searches);
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Late_Game", links.PcgwPageFor(game));
    }

    [Fact]
    public async Task SteamSearch_ARateLimit_PausesStoreSearchesForFiveMinutes()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-15T12:00:00Z"));
        var limited = true;
        var http = Lists(request => request.RequestUri!.Host == "store.steampowered.com"
            ? limited ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Text("""{"total": 0, "items": []}""", "application/json")
            : null);
        int SteamRequests() => http.Requests.Count(r => r.RequestUri!.Host == "store.steampowered.com");
        var links = new GameLinkCatalogue(new HttpClient(http), CacheDir, timeProvider: clock); // no PCGW search: each lookup ends after Steam

        await links.ResolvePcgwAsync(Named("First Game"));
        Assert.Equal(1, SteamRequests()); // 429

        await links.ResolvePcgwAsync(Named("Second Game"));
        clock.Now += TimeSpan.FromMinutes(4);
        await links.ResolvePcgwAsync(Named("Third Game"));
        Assert.Equal(1, SteamRequests()); // paused: Steam is not asked again

        clock.Now += TimeSpan.FromMinutes(2);
        limited = false;
        await links.ResolvePcgwAsync(Named("Fourth Game"));
        Assert.Equal(2, SteamRequests());
    }

    [Fact]
    public void SteamSearch_MatchesOnlyTheSameNormalisedName()
    {
        var raw = Encoding.UTF8.GetBytes("""{"items": [{"id": 1, "name": "Game 2"}, {"id": 2, "name": "GAME™"}, {"id": "x", "name": "Game"}]}""");
        Assert.Equal(2, GameLinkCatalogue.MatchSteamSearch("Game", raw));
        Assert.Null(GameLinkCatalogue.MatchSteamSearch("Other", raw));
        Assert.Equal(570, GameLinkCatalogue.SteamAppIdFile(WriteAppIdFile("570")));
        Assert.Null(GameLinkCatalogue.SteamAppIdFile(WriteAppIdFile("abc")));
        Assert.Null(GameLinkCatalogue.SteamAppIdFile(WriteAppIdFile("-5")));
    }

    private string WriteAppIdFile(string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "appid-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(dir, "steam_appid.txt"), content);
        return dir;
    }

    // ── Card menu ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MenuItems_ListOnlyThePagesAGameHas_InAWebPagesSubmenu()
    {
        var presenter = new GameLinksPresenter(null!, null!, null!) { Links = new GameLinkCatalogue(new HttpClient(Lists()), CacheDir) };
        await presenter.Links.LoadAsync();
        var opened = new List<string>();

        var shared = Named("Shared Game");
        shared.PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Shared_Game";
        var items = presenter.MenuItemsFor(shared, opened.Add);
        var pages = Assert.Single(items);
        Assert.Equal("web-pages", pages.Id);
        Assert.Equal(UiLocalizationService.Text("Cat.WebPages"), pages.Label);
        Assert.Equal(["pcgw-page", "uwfix-page"], pages.Children!.Select(c => c.Id));
        pages.Children![1].Command.Execute(null);
        Assert.Equal(["https://codeberg.org/Lyall/Shared"], opened);
        Assert.Equal("https://codeberg.org/Lyall/Shared", pages.Children[1].Tip);

        var cyberpunk = presenter.MenuItemsFor(Named("Cyberpunk 2077"), opened.Add).Single();
        Assert.Equal(["ultraplus-page", "hdr-database-page"], cyberpunk.Children!.Select(c => c.Id));

        Assert.Empty(presenter.MenuItemsFor(Named("Plain Quest"), opened.Add)); // no page: nothing, not a disabled entry
        Assert.Empty(new GameLinksPresenter(null!, null!, null!).MenuItemsFor(shared, opened.Add)); // no catalogue wired
    }

    [Fact]
    public async Task Discussion_IsOffered_ForTheMatchedRenoDxModsHttpsPage()
    {
        var db = """
            [{"name": "Discussed Game", "status": "Done", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/renodx-discussed.addon64", "discussionUrl": "https://github.com/clshortfuse/renodx/discussions/490"},
             {"name": "Plain Http Game", "status": "Done", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/renodx-plain.addon64", "discussionUrl": "http://example.com/d"},
             {"name": "Silent Game", "status": "Done", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/renodx-silent.addon64"}]
            """;
        var renoDx = new RenoDxCatalogService(new HttpClient(new Fake(_ => Text(db, "application/json"))));
        var presenter = new GameLinksPresenter(null!, null!, null!) { RenoDx = renoDx };
        foreach (var name in new[] { "Discussed Game", "Plain Http Game", "Silent Game" })
            await renoDx.PrimeResolvedMatchAsync(Named(name), []);

        var item = Assert.Single(presenter.MenuItemsFor(Named("Discussed Game"), _ => { }));
        Assert.Equal("discussion", item.Id);
        Assert.Equal("https://github.com/clshortfuse/renodx/discussions/490", item.Tip);
        Assert.Equal(UiLocalizationService.Text("Cat.DiscussionPage"), item.Label);
        Assert.Empty(presenter.MenuItemsFor(Named("Plain Http Game"), _ => { }));
        Assert.Empty(presenter.MenuItemsFor(Named("Silent Game"), _ => { }));
        Assert.Empty(presenter.MenuItemsFor(Named("Never Probed"), _ => { }));
    }

    [Fact]
    public void WikiFallback_TakesTheNameCellsLinkAsTheDiscussionPage()
    {
        var html = """
            <table><tr><th>Name</th><th>Mod</th><th>Status</th><th>Notes</th></tr>
            <tr><td><a href="/clshortfuse/renodx/discussions/7">Wiki Game</a></td><td><a href="https://github.com/a/b/releases/download/snapshot/renodx-wiki.addon64">x</a></td><td>✅</td><td>note</td></tr>
            </table>
            """;
        var entry = Assert.Single(RenoDxCatalogService.ParseWiki(html));
        Assert.Equal("https://github.com/clshortfuse/renodx/discussions/7", entry.DiscussionUrl);
    }

    [Fact]
    public void Database_KeepsOnlyAnHttpsDiscussionUrl()
    {
        var db = """
            [{"name": "A", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/a.addon64", "discussionUrl": "https://github.com/clshortfuse/renodx/discussions/1"},
             {"name": "B", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/b.addon64", "discussionUrl": "http://github.com/clshortfuse/renodx/discussions/2"},
             {"name": "C", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/c.addon64", "discussionUrl": "javascript:alert(1)"}]
            """;
        var entries = RenoDxCatalogService.ParseStructuredDatabase(db).ToDictionary(e => e.Name, e => e.DiscussionUrl);
        Assert.Equal("https://github.com/clshortfuse/renodx/discussions/1", entries["A"]);
        Assert.Null(entries["B"]);
        Assert.Null(entries["C"]);
    }

    [Fact]
    public async Task Manifest_BindsPcgwUseAppIdAndCacheVersion()
    {
        var json = """{"version": 3, "pcgwUseAppId": true, "pcgwUrlCacheVersion": 4}""";
        var service = new UpstreamRhiManifestService(new HttpClient(new Fake(_ => Text(json, "application/json"))),
            "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json");
        var policy = await service.FetchLibraryPolicyAsync();
        Assert.True(policy.PcgwUseAppId);
        Assert.Equal(4, policy.PcgwUrlCacheVersion);

        var empty = new UpstreamRhiManifestService(new HttpClient(new Fake(_ => Text("""{"version": 3}""", "application/json"))),
            "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json");
        var defaults = await empty.FetchLibraryPolicyAsync();
        Assert.False(defaults.PcgwUseAppId);
        Assert.Equal(0, defaults.PcgwUrlCacheVersion);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A fake server whose request log is safe for the lists' parallel loads.</summary>
    internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<HttpRequestMessage> _requests = new();
        public IReadOnlyList<HttpRequestMessage> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>A fake server that holds every answer until <see cref="Release"/>; <see cref="Asked"/> completes once a URL was requested.</summary>
    internal sealed class HeldFake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _asked = new(StringComparer.Ordinal);

        public void Release() => _gate.TrySetResult();

        public Task Asked(string url) => _asked.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _asked.GetOrAdd(request.RequestUri!.AbsoluteUri, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var response = respond(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}

/// <summary>#183 in the app: the card menu offers the web pages the app's catalogue knows, and none for a game without any.</summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GameLinksCardMenuTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-game-links-menu-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheAppsCardMenu_OffersWebPages_OnlyWhereTheGameHasOne()
    {
        var linked = new DetectedGame { Name = "Linked Quest", Source = "Steam", InstallPath = Path.Combine(_root, "linked"), SteamAppId = 101,
            UltraPlusUrlOverride = "https://theultraplace.com/games/linked/", PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Linked_Quest" };
        var plain = new DetectedGame { Name = "Plain Quest", Source = "Steam", InstallPath = Path.Combine(_root, "plain"), SteamAppId = 102 };
        Directory.CreateDirectory(linked.InstallPath);
        Directory.CreateDirectory(plain.InstallPath);
        using var fx = await ViewModelFixture.StartAsync([linked, plain]);
        using var http = new HttpClient(new GameLinkCatalogueTests.Fake(_ => throw new HttpRequestException("offline test")));
        var wine = new WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        fx.Vm.Parity = services.Parity;
        Assert.NotNull(services.Parity!.GameLinks.Links); // the app wires the catalogue
        Assert.NotNull(services.Parity.GameLinks.RenoDx);

        var pages = Assert.Single(fx.Vm.CardMenuFor(fx.Vm.Games.Single(r => r.Name == "Linked Quest")), i => i.Id == "web-pages");
        Assert.Equal(["pcgw-page", "ultraplus-page"], pages.Children!.Select(c => c.Id));
        Assert.Equal(UiLocalizationService.Text("Cat.UltraPlusPage"), pages.Children![1].Label);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(fx.Vm.Games.Single(r => r.Name == "Plain Quest")), i => i.Id is "web-pages" or "discussion");
    }
}
