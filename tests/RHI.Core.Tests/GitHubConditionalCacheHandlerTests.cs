using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #35 (DEP-16): GitHub API lookups are revalidated with ETags (upstream 2.8.5 GitHubETagCache): a repeated lookup sends
/// If-None-Match and a 304 hands back the cached answer; a rate-limited GitHub falls back to it. Since #244 an answer is
/// reused without asking for 60 s, so these lookups are a minute apart on the test clock.
/// </summary>
public sealed class GitHubConditionalCacheHandlerTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/praydog/REFramework-nightly/releases?per_page=1";
    private static readonly TimeSpan PastFreshness = TimeSpan.FromSeconds(61);
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task SecondLookup_IsConditional_And304ReturnsTheCachedBody()
    {
        var inner = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        var cache = new GitHubConditionalCacheHandler(inner, () => _now);
        using var http = new HttpClient(cache);

        var first = await http.GetStringAsync(ReleasesUrl);
        _now += PastFreshness;
        var second = await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(first, second);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Empty(inner.Requests[0].IfNoneMatch);
        Assert.Equal("\"etag-1\"", Assert.Single(inner.Requests[1].IfNoneMatch).Tag);
        Assert.Equal(1, inner.NotModifiedAnswers);
        Assert.Equal(1, cache.CacheHits);
    }

    [Fact]
    public async Task ChangedResource_ReplacesTheCachedBody()
    {
        var inner = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(new GitHubConditionalCacheHandler(inner, () => _now));

        await http.GetStringAsync(ReleasesUrl);
        inner.Publish("[{\"tag_name\":\"v2\"}]", "\"etag-2\"");
        _now += PastFreshness;
        var after = await http.GetStringAsync(ReleasesUrl);
        _now += PastFreshness;
        var again = await http.GetStringAsync(ReleasesUrl);

        Assert.Contains("v2", after, StringComparison.Ordinal);
        Assert.Contains("v2", again, StringComparison.Ordinal);
        Assert.Equal("\"etag-2\"", Assert.Single(inner.Requests[^1].IfNoneMatch).Tag);
    }

    [Fact]
    public async Task RateLimitedGitHub_ServesTheCachedAnswer_AndStopsAskingUntilReset()
    {
        var now = DateTimeOffset.UtcNow;
        var inner = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(new GitHubConditionalCacheHandler(inner, () => now));

        await http.GetStringAsync(ReleasesUrl);
        now += PastFreshness;
        inner.RateLimitUntil = now.AddMinutes(30);
        var limited = await http.GetStringAsync(ReleasesUrl);
        var requestsWhileLimited = inner.Requests.Count;
        now += PastFreshness;
        var cachedAgain = await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(2, requestsWhileLimited); // the refused request was sent; the next one was not
        Assert.Contains("v1", limited, StringComparison.Ordinal);
        Assert.Contains("v1", cachedAgain, StringComparison.Ordinal);
        Assert.Equal(requestsWhileLimited, inner.Requests.Count);
    }

    [Fact]
    public async Task PayloadHosts_AreNeverCached()
    {
        var inner = new GitHubFake("payload", "\"etag-1\"");
        using var http = new HttpClient(new GitHubConditionalCacheHandler(inner));

        await http.GetStringAsync("https://github.com/praydog/REFramework-nightly/releases/download/v1/RE2.zip");
        await http.GetStringAsync("https://github.com/praydog/REFramework-nightly/releases/download/v1/RE2.zip");

        Assert.All(inner.Requests, request => Assert.Empty(request.IfNoneMatch));
    }

    [Fact]
    public async Task ReleaseLookupThroughAService_CostsOneFullAnswerPerSession()
    {
        // The ticket's check: two consecutive update checks; the second one is answered by a 304.
        var inner = new GitHubFake(
            "{\"tag_name\":\"v1186\",\"assets\":[{\"name\":\"RE2.zip\",\"browser_download_url\":\"https://github.com/praydog/REFramework-nightly/releases/download/v1186/RE2.zip\"}]}",
            "\"etag-r\"");
        using var http = new HttpClient(new GitHubConditionalCacheHandler(inner, () => _now));
        var resolver = new GitHubReleaseAssetResolver(http);
        var query = new GitHubReleaseAssetQuery("https://api.github.com/repos/praydog/REFramework-nightly/releases/tags/v1186", ExactAssetName: "RE2.zip");

        var first = await resolver.ResolveAsync(query);
        _now += PastFreshness;
        var second = await resolver.ResolveAsync(query);

        Assert.Equal(first, second);
        Assert.Equal(1, inner.NotModifiedAnswers);
    }

    private sealed class GitHubFake(string body, string etag) : HttpMessageHandler
    {
        private string _body = body;
        private string _etag = etag;

        public List<HttpRequestHeaders> Requests { get; } = [];
        public int NotModifiedAnswers { get; private set; }
        public DateTimeOffset? RateLimitUntil { get; set; }

        public void Publish(string body, string etag) => (_body, _etag) = (body, etag);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage().Headers;
            foreach (var header in request.Headers) copy.TryAddWithoutValidation(header.Key, header.Value);
            Requests.Add(copy);
            if (RateLimitUntil is { } until)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
                limited.Headers.Add("X-RateLimit-Remaining", "0");
                limited.Headers.Add("X-RateLimit-Reset", until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(limited);
            }
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == _etag))
            {
                NotModifiedAnswers++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            response.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
