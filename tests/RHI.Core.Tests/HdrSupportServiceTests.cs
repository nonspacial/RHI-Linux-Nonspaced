using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// #251: the HDR support resolver. RHI's curated list wins, then PCGamingWiki's <c>hdr</c> field (wikitext through
/// <c>action=parse</c>; HTTP fakes only, never the live wiki), then a Microsoft Store record's PC HDR flag for a game the
/// app that installed it keeps one for. Offline, a rate limit or a time-out is Unknown (shown) and never cached; answers
/// are cached with the page name and fetch time and asked again only after an explicit full Refresh.
/// </summary>
public sealed class HdrSupportServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-support-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("true", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki)]
    [InlineData("True ", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki)]
    [InlineData("hackable", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWikiHackable)]
    [InlineData("true<ref>Patch 1.1</ref>", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki)]
    [InlineData("false{{note|x}}", HdrSupportAnswer.No, HdrSupportSource.PcGamingWiki)]
    [InlineData("limited", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki)]
    [InlineData("always on", HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki)]
    [InlineData("false", HdrSupportAnswer.No, HdrSupportSource.PcGamingWiki)]
    [InlineData("unknown", HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown)]
    [InlineData("n/a", HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown)]
    [InlineData("", HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown)]
    [InlineData(null, HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown)]
    public void PcgwsHdrField_MapsToAnAnswer(string? value, HdrSupportAnswer answer, HdrSupportSource source)
    {
        var evidence = HdrSupportService.FromWikitext(Wikitext(value), "Some Game");

        Assert.Equal((answer, source, "Some Game"), (evidence.Answer, evidence.Source, evidence.Page));
    }

    [Fact]
    public void TheHdrNotesRow_IsNotTheHdrField()
    {
        var text = "{{Video\n|hdr notes                  = Needs a mod.\n|ultrawidescreen = true\n}}";
        Assert.Equal(HdrSupportAnswer.Unknown, HdrSupportService.FromWikitext(text, null).Answer);
        Assert.Equal(HdrSupportAnswer.No, HdrSupportService.FromWikitext(text + "\n|hdr = false\n", null).Answer);
    }

    [Fact]
    public async Task ASteamGame_IsFoundThroughItsAppId_AndItsAnswerIsCachedWithThePageAndTime()
    {
        var wiki = new FakeWiki { AppIdPages = { [397540] = "Borderlands_3" }, Pages = { ["Borderlands 3"] = Wikitext("true") } };
        var game = Steam("Borderlands 3", 397540);

        var evidence = await Service(wiki).ResolveAsync(game);

        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki, "Borderlands 3"), evidence);
        Assert.Equal(2, wiki.Requests.Count); // appid.php, then the page's wikitext
        Assert.Contains(wiki.Requests, r => r.Contains("action=parse&page=Borderlands%203&prop=wikitext&format=json&formatversion=2&redirects=1", StringComparison.Ordinal));
        using var cache = JsonDocument.Parse(File.ReadAllText(CachePath));
        var entry = cache.RootElement.GetProperty("Games").GetProperty("steam:397540");
        Assert.Equal("Borderlands 3", entry.GetProperty("Page").GetString());
        Assert.Equal("Yes", entry.GetProperty("Answer").GetString());
        Assert.True(entry.TryGetProperty("FetchedAt", out _));

        // Looked up once per game: a new start reads the cache, no request.
        var again = Service(wiki);
        Assert.Equal(evidence, await again.ResolveAsync(game));
        Assert.Equal(evidence, again.Peek(game));
        Assert.Equal(2, wiki.Requests.Count);
    }

    [Fact]
    public async Task AGameWithoutAppId_IsFoundByPcgwsSearch()
    {
        var wiki = new FakeWiki { Search = { ["A Game About Digging A Hole"] = "A_Game_About_Digging_A_Hole" }, Pages = { ["A Game About Digging A Hole"] = Wikitext("false") } };

        var evidence = await Service(wiki).ResolveAsync(Named("A Game About Digging A Hole"));

        Assert.Equal((HdrSupportAnswer.No, "A Game About Digging A Hole"), (evidence.Answer, evidence.Page));
    }

    [Fact]
    public async Task TheManifestsPageUrl_IsUsedWithoutASearch()
    {
        var wiki = new FakeWiki { Pages = { ["Metro Exodus"] = Wikitext("true") } };
        var game = Named("Metro Exodus Enhanced Edition");
        game.PcgwUrlOverride = "https://www.pcgamingwiki.com/wiki/Metro_Exodus";

        Assert.Equal(HdrSupportAnswer.Yes, (await Service(wiki).ResolveAsync(game)).Answer);
        Assert.Single(wiki.Requests);
    }

    [Fact]
    public async Task TheCuratedList_Wins_WithoutAnyRequest()
    {
        var wiki = new FakeWiki { AppIdPages = { [1449560] = "Metro_Exodus" }, Pages = { ["Metro Exodus"] = Wikitext("true") } };
        var service = Service(wiki, """{"games":{"steam:1449560":{"hdr":false,"note":"test"}, "name:control":{"hdr":true}}}""");

        var metro = await service.ResolveAsync(Steam("Metro Exodus Enhanced Edition", 1449560));
        var control = service.Peek(Named("Control"));

        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.No, HdrSupportSource.CuratedList), metro);
        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.Yes, HdrSupportSource.CuratedList), control);
        Assert.Empty(wiki.Requests);
    }

    [Fact]
    public void TheShippedCuratedList_Reads()
    {
        // The embedded resource parses (empty at first): no warning, nothing decided by it.
        var log = Path.Combine(_root, "rhi.log");
        using (RHI.Core.Services.AppLog.UseInThisFlow(new RHI.Core.Services.AppLog(log)))
            Assert.Null(new HdrSupportService(null, cachePath: CachePath).Peek(Named("Control")));
        Assert.False(File.Exists(log) && File.ReadAllText(log).Contains("curated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("rate-limit")]
    [InlineData("server-error")]
    public async Task AFailedLookup_IsUnknown_AndNotCached(string how)
    {
        var wiki = new FakeWiki { Fail = how, AppIdPages = { [397540] = "Borderlands_3" }, Pages = { ["Borderlands 3"] = Wikitext("false") } };
        var game = Steam("Borderlands 3", 397540);

        var evidence = await Service(wiki).ResolveAsync(game);

        Assert.Equal(HdrSupportEvidence.Unchecked, evidence);
        Assert.True(HdrSupportRule.ShowsHdrSection(evidence, hdrModInstalled: false));
        Assert.False(File.Exists(CachePath));
        // Back online, a later start asks again and gets the answer.
        wiki.Fail = null;
        Assert.Equal(HdrSupportAnswer.No, (await Service(wiki).ResolveAsync(game)).Answer);
    }

    [Fact]
    public async Task NoPage_IsADefiniteUnknown_AndIsCached()
    {
        var wiki = new FakeWiki();
        var game = Named("Obscure Game Nobody Wrote Up");

        var evidence = await Service(wiki).ResolveAsync(game);

        Assert.Equal((HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown), (evidence.Answer, evidence.Source));
        var requests = wiki.Requests.Count;
        Assert.Equal(evidence, await Service(wiki).ResolveAsync(game));
        Assert.Equal(requests, wiki.Requests.Count);
    }

    [Fact]
    public async Task AFullRefresh_AsksAgain_AndKeepsTheEarlierAnswerWhenThatFails()
    {
        var wiki = new FakeWiki { AppIdPages = { [377160] = "Fallout_4" }, Pages = { ["Fallout 4"] = Wikitext("false") } };
        var game = Steam("Fallout 4", 377160);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero));
        var service = Service(wiki, time: clock);
        Assert.Equal(HdrSupportAnswer.No, (await service.ResolveAsync(game)).Answer);
        var requests = wiki.Requests.Count;

        clock.Now += TimeSpan.FromMinutes(1);
        service.MarkStale();
        Assert.Null(service.Peek(game));
        wiki.Fail = "offline";
        Assert.Equal(HdrSupportAnswer.No, (await service.ResolveAsync(game)).Answer); // the earlier answer stands
        Assert.True(wiki.Requests.Count > requests);

        wiki.Fail = null;
        clock.Now += TimeSpan.FromMinutes(20); // past PCGW's pause after the failure
        wiki.Pages["Fallout 4"] = Wikitext("true");
        Assert.Equal(HdrSupportAnswer.Yes, (await service.ResolveAsync(game)).Answer);
        Assert.Equal(HdrSupportAnswer.Yes, service.Peek(game)?.Answer);
    }

    [Theory]
    [InlineData("""["Desktop","Xbox"]""", null, HdrSupportAnswer.Yes)]
    [InlineData("null", null, HdrSupportAnswer.Yes)]
    [InlineData("""["Xbox"]""", null, HdrSupportAnswer.Unknown)]
    [InlineData("""["Desktop"]""", "false", HdrSupportAnswer.Yes)] // correction F3: the Store's PC yes beats PCGW false
    [InlineData("""["Xbox"]""", "false", HdrSupportAnswer.No)] // a console-only flag does not
    [InlineData("""["Desktop"]""", "unknown", HdrSupportAnswer.Yes)]
    [InlineData("""["Desktop"]""", "true", HdrSupportAnswer.Yes)]
    public async Task AnXboxGame_UsesItsStoreRecordsPcHdrFlag_UnlessPcgwSaysYes(string platforms, string? pcgw, HdrSupportAnswer expected)
    {
        var state = Directory.CreateDirectory(Path.Combine(_root, "installer-state")).FullName;
        var products = Directory.CreateDirectory(Path.Combine(state, "catalogue", "products")).FullName;
        File.WriteAllText(Path.Combine(products, "9NBLGGH4R315.json"),
            """{"ProductId":"9NBLGGH4R315","Properties":{"Attributes":[{"Name":"XblLocalCoop","ApplicablePlatforms":null},{"Name":"CapabilityHDR","ApplicablePlatforms":"""
            + platforms + "}]}}");
        var wiki = new FakeWiki();
        if (pcgw is not null)
        {
            wiki.Search["Forza Quest"] = "Forza_Quest";
            wiki.Pages["Forza Quest"] = Wikitext(pcgw);
        }
        var game = Named("Forza Quest");
        game.Source = "Xbox";
        game.XboxPackage = new XboxPackageInfo("9NBLGGH4R315", "Installer", null, Path.Combine(state, "jobs.json"));

        var evidence = await Service(wiki).ResolveAsync(game);

        Assert.Equal(expected, evidence.Answer);
        if (pcgw != "true" && expected == HdrSupportAnswer.Yes) Assert.Equal(HdrSupportSource.MicrosoftStore, evidence.Source); // its tooltip
        if (pcgw == "true") Assert.Equal(HdrSupportSource.PcGamingWiki, evidence.Source);
    }

    [Fact]
    public async Task TheCuratedList_StillBeatsTheStoreFlag()
    {
        var state = Directory.CreateDirectory(Path.Combine(_root, "installer-state")).FullName;
        var products = Directory.CreateDirectory(Path.Combine(state, "catalogue", "products")).FullName;
        File.WriteAllText(Path.Combine(products, "9NBLGGH4R315.json"),
            """{"ProductId":"9NBLGGH4R315","Properties":{"Attributes":[{"Name":"CapabilityHDR","ApplicablePlatforms":["Desktop"]}]}}""");
        var game = Named("Forza Quest");
        game.Source = "Xbox";
        game.XboxPackage = new XboxPackageInfo("9NBLGGH4R315", "Installer", null, Path.Combine(state, "jobs.json"));

        var evidence = await Service(new FakeWiki(), """{"games":{"xbox:9NBLGGH4R315":{"hdr":false}}}""").ResolveAsync(game);

        Assert.Equal(new HdrSupportEvidence(HdrSupportAnswer.No, HdrSupportSource.CuratedList), evidence);
    }

    [Theory]
    [InlineData(HdrSupportAnswer.Yes, false, true)]
    [InlineData(HdrSupportAnswer.Unknown, false, true)]
    [InlineData(HdrSupportAnswer.No, false, false)]
    [InlineData(HdrSupportAnswer.No, true, true)]
    [InlineData(HdrSupportAnswer.No, null, true)]
    public void TheRule_HidesOnlyANoWithoutAnHdrMod(HdrSupportAnswer answer, bool? hdrMod, bool shown)
    {
        Assert.Equal(shown, HdrSupportRule.ShowsHdrSection(new HdrSupportEvidence(answer, HdrSupportSource.PcGamingWiki), hdrMod));
        Assert.True(HdrSupportRule.ShowsHdrSection(null, false)); // not known yet
    }

    [Fact]
    public void TheHdrMods_AreRenoDxLumaAndDx9Hdr_InstalledByRhi()
    {
        ManagedComponent Row(ManagedComponentKind kind, bool rhi, ComponentAvailability availability)
            => new() { Kind = kind, DisplayName = kind.ToString(), ManagedByRhi = rhi, Availability = availability };

        foreach (var kind in new[] { ManagedComponentKind.RenoDX, ManagedComponentKind.LumaFramework, ManagedComponentKind.DxvkHdr })
        {
            Assert.NotNull(HdrSupportRule.InstalledHdrMod([Row(kind, true, ComponentAvailability.Installed)]));
            Assert.NotNull(HdrSupportRule.InstalledHdrMod([Row(kind, true, ComponentAvailability.UpdateAvailable)]));
            Assert.Null(HdrSupportRule.InstalledHdrMod([Row(kind, false, ComponentAvailability.Installed)]));
            Assert.Null(HdrSupportRule.InstalledHdrMod([Row(kind, true, ComponentAvailability.Available)]));
        }
        Assert.Null(HdrSupportRule.InstalledHdrMod([Row(ManagedComponentKind.ReShade, true, ComponentAvailability.Installed)]));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private string CachePath => Path.Combine(_root, "cache", "pcgw_hdr_cache.json");

    private HdrSupportService Service(FakeWiki wiki, string? overrides = """{"games":{}}""", TimeProvider? time = null)
        => new(new PcGamingWikiGraphicsApiService(new HttpClient(wiki), Path.Combine(_root, "cache", "pcgw-api.json"), time, minRequestGap: TimeSpan.Zero),
            cachePath: CachePath, overridesJson: overrides, timeProvider: time, lookupTimeout: TimeSpan.FromSeconds(20));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static string Wikitext(string? hdr)
        => "{{Video\n|4k ultra hd               = true\n" + (hdr is null ? "" : $"|hdr                        = {hdr}\n") + "|hdr notes                  = \n}}";

    private static DetectedGame Steam(string name, int appId) => new() { Name = name, Source = "Steam", SteamAppId = appId, InstallPath = "/games/" + name };

    private static DetectedGame Named(string name) => new() { Name = name, Source = "Manual", InstallPath = "/games/" + name };

    /// <summary>PCGamingWiki as the service reads it: appid.php pages, opensearch, action=parse; counts every request.</summary>
    internal sealed class FakeWiki : HttpMessageHandler
    {
        public Dictionary<int, string> AppIdPages { get; } = new();
        public Dictionary<string, string> Search { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> Requests { get; } = new();
        public volatile string? Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri.AbsoluteUri);
            switch (Fail)
            {
                case "offline": throw new HttpRequestException("offline test");
                case "rate-limit": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                case "server-error": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (uri.AbsolutePath == "/api/appid.php")
                return Task.FromResult(AppIdPages.TryGetValue(int.Parse(query["appid"]!), out var page)
                    ? Text($"<html><script>RLCONF={{\"wgPageName\":\"{page}\",\"wgTitle\":\"x\"}};</script></html>")
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            if (query["action"] == "opensearch")
            {
                var name = query["search"]!;
                var urls = Search.TryGetValue(name, out var found) ? $"[\"https://www.pcgamingwiki.com/wiki/{found}\"]" : "[]";
                return Task.FromResult(Text($"[\"{name}\",[],[],{urls}]"));
            }
            if (query["action"] == "parse")
            {
                var title = query["page"]!;
                return Task.FromResult(Pages.TryGetValue(title, out var text)
                    ? Text(JsonSerializer.Serialize(new { parse = new { title, pageid = 1, wikitext = text } }))
                    : Text("""{"error":{"code":"missingtitle","info":"The page you specified doesn't exist."}}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }
}
