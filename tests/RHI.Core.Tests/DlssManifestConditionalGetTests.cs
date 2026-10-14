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

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
