using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Artwork;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-b (#305): upstream 2.9.0–2.9.4's Spotlight layout — the layout switch and side panel, the banner and cover
/// sizes, the strip's ⚙, the "What's installed" tiles, the Steam artwork service (CDN, store API with hashed paths pinned
/// from a real answer, misses, custom art), the screenshots listing (ReShade's SavePath mapped through the Wine prefix) and
/// the freedesktop Trash.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SpotlightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-spotlight-{Guid.NewGuid():N}");

    public SpotlightTests()
    {
        UiLocalizationService.Apply("en-US");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        GameLayoutPresenter.SpotlightAvailable = true;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotlight", name);

    private (LinuxSettingsStore Store, LinuxAppSettings Settings, Func<Action<LinuxAppSettings>, string?> Save) NewSettings(LinuxAppSettings? start = null)
    {
        var store = new LinuxSettingsStore(Path.Combine(_root, "config", "settings.json"), Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        var settings = start ?? new LinuxAppSettings();
        store.Save(settings);
        string? Save(Action<LinuxAppSettings> change)
        {
            var copy = store.Load();
            change(copy);
            store.Save(copy);
            return null;
        }
        return (store, settings, Save);
    }

    // ---- the layout and the side panel -------------------------------------------------------------------------------

    [Fact]
    public void Spotlight_IsAvailable_AndOpensSectionsInTheSidePanel_NotSaved()
    {
        var (store, settings, save) = NewSettings();
        var layout = new GameLayoutPresenter(() => settings, save);
        Assert.True(layout.Choices.Single(c => c.Layout == MainLayout.Spotlight).IsEnabled);
        Assert.Null(layout.SetLayout(MainLayout.Spotlight));
        Assert.Equal("Spotlight", store.Load().MainLayout);
        Assert.True(layout.IsSpotlight && layout.UsesTabsChrome && !layout.IsTabs);
        Assert.False(layout.ShowsSectionHeaders);

        // Nothing shows on the page until a tile opens its section in the side panel.
        Assert.All(GamePageSections.TabOrder.Skip(1), key => Assert.False(layout.IsSectionShown(key)));
        Assert.Null(layout.ShowSection(GamePageSections.Driver));
        Assert.Equal(GamePageSections.Driver, layout.PanelKey);
        Assert.True(layout.IsSectionShown(GamePageSections.Driver));
        Assert.False(layout.IsSectionShown(GamePageSections.Extras));
        Assert.Equal("", store.Load().DetailSelectedTab); // Tabs' remembered tab is not touched

        // Manage components: the component cards (the Components section).
        layout.OpenPanel(GamePageSections.Overview);
        Assert.True(layout.IsSectionShown(GamePageSections.Components));
        layout.ClosePanel();
        Assert.Null(layout.PanelKey);
        Assert.False(layout.IsSectionShown(GamePageSections.Components));
    }

    [Fact]
    public void LeavingSpotlight_ClosesThePanel_AndThePanelIsSpotlightsOnly()
    {
        var (_, settings, save) = NewSettings(new LinuxAppSettings { MainLayout = "Spotlight" });
        var layout = new GameLayoutPresenter(() => settings, save);
        layout.OpenPanel(SpotlightPresenter.ScreenshotsKey);
        Assert.Equal(SpotlightPresenter.ScreenshotsKey, layout.PanelKey);
        layout.SetLayout(MainLayout.Tabs);
        Assert.Null(layout.PanelKey);
        layout.OpenPanel(GamePageSections.Extras);
        Assert.Null(layout.PanelKey);
        Assert.Null(layout.ShowSection(GamePageSections.Extras)); // Tabs: the tile opens the tab
        Assert.Equal(GamePageSections.Extras, layout.SelectedTab);
    }

    [Fact]
    public void PanelTitles_AreUpstreamsNames()
    {
        Assert.Equal("Components", SpotlightPresenter.PanelTitle(GamePageSections.Overview));
        Assert.Equal("Game Overrides", SpotlightPresenter.PanelTitle(GamePageSections.GameOverrides));
        Assert.Equal("Screenshots", SpotlightPresenter.PanelTitle(SpotlightPresenter.ScreenshotsKey));
        Assert.Equal("Achievements", SpotlightPresenter.PanelTitle(SpotlightPresenter.AchievementsKey));
        Assert.Equal("Game settings", SpotlightPresenter.PanelTitle(SpotlightPresenter.GameSettingsKey));
        Assert.Equal("Artwork", SpotlightPresenter.PanelTitle(SpotlightPresenter.ArtworkPickerKey));
        Assert.Equal(GamePageSections.GlanceSections, SpotlightPresenter.TuneSections);
        UiLocalizationService.Apply("pt-BR");
        Assert.Equal("Capturas de tela", SpotlightPresenter.PanelTitle(SpotlightPresenter.ScreenshotsKey));
    }

    // ---- sizes -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(800, 340)]    // narrow: the minimum
    [InlineData(1240, 400)]   // follows the art's shape (3840 x 1240)
    [InlineData(3000, 500)]   // ultrawide: capped
    [InlineData(0, 340)]
    public void Banner_FollowsTheArtsShape_BetweenMinAndMax(double width, double height)
    {
        var (_, settings, save) = NewSettings();
        Assert.Equal(height, new SpotlightPresenter(() => settings, save).HeroHeight(width));
    }

    [Fact]
    public void Banner_DraggedHeight_IsSavedAndClamped_DoubleClickGoesBackToAutomatic()
    {
        var (store, settings, save) = NewSettings();
        var spot = new SpotlightPresenter(() => settings, save);
        var changes = 0;
        spot.Changed += () => changes++;
        Assert.Equal(900, SpotlightPresenter.DraggedHeroHeight(700, 400));
        Assert.Equal(200, SpotlightPresenter.DraggedHeroHeight(340, -300));
        Assert.Null(spot.SetHeroHeight(612.4));
        Assert.Equal(612, store.Load().SpotlightHeroHeight);
        Assert.Equal(612, spot.HeroHeight(3000)); // the dragged height wins over the width
        Assert.Null(spot.SetHeroHeight(0));
        Assert.Equal(0, store.Load().SpotlightHeroHeight);
        Assert.Equal(400, spot.HeroHeight(1240));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Covers_ResizeFromTheEdgeFacingThePage_ClampedAndSaved()
    {
        var (store, settings, save) = NewSettings();
        var spot = new SpotlightPresenter(() => settings, save);
        Assert.Equal((120, 180), (spot.TileWidth, spot.CoverHeight));
        Assert.Equal(1.5, spot.CoverScaleFromDrag(180, 90));   // covers on top: dragging down (into the page) grows them
        Assert.Equal(2.0, spot.CoverScaleFromDrag(180, 900));
        Assert.Equal(0.6, spot.CoverScaleFromDrag(180, -900));
        spot.SetShelfAtBottom(true);
        Assert.Equal(1.5, spot.CoverScaleFromDrag(180, -90));  // at the bottom the page is above: dragging up grows them
        Assert.Null(spot.SetCoverScale(1.5));
        Assert.Equal(1.5, store.Load().SpotlightCoverScale);
        Assert.Equal((180, 270), (spot.TileWidth, spot.CoverHeight));
        Assert.Null(spot.SetCoverScale(9));
        Assert.Equal(2.0, store.Load().SpotlightCoverScale);
    }

    [Theory]
    [InlineData(5, 120, 1400, 380)] // 5 covers (5 x 128) fit: centred
    [InlineData(20, 120, 1400, 20)] // a longer row starts at the left
    [InlineData(0, 120, 1400, 20)]
    [InlineData(10, 120, 1288, 20)] // 1280 + 2 x 20 does not fit
    [InlineData(10, 120, 1320, 20)] // exactly fits with the 20 px sides: centred at 20
    public void Covers_CentreWhenTheyAllFit(int count, double tile, double width, double side)
        => Assert.Equal(side, SpotlightPresenter.ShelfSidePadding(count, tile, width));

    [Fact]
    public void StripCog_CoversBelowAndNoBackground_AreSaved_SettingsNormalisesSizes()
    {
        var (store, settings, save) = NewSettings();
        var spot = new SpotlightPresenter(() => settings, save);
        Assert.Null(spot.SetShelfAtBottom(true));
        Assert.Null(spot.SetShelfPlain(true));
        var saved = store.Load();
        Assert.True(saved.SpotlightShelfAtBottom && saved.SpotlightShelfPlain);
        Assert.True(spot.ShelfAtBottom && spot.ShelfPlain);
        var odd = new LinuxAppSettings { SpotlightHeroHeight = 5000, SpotlightCoverScale = -1 }.Normalize();
        Assert.Equal((900.0, 1.0), (odd.SpotlightHeroHeight, odd.SpotlightCoverScale));
        Assert.True(new LinuxAppSettings().ControllerSupport);   // #308 upstream default: on
        Assert.False(new LinuxAppSettings().SteamAchievements);  // #307 upstream default: opt-in
    }

    // ---- What's installed ------------------------------------------------------------------------------------------

    private static ComponentRowViewModel Row(ManagedComponentKind kind, ComponentAvailability availability, string? installed = null)
        => new(new ManagedComponent { Kind = kind, DisplayName = kind.ToString(), Availability = availability, InstalledVersion = installed },
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

    [Fact]
    public void StatusTiles_OnePerComponent_FrameLimiterSharesOne_WithUpstreamsWords()
    {
        var tiles = SpotlightPresenter.StatusTiles([
            Row(ManagedComponentKind.ReShade, ComponentAvailability.Installed),
            Row(ManagedComponentKind.RenoDX, ComponentAvailability.UpdateAvailable, "0.1"),
            Row(ManagedComponentKind.ReLimiter, ComponentAvailability.Available),
            Row(ManagedComponentKind.DisplayCommander, ComponentAvailability.Available),
            Row(ManagedComponentKind.OptiScaler, ComponentAvailability.Unsupported),
            Row(ManagedComponentKind.LumaFramework, ComponentAvailability.Unknown),
        ]);
        Assert.Equal(["ReShade", "RenoDX", "Frame limiter", "OptiScaler", "LumaFramework"], tiles.Select(t => t.Name));
        Assert.Equal([StatusTone.Installed, StatusTone.Update, StatusTone.None, StatusTone.Unsupported, StatusTone.None], tiles.Select(t => t.Tone));
        Assert.Equal("Update available · 0.1", tiles[1].Status);
        Assert.Equal("Choose ReLimiter or DisplayCommander", tiles[2].Status);
        Assert.Equal("Not installed", tiles[4].Status);
    }

    [Fact]
    public void StatusTiles_TheInstalledFrameLimiterShows()
    {
        var tiles = SpotlightPresenter.StatusTiles([
            Row(ManagedComponentKind.ReLimiter, ComponentAvailability.Available),
            Row(ManagedComponentKind.DisplayCommander, ComponentAvailability.Installed),
        ]);
        var tile = Assert.Single(tiles);
        Assert.Equal(("DisplayCommander", StatusTone.Installed), (tile.Name, tile.Tone));
    }

    // ---- artwork ---------------------------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat((byte)0x42, 4096)];

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("") };

    [Fact]
    public void StoreApiAnswer_PinnedFromSteam_GivesTheHashedAssetLinks()
    {
        var assets = SteamArtworkService.ParseAssets(File.ReadAllText(Fixture("storebrowse-2828710.json")))!;
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2828710/deef1b198941015428f5b3d3e29e819f7ef86abc/library_capsule.jpg?t=1793135279",
            assets["library_capsule"]);
        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2828710/fcaf991a4097da7178b0f27c41176f0dd62ae4cc/library_hero.jpg?t=1793135279",
            assets["library_hero"]);
        Assert.True(assets.ContainsKey("header"));
        Assert.Null(SteamArtworkService.ParseAssets("{\"response\":{\"store_items\":[]}}"));
        Assert.Null(SteamArtworkService.ParseAssets("not json"));
    }

    [Fact]
    public async Task SteamArt_CdnFirst_ThenTheStoreApisHashedPath_CachedOnce()
    {
        var handler = new FakeHandler(r =>
        {
            var url = r.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://api.steampowered.com/IStoreBrowseService/GetItems/v1", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Fixture("storebrowse-2828710.json"))) };
            return url.Contains("/deef1b198941015428f5b3d3e29e819f7ef86abc/library_capsule.jpg", StringComparison.Ordinal) ? Image(Jpeg) : NotFound();
        });
        var art = new SteamArtworkService(new HttpClient(handler), Path.Combine(_root, "cache"), Path.Combine(_root, "custom"));
        var path = await art.GetAsync(2828710, ArtworkKind.Cover, CancellationToken.None);
        Assert.Equal(Path.Combine(_root, "cache", "2828710", "cover.jpg"), path);
        Assert.Equal(Jpeg, File.ReadAllBytes(path!));
        Assert.Equal(SteamArtworkService.CdnLinks(2828710, ArtworkKind.Cover).Concat([SteamArtworkService.AssetLookupUrl(2828710)]),
            handler.Requests.Take(3).Select(r => r.RequestUri!.AbsoluteUri));
        var asked = handler.Requests.Count;
        Assert.Equal(path, await art.GetAsync(2828710, ArtworkKind.Cover, CancellationToken.None));
        Assert.Equal(asked, handler.Requests.Count); // from the cache, never downloaded again
    }

    [Fact]
    public async Task SteamArt_AMiss_IsRememberedForAWeek_AndAWebPageIsNeverSavedAsArt()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var handler = new FakeHandler(r => r.RequestUri!.Host == "api.steampowered.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{}}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>soft 404</html>", Encoding.UTF8, "text/html") });
        var art = new SteamArtworkService(new HttpClient(handler), Path.Combine(_root, "cache"), Path.Combine(_root, "custom"), () => now);
        Assert.Null(await art.GetAsync(10, ArtworkKind.Hero, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "cache", "10", "hero.jpg")));
        var asked = handler.Requests.Count;
        now = now.AddDays(6);
        Assert.Null(await art.GetAsync(10, ArtworkKind.Hero, CancellationToken.None));
        Assert.Equal(asked, handler.Requests.Count); // within a week: not asked again
        now = now.AddDays(2);
        Assert.Null(await art.GetAsync(10, ArtworkKind.Hero, CancellationToken.None));
        Assert.True(handler.Requests.Count > asked);
    }

    [Fact]
    public void AppId_ManifestOverride_ThenSteam_ThenPcgwByStoreName_ThenTheFullGamesForADemo()
    {
        var json = "{\"games\":{\"Dishonored\":{\"steam_appid\":205100},\"Hela: Of Mice & Magic\":{\"steam_appid\":2523190}}}";
        var pcgw = PcgwCentralData.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(1, SteamArtworkService.ResolveAppId(new DetectedGame { Name = "x", ManifestSteamAppIdOverride = 1, SteamAppId = 2 }, pcgw));
        Assert.Equal(2, SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Dishonored", SteamAppId = 2 }, pcgw));
        Assert.Equal(205100, SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Dishonored® Definitive Edition (PC)", Source = "Xbox" }, pcgw));
        Assert.Equal(2523190, SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Hela: Of Mice & Magic Demo", Source = "Epic" }, pcgw));
        Assert.Equal(205100, SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Some Store Name", UserDisplayName = "Dishonored", Source = "GOG" }, pcgw));
        Assert.Null(SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Unknown Game" }, pcgw));
        Assert.Null(SteamArtworkService.ResolveAppId(new DetectedGame { Name = "Dishonored" }, null));
    }

    [Fact]
    public void CustomArt_IsACopy_TheUsersFileIsNeverChanged_UseSteamRemovesOnlyRhisCopy()
    {
        var art = new SteamArtworkService(new HttpClient(new FakeHandler(_ => NotFound())), Path.Combine(_root, "cache"), Path.Combine(_root, "custom"));
        var picked = Path.Combine(_root, "Pictures", "cover.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(picked)!);
        File.WriteAllBytes(picked, Jpeg);
        var copy = art.SetCustom("steam:1", ArtworkKind.Cover, picked, out var error);
        Assert.Null(error);
        Assert.StartsWith(Path.Combine(_root, "custom"), copy);
        Assert.EndsWith(".jpg", copy);
        Assert.Equal(copy, art.TryGetCustom("steam:1", ArtworkKind.Cover));
        Assert.Null(art.TryGetCustom("steam:1", ArtworkKind.Hero));

        var second = art.SetCustom("steam:1", ArtworkKind.Cover, picked, out _);
        Assert.NotEqual(copy, second);          // a new name per change
        Assert.False(File.Exists(copy));        // RHI's old copy goes
        art.ClearCustom("steam:1", ArtworkKind.Cover);
        Assert.Null(art.TryGetCustom("steam:1", ArtworkKind.Cover));
        Assert.Equal(Jpeg, File.ReadAllBytes(picked)); // the user's file is untouched throughout

        var text = Path.Combine(_root, "Pictures", "notes.png");
        File.WriteAllText(text, "not an image");
        Assert.Null(art.SetCustom("steam:1", ArtworkKind.Hero, text, out error));
        Assert.Equal(SteamArtworkService.NotAnImageMessage, error);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, true)]
    [InlineData(new byte[] { (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l' }, false)]
    [InlineData(new byte[] { 0x4D, 0x5A }, false)]
    public void ImageSniffing(byte[] head, bool image) => Assert.Equal(image, SteamArtworkService.LooksLikeImage(head));

    // ---- SteamGridDB --------------------------------------------------------------------------------------------------

    private SteamGridDbService Sgdb(FakeHandler handler, string? key, string config = "config")
    {
        var store = new RHI.Platform.Linux.Persistence.SecretFileStore(Path.Combine(_root, config, "steamgriddb_api.txt"));
        if (key is not null) store.Write(key);
        return new SteamGridDbService(new HttpClient(handler), store, Path.Combine(_root, "sgdb"));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string GridReply = """
        {"success":true,"page":0,"total":2,"limit":50,"data":[
          {"id":101,"score":3,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"notes":null,"mime":"image/png","language":"en",
           "url":"https://cdn2.steamgriddb.com/grid/abc.png","thumb":"https://cdn2.steamgriddb.com/thumb/abc.jpg","lock":false,"epilepsy":false,"upvotes":3,"downvotes":0,
           "author":{"name":"Someone","steam64":"76561197960265729","avatar":"https://avatars.steamstatic.com/x.jpg"}},
          {"id":102,"score":0,"width":600,"height":900,"mime":"image/png","url":"https://evil.example/grid.png","thumb":"https://evil.example/t.png","author":{"name":"x"}}
        ]}
        """;

    [Fact]
    public async Task SteamGridDb_NoKey_AsksForOne_AndNothingIsSent()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        var page = await Sgdb(handler, null).GetArtAsync(238010, "Game", ArtworkKind.Cover, 0, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.NoKey, page.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SteamGridDb_ByAppId_KeyOnlyToTheApi_ImagesOffItsServersLeftOut_ListCached()
    {
        var handler = new FakeHandler(r => r.RequestUri!.Host == "www.steamgriddb.com" ? Json(GridReply) : Image(Jpeg));
        var sgdb = Sgdb(handler, "k3y-abc");
        var page = await sgdb.GetArtAsync(238010, "Game", ArtworkKind.Cover, 0, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.Ok, page.Status);
        var art = Assert.Single(page.Items);
        Assert.Equal((101, "Someone", 600, 900), (art.Id, art.Author, art.Width, art.Height));
        var api = Assert.Single(handler.Requests);
        Assert.Equal("https://www.steamgriddb.com/api/v2" + SteamGridDbService.ListPath("steam/238010", ArtworkKind.Cover, 0), api.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer k3y-abc", api.Headers.Authorization!.ToString());

        Assert.NotNull(await sgdb.GetThumbAsync(art, CancellationToken.None));
        var (full, error) = await sgdb.DownloadFullAsync(art, CancellationToken.None);
        Assert.Null(error);
        Assert.True(File.Exists(full));
        Assert.All(handler.Requests.Skip(1), r => Assert.Null(r.Headers.Authorization)); // the image servers never get the key

        var asked = handler.Requests.Count;
        await sgdb.GetArtAsync(238010, "Game", ArtworkKind.Cover, 0, CancellationToken.None);
        Assert.Equal(asked, handler.Requests.Count); // the list is cached for a day
    }

    [Fact]
    public async Task SteamGridDb_UnknownAppId_SearchesByName_ARefusedKeyIsSaid()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.StartsWith("/api/v2/heroes/steam/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"success\":false}") },
            var p when p.StartsWith("/api/v2/search/autocomplete/", StringComparison.Ordinal) => Json("{\"success\":true,\"data\":[{\"id\":5555,\"name\":\"My Game\"}]}"),
            "/api/v2/heroes/game/5555" => Json(GridReply),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") },
        });
        var page = await Sgdb(handler, "key").GetArtAsync(1, "My Game", ArtworkKind.Hero, 0, CancellationToken.None);
        Assert.Equal(SteamGridDbStatus.Ok, page.Status);
        Assert.Single(page.Items);
        Assert.Equal("/api/v2/search/autocomplete/My%20Game", handler.Requests[1].RequestUri!.AbsolutePath);

        var refused = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") });
        Assert.Equal(SteamGridDbStatus.BadKey, (await Sgdb(refused, "key").GetArtAsync(2, "x", ArtworkKind.Cover, 0, CancellationToken.None)).Status);
        var fresh = Sgdb(refused, null, "fresh");
        Assert.Equal(SteamGridDbStatus.BadKey, await fresh.SaveKeyAsync("wrong-key"));
        Assert.False(fresh.HasKey); // a refused key is not saved
    }

    // ---- the game menu's Artwork -------------------------------------------------------------------------------------

    [Fact]
    public async Task ArtworkMenu_InSpotlightOnly_UseSteamOnlyWhenCustomArtIsSet()
    {
        var detected = new DetectedGame
        {
            Name = "Art Game", InstallPath = Directory.CreateDirectory(Path.Combine(_root, "Art Game")).FullName, Source = "Steam", Runner = "Proton",
        };
        using var fx = await ViewModelFixture.StartAsync([detected]);
        var vm = fx.Vm;
        var game = vm.AllLibraryGames.Single(g => g.Name == "Art Game");
        vm.UseSpotlightServices(null, null, new SteamArtworkService(new HttpClient(new FakeHandler(_ => NotFound())), Path.Combine(_root, "cache"), Path.Combine(_root, "custom")));
        vm.Layout.SetLayout(MainLayout.Classic);
        Assert.Null(vm.ArtworkMenuFor(game));
        vm.Layout.SetLayout(MainLayout.Spotlight);
        var menu = vm.ArtworkMenuFor(game)!;
        Assert.Equal("Artwork", menu.Label);
        Assert.Equal(["Set custom cover...", "Set custom banner...", "Browse covers...", "Browse banners..."], menu.Children!.Select(c => c.Label));
        var picked = Path.Combine(_root, "banner.png");
        File.WriteAllBytes(picked, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);
        Assert.True(vm.SetCustomArt(game, ArtworkKind.Hero, picked));
        Assert.Equal("Use Steam banner", vm.ArtworkMenuFor(game)!.Children!.Last().Label);
        vm.ArtworkMenuFor(game)!.Children!.Last().Command.Execute(null);
        Assert.Null(vm.Artwork!.TryGetCustom(MainWindowViewModel.ArtKey(game), ArtworkKind.Hero));
        Assert.True(File.Exists(picked));
        vm.Layout.SetLayout(MainLayout.Classic);
    }

    // ---- screenshots -----------------------------------------------------------------------------------------------

    [Fact]
    public void ReShadeSavePath_FromTheScreenshotSection()
    {
        Assert.Equal(@"Z:\home\me\Shots", ScreenshotLibrary.ParseSavePath(["[GENERAL]", "SavePath=wrong", "", "[SCREENSHOT]", "ClearAlpha=1", @"SavePath=Z:\home\me\Shots", "[OVERLAY]"]));
        Assert.Null(ScreenshotLibrary.ParseSavePath(["[GENERAL]", "SavePath=x"]));
    }

    [Fact]
    public void ReShadeSavePath_WindowsPathsMapToLinuxFolders()
    {
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "Shots"));
        var gameDir = Path.Combine(_root, "game");
        Assert.Equal("/home/me/Pictures/RHI", ScreenshotLibrary.ToLinuxFolder(@"Z:\home\me\Pictures\RHI", gameDir, prefix));
        Assert.Equal(Path.Combine(prefix, "drive_c", "Shots"), ScreenshotLibrary.ToLinuxFolder(@"C:\shots", gameDir, prefix)); // Windows ignores case
        Assert.Null(ScreenshotLibrary.ToLinuxFolder(@"C:\Shots", gameDir, null)); // no prefix: no C: drive
        Assert.Equal(Path.Combine(gameDir, "shots"), ScreenshotLibrary.ToLinuxFolder(@".\shots", gameDir, prefix));
        Assert.Equal("/abs", ScreenshotLibrary.ToLinuxFolder("\"/abs\"", gameDir, prefix));
    }

    [Theory]
    [InlineData("Game 2026-11-08 21-30-00.png", "Game", true)]
    [InlineData("game_2026.png", "Game", true)]
    [InlineData("Game.png", "Game", true)]
    [InlineData("GameOfThrones 2026.png", "Game", false)]
    [InlineData("Other 2026.png", "Game", false)]
    public void Screenshots_MatchTheGamesExeName(string file, string exe, bool expected)
        => Assert.Equal(expected, ScreenshotLibrary.MatchesExe(file, exe));

    [Fact]
    public void Screenshots_FromReShadeIni_NewestFirst_ASharedFolderShowsOnlyThisGames()
    {
        var gameDir = Path.Combine(_root, "steamapps", "common", "Game");
        var shared = Path.Combine(_root, "Pictures", "ReShade");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(shared);
        File.WriteAllLines(Path.Combine(gameDir, "ReShade.ini"), ["[SCREENSHOT]", $"SavePath=Z:{shared.Replace('/', '\\')}"]);
        void Shot(string name, int day) { var p = Path.Combine(shared, name); File.WriteAllBytes(p, Jpeg); File.SetLastWriteTimeUtc(p, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc)); }
        Shot("Game 2026-11-01.png", 1);
        Shot("Game 2026-11-03.jpg", 3);
        Shot("Other 2026-11-04.png", 4);
        Shot("Game notes.txt", 5);
        var game = new DetectedGame { Name = "Game", InstallPath = gameDir, BinaryDirectory = gameDir, ExecutablePath = Path.Combine(gameDir, "Game.exe") };
        var listing = ScreenshotLibrary.Find(game, rhiFolder: null, perGameFolders: false);
        Assert.Equal(shared, listing.Folder);
        Assert.Equal(["Game 2026-11-03.jpg", "Game 2026-11-01.png"], listing.Shots.Select(f => f.Name));

        // The game's own folder (no ReShade.ini SavePath): every screenshot there.
        var own = new DetectedGame { Name = "Own", InstallPath = shared, BinaryDirectory = shared, ExecutablePath = Path.Combine(shared, "Nothing.exe") };
        Assert.Equal(3, ScreenshotLibrary.Find(own, null, false).Shots.Count);
    }

    // ---- the Trash ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Trash_SameFileSystem_GoesToTheHomeTrash_WithItsTrashInfo_NamesNeverClash()
    {
        var home = Path.Combine(_root, "home-trash");
        var trash = new FreedesktopTrash(home, _ => "/", () => new DateTime(2026, 11, 16, 21, 5, 9), "1000");
        var file = Path.Combine(_root, "shots", "My Game 1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Jpeg);
        var moved = trash.Trash(file, out var error);
        Assert.Null(error);
        Assert.Equal(Path.Combine(home, "files", "My Game 1.png"), moved);
        Assert.False(File.Exists(file));
        Assert.Equal(Jpeg, File.ReadAllBytes(moved!)); // moved, not deleted
        Assert.Equal($"[Trash Info]\nPath={FreedesktopTrash.EncodePath(file)}\nDeletionDate=2026-11-16T21:05:09\n",
            File.ReadAllText(Path.Combine(home, "info", "My Game 1.png.trashinfo")));
        Assert.Contains("My%20Game%201.png", FreedesktopTrash.EncodePath(file));

        File.WriteAllBytes(file, Jpeg);
        Assert.Equal(Path.Combine(home, "files", "My Game 1.2.png"), trash.Trash(file, out _));
        Assert.True(File.Exists(Path.Combine(home, "info", "My Game 1.2.png.trashinfo")));
    }

    [Fact]
    public void Trash_AnotherDrive_UsesItsOwnTrash_RelativePath_NeverCopiesAcross()
    {
        var drive = Path.Combine(_root, "drive");
        var file = Path.Combine(drive, "Games", "Shot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Jpeg);
        var home = Path.Combine(_root, "home-trash");
        var trash = new FreedesktopTrash(home, p => p.StartsWith(drive, StringComparison.Ordinal) ? drive : "/", () => new DateTime(2026, 1, 2, 3, 4, 5), "1000");
        var moved = trash.Trash(file, out _);
        Assert.Equal(Path.Combine(drive, ".Trash-1000", "files", "Shot.png"), moved);
        Assert.Contains("Path=Games/Shot.png\n", File.ReadAllText(Path.Combine(drive, ".Trash-1000", "info", "Shot.png.trashinfo")));
        Assert.False(Directory.Exists(Path.Combine(home, "files")) && Directory.EnumerateFiles(Path.Combine(home, "files")).Any());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(drive, ".Trash-1000")));

        // A shared .Trash with the sticky bit: the user's folder in it.
        var shared = Path.Combine(drive, ".Trash");
        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, (UnixFileMode)0b1_111_111_111);
        File.WriteAllBytes(file, Jpeg);
        Assert.Equal(Path.Combine(shared, "1000", "files", "Shot.png"), trash.Trash(file, out _));
    }

    [Fact]
    public void Trash_WhenNoTrashCanTakeIt_TheFileStays()
    {
        var drive = Path.Combine(_root, "ro-drive");
        var file = Path.Combine(drive, "Shot.png");
        Directory.CreateDirectory(drive);
        File.WriteAllBytes(file, Jpeg);
        // The drive's own trash name is taken by a file: no trash there, and nothing is copied home.
        File.WriteAllText(Path.Combine(drive, ".Trash-1000"), "");
        var trash = new FreedesktopTrash(Path.Combine(_root, "home-trash"), p => p.StartsWith(drive, StringComparison.Ordinal) ? drive : "/", null, "1000");
        Assert.Null(trash.Trash(file, out var error));
        Assert.StartsWith(FreedesktopTrash.NotTrashedPrefix, error);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void MountPoint_IsTheLongestMountContainingThePath()
    {
        string[] mountinfo =
        [
            "22 1 0:21 / / rw,relatime - btrfs /dev/nvme0n1p2 rw",
            "40 22 0:35 / /home rw - btrfs /dev/nvme0n1p2 rw",
            "41 22 8:1 / /mnt/vdo\\040storage rw - ext4 /dev/sda1 rw",
        ];
        Assert.Equal("/mnt/vdo storage", FreedesktopTrash.MountPointOf("/mnt/vdo storage/Games/x.png", mountinfo));
        Assert.Equal("/home", FreedesktopTrash.MountPointOf("/home/me/a.png", mountinfo));
        Assert.Equal("/", FreedesktopTrash.MountPointOf("/homework/a.png", mountinfo));
    }
}
