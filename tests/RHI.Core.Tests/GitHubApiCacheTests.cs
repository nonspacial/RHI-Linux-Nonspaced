using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Desktop;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #244 (upstream 2.8.8 <c>feb126c</c>, <c>1b51cb2</c>): GitHub API answers are kept between launches (revalidated before
/// use, 30-day expiry, existing caps, never a token on disk; an unreadable file is set aside and rebuilt), overlapping
/// identical GETs share one request, an answer is reused for 60 s, and an API request gives up after 30 s as a network
/// failure (could not check), never as a Cancel. Payload downloads are neither cached nor timed.
/// </summary>
public sealed class GitHubApiCacheTests : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/rhi-tests/opti/releases?per_page=20";
    private const string PayloadUrl = "https://github.com/rhi-tests/opti/releases/download/1.0/OptiScaler.7z";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-ghc-{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public GitHubApiCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CachePath => Path.Combine(_root, "cache", GitHubApiCacheStore.FileName);

    private GitHubConditionalCacheHandler Handler(HttpMessageHandler inner, bool persisted = true, TimeSpan? apiTimeout = null)
        => new(inner, () => _now, persisted ? new GitHubApiCacheStore(CachePath) : null, apiTimeout, persistDelay: TimeSpan.FromHours(1));

    // ── Kept between launches ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NextStart_AsksWithTheSavedETag_AndServesThe304Body()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using (var first = new HttpClient(Handler(github)))
            Assert.Contains("v1", await first.GetStringAsync(ReleasesUrl), StringComparison.Ordinal);
        // A new process: a new handler over the same file, at the same instant (inside FreshFor of the save).
        var restarted = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        var cache = Handler(restarted);
        using var http = new HttpClient(cache);

        var body = await http.GetStringAsync(ReleasesUrl);

        Assert.Contains("v1", body, StringComparison.Ordinal);
        var asked = Assert.Single(restarted.Requests); // a saved answer is never used without asking
        Assert.Equal("\"etag-1\"", Assert.Single(asked.IfNoneMatch).Tag);
        Assert.Equal(1, restarted.NotModifiedAnswers);
        Assert.Equal(1, cache.CacheHits);
    }

    [Fact]
    public async Task NextStart_WithAChangedRelease_GetsTheNewBody()
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        using var http = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v2\"}]", "\"etag-2\"")));

        Assert.Contains("v2", await http.GetStringAsync(ReleasesUrl), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableFile_IsSetAside_AndRebuilt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var garbage = Encoding.UTF8.GetBytes("{\"version\":1,\"entries\":[{\"url\":");
        await File.WriteAllBytesAsync(CachePath, garbage);
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        var cache = Handler(github);
        using var http = new HttpClient(cache);

        await http.GetStringAsync(ReleasesUrl);
        cache.Flush();

        Assert.Empty(Assert.Single(github.Requests).IfNoneMatch);
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CachePath)!, GitHubApiCacheStore.FileName + ".corrupt-*"));
        Assert.Equal(garbage, await File.ReadAllBytesAsync(aside));
        Assert.Single(new GitHubApiCacheStore(CachePath).Load(_now));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(31, false)]
    [InlineData(-2, false)] // saved "in the future": the clock was set back since
    public async Task SavedAnswer_IsUsedFor30Days(int daysLater, bool conditional)
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromDays(daysLater);
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github));

        await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(conditional, Assert.Single(github.Requests).IfNoneMatch.Count == 1);
    }

    [Fact]
    public async Task A304_CountsAsUse_SoTheThirtyDaysStartAgain()
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromDays(20);
        using (var second = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await second.GetStringAsync(ReleasesUrl); // answered 304
        _now += TimeSpan.FromDays(20); // 40 days after the body came, 20 after GitHub last confirmed it
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github));

        await http.GetStringAsync(ReleasesUrl);

        Assert.Single(Assert.Single(github.Requests).IfNoneMatch);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(403)] // no rate limit (no "X-RateLimit-Remaining: 0"): GitHub's secondary limit in a start-up burst looks like this
    public async Task SavedAnswer_AndAGitHubErrorAtTheNextStart_ReadsAsTheError_NeverAsTheSavedAnswer(int status)
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromDays(20); // v2 may be out by now
        var github = new GitHubFake("[{\"tag_name\":\"v2\"}]", "\"etag-2\"") { Refuse = (HttpStatusCode)status };
        var cache = Handler(github);
        using var http = new HttpClient(cache);

        using var response = await http.GetAsync(ReleasesUrl);

        Assert.Single(Assert.Single(github.Requests).IfNoneMatch); // asked about the saved answer
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.DoesNotContain("v1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, cache.CacheHits);
    }

    [Fact]
    public async Task SavedAnswer_AndAGitHubErrorAtTheNextStart_TheAppUpdateCheckReadsCouldNotCheck()
    {
        const string repository = "rhi-tests/rhi";
        const string releases = "[{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/rhi-tests/rhi/releases/tag/v1.0.0\"}]";
        using (var first = new HttpClient(Handler(new GitHubFake(releases, "\"etag-1\""))))
            Assert.Equal(AppUpdateCheckResult.UpToDate, (await new AppUpdateCheckService(first, "1.0.0", repository).CheckAsync()).Result);
        _now += TimeSpan.FromDays(20);
        using var http = new HttpClient(Handler(new GitHubFake(releases, "\"etag-1\"") { Refuse = HttpStatusCode.ServiceUnavailable }));

        var check = await new AppUpdateCheckService(http, "1.0.0", repository).CheckAsync().WaitAsync(HangGuard);

        Assert.Equal(AppUpdateCheckResult.CouldNotCheck, check.Result); // never "up to date" from a 20-day-old answer
    }

    [Fact]
    public async Task SavedAnswer_InsideGitHubsAdvertisedRateLimitWindow_IsServed_AndNotAskedAgainUntilTheReset()
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromDays(20);
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"") { Refuse = HttpStatusCode.Forbidden, QuotaResetAt = _now.AddMinutes(30) };
        using var http = new HttpClient(Handler(github));

        var limited = await http.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromMinutes(1);
        var stillLimited = await http.GetStringAsync(ReleasesUrl);
        var askedWhileLimited = github.Requests.Count;
        _now += TimeSpan.FromMinutes(30);
        github.Refuse = null;
        await http.GetStringAsync(ReleasesUrl);

        Assert.Contains("v1", limited, StringComparison.Ordinal);
        Assert.Contains("v1", stillLimited, StringComparison.Ordinal);
        Assert.Equal(1, askedWhileLimited);
        Assert.Equal(2, github.Requests.Count); // asked again after the reset
    }

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public async Task AnswerConfirmedThisSession_StillStandsInForAGitHubError(int status)
    {
        using (var first = new HttpClient(Handler(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""))))
            await first.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromDays(20);
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github));
        await http.GetStringAsync(ReleasesUrl); // 304: GitHub confirmed the saved answer this session
        _now += TimeSpan.FromSeconds(61);
        github.Refuse = (HttpStatusCode)status;

        using var response = await http.GetAsync(ReleasesUrl);

        Assert.Equal(2, github.Requests.Count);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // #35: unchanged rule for answers confirmed this session
        Assert.Contains("v1", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedFile_KeepsAtMost512Answers_TheNewest()
    {
        var github = new GitHubFake("[]", "\"etag-1\"");
        var cache = Handler(github);
        using var http = new HttpClient(cache);
        for (var i = 0; i < 513; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            await http.GetStringAsync($"https://api.github.com/repos/rhi-tests/r{i}/releases");
        }
        cache.Flush();

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(CachePath)); // the file itself, not a capped load
        var urls = file.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("url").GetString()).ToList();
        Assert.Equal(512, urls.Count);
        Assert.DoesNotContain("https://api.github.com/repos/rhi-tests/r0/releases", urls);
        Assert.Contains("https://api.github.com/repos/rhi-tests/r512/releases", urls);
    }

    [Fact]
    public void Load_TakesAtMost512Answers_TheNewest_AndDropsBodiesOver8MiB()
    {
        var store = new GitHubApiCacheStore(CachePath);
        var entries = Enumerable.Range(0, 513).Select(i => new GitHubApiCacheEntry
        {
            Url = $"https://api.github.com/repos/rhi-tests/r{i}/releases", Accept = "", ETag = "\"e\"", SavedUtc = _now.AddSeconds(i), Body = "[]"
        }).ToList();
        entries.Add(new GitHubApiCacheEntry
        {
            Url = "https://api.github.com/repos/rhi-tests/big/releases", Accept = "", ETag = "\"e\"", SavedUtc = _now.AddSeconds(600),
            Body = new string('x', 8 * 1024 * 1024 + 1)
        });
        store.Save(entries);

        var loaded = store.Load(_now.AddSeconds(600));

        Assert.Equal(512, loaded.Count);
        Assert.DoesNotContain(loaded, entry => entry.Url.Contains("/big/", StringComparison.Ordinal));
        Assert.DoesNotContain(loaded, entry => entry.Url == "https://api.github.com/repos/rhi-tests/r0/releases");
    }

    [Fact]
    public void Load_DropsAnswersForAnyPlaceButHttpsApiGitHubCom()
    {
        var store = new GitHubApiCacheStore(CachePath);
        string[] urls =
        [
            "https://api.github.com/repos/rhi-tests/opti/releases",
            "https://github.com/rhi-tests/opti/releases",
            "http://api.github.com/repos/rhi-tests/opti/releases",
            "https://api.github.com:8443/repos/rhi-tests/opti/releases",
            "https://api.github.com.example/repos/rhi-tests/opti/releases",
        ];
        store.Save(urls.Select(url => new GitHubApiCacheEntry { Url = url, Accept = "", ETag = "\"e\"", SavedUtc = _now, Body = "[]" }).ToList());

        var loaded = store.Load(_now);

        Assert.Equal(urls[0], Assert.Single(loaded).Url);
    }

    [Fact]
    public async Task Saves_WaitForTheDelay_OrAFlush()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        var held = Handler(github); // an hour's delay
        using (var http = new HttpClient(held, disposeHandler: false))
            await http.GetStringAsync(ReleasesUrl);
        Assert.False(File.Exists(CachePath)); // one write per burst, not one per answer
        held.Flush();
        Assert.True(File.Exists(CachePath));
        held.Dispose();

        File.Delete(CachePath);
        var timed = new GitHubConditionalCacheHandler(new GitHubFake("[]", "\"etag-2\""), () => _now, new GitHubApiCacheStore(CachePath),
            persistDelay: TimeSpan.FromMilliseconds(20));
        using var timedHttp = new HttpClient(timed, disposeHandler: false);
        await timedHttp.GetStringAsync(ReleasesUrl);
        await WaitForFileAsync(CachePath).WaitAsync(HangGuard); // written by the timer, no Flush
        timed.Dispose();
    }

    private static async Task WaitForFileAsync(string path)
    {
        while (!File.Exists(path)) await Task.Delay(10);
    }

    [Fact]
    public async Task AnswersTooBigOrNotText_AreNotSaved()
    {
        var big = new string('x', 8 * 1024 * 1024 + 1);
        var github = new GitHubFake(big, "\"etag-big\"");
        var cache = Handler(github);
        using var http = new HttpClient(cache);

        Assert.Equal(big.Length, (await http.GetStringAsync(ReleasesUrl)).Length); // reaches the caller whole
        github.PublishBytes([0xFF, 0xFE, 0x00, 0x81], "\"etag-bin\"");
        await http.GetByteArrayAsync("https://api.github.com/repos/rhi-tests/opti/zipball/1.0");
        github.Publish("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        await http.GetStringAsync("https://api.github.com/repos/rhi-tests/opti/releases/latest");
        cache.Flush();

        var saved = Assert.Single(new GitHubApiCacheStore(CachePath).Load(_now));
        Assert.Equal("https://api.github.com/repos/rhi-tests/opti/releases/latest", saved.Url);
    }

    [Fact]
    public async Task SavedFile_NeverHoldsTheToken()
    {
        const string token = "ghp_rhiTestToken0123456789abcdefABCDEF0123";
        var tokenPath = Path.Combine(_root, "github_api.txt");
        await File.WriteAllTextAsync(tokenPath, token + "\n");
        File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        // The app's chain: cache → token handler → network.
        var cache = Handler(new GitHubApiTokenHandler(tokenPath, github));
        using var http = new HttpClient(cache);

        await http.GetStringAsync(ReleasesUrl);
        cache.Flush();

        Assert.Equal(token, Assert.Single(github.Requests).Authorization?.Parameter); // the token was in play
        var text = await File.ReadAllTextAsync(CachePath);
        Assert.Contains("etag-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestsWithTheirOwnAuthorization_AreNeitherCachedNorSaved()
    {
        var github = new GitHubFake("{\"rate\":{}}", "\"etag-r\"");
        var cache = Handler(github);
        using var http = new HttpClient(cache);

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "candidate-token");
            using var response = await http.SendAsync(request);
            _now += TimeSpan.FromSeconds(1);
        }
        cache.Flush();

        Assert.Equal(2, github.Requests.Count);
        Assert.All(github.Requests, asked => Assert.Empty(asked.IfNoneMatch));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task AnswerRedirectedOffTheApiHost_IsNotKept()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"") { FinalUrl = "https://objects.githubusercontent.com/rhi-tests/releases.json" };
        using var http = new HttpClient(Handler(github, persisted: false));

        await http.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromSeconds(61);
        await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(2, github.Requests.Count);
        Assert.Empty(github.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task PayloadDownloads_AreNotCachedOrSaved()
    {
        var github = new GitHubFake("payload", "\"etag-p\"");
        var cache = Handler(github);
        using var http = new HttpClient(cache);

        await http.GetStringAsync(PayloadUrl);
        await http.GetStringAsync(PayloadUrl);
        cache.Flush();

        Assert.Equal(2, github.Requests.Count);
        Assert.All(github.Requests, asked => Assert.Empty(asked.IfNoneMatch));
        Assert.False(File.Exists(CachePath));
    }

    // ── Shared requests and 60 s freshness ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoOverlappingLookups_ShareOneRequest_EachGetsItsOwnResponse()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"") { Gate = new() };
        var cache = Handler(github, persisted: false);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.JoinedForTesting = _ => joined.TrySetResult();
        using var http = new HttpClient(cache);

        var first = http.GetAsync(ReleasesUrl);
        await github.Entered.Task.WaitAsync(HangGuard);
        var second = http.GetAsync(ReleasesUrl);
        await joined.Task.WaitAsync(HangGuard);
        github.Gate.SetResult();
        using var a = await first.WaitAsync(HangGuard);
        using var b = await second.WaitAsync(HangGuard);

        Assert.Single(github.Requests);
        Assert.NotSame(a, b);
        Assert.NotSame(a.Content, b.Content);
        a.Dispose(); // one caller's dispose leaves the other's answer readable
        Assert.Contains("v1", await b.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("\"etag-1\"", b.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task LookupsWithADifferentAccept_AreNeverShared_NorReusedForEachOther()
    {
        var github = new GitHubFake("", "\"etag-1\"") { Gate = new(), BodyFor = asked => "accept=" + string.Join(",", asked.Headers.Accept) };
        var cache = Handler(github, persisted: false);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.JoinedForTesting = _ => joined.TrySetResult();
        using var http = new HttpClient(cache);
        async Task<string> Lookup(string accept)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.Accept.ParseAdd(accept);
            using var response = await http.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        var json = Lookup("application/vnd.github+json");
        await github.Entered.Task.WaitAsync(HangGuard);
        var raw = Lookup("application/vnd.github.raw");
        var next = await Task.WhenAny(github.SecondEntered.Task, joined.Task).WaitAsync(HangGuard);
        Assert.Same(github.SecondEntered.Task, next); // a request of its own, never a join
        github.Gate.SetResult();

        Assert.Equal("accept=application/vnd.github+json", await json.WaitAsync(HangGuard));
        Assert.Equal("accept=application/vnd.github.raw", await raw.WaitAsync(HangGuard));
        // Inside the 60 s each one is reused from its own answer.
        Assert.Equal("accept=application/vnd.github.raw", await Lookup("application/vnd.github.raw").WaitAsync(HangGuard));
        Assert.Equal("accept=application/vnd.github+json", await Lookup("application/vnd.github+json").WaitAsync(HangGuard));
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task OneCallersCancel_EndsOnlyItsOwnWait()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"") { Gate = new() };
        var cache = Handler(github, persisted: false);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.JoinedForTesting = _ => joined.TrySetResult();
        using var http = new HttpClient(cache);
        using var cancel = new CancellationTokenSource();

        var kept = http.GetStringAsync(ReleasesUrl);
        await github.Entered.Task.WaitAsync(HangGuard);
        var cancelled = http.GetStringAsync(ReleasesUrl, cancel.Token);
        await joined.Task.WaitAsync(HangGuard);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(HangGuard));
        github.Gate.SetResult();

        Assert.Contains("v1", await kept.WaitAsync(HangGuard), StringComparison.Ordinal);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task TheOnlyCallersCancel_StopsTheRequest_AndStaysACancel()
    {
        var github = new GitHubFake("[]", "\"etag-1\"") { Hang = true };
        using var http = new HttpClient(Handler(github, persisted: false, apiTimeout: TimeSpan.FromMinutes(10)));
        using var cancel = new CancellationTokenSource();

        var lookup = http.GetStringAsync(ReleasesUrl, cancel.Token);
        await github.Entered.Task.WaitAsync(HangGuard);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(HangGuard));
        await github.Stopped.Task.WaitAsync(HangGuard); // the request to GitHub was cancelled too
    }

    [Fact]
    public async Task AnAnswer_IsReusedFor60Seconds_ThenAskedAgain()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github, persisted: false));

        await http.GetStringAsync(ReleasesUrl);
        _now += TimeSpan.FromSeconds(59);
        Assert.Contains("v1", await http.GetStringAsync(ReleasesUrl), StringComparison.Ordinal);
        var withinAMinute = github.Requests.Count;
        _now += TimeSpan.FromSeconds(2);
        await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(1, withinAMinute);
        Assert.Equal(2, github.Requests.Count);
        Assert.Single(github.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task ClockSetBack_AfterAnAnswer_DoesNotKeepItFresh()
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github, persisted: false));

        await http.GetStringAsync(ReleasesUrl);
        _now -= TimeSpan.FromHours(1); // the clock jumped back after GitHub confirmed the answer
        await http.GetStringAsync(ReleasesUrl);

        Assert.Equal(2, github.Requests.Count);
        Assert.Single(github.Requests[1].IfNoneMatch);
    }

    // ── 30 s API timeout ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApiTimeout_Is30Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(30), GitHubConditionalCacheHandler.DefaultApiTimeout);

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // an answer is cached: the timeout still reads as could not check, never as that answer
    public async Task ApiRequest_GivesUpAfterTheTimeout_AsANetworkFailure(bool cachedBefore)
    {
        var github = new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\"");
        using var http = new HttpClient(Handler(github, persisted: false, apiTimeout: TimeSpan.FromMilliseconds(200)));
        if (cachedBefore)
        {
            await http.GetStringAsync(ReleasesUrl);
            _now += TimeSpan.FromSeconds(61);
        }
        github.Hang = true;

        var error = await Assert.ThrowsAsync<DownloadTimedOutException>(() => http.GetStringAsync(ReleasesUrl).WaitAsync(HangGuard));

        Assert.IsAssignableFrom<HttpRequestException>(error);
        Assert.Equal("The server took too long to answer: RHI stopped waiting after 1 second. Source: GitHub API", error.Message);
    }

    [Fact]
    public async Task UncachedApiRequest_GivesUpAfterTheTimeout_Too()
    {
        var github = new GitHubFake("{}", "\"etag-1\"") { Hang = true };
        using var http = new HttpClient(Handler(github, persisted: false, apiTimeout: TimeSpan.FromMilliseconds(200)));

        await Assert.ThrowsAsync<DownloadTimedOutException>(()
            => http.PostAsync("https://api.github.com/graphql", new StringContent("{}")).WaitAsync(HangGuard));
    }

    [Fact]
    public async Task ApiAnswer_WhoseBodyStallsAfterTheHeaders_GivesUpAfterTheApiTimeout()
    {
        var github = new GitHubFake("", "\"etag-1\"") { StallBody = true };
        using var http = new HttpClient(Handler(github, persisted: false, apiTimeout: TimeSpan.FromMilliseconds(200)));

        // The 60 s idle limit (#233) would end the read much later: the API timeout covers the body the cache reads.
        var error = await Assert.ThrowsAsync<DownloadTimedOutException>(() => http.GetStringAsync(ReleasesUrl).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal("The server took too long to answer: RHI stopped waiting after 1 second. Source: GitHub API", error.Message);
    }

    [Fact]
    public async Task UserCancel_DuringAnApiRequest_StaysACancel()
    {
        var github = new GitHubFake("{}", "\"etag-1\"") { Hang = true };
        using var http = new HttpClient(Handler(github, persisted: false, apiTimeout: TimeSpan.FromMinutes(10)));
        using var cancel = new CancellationTokenSource();

        var lookup = http.GetStringAsync(ReleasesUrl, cancel.Token);
        await github.Entered.Task.WaitAsync(HangGuard);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(HangGuard)); // not DownloadTimedOutException
    }

    [Fact]
    public async Task PayloadDownload_HasNoApiTimeout()
    {
        var github = new GitHubFake("payload", "\"etag-p\"") { Gate = new() };
        var api = new GitHubFake("{}", "\"etag-1\"") { Hang = true };
        using var http = new HttpClient(Handler(new ByHost(api, github), persisted: false, apiTimeout: TimeSpan.FromMilliseconds(200)));

        var payload = http.GetStringAsync(PayloadUrl);
        await github.Entered.Task.WaitAsync(HangGuard);
        // An API request started after the payload times out while the payload is still waiting.
        await Assert.ThrowsAsync<DownloadTimedOutException>(() => http.GetStringAsync(ReleasesUrl).WaitAsync(HangGuard));
        github.Gate.SetResult();

        Assert.Equal("payload", await payload.WaitAsync(HangGuard));
    }

    [Fact]
    public void TimeoutLine_ReadsInBothLanguages()
    {
        var message = DownloadTimedOutException.MessageFor(RHI.Platform.Linux.Deployment.DownloadSources.GitHubApi.Name, GitHubConditionalCacheHandler.DefaultApiTimeout);

        Assert.Equal("The server took too long to answer: RHI stopped waiting after 30 seconds. Source: GitHub API", message);
        Assert.Equal("O servidor demorou demais para responder: o RHI parou de esperar após 30 segundos. Origem: GitHub API",
            UiLocalizationService.PresentFor("pt-BR", message));
    }

    // ── The app's client ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppClient_SavesUnderTheCacheFolder_AndKeepsTheFiveMinuteTimeoutForPayloads()
    {
        var path = GitHubApiCacheStore.DefaultPath;
        Assert.Equal(Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "github-api-cache.json"), path);
        Assert.StartsWith(TestEnvironmentIsolation.Root, path, StringComparison.Ordinal); // never the real ~/.cache
        var store = new GitHubApiCacheStore(CachePath); // this test's own file (the default is shared by the test run)
        var (http, cache) = AppComposition.CreateHttpClient(new GitHubFake("[{\"tag_name\":\"v1\"}]", "\"etag-1\""), store);
        using (http)
        {
            await http.GetStringAsync(ReleasesUrl);
            Assert.Equal(TimeSpan.FromMinutes(5), http.Timeout);
        }

        // Disposing the client writes the batched save.
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(CachePath));
        Assert.Equal(ReleasesUrl, saved.RootElement.GetProperty("entries")[0].GetProperty("url").GetString());
    }

    /// <summary>Routes payload hosts to one fake and api.github.com to another.</summary>
    private sealed class ByHost(HttpMessageHandler api, HttpMessageHandler other) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _api = new(api);
        private readonly HttpMessageInvoker _other = new(other);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => (request.RequestUri!.Host == "api.github.com" ? _api : _other).SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// GitHub: answers 304 to its current ETag, else the body; can hold a request on a gate or never answer, refuse (with or
    /// without an advertised rate-limit reset), send headers and then stall the body, or answer each request its own body.
    /// </summary>
    internal sealed class GitHubFake(string body, string etag) : HttpMessageHandler
    {
        private byte[] _body = Encoding.UTF8.GetBytes(body);
        private string _etag = etag;

        public List<HttpRequestHeaders> Requests { get; } = [];
        public int NotModifiedAnswers { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public bool Hang { get; set; }
        public string? FinalUrl { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpStatusCode? Refuse { get; set; }
        /// <summary>With <see cref="Refuse"/>: GitHub's rate-limit headers (no quota left, reset at this time).</summary>
        public DateTimeOffset? QuotaResetAt { get; set; }
        public bool StallBody { get; set; }
        public Func<HttpRequestMessage, string>? BodyFor { get; init; }

        public void Publish(string text, string tag) => (_body, _etag) = (Encoding.UTF8.GetBytes(text), tag);
        public void PublishBytes(byte[] bytes, string tag) => (_body, _etag) = (bytes, tag);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage().Headers;
            foreach (var header in request.Headers) copy.TryAddWithoutValidation(header.Key, header.Value);
            int count;
            lock (Requests)
            {
                Requests.Add(copy);
                count = Requests.Count;
            }
            Entered.TrySetResult();
            if (count == 2) SecondEntered.TrySetResult();
            try
            {
                if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Stopped.TrySetResult();
                throw;
            }
            if (Refuse is { } status)
            {
                var refused = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"refused\"}"), RequestMessage = request };
                if (QuotaResetAt is { } reset)
                {
                    refused.Headers.Add("X-RateLimit-Remaining", "0");
                    refused.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return refused;
            }
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == _etag))
            {
                NotModifiedAnswers++;
                return new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
            }
            HttpContent content = StallBody ? new StreamContent(new StallingStream())
                : new ByteArrayContent(BodyFor is null ? _body : Encoding.UTF8.GetBytes(BodyFor(request)));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            response.Headers.ETag = EntityTagHeaderValue.Parse(_etag);
            if (FinalUrl is not null) request.RequestUri = new Uri(FinalUrl); // as the inner handler does on a redirect
            response.RequestMessage = request;
            return response;
        }
    }

    /// <summary>A body whose bytes never come (the headers already did).</summary>
    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
