using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class DonationAuthorsTests
{
    [Fact]
    public async Task ListsAuthorsAlphabeticallyWithRolesAndKoFiLinks()
    {
        var authors = await Fetch("""
        {
          "version": 3,
          "donationUrls": {
            "ShortFuse": "https://ko-fi.com/shortfuse",
            "bit viper": "https://ko-fi.com/bitviper",
            "Pumbo": "https://ko-fi.com/realpumbo",
            "souperman9": "https://ko-fi.com/souperman9"
          },
          "authorRoles": {
            "shortfuse": "RenoDX, RenoDX mods",
            "Pumbo": "  Luma Framework, Luma mods  ",
            "Nobody": "Not a donation entry"
          }
        }
        """);

        Assert.Equal(["bit viper", "Pumbo", "ShortFuse", "souperman9"], authors.Select(a => a.Name));
        Assert.Equal(["", "Luma Framework, Luma mods", "RenoDX, RenoDX mods", ""], authors.Select(a => a.Role));
        Assert.Equal("https://ko-fi.com/realpumbo", authors[1].DonationUrl.AbsoluteUri);
    }

    [Fact]
    public async Task OffersOnlyAbsoluteHttpsLinks()
    {
        var authors = await Fetch("""
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

        var only = Assert.Single(authors);
        Assert.Equal("Secure", only.Name);
    }

    [Fact]
    public async Task ManifestWithoutDonationUrlsGivesEmptyList()
    {
        Assert.Empty(await Fetch("""{ "version": 3, "authorRoles": { "Pumbo": "Luma" } }"""));
    }

    [Fact]
    public async Task RefreshPicksUpManifestChangesWithoutRestart()
    {
        var handler = new SwappableHandler("""{ "version": 3, "donationUrls": { "A": "https://ko-fi.com/a" } }""");
        using var http = new HttpClient(handler);
        var service = new UpstreamRhiManifestService(http, "https://example.test/manifest.json");
        Assert.Single(await service.FetchDonationAuthorsAsync());

        handler.Json = """{ "version": 4, "donationUrls": { "A": "https://ko-fi.com/a", "B": "https://ko-fi.com/b" }, "authorRoles": { "B": "RenoDX mods" } }""";
        service.InvalidateProcessCache();
        var authors = await service.FetchDonationAuthorsAsync();

        Assert.Equal(["A", "B"], authors.Select(a => a.Name));
        Assert.Equal("RenoDX mods", authors[1].Role);
    }

    private static async Task<IReadOnlyList<UpstreamDonationAuthor>> Fetch(string json)
    {
        using var http = new HttpClient(new SwappableHandler(json));
        return await new UpstreamRhiManifestService(http, "https://example.test/manifest.json").FetchDonationAuthorsAsync();
    }

    private sealed class SwappableHandler(string json) : HttpMessageHandler
    {
        public string Json { get; set; } = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json")
            });
    }
}
