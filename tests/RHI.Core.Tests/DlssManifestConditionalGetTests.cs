using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>#35 residual: the DLSS manifest is revalidated with its ETag; an unchanged manifest is a 304 read from the cache.</summary>
public sealed class DlssManifestConditionalGetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-etag-" + Guid.NewGuid().ToString("N"));
    private const string Manifest = "{\"dlss\":[{\"version\":\"310.9.16\",\"url\":\"https://github.com/rhi-tests/sr.zip\"}]}";

    public DlssManifestConditionalGetTests() => Directory.CreateDirectory(_root);

    private sealed class EtagSite(string body, string etag) : HttpMessageHandler
    {
        public List<string?> IfNoneMatch { get; } = [];
        public int Bodies { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            IfNoneMatch.Add(sent);
            if (sent == etag) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            Bodies++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            return Task.FromResult(response);
        }
    }

    private DlssManagerService Service(HttpMessageHandler site)
        => new(new HttpClient(site), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache"));

    [Fact]
    public async Task SecondFetch_SendsTheEtag_AndAnUnchangedManifestComesFromTheCache()
    {
        var site = new EtagSite(Manifest, "\"v1\"");

        var first = await Service(site).FetchManifestAsync();
        var second = await Service(site).FetchManifestAsync(); // a later session: new service, same cache folder

        Assert.Equal([null, "\"v1\""], site.IfNoneMatch);
        Assert.Equal(1, site.Bodies);
        Assert.Equal("310.9.16", second.Dlss.Single().Version);
        Assert.Equal(first.Dlss.Single().Version, second.Dlss.Single().Version);
    }

    [Fact]
    public async Task NotModified_OverACacheThatNoLongerReads_AsksForTheWholeManifestOnce()
    {
        var site = new EtagSite(Manifest, "\"v1\"");
        await Service(site).FetchManifestAsync();
        File.WriteAllText(Path.Combine(_root, "cache", "dlss_manifest.json"), "{ broken");

        var again = await Service(site).FetchManifestAsync();

        Assert.Equal([null, "\"v1\"", null], site.IfNoneMatch);
        Assert.Equal("310.9.16", again.Dlss.Single().Version);
        Assert.Equal(Manifest, File.ReadAllText(Path.Combine(_root, "cache", "dlss_manifest.json")));
    }

    [Fact]
    public async Task WithoutACachedCopy_NoEtagIsSent_EvenIfAnOldEtagFileIsLeft()
    {
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "dlss_manifest.json.etag"), "\"v1\"");
        var site = new EtagSite(Manifest, "\"v1\"");

        await Service(site).FetchManifestAsync();

        Assert.Equal([null], site.IfNoneMatch);
    }

    // w5 review finding 3 (#177): whether the network answered is returned with each fetch, never kept on the shared
    // service, so a cached fallback is never read as answered.
    [Fact]
    public async Task FetchWithStatus_IsAnsweredFor200And304_NotForTheCachedFallback()
    {
        var site = new EtagSite(Manifest, "\"v1\"");
        var service = Service(site);

        var downloaded = await service.FetchManifestWithStatusAsync();
        var notModified = await service.FetchManifestWithStatusAsync();
        var fallback = await Service(new Failing()).FetchManifestWithStatusAsync();

        Assert.Equal((true, true, false), (downloaded.Answered, notModified.Answered, fallback.Answered));
        Assert.Equal("310.9.16", fallback.Manifest.Dlss.Single().Version);
    }

    [Fact]
    public async Task FetchWithStatus_IsPerCall_AConcurrentFetchCannotChangeIt()
    {
        await Service(new EtagSite(Manifest, "\"v1\"")).FetchManifestAsync(); // the cache now holds a copy
        var site = new HeldThenFailing(Manifest);
        var service = Service(site);

        var held = service.FetchManifestWithStatusAsync();   // answered, but only once released
        await site.FirstArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var failed = await service.FetchManifestWithStatusAsync(); // fails meanwhile: the cached copy stands in
        site.Release.SetResult();
        var answered = await held.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(failed.Answered);
        Assert.True(answered.Answered);
    }

    private sealed class Failing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }

    // The first request waits until released, then answers; every later one fails at once.
    private sealed class HeldThenFailing(string body) : HttpMessageHandler
    {
        private int _requests;
        public TaskCompletionSource FirstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) > 1) throw new HttpRequestException("offline test");
            FirstArrived.SetResult();
            await Release.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
