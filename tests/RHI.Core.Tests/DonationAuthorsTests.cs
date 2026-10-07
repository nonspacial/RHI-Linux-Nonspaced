using System.Net;
using System.Text;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class DonationAuthorsTests
{
    // Upstream 2.8.5 GameCardViewModel.UI.cs AuthorDonationUrls, sorted as the dialog shows them.
    private static readonly string[] UpstreamBuiltInNames =
        ["Bit Viper", "Forge", "Jon", "Lilium", "Musa", "Nukem", "Pumbo", "ShortFuse", "Voosh"];

    [Fact]
    public async Task MergesManifestOntoBuiltInAuthorsSortedWithRoles()
    {
        var list = await Fetch("""
        {
          "version": 3,
          "donationUrls": {
            "ShortFuse": "https://ko-fi.com/shortfuse",
            "bit viper": "https://ko-fi.com/bitviper",
            "pumbo": "https://ko-fi.com/realpumbo",
            "souperman9": "https://ko-fi.com/souperman9"
          },
          "authorRoles": {
            "shortfuse": "RenoDX, RenoDX mods",
            "Pumbo": "  Luma Framework, Luma mods  ",
            "Nobody": "Not a donation entry"
          }
        }
        """);

        Assert.Null(list.ManifestError);
        var authors = list.Authors;
        // Built-ins the manifest omits stay (upstream merges onto its list); built-in spelling wins on case-only clashes.
        Assert.Equal(["Bit Viper", "Forge", "Jon", "Lilium", "Musa", "Nukem", "Pumbo", "ShortFuse", "souperman9", "Voosh"],
            authors.Select(a => a.Name));
        var byName = authors.ToDictionary(a => a.Name);
        Assert.Equal("Luma Framework, Luma mods", byName["Pumbo"].Role);
        Assert.Equal("RenoDX, RenoDX mods", byName["ShortFuse"].Role);
        Assert.Equal("", byName["souperman9"].Role);
        Assert.Equal("", byName["Forge"].Role);
        // The manifest link replaces the built-in one.
        Assert.Equal("https://ko-fi.com/realpumbo", byName["Pumbo"].DonationUrl.AbsoluteUri);
    }

    [Fact]
    public async Task OfflineFirstRunWithNoCachedManifestShowsBuiltInAuthors()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "rhi-donate-" + Guid.NewGuid().ToString("N"));
        var handler = new SwappableHandler("") { Fail = true };
        using var http = new HttpClient(handler);
        var service = new UpstreamRhiManifestService(http,
            "https://example.test/live.json", "https://example.test/pinned.json", Path.Combine(cacheDir, "manifest.json"));

        var list = await service.FetchDonationAuthorsAsync();

        Assert.Equal(2, handler.Requests);                       // live and pinned both tried
        Assert.False(Directory.Exists(cacheDir));                // nothing cached, nothing written
        Assert.False(string.IsNullOrWhiteSpace(list.ManifestError));
        Assert.Equal(UpstreamBuiltInNames, list.Authors.Select(a => a.Name));
        Assert.All(list.Authors, a =>
        {
            Assert.Equal("", a.Role);
            Assert.Equal(Uri.UriSchemeHttps, a.DonationUrl.Scheme);
            Assert.Equal("ko-fi.com", a.DonationUrl.Host);
            Assert.Equal("Ko-fi", a.LinkLabel);
        });
        Assert.Equal(UpstreamBuiltInNames, UpstreamRhiManifestService.BuiltInDonationAuthors.Select(a => a.Name));
    }

    [Fact]
    public async Task OfflineWithLastKnownGoodManifestUsesIt()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "rhi-donate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var cache = Path.Combine(cacheDir, "manifest.json");
            File.WriteAllText(cache, """{ "version": 3, "donationUrls": { "spiwar": "https://ko-fi.com/spiwar" }, "authorRoles": { "spiwar": "RenoDX mods" } }""");
            using var http = new HttpClient(new SwappableHandler("") { Fail = true });
            var service = new UpstreamRhiManifestService(http, "https://example.test/live.json", null, cache);

            var list = await service.FetchDonationAuthorsAsync();

            Assert.Null(list.ManifestError);
            Assert.Equal("RenoDX mods", Assert.Single(list.Authors, a => a.Name == "spiwar").Role);
            Assert.Equal(UpstreamBuiltInNames.Length + 1, list.Authors.Count);
        }
        finally
        {
            Directory.Delete(cacheDir, true);
        }
    }

    [Fact]
    public async Task CancellationIsNotTurnedIntoTheBuiltInList()
    {
        using var http = new HttpClient(new SwappableHandler("") { Fail = true });
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchDonationAuthorsAsync(cts.Token));
    }

    [Fact]
    public async Task OffersOnlyAbsoluteHttpsLinks()
    {
        var list = await Fetch("""
        {
          "version": 3,
          "donationUrls": {
            "Secure": "https://ko-fi.com/secure",
            "Plain": "http://ko-fi.com/plain",
            "Script": "javascript:alert(1)",
            "File": "file:///etc/passwd",
            "Relative": "/ko-fi/relative",
            "Credentials": "https://user:pw@ko-fi.com/creds",
            "Empty": "  ",
            "Null": null,
            "  ": "https://ko-fi.com/blank-name"
          }
        }
        """);

        Assert.Equal(["Secure"], list.Authors.Select(a => a.Name).Except(UpstreamBuiltInNames));
    }

    [Fact]
    public void BuiltInAndMergedLinksGoThroughTheSameFilter()
    {
        var authors = UpstreamRhiManifestService.BuildDonationAuthors(
            [new("Plain", "http://ko-fi.com/plain"), new("Creds", "https://u:p@ko-fi.com/c"), new("Good", "https://ko-fi.com/good"),
             new("Pumbo", "https://ko-fi.com/pumbo")],
            // A manifest override with a bad link drops the author rather than reviving the built-in link.
            new Dictionary<string, string> { ["pumbo"] = "http://ko-fi.com/pumbo" },
            null);

        Assert.Equal(["Good"], authors.Select(a => a.Name));
    }

    [Fact]
    public void CaseOnlyDuplicatesKeepTheFirstSpellingAndTheLaterLink()
    {
        var authors = UpstreamRhiManifestService.BuildDonationAuthors(
            [new("Bit Viper", "https://ko-fi.com/old")],
            [new("bit viper", "https://ko-fi.com/newer"), new("BIT VIPER", "https://ko-fi.com/newest")],
            null);

        var only = Assert.Single(authors);
        Assert.Equal("Bit Viper", only.Name);
        Assert.Equal("https://ko-fi.com/newest", only.DonationUrl.AbsoluteUri);
    }

    [Fact]
    public async Task ManifestWithoutDonationUrlsStillListsBuiltInAuthors()
    {
        var list = await Fetch("""{ "version": 3, "authorRoles": { "Pumbo": "Luma" } }""");

        Assert.Null(list.ManifestError);
        Assert.Equal(UpstreamBuiltInNames, list.Authors.Select(a => a.Name));
        Assert.Equal("Luma", list.Authors.Single(a => a.Name == "Pumbo").Role);
    }

    [Fact]
    public async Task RefreshPicksUpManifestChangesWithoutRestart()
    {
        var handler = new SwappableHandler("""{ "version": 3, "donationUrls": { "A": "https://ko-fi.com/a" } }""");
        using var http = new HttpClient(handler);
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        Assert.Equal(["A"], (await service.FetchDonationAuthorsAsync()).Authors.Select(a => a.Name).Except(UpstreamBuiltInNames));

        handler.Json = """{ "version": 4, "donationUrls": { "A": "https://ko-fi.com/a", "B": "https://ko-fi.com/b" }, "authorRoles": { "B": "RenoDX mods" } }""";
        service.InvalidateProcessCache();
        var authors = (await service.FetchDonationAuthorsAsync()).Authors;

        Assert.Equal(["A", "B"], authors.Select(a => a.Name).Except(UpstreamBuiltInNames));
        Assert.Equal("RenoDX mods", authors.Single(a => a.Name == "B").Role);
    }

    [Theory]
    [InlineData("https://ko-fi.com/shortfuse", "Ko-fi")]
    [InlineData("https://www.ko-fi.com/shortfuse", "Ko-fi")]
    [InlineData("https://KO-FI.COM/shortfuse", "Ko-fi")]
    [InlineData("https://www.patreon.com/someone", "patreon.com")]
    [InlineData("https://github.com/sponsors/someone", "github.com")]
    [InlineData("https://notko-fi.com/x", "notko-fi.com")]
    public void LinkLabelNamesTheSiteActuallyLinked(string url, string expected)
        => Assert.Equal(expected, UpstreamRhiManifestService.DonationLinkLabel(new Uri(url)));

    [Theory]
    [InlineData("en-US", 0, "0 authors")]
    [InlineData("en-US", 1, "1 author")]
    [InlineData("en-US", 20, "20 authors")]
    [InlineData("pt-BR", 0, "0 autores")]
    [InlineData("pt-BR", 1, "1 autor")]
    [InlineData("pt-BR", 9, "9 autores")]
    public void AuthorCountIsSingularForOne(string language, int count, string expected)
        => Assert.Equal(expected, DonateText.AuthorCount(language, count));

    private static async Task<UpstreamDonationList> Fetch(string json)
    {
        using var http = new HttpClient(new SwappableHandler(json));
        return await new UpstreamRhiManifestService(http, "https://example.test/manifest.json").FetchDonationAuthorsAsync();
    }

    // Re-review nit 1: pin upstream 2.8.5 AuthorDonationUrls exactly (names and links), not just the names and host.
    [Fact]
    public void BuiltInAuthorsAreUpstreamsNineKoFiLinksExactly()
    {
        var expected = new Dictionary<string, string>
        {
            ["ShortFuse"] = "https://ko-fi.com/shortfuse",
            ["Jon"] = "https://ko-fi.com/kickfister",
            ["Forge"] = "https://ko-fi.com/forge87682",
            ["Voosh"] = "https://ko-fi.com/notvoosh",
            ["Musa"] = "https://ko-fi.com/musaqh",
            ["Pumbo"] = "https://ko-fi.com/pumbo",
            ["Nukem"] = "https://ko-fi.com/nukem9",
            ["Lilium"] = "https://ko-fi.com/endlesslyflowering",
            ["Bit Viper"] = "https://ko-fi.com/bitviper",
        };

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal),
            UpstreamRhiManifestService.BuiltInDonationUrls.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal),
            UpstreamRhiManifestService.BuiltInDonationAuthors
                .Select(a => new KeyValuePair<string, string>(a.Name, a.DonationUrl.AbsoluteUri))
                .OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    // Re-review nit 2: a write cancelled after the temp file exists leaves no manifest.json.tmp-* orphan.
    [Fact]
    public async Task CancelledCacheWriteLeavesNoTempFile()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "rhi-donate-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new SwappableHandler("{}"));
            var service = new UpstreamRhiManifestService(http,
                "https://example.test/live.json", "https://example.test/pinned.json", Path.Combine(cacheDir, "manifest.json"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.PersistLastKnownGoodAsync(Encoding.UTF8.GetBytes("{\"version\":3}"), cts.Token));

            Assert.True(Directory.Exists(cacheDir));
            Assert.Empty(Directory.EnumerateFileSystemEntries(cacheDir));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    private sealed class SwappableHandler(string json) : HttpMessageHandler
    {
        public string Json { get; set; } = json;
        public bool Fail { get; init; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Fail) throw new HttpRequestException("Name or service not known (offline)");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json")
            });
        }
    }
}
