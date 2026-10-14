using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class GitHubReleaseAssetResolverTests
{
    [Fact]
    public async Task Resolve_DynamicZipFilename_IsPayloadAgnostic()
    {
        var json = """
        {
          "tag_name": "v0.13.1-beta.1",
          "assets": [
            { "name": "AUTOMATIC_INSTALLATION_AVAILABLE.txt", "browser_download_url": "https://github.com/rhi-tests/info" },
            { "name": "DLSS5-Feeder-0.13.1-beta.1.zip", "browser_download_url": "https://github.com/rhi-tests/feed.zip", "digest": "sha256:0123456789abcdef" }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(JsonResponse(json)));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest",
            AssetNamePattern: @"^DLSS5-Feeder-.*\.zip$"));

        Assert.Equal("0.13.1-beta.1", result.Version);
        Assert.Equal("DLSS5-Feeder-0.13.1-beta.1.zip", result.AssetName);
        Assert.Equal("https://github.com/rhi-tests/feed.zip", result.AssetUrl);
        Assert.Equal("0123456789abcdef", result.Sha256);
    }

    /// <summary>
    /// #153 (upstream 2.8.6, 2bfb083): /releases/latest is asked as written. Shape of
    /// Jahbanny/reshade-screenshot-discord-fix on 2026-10-14: GitHub's "Latest" release is v1.03, while the
    /// list sorted by creation date starts with the older v1.0.2. The old rewrite to ?per_page=1 installed v1.0.2.
    /// </summary>
    [Fact]
    public async Task Resolve_LatestEndpoint_IsAskedAsWritten_NotRewrittenToTheCreationOrderedList()
    {
        const string latest = """
        { "tag_name": "v1.03", "prerelease": false, "assets": [ { "name": "screenshot-discord-fix.addon64", "browser_download_url": "https://github.com/rhi-tests/v103.addon64" } ] }
        """;
        const string list = """
        [ { "tag_name": "v1.0.2", "prerelease": false, "assets": [ { "name": "screenshot-discord-fix.addon64", "browser_download_url": "https://github.com/rhi-tests/v102.addon64" } ] } ]
        """;
        var requested = new List<string>();
        using var http = new HttpClient(new CaptureHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requested.Add(url);
            return url switch
            {
                "https://api.github.com/repos/Jahbanny/reshade-screenshot-discord-fix/releases/latest" => JsonResponse(latest),
                "https://api.github.com/repos/Jahbanny/reshade-screenshot-discord-fix/releases?per_page=1" => JsonResponse(list),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            "https://api.github.com/repos/Jahbanny/reshade-screenshot-discord-fix/releases/latest",
            ExactAssetName: "screenshot-discord-fix.addon64"));

        Assert.Equal("1.03", result.Version);
        Assert.Equal("https://github.com/rhi-tests/v103.addon64", result.AssetUrl);
        Assert.Equal(["https://api.github.com/repos/Jahbanny/reshade-screenshot-discord-fix/releases/latest"], requested);
    }

    /// <summary>A prerelease-only repo opts in with the list endpoint (upstream manifest e9eef5d); the newest entry wins.</summary>
    [Fact]
    public async Task Resolve_ExplicitOneItemList_IsAskedAsWritten_AndReturnsThePrerelease()
    {
        const string list = """
        [ { "tag_name": "v1.4.13-pre8", "prerelease": true, "assets": [ { "name": "dlss5-bridge.addon64", "browser_download_url": "https://github.com/rhi-tests/pre8.addon64" } ] } ]
        """;
        var requested = new List<string>();
        using var http = new HttpClient(new CaptureHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return JsonResponse(list);
        }));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            "https://api.github.com/repos/NIGos/dlss5-bridge/releases?per_page=1",
            ExactAssetName: "dlss5-bridge.addon64"));

        Assert.Equal("1.4.13-pre8", result.Version);
        Assert.Equal(["https://api.github.com/repos/NIGos/dlss5-bridge/releases?per_page=1"], requested);
    }

    [Fact]
    public async Task Resolve_ReleaseList_SkipsDraftAndFindsLaterMatchingAsset()
    {
        var json = """
        [
          { "tag_name": "v3", "draft": true, "assets": [ { "name": "bridge-v3.zip", "browser_download_url": "https://github.com/rhi-tests/v3" } ] },
          { "tag_name": "v2", "draft": false, "assets": [ { "name": "notes.txt", "browser_download_url": "https://github.com/rhi-tests/notes" } ] },
          { "tag_name": "v1", "draft": false, "assets": [ { "name": "bridge-v1.zip", "browser_download_url": "https://github.com/rhi-tests/v1" } ] }
        ]
        """;
        using var http = new HttpClient(new FakeHandler(JsonResponse(json)));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            "https://api.github.com/repos/example/releases?per_page=100",
            AssetNamePattern: @"^bridge-.*\.zip$"));

        Assert.Equal("1", result.Version);
        Assert.Equal("bridge-v1.zip", result.AssetName);
    }

    [Fact]
    public async Task Resolve_ExactName_TakesPriorityButPatternIsFallback()
    {
        var json = """
        {
          "tag_name": "v7",
          "assets": [
            { "name": "dynamic-v7.addon64", "browser_download_url": "https://github.com/rhi-tests/dynamic" }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(JsonResponse(json)));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(new GitHubReleaseAssetQuery(
            "https://api.github.com/repos/example/releases/latest",
            ExactAssetName: "stable.addon64",
            AssetNamePattern: @"^dynamic-.*\.addon64$"));

        Assert.Equal("dynamic-v7.addon64", result.AssetName);
    }


    [Fact]
    public async Task Resolve_FeederCurrentDirectAssets_MatchesLatestReleaseShape()
    {
        var json = """
        {
          "tag_name": "v0.5.1",
          "assets": [
            { "name": "dlss5-feed.addon64", "browser_download_url": "https://github.com/rhi-tests/dlss5-feed.addon64", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            { "name": "DLSS5_Feed.fx", "browser_download_url": "https://github.com/rhi-tests/DLSS5_Feed.fx", "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            { "name": "feed-vk-layer.zip", "browser_download_url": "https://github.com/rhi-tests/feed-vk-layer.zip" }
          ]
        }
        """;

        using var addonHttp = new HttpClient(new FakeHandler(JsonResponse(json)));
        var addonResolver = new GitHubReleaseAssetResolver(addonHttp);
        var addon = await addonResolver.ResolveAsync(Dlss5FeederFullDeploymentService.CreateFeederAddonReleaseQuery());
        Assert.Equal("0.5.1", addon.Version);
        Assert.Equal("dlss5-feed.addon64", addon.AssetName);

        using var fxHttp = new HttpClient(new FakeHandler(JsonResponse(json)));
        var fxResolver = new GitHubReleaseAssetResolver(fxHttp);
        var fx = await fxResolver.ResolveAsync(Dlss5FeederFullDeploymentService.CreateFeederFxReleaseQuery());
        Assert.Equal("0.5.1", fx.Version);
        Assert.Equal("DLSS5_Feed.fx", fx.AssetName);
    }

    [Fact]
    public async Task Resolve_FeederArchiveAsset_MatchesCurrentZipPackaging()
    {
        var json = """
        {
          "tag_name": "v0.13.1-beta.1",
          "assets": [
            {
              "name": "AUTOMATIC_INSTALLATION_AVAILABLE.txt",
              "browser_download_url": "https://github.com/rhi-tests/AUTOMATIC_INSTALLATION_AVAILABLE.txt"
            },
            {
              "name": "DLSS5-Feeder-0.13.1-beta.1.zip",
              "browser_download_url": "https://github.com/rhi-tests/DLSS5-Feeder-0.13.1-beta.1.zip",
              "digest": "sha256:8da626ed906a29289f001cb613640015360e36b0c3478348bf097a304b34bb18"
            }
          ]
        }
        """;
        using var http = new HttpClient(new FakeHandler(JsonResponse(json)));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(Dlss5FeederFullDeploymentService.CreateFeederArchiveReleaseQuery());

        Assert.Equal("0.13.1-beta.1", result.Version);
        Assert.Equal("DLSS5-Feeder-0.13.1-beta.1.zip", result.AssetName);
        Assert.Equal("https://github.com/rhi-tests/DLSS5-Feeder-0.13.1-beta.1.zip", result.AssetUrl);
    }


    [Fact]
    public async Task Resolve_PinnedTagUsesExactTagEndpointAndPreservesSelectedRelease()
    {
        var json = """
        {
          "tag_name": "v0.12.4-beta.2",
          "prerelease": true,
          "assets": [
            { "name": "bridge.addon64", "browser_download_url": "https://github.com/rhi-tests/bridge.addon64" }
          ]
        }
        """;
        string? requested = null;
        using var http = new HttpClient(new CaptureHandler(request =>
        {
            requested = request.RequestUri?.AbsoluteUri;
            return JsonResponse(json);
        }));
        var resolver = new GitHubReleaseAssetResolver(http);

        var result = await resolver.ResolveAsync(
            new GitHubReleaseAssetQuery(
                "https://api.github.com/repos/example/tool/releases/latest",
                ExactAssetName: "bridge.addon64"),
            releaseTag: "v0.12.4-beta.2");

        Assert.Equal("0.12.4-beta.2", result.Version);
        Assert.Equal("https://api.github.com/repos/example/tool/releases/tags/v0.12.4-beta.2", requested);
    }

    [Fact]
    public async Task ListVersions_ReturnsPublishedReleaseHistoryIncludingPrereleases()
    {
        var json = """
        [
          { "tag_name": "v2.0.0-beta.1", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "v1.9.0", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "v1.8.0-internal", "draft": true, "prerelease": false, "assets": [] }
        ]
        """;
        using var http = new HttpClient(new CaptureHandler(_ => JsonResponse(json)));
        var resolver = new GitHubReleaseAssetResolver(http);

        var versions = await resolver.ListVersionsAsync("https://api.github.com/repos/example/tool/releases/latest");

        Assert.Collection(versions,
            first =>
            {
                Assert.Equal("v2.0.0-beta.1", first.Tag);
                Assert.Equal("2.0.0-beta.1", first.Version);
                Assert.True(first.Prerelease);
            },
            second =>
            {
                Assert.Equal("v1.9.0", second.Tag);
                Assert.Equal("1.9.0", second.Version);
                Assert.False(second.Prerelease);
            });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
