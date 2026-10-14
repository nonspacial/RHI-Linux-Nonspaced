using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#156 (upstream 2.8.6 2975485, a531660): the nightly build list and one pinned build, cached per tag.</summary>
public sealed partial class OptiScalerPackageProviderTests
{
    private static string NightlyRelease(string tag, string assetUrl, byte[] archive, bool draft = false)
        => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease = true,
            assets = new[] { new { name = $"OptiScaler_v10.0.0-pre1_{tag[8..]}.7z", browser_download_url = assetUrl, size = archive.LongLength,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant() } }
        });

    [Fact]
    public async Task ListNightlyBuilds_GivesTheNewest30Dates_NewestFirst_SkippingDraftsAndForeignTags()
    {
        var archive = CreateArchiveBytes();
        var releases = Enumerable.Range(0, 34).Select(i => NightlyRelease($"nightly-{20260901 + i}", "https://github.com/rhi-tests/n.7z", archive, draft: i == 33))
            .Append(JsonSerializer.Serialize(new { tag_name = "v0.9.4", draft = false, assets = Array.Empty<object>() }))
            .Reverse();
        using var http = new HttpClient(new MapHandler(new() { [OptiScalerPackageProvider.NightlyReleasesApi] = "[" + string.Join(",", releases) + "]" }));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "list"));

        var builds = await provider.ListNightlyBuildsAsync();

        Assert.Equal(30, builds.Count);
        Assert.Equal("20260933", builds[0]); // the draft 20260934 is skipped
        Assert.Equal("20260904", builds[^1]);
        Assert.All(builds, build => Assert.True(OptiScalerPackageProvider.IsNightlyBuild(build)));
    }

    [Fact]
    public async Task GetNightlyBuild_AsksThatTag_AndCachesEachTagOnItsOwn()
    {
        var older = CreateArchiveBytes();
        var newer = Enumerable.Reverse(CreateArchiveBytes()).ToArray();
        var map = new Dictionary<string, object>
        {
            [OptiScalerPackageProvider.NightlyTagApiUrl("20261004")] = NightlyRelease("nightly-20261004", "https://github.com/rhi-tests/n1004.7z", older),
            ["https://github.com/rhi-tests/n1004.7z"] = older,
            [OptiScalerPackageProvider.NightlyTagApiUrl("20261012")] = NightlyRelease("nightly-20261012", "https://github.com/rhi-tests/n1012.7z", newer),
            ["https://github.com/rhi-tests/n1012.7z"] = newer
        };
        var handler = new MapHandler(map);
        using var http = new HttpClient(handler);
        var extractor = new FakeExtractor();
        var provider = new OptiScalerPackageProvider(http, extractor, Path.Combine(_root, "pinned"));

        var pinned = await provider.GetNightlyBuildAsync("20261004");
        var other = await provider.GetNightlyBuildAsync("20261012");
        var again = await provider.GetNightlyBuildAsync("20261004");

        Assert.Equal(("20261004", OptiScalerVariant.Nightly), (pinned.Version, pinned.Variant));
        Assert.Equal("https://github.com/rhi-tests/n1004.7z", pinned.SourceUrl);
        Assert.Equal("20261012", other.Version);
        Assert.NotEqual(pinned.RootDirectory, other.RootDirectory);
        Assert.Equal(pinned.RootDirectory, again.RootDirectory);
        Assert.Equal(1, handler.Count("https://github.com/rhi-tests/n1004.7z")); // stamped per tag: reused, never re-downloaded
        Assert.Equal(2, extractor.CallCount);
        Assert.Equal(0, handler.Count(OptiScalerPackageProvider.NightlyReleasesApi)); // a pin never asks for \"latest\"
    }

    [Fact]
    public async Task GetNightlyBuild_RefusesAnotherBuildThanThePinnedOne_AndABadBuild()
    {
        var archive = CreateArchiveBytes();
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.NightlyTagApiUrl("20261004")] = NightlyRelease("nightly-20261013", "https://github.com/rhi-tests/x.7z", archive)
        }));
        var provider = new OptiScalerPackageProvider(http, new FakeExtractor(), Path.Combine(_root, "mismatch"));

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetNightlyBuildAsync("20261004"));
        Assert.StartsWith(OptiScalerPackageProvider.NightlyBuildMismatchPrefix, mismatch.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetNightlyBuildAsync("../../x"));
    }

    private sealed class MapHandler(Dictionary<string, object> map) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        public int Count(string url) => _counts.GetValueOrDefault(url);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            _counts[url] = Count(url) + 1;
            return Task.FromResult(map.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent((string)body, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
