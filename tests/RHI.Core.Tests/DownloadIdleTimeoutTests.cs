using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Persistence;

namespace RHI.Core.Tests;

/// <summary>
/// #233 (wizardkof v0.9.25.0-beta.1 PAR-D111, as an idle limit): every HTTP body RHI reads gives up when no byte
/// arrives for <see cref="TrustedDownload.IdleLimit"/>, with a plain message naming the source; never as a Cancel, never
/// as "current"; a slow but steady body still finishes (per gap, never a total deadline); the caller's own Cancel stays
/// a Cancel; a stalled download leaves no temp file and no stamped cache entry.
/// </summary>
public sealed class DownloadIdleTimeoutTests : IDisposable
{
    private const string PayloadUrl = "https://github.com/rhi-tests/opti/releases/download/1.0/OptiScaler.7z";
    private const string ApiUrl = "https://api.github.com/repos/rhi-tests/opti/releases/latest";
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(300);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-idle-{Guid.NewGuid():N}");

    public DownloadIdleTimeoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IdleLimit_IsSixtySecondsWithNoByte_ByDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), TrustedDownload.DefaultIdleLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), TrustedDownload.IdleLimit);
    }

    [Fact]
    public void Stall_IsANetworkFailure_NeverACancel()
    {
        var stall = new DownloadStalledException("OptiScaler", TimeSpan.FromSeconds(60));
        Assert.IsAssignableFrom<HttpRequestException>(stall);
        Assert.False(typeof(OperationCanceledException).IsAssignableFrom(typeof(DownloadStalledException)));
        Assert.Null(stall.StatusCode);
    }

    [Fact]
    public void StallMessage_IsFixedTextFirst_SourceLast_InEnglishAndPortuguese()
    {
        var message = DownloadStalledException.MessageFor("OptiScaler", TimeSpan.FromSeconds(60));

        Assert.Equal("The download stopped: no data arrived for 60 seconds. Source: OptiScaler", message);
        Assert.Equal("O download parou: nenhum dado chegou durante 60 segundos. Origem: OptiScaler", UiLocalizationService.PresentFor("pt-BR", message));
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));

        // One second reads singular in both languages (the tests' sub-second limits round up to it).
        var oneSecond = DownloadStalledException.MessageFor("GitHub API", TimeSpan.FromMilliseconds(300));
        Assert.Equal("The download stopped: no data arrived for 1 second. Source: GitHub API", oneSecond);
        Assert.Equal("O download parou: nenhum dado chegou durante 1 segundo. Origem: GitHub API", UiLocalizationService.PresentFor("pt-BR", oneSecond));
    }

    // #233 correction: HttpClient's own timeout (.NET: "The request was canceled due to the configured HttpClient.Timeout
    // of 300 seconds elapsing.") is shown as RHI's own line in both languages.
    [Fact]
    public void TimedOutMessage_IsFixedTextFirst_SourceLast_InEnglishAndPortuguese()
    {
        var message = DownloadTimedOutException.MessageFor("GitHub API", TimeSpan.FromMinutes(5));

        Assert.Equal("The server took too long to answer: RHI stopped waiting after 300 seconds. Source: GitHub API", message);
        Assert.Equal("O servidor demorou demais para responder: o RHI parou de esperar após 300 segundos. Origem: GitHub API",
            UiLocalizationService.PresentFor("pt-BR", message));
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
        Assert.Equal("The server took too long to answer: RHI stopped waiting after 1 second. Source: Nexus Mods API",
            DownloadTimedOutException.MessageFor("Nexus Mods API", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Payload_BodyThatStopsSendingData_GivesUpWithTheStallMessage_AndLeavesNoTempFile()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "payload")).FullName;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 4096)));

        var error = await Assert.ThrowsAsync<DownloadStalledException>(() => TrustedDownload.DownloadToTempAsync(
            http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler).WaitAsync(StalledHttp.HangGuard));

        Assert.Equal("The download stopped: no data arrived for 1 second. Source: OptiScaler", error.Message);
        Assert.Equal("OptiScaler", error.DownloadSource);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir)); // the partial temp file is gone
    }

    [Theory]
    [InlineData("get-string")]
    [InlineData("get-bytes")]
    [InlineData("post-json")]
    public async Task Metadata_BodyThatStopsSendingData_GivesUpWithTheStallMessage(string call)
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 10)));
        Func<Task> read = call switch
        {
            "get-string" => () => TrustedDownload.GetStringAsync(http, ApiUrl, DownloadSources.GitHubApi),
            "get-bytes" => () => TrustedDownload.GetBytesAsync(http, ApiUrl, DownloadSources.GitHubApi),
            _ => () => TrustedDownload.PostJsonAsync(http, ApiUrl, DownloadSources.GitHubApi, "{}"),
        };

        var error = await Assert.ThrowsAsync<DownloadStalledException>(() => read().WaitAsync(StalledHttp.HangGuard));

        Assert.Equal("The download stopped: no data arrived for 1 second. Source: GitHub API", error.Message);
    }

    [Fact]
    public async Task CachedPayload_Stall_PlacesNothing_AndWritesNoStamp()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
        var entry = Path.Combine(dir, "OptiScaler.7z");
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 4096)));

        await Assert.ThrowsAsync<DownloadStalledException>(() => TrustedDownload.EnsureVerifiedAsync(
            http, PayloadUrl, entry, DownloadSources.OptiScaler, "1.0").WaitAsync(StalledHttp.HangGuard));

        Assert.False(File.Exists(entry));
        Assert.False(File.Exists(CacheStamp.StampPath(entry)));
        Assert.False(CacheStamp.IsValid(entry, PayloadUrl, "1.0"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    // Per gap, never a total deadline, on a manual clock (no real waiting, so machine load cannot move the margin): 40
    // chunks 59 s apart (39 minutes in all) finish under the 60 s limit; one gap of exactly 60 s is a stall.
    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public async Task SlowButSteadyPayload_LongerThanTheLimitInTotal_Finishes(int gapSeconds, bool finishes)
    {
        var clock = new ManualClock();
        TrustedDownload.IdleClockForTesting = clock;
        const int chunks = 40, size = 1024;
        var gap = TimeSpan.FromSeconds(gapSeconds);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "trickle")).FullName;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Response(new TrickleStream(chunks, size, gap, clock))));

        var download = TrustedDownload.DownloadToTempAsync(http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler)
            .WaitAsync(StalledHttp.HangGuard);

        if (finishes)
        {
            using var done = await download;
            Assert.Equal(gap * chunks, clock.Elapsed); // 40 × 59 s: forty times the limit's worth of gaps in all
            Assert.Equal(chunks * size, done.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(TrickleStream.Expected(chunks, size))), done.Sha256);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DownloadStalledException>(() => download);
            Assert.Equal("The download stopped: no data arrived for 60 seconds. Source: OptiScaler", error.Message);
            Assert.Equal(gap, clock.Elapsed); // the first gap already
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        Assert.Equal(0, clock.LiveTimers); // every read's own timer ended with its read
    }

    [Fact]
    public async Task CallersCancel_DuringAStall_IsStillACancel_NotAStall()
    {
        TrustedDownload.IdleLimitForTesting = TimeSpan.FromSeconds(30);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "cancel")).FullName;
        var body = new StallingStream(4096);
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(stream: body)));
        using var cancel = new CancellationTokenSource();
        var download = TrustedDownload.DownloadToTempAsync(http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler,
            cancellationToken: cancel.Token);
        await body.Stalled.WaitAsync(StalledHttp.HangGuard);

        cancel.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(StalledHttp.HangGuard));
        Assert.IsNotType<DownloadStalledException>(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    // #233: two stagings of one cache entry at once (the coordinator gate keeps them apart in the app; this is what a
    // second copy would do): unique temp names, an atomic rename, the stamp hashing what is in place — both end with the
    // same valid, stamped entry and nothing else in the folder.
    [Fact]
    public async Task TwoStagingsOfOneCacheEntry_AtOnce_EndWithOneValidStampedEntry()
    {
        var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7)).ToArray();
        var dir = Directory.CreateDirectory(Path.Combine(_root, "twice")).FullName;
        var entry = Path.Combine(dir, "OptiScaler.7z");
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        using var http = new HttpClient(new HeldHandler(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
            await both.Task.WaitAsync(StalledHttp.HangGuard);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));

        var results = await Task.WhenAll(
            TrustedDownload.EnsureVerifiedAsync(http, PayloadUrl, entry, DownloadSources.OptiScaler, "1.0"),
            TrustedDownload.EnsureVerifiedAsync(http, PayloadUrl, entry, DownloadSources.OptiScaler, "1.0")).WaitAsync(StalledHttp.HangGuard);

        var sha = Convert.ToHexString(SHA256.HashData(payload));
        Assert.All(results, result => Assert.Equal(sha, result.Sha256, ignoreCase: true));
        Assert.Equal(payload, File.ReadAllBytes(entry));
        Assert.True(CacheStamp.IsValid(entry, PayloadUrl, "1.0"));
        Assert.Equal(new[] { entry, CacheStamp.StampPath(entry) }.Order(), Directory.EnumerateFiles(dir).Order());
    }

    // ---- #233 correction: the app's HttpClient has the GitHub API cache handler in front ----

    private const string DisplayCommanderApi = "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest";

    // The handler reads an api.github.com answer that carries a validator inside SendAsync, before TrustedDownload sees
    // it: that read is under the idle limit too, so a stalled answer gives up at the limit, long before HttpClient.Timeout.
    [Theory]
    [InlineData("etag")]
    [InlineData("last-modified")]
    [InlineData("none")]
    public async Task GitHubApiAnswer_ThroughTheAppsCacheHandler_ThatStopsSendingData_GivesUpAtTheIdleLimit(string validator)
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var clientTimeout = TimeSpan.FromSeconds(10);
        using var http = new HttpClient(new GitHubConditionalCacheHandler(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 10,
            etag: validator == "etag" ? "\"dc-1\"" : null,
            lastModified: validator == "last-modified" ? DateTimeOffset.UnixEpoch : null)))) { Timeout = clientTimeout };
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<DownloadStalledException>(() => TrustedDownload.GetStringAsync(
            http, DisplayCommanderApi, DownloadSources.GitHubApi).WaitAsync(StalledHttp.HangGuard));

        Assert.True(watch.Elapsed < clientTimeout / 2, $"gave up only after {watch.Elapsed}");
        Assert.Equal("The download stopped: no data arrived for 1 second. Source: GitHub API", error.Message);
    }

    // The handler holds at most its cache limit + 1 byte: a longer answer is not kept, and the caller still gets all of it.
    [Fact]
    public async Task GitHubApiAnswer_LongerThanTheCacheKeeps_ReachesTheCallerWhole_AndIsNotKept()
    {
        var size = GitHubConditionalCacheHandler.MaxCachedBodyBytes + 100_000;
        var body = TrickleStream.Expected(size / 1000, 1000);
        var inner = new StalledHttp.Handler(_ =>
        {
            var response = StalledHttp.Response(new TrickleStream(size / 1000, 1000, TimeSpan.Zero)); // no Content-Length
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse("\"big-1\"");
            return response;
        });
        var cache = new GitHubConditionalCacheHandler(inner);
        using var http = new HttpClient(cache);

        var first = await TrustedDownload.GetBytesAsync(http, DisplayCommanderApi, DownloadSources.GitHubApi).WaitAsync(StalledHttp.HangGuard);
        var second = await TrustedDownload.GetBytesAsync(http, DisplayCommanderApi, DownloadSources.GitHubApi).WaitAsync(StalledHttp.HangGuard);

        Assert.Equal(body, first);
        Assert.Equal(body, second);
        Assert.Equal(0, cache.CacheHits);
    }

    // HttpClient's own timeout (a server that never answers) is RHI's network failure with its own text, on every
    // TrustedDownload path, never .NET's "The request was canceled due to the configured HttpClient.Timeout" line.
    [Theory]
    [InlineData("get-string")]
    [InlineData("payload")]
    [InlineData("head")]
    public async Task ServerThatNeverAnswers_GivesUpAtTheClientTimeout_WithRhisOwnLine(string call)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "silent")).FullName;
        using var http = new HttpClient(new StalledHttp.NeverAnswers()) { Timeout = TimeSpan.FromMilliseconds(300) };
        Func<Task> read = call switch
        {
            "get-string" => () => TrustedDownload.GetStringAsync(http, ApiUrl, DownloadSources.GitHubApi),
            "payload" => () => TrustedDownload.DownloadToTempAsync(http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler),
            _ => () => TrustedDownload.HeadAsync(http, PayloadUrl, DownloadSources.OptiScaler),
        };

        var error = await Assert.ThrowsAsync<DownloadTimedOutException>(() => read().WaitAsync(StalledHttp.HangGuard));

        var source = call == "get-string" ? "GitHub API" : "OptiScaler";
        Assert.Equal($"The server took too long to answer: RHI stopped waiting after 1 second. Source: {source}", error.Message);
        Assert.IsAssignableFrom<HttpRequestException>(error);
        Assert.IsType<TaskCanceledException>(error.InnerException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task CallersCancel_WhileWaitingForTheAnswer_IsStillACancel()
    {
        using var http = new HttpClient(new StalledHttp.NeverAnswers()) { Timeout = TimeSpan.FromSeconds(30) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrustedDownload.GetStringAsync(
            http, ApiUrl, DownloadSources.GitHubApi, cancellationToken: cancel.Token).WaitAsync(StalledHttp.HangGuard));

        Assert.True(cancel.IsCancellationRequested);
        Assert.IsNotType<TimeoutException>(error.InnerException);
    }

    // ---- the other body readers (audit): each now runs under the same idle limit ----

    [Fact]
    public async Task LumaWiki_Stall_GivesUp_AndTheNextLookupTriesAgain()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var calls = 0;
        using var http = new HttpClient(new StalledHttp.Handler(request => request.RequestUri!.AbsoluteUri == LumaCatalogService.WikiUrl
            ? Interlocked.Increment(ref calls) == 1 ? StalledHttp.Stalled() : null
            : null));
        var luma = new LumaCatalogService(http);

        var error = await Assert.ThrowsAsync<DownloadStalledException>(() => luma.FetchAsync().WaitAsync(StalledHttp.HangGuard));
        Assert.Equal("The download stopped: no data arrived for 1 second. Source: Luma Framework", error.Message);
        // The wiki gate was released: the next lookup asks again (offline now) instead of waiting behind the stall.
        await Assert.ThrowsAsync<HttpRequestException>(() => luma.FetchAsync().WaitAsync(StalledHttp.HangGuard));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GitHubTokenCheck_Stall_IsCouldNotCheck_AndSavesNothing()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var tokenPath = Path.Combine(_root, "github_api.txt");
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 10)));
        var service = new GitHubTokenSettingsService(http, new SecretFileStore(tokenPath));

        var status = await service.SaveAsync("ghp_" + new string('a', 36)).WaitAsync(StalledHttp.HangGuard);

        Assert.Equal(GitHubTokenCheck.CouldNotCheck, status.Result);
        Assert.False(File.Exists(tokenPath));
    }

    [Fact]
    public async Task NexusAnswer_Stall_IsTheClientsFailure_WithTheStallText()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 10)));
        var nexus = new NexusApiClient(http);

        var error = await Assert.ThrowsAsync<NexusApiException>(() => nexus.ValidateKeyAsync("key").WaitAsync(StalledHttp.HangGuard));

        Assert.Equal("The download stopped: no data arrived for 1 second. Source: Nexus Mods API", error.Message);
        Assert.IsType<DownloadStalledException>(error.InnerException);
        Assert.False(error.Rejected);
    }

    [Fact]
    public async Task NexusAnswer_ClientTimeout_IsTheClientsFailure_WithRhisOwnLine()
    {
        using var http = new HttpClient(new StalledHttp.NeverAnswers()) { Timeout = TimeSpan.FromMilliseconds(300) };
        var nexus = new NexusApiClient(http);

        var error = await Assert.ThrowsAsync<NexusApiException>(() => nexus.ValidateKeyAsync("key").WaitAsync(StalledHttp.HangGuard));

        Assert.Equal("The server took too long to answer: RHI stopped waiting after 1 second. Source: Nexus Mods API", error.Message);
        Assert.IsType<TaskCanceledException>(error.InnerException);
        Assert.False(error.Rejected);
    }

    // w11 follow-up E1 (download-idle re-review 1): no connection within the handler's ConnectTimeout (15 s in the app)
    // reaches the client as a TaskCanceledException with a TimeoutException, like HttpClient's own 30-s timeout. Nexus
    // was never reached: the line says so, never "stopped waiting after 30 seconds" after 15. The real handler, a connect
    // that never finishes (no network), EN + PT-BR.
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task NexusConnectTimeout_ReadsAsUnreachable_NeverTheClientsSeconds(string language)
    {
        var handler = NexusApiClient.CreateHandler();
        handler.UseProxy = false;
        handler.ConnectTimeout = TimeSpan.FromMilliseconds(300);
        handler.ConnectCallback = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var nexus = new NexusApiClient(http);

        var error = await Assert.ThrowsAsync<NexusApiException>(() => nexus.ValidateKeyAsync("key").WaitAsync(StalledHttp.HangGuard));

        Assert.Equal(NexusApiClient.UnreachableMessage, error.Message);
        Assert.True(DownloadTimedOutException.IsClientTimeout(error.InnerException!, CancellationToken.None)); // the shape that misled
        Assert.False(error.Rejected);
        var line = UiLocalizationService.PresentFor(language, error.Message);
        Assert.DoesNotContain("30", line, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
        Assert.False(DownloadTimedOutException.IsConnectTimeout( // HttpClient's own timeout wraps the cancellation it caused
            new TaskCanceledException("client", new TimeoutException("client", new TaskCanceledException()))));
        Assert.Null(error.InnerException!.InnerException!.InnerException); // .NET 8's connect-limit shape, as the probe saw it
    }

    // ---- the background loop: a pass that stalls is retried after its pause, never at once ----

    [Fact]
    public async Task BackgroundLoop_PassThatStalls_IsRetriedAfterItsPause_NeverAtOnce()
    {
        using var stop = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var reported = new List<(Exception Error, TimeSpan Next)>();
        var passes = 0;

        await BackgroundUpdateLoop.RunAsync(
            _ =>
            {
                if (++passes == 1) return Task.FromException(new DownloadStalledException("OptiScaler", TrustedDownload.DefaultIdleLimit));
                stop.Cancel(); // the retry ran: end the loop (the shell closing)
                return Task.FromCanceled(stop.Token);
            },
            TimeSpan.FromHours(4), runImmediate: true, (error, next) => reported.Add((error, next)), stop.Token,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; }).WaitAsync(StalledHttp.HangGuard);

        var failure = Assert.Single(reported);
        Assert.IsType<DownloadStalledException>(failure.Error);
        Assert.Equal(BackgroundUpdateLoop.FirstRetryDelay, failure.Next);
        Assert.Equal([BackgroundUpdateLoop.FirstRetryDelay], waits); // the one wait before the retry: 15 min, not zero
        Assert.Equal(2, passes);
    }

    private sealed class HeldHandler(Func<Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer();
    }
}
