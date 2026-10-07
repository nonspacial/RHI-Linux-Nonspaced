using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxCatalogServiceTests
{
    [Fact]
    public void ParseWiki_Extracts64And32BitAddonLinks()
    {
        const string html = """
        <table><tr><th>Name</th><th>Links</th><th>Status</th></tr>
        <tr><td>Street Fighter™ 6</td><td>
        <a href="https://example.test/renodx-sf6.addon64">snapshot</a>
        <a href="https://example.test/renodx-sf6.addon32">32</a></td><td>✅</td></tr></table>
        """;
        var mods = RenoDxCatalogService.ParseWiki(html);
        var mod = Assert.Single(mods);
        Assert.Equal("Street Fighter™ 6", mod.Name);
        Assert.EndsWith(".addon64", mod.SnapshotUrl64);
        Assert.EndsWith(".addon32", mod.SnapshotUrl32);
        Assert.Equal("streetfighter6", RenoDxCatalogService.NormalizeName(mod.Name));
    }

    [Fact]
    public async Task Resolve_UsesGenericUnityFallback()
    {
        var handler = new StaticHandler("<html></html>");
        var service = new RenoDxCatalogService(new HttpClient(handler));
        var game = new DetectedGame { Name = "Unknown Unity Game", InstallPath = "/tmp/game" };
        var found = await service.ResolveAsync(game, new[] { "/tmp/game/UnityPlayer.dll" });
        Assert.NotNull(found);
        Assert.True(found!.IsGeneric);
        Assert.Equal(RenoDxCatalogService.GenericUnityUrl64, found.SnapshotUrl64);
        Assert.Equal("Voosh", found.Author);
    }


    [Fact]
    public async Task Resolve_UsesPinnedManifestSnapshotOverrideBeforeWiki()
    {
        const string manifestJson = """
        {
          "version": 2,
          "engineHintOverrides": {
            "Hogwarts Legacy": "Unreal Engine 4.27"
          },
          "snapshotOverrides": {
            "Hogwarts Legacy": "https://marat569.github.io/renodx/renodx-ue-extended.addon64"
          }
        }
        """;
        using var manifestHttp = new HttpClient(new StaticHandler(manifestJson));
        var manifest = new UpstreamRhiManifestService(manifestHttp, "https://example.test/manifest.json");
        var wikiHandler = new CountingStaticHandler("<html><body>should not be needed</body></html>");
        using var wikiHttp = new HttpClient(wikiHandler);
        var service = new RenoDxCatalogService(wikiHttp, manifest);
        var game = new DetectedGame { Name = "Hogwarts Legacy", InstallPath = "/tmp/hogwarts" };

        var found = await service.ResolveAsync(game, []);

        Assert.NotNull(found);
        Assert.Equal("https://marat569.github.io/renodx/renodx-ue-extended.addon64", found!.SnapshotUrl64);
        Assert.Contains("Unreal Engine 4.27", found.Notes);
        Assert.Equal("Marat", ModAuthorCredit.ForRenoDx(found, game)); // UE-Extended build credit (upstream 2.8.1)
        Assert.Equal(0, wikiHandler.RequestCount);
    }

    // Shaped like upstream RHI 2.8.5 data: docs/RenoDXdb.json entries (CODE VEIN II has no download of its own) and
    // manifest.json snapshotOverrides / authorOverrides for the same titles.
    private const string Db285 = """
    [
      { "notes": null, "name": "CODE VEIN II", "discordUrl": null, "snapshotUrl": null, "status": "Done", "nexusUrl": null, "author": "Marat", "snapshotUrl32": null, "discussionUrl": null },
      { "notes": null, "name": "Days Gone", "discordUrl": null, "snapshotUrl": "https://oopydoopy.github.io/renodx/renodx-daysgone.addon64", "status": "WIP", "nexusUrl": null, "author": "OopyDoopy (Jon)", "snapshotUrl32": null, "discussionUrl": null },
      { "notes": null, "name": "RoboCop: Rogue City", "discordUrl": null, "snapshotUrl": "https://mqhaji.github.io/renodx/renodx-robocop.addon64", "status": "Done", "nexusUrl": null, "author": "Musa", "snapshotUrl32": null, "discussionUrl": null },
      { "notes": null, "name": "Routine", "discordUrl": null, "snapshotUrl": "https://github.com/mqhaji/renodx/releases/download/snapshot/renodx-routine.addon64", "status": "Done", "nexusUrl": null, "author": "Musa", "snapshotUrl32": null, "discussionUrl": null },
      { "notes": null, "name": "GreedFall: The Dying World", "discordUrl": null, "snapshotUrl": "https://github.com/RankFTW/renodx/releases/download/snapshot/renodx-greedfall2.addon64", "status": "Done", "nexusUrl": null, "author": "RankFTW", "snapshotUrl32": null, "discussionUrl": null }
    ]
    """;

    private const string Manifest285 = """
    {
      "version": 2,
      "snapshotOverrides": {
        "Hogwarts Legacy": "https://marat569.github.io/renodx/renodx-ue-extended.addon64",
        "Assassin's Creed": "https://clshortfuse.github.io/renodx/renodx-asscreed1.addon32",
        "Cities: Skylines": "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64",
        "CODE VEIN II": "https://marat569.github.io/renodx/renodx-codevein2.addon64",
        "Days Gone": "https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-daysgone.addon64",
        "Far Cry 2": "https://clshortfuse.github.io/renodx/renodx-duniaengine.addon32",
        "RoboCop: Rogue City": "https://mqhaji.github.io/renodx/renodx-robocop.addon64",
        "Routine": "https://github.com/mqhaji/renodx/releases/download/snapshot/renodx-routine.addon64"
      },
      "authorOverrides": {
        "Assassin's Creed": "Musa",
        "GreedFall: The Dying World": "RankFTW"
      }
    }
    """;

    [Theory]
    // Review cases: a snapshot override replaces only the download; the RHI database author stays.
    [InlineData("CODE VEIN II", "https://marat569.github.io/renodx/renodx-codevein2.addon64", "Marat")]
    [InlineData("Days Gone", "https://github.com/OopyDoopy/renodx/releases/download/snapshot/renodx-daysgone.addon64", "Jon")]
    [InlineData("RoboCop: Rogue City", "https://mqhaji.github.io/renodx/renodx-robocop.addon64", "Musa")]
    [InlineData("Routine", "https://github.com/mqhaji/renodx/releases/download/snapshot/renodx-routine.addon64", "Musa")]
    // UE-Extended build: Marat. No database entry: the manifest authorOverrides entry, else the generic engine
    // add-on's maintainer, else nobody.
    [InlineData("Hogwarts Legacy", "https://marat569.github.io/renodx/renodx-ue-extended.addon64", "Marat")]
    [InlineData("Assassin's Creed", null, "Musa")]
    [InlineData("Cities: Skylines", "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64", "Voosh")]
    [InlineData("Far Cry 2", null, null)]
    public async Task Resolve_SnapshotOverrideKeepsTheDatabaseAuthor(string title, string? url64, string? expectedAuthor)
    {
        using var manifestHttp = new HttpClient(new StaticHandler(Manifest285));
        var manifest = new UpstreamRhiManifestService(manifestHttp, "https://example.test/manifest.json");
        using var dbHttp = new HttpClient(new RoutedCatalogHandler(Db285));
        var service = new RenoDxCatalogService(dbHttp, manifest);
        // The library policy copies manifest authorOverrides onto the game (UpstreamGameLibraryPolicyService).
        using var manifestDoc = System.Text.Json.JsonDocument.Parse(Manifest285);
        var authorOverride = manifestDoc.RootElement.GetProperty("authorOverrides").TryGetProperty(title, out var a) ? a.GetString() : null;
        var game = new DetectedGame { Name = title, InstallPath = "/tmp/" + title, AuthorOverride = authorOverride };

        var found = await service.ResolveAsync(game, []);

        Assert.NotNull(found);
        if (url64 is not null) Assert.Equal(url64, found!.SnapshotUrl64);
        else Assert.NotNull(found!.SnapshotUrl32);
        Assert.Equal(expectedAuthor, ModAuthorCredit.ForRenoDx(found, game));
    }

    [Fact]
    public void ParseStructuredAuthors_KeepsAuthorsOfEntriesWithoutADownload()
    {
        var authors = RenoDxCatalogService.ParseStructuredAuthors(Db285);

        Assert.Equal("Marat", authors.Single(e => e.Name == "CODE VEIN II").Author);
        Assert.Equal(5, authors.Count);
        // The installable catalogue still drops CODE VEIN II: it has nothing to download without the override.
        Assert.DoesNotContain(RenoDxCatalogService.ParseStructuredDatabase(Db285), e => e.Name == "CODE VEIN II");
    }

    [Fact]
    public async Task Resolve_WikiUnlinkSuppressesNamedAndGenericFallbackWithoutNetwork()
    {
        var handler = new CountingStaticHandler("<html></html>");
        using var http = new HttpClient(handler);
        var service = new RenoDxCatalogService(http);
        var game = new DetectedGame
        {
            Name = "Unlinked Game",
            InstallPath = "/tmp/unlinked",
            WikiUnlinkedByManifest = true
        };

        var found = await service.ResolveAsync(game, new[] { "/tmp/unlinked/UnityPlayer.dll" });

        Assert.Null(found);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Resolve_UsesManifestWikiNameOverrideForWikiMatch()
    {
        const string html = "<table><tr><td>Canonical Wiki Game</td><td><a href=\"https://cdn.test/canonical.addon64\">snapshot</a></td></tr></table>";
        using var http = new HttpClient(new StaticHandler(html));
        var service = new RenoDxCatalogService(http);
        var game = new DetectedGame
        {
            Name = "Launcher Alias",
            WikiNameOverride = "Canonical Wiki Game",
            InstallPath = "/tmp/alias"
        };

        var found = await service.ResolveAsync(game, []);

        Assert.NotNull(found);
        Assert.Equal("Canonical Wiki Game", found!.Name);
        Assert.Equal("https://cdn.test/canonical.addon64", found.SnapshotUrl64);
    }

    [Fact]
    public async Task Resolve_ManifestFailureFallsBackToWikiAndGenericDetection()
    {
        using var manifestHttp = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.ServiceUnavailable));
        var manifest = new UpstreamRhiManifestService(manifestHttp, "https://example.test/offline-manifest.json");
        using var wikiHttp = new HttpClient(new StaticHandler("<html></html>"));
        var service = new RenoDxCatalogService(wikiHttp, manifest);
        var game = new DetectedGame { Name = "Offline Unity Game", InstallPath = "/tmp/offline" };

        var found = await service.ResolveAsync(game, new[] { "/tmp/offline/UnityPlayer.dll" });

        Assert.NotNull(found);
        Assert.True(found!.IsGeneric);
        Assert.Equal(RenoDxCatalogService.GenericUnityUrl64, found.SnapshotUrl64);
    }


    [Fact]
    public async Task PrimeResolvedMatch_CachesUnsupportedCustomEngineAsKnownMiss()
    {
        using var http = new HttpClient(new StaticHandler("<html></html>"));
        var service = new RenoDxCatalogService(http);
        var game = new DetectedGame
        {
            Name = "World of Warcraft",
            InstallPath = "/tmp/wow",
            ExecutablePath = "/tmp/wow/Wow.exe"
        };

        var primed = await service.PrimeResolvedMatchAsync(game, ["/tmp/wow/d3d12.dll", "/tmp/wow/d3d11.dll"]);

        Assert.Null(primed);
        Assert.True(service.TryGetResolvedMatch(game, out var cached));
        Assert.Null(cached);
    }

    [Fact]
    public async Task PrimeResolvedMatch_CachesNamedMatchForSupportedTitle()
    {
        const string html = "<table><tr><td>Street Fighter 6</td><td><a href=\"https://cdn.test/renodx-sf6.addon64\">snapshot</a></td></tr></table>";
        using var http = new HttpClient(new StaticHandler(html));
        var service = new RenoDxCatalogService(http);
        var game = new DetectedGame
        {
            Name = "Street Fighter 6",
            InstallPath = "/tmp/sf6",
            ExecutablePath = "/tmp/sf6/StreetFighter6.exe"
        };

        var primed = await service.PrimeResolvedMatchAsync(game, []);

        Assert.NotNull(primed);
        Assert.True(service.TryGetResolvedMatch(game, out var cached));
        Assert.NotNull(cached);
        Assert.Equal("Street Fighter 6", cached!.Name);
    }

    [Fact]
    public void ParseStructuredDatabase_UsesPublishedSnapshotUrlsAndIgnoresEntriesWithoutDeployableAddon()
    {
        const string json = """
        [
          { "name":"Game 64", "snapshotUrl":"https://cdn.test/game64.addon64", "snapshotUrl32":null, "notes":"Done", "author":" OopyDoopy (Jon) " },
          { "name":"Game 32", "snapshotUrl":null, "snapshotUrl32":"https://cdn.test/game32.addon32" },
          { "name":"No Payload", "snapshotUrl":null, "snapshotUrl32":null },
          { "name":"Wrong Asset", "snapshotUrl":"https://cdn.test/readme.zip", "snapshotUrl32":null }
        ]
        """;

        var mods = RenoDxCatalogService.ParseStructuredDatabase(json);

        Assert.Equal(2, mods.Count);
        Assert.Equal("https://cdn.test/game64.addon64", mods.Single(x => x.Name == "Game 64").SnapshotUrl64);
        Assert.Equal("https://cdn.test/game32.addon32", mods.Single(x => x.Name == "Game 32").SnapshotUrl32);
        Assert.Equal("OopyDoopy (Jon)", mods.Single(x => x.Name == "Game 64").Author);
        Assert.Null(mods.Single(x => x.Name == "Game 32").Author);
    }

    [Fact]
    public void MergeCatalogs_RhiDatabaseRemainsAuthoritativeAndWikiOnlyFillsMissingFields()
    {
        var structured = new[]
        {
            new RenoDxModEntry("Shared Game", "https://cdn.test/pinned.addon64", "https://cdn.test/pinned.addon32", "Pinned")
        };
        var live = new[]
        {
            new RenoDxModEntry("Shared Game", "https://cdn.test/live.addon64", null, "Live")
        };

        var entry = Assert.Single(RenoDxCatalogService.MergeCatalogs(structured, live));

        Assert.Equal("https://cdn.test/pinned.addon64", entry.SnapshotUrl64);
        Assert.Equal("https://cdn.test/pinned.addon32", entry.SnapshotUrl32);
        Assert.Equal("Pinned", entry.Notes);
    }

    [Fact]
    public async Task FetchAsync_UsesCurrentRhiDatabaseWithoutScrapingWikiWhenDatabaseIsHealthy()
    {
        const string json = """
        [
          { "name":"Database Game", "snapshotUrl":"https://cdn.test/db.addon64", "snapshotUrl32":null, "notes":"RHI DB" }
        ]
        """;
        var handler = new RoutedCatalogHandler(json);
        using var http = new HttpClient(handler);
        var service = new RenoDxCatalogService(http);

        var mods = await service.FetchAsync();

        var entry = Assert.Single(mods);
        Assert.Equal("Database Game", entry.Name);
        Assert.Equal("https://cdn.test/db.addon64", entry.SnapshotUrl64);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(RenoDxCatalogService.StructuredDbUrl, Assert.Single(handler.RequestUris));
    }

    private sealed class StaticHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) });
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class CountingStaticHandler(string content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }

    private sealed class RoutedCatalogHandler(string databaseJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var uri = request.RequestUri?.AbsoluteUri ?? "";
            RequestUris.Add(uri);
            if (uri == RenoDxCatalogService.StructuredDbUrl)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(databaseJson)
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
