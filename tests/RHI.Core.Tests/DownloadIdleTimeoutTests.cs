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
    }

    [Fact]
    public async Task Payload_BodyThatStopsSendingData_GivesUpWithTheStallMessage_AndLeavesNoTempFile()
    {
        TrustedDownload.IdleLimitForTesting = ShortLimit;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "payload")).FullName;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Stalled(bytesFirst: 4096)));

        var error = await Assert.ThrowsAsync<DownloadStalledException>(() => TrustedDownload.DownloadToTempAsync(
            http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler).WaitAsync(StalledHttp.HangGuard));

        Assert.Equal("The download stopped: no data arrived for 1 seconds. Source: OptiScaler", error.Message);
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

        Assert.Equal("The download stopped: no data arrived for 1 seconds. Source: GitHub API", error.Message);
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

    [Fact]
    public async Task SlowButSteadyPayload_LongerThanTheLimitInTotal_Finishes()
    {
        // 40 chunks, one every 75 ms: about 3 s in all, three times the 1 s idle limit, and never 1 s without a byte.
        TrustedDownload.IdleLimitForTesting = TimeSpan.FromSeconds(1);
        const int chunks = 40, size = 1024;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "trickle")).FullName;
        using var http = new HttpClient(new StalledHttp.Handler(_ => StalledHttp.Response(new TrickleStream(chunks, size, TimeSpan.FromMilliseconds(75)))));
        var watch = Stopwatch.StartNew();

        using var download = await TrustedDownload.DownloadToTempAsync(http, PayloadUrl, Path.Combine(dir, "OptiScaler.7z"), DownloadSources.OptiScaler)
            .WaitAsync(StalledHttp.HangGuard);

        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2), $"the trickle took only {watch.Elapsed}");
        Assert.Equal(chunks * size, download.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(TrickleStream.Expected(chunks, size))), download.Sha256);
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
        Assert.Equal("The download stopped: no data arrived for 1 seconds. Source: Luma Framework", error.Message);
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

        Assert.Equal("The download stopped: no data arrived for 1 seconds. Source: Nexus Mods API", error.Message);
        Assert.IsType<DownloadStalledException>(error.InnerException);
        Assert.False(error.Rejected);
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
