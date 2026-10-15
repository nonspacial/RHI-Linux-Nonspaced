using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class LumaCatalogServiceTests
{
    [Fact]
    public void ParseCompletedMods_ExtractsMetadataNotesAndFeatureSection()
    {
        var html = """
        <html><body>
        <table>
          <tr><th>Name</th><th>Author</th><th>Download Link</th><th>Status</th><th>Special Notes</th><th>Features</th></tr>
          <tr>
            <td>Batman Arkham Knight</td><td>Filoppi</td>
            <td><a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-Batman_Arkham_Knight.zip">Download</a></td>
            <td>✅</td><td>Use DX11<br/>Keep HDR enabled</td><td><a href="#batman-features">📌</a></td>
          </tr>
          <tr>
            <td>Work In Progress</td><td>Filoppi</td>
            <td><a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/Luma-WIP.zip">Download</a></td>
            <td>🚧</td><td></td><td></td>
          </tr>
        </table>
        <h3 id="batman-features">Batman features</h3>
        <p>HDR upgrade path</p>
        <ul><li>Tonemap upgrade</li><li>UI correction</li></ul>
        </body></html>
        """;

        var mods = LumaCatalogService.ParseCompletedMods(html);

        Assert.Equal(2, mods.Count);
        var batman = mods.Single(x => x.Name == "Batman Arkham Knight");
        Assert.Equal("Filoppi", batman.Author);
        Assert.Equal("✅", batman.Status);
        Assert.Contains("Use DX11", batman.SpecialNotes);
        Assert.Contains("Keep HDR enabled", batman.SpecialNotes);
        Assert.Contains("HDR upgrade path", batman.FeatureNotes);
        Assert.Contains("Tonemap upgrade", batman.FeatureNotes);
        Assert.Equal("🚧", mods.Single(x => x.Name == "Work In Progress").Status);
    }

    [Fact]
    public async Task ResolveAsync_UsesPinnedManifestLumaNameOverrideBeforeDirectName()
    {
        const string manifestUrl = "https://example.test/manifest.json";
        const string wikiUrl = "https://example.test/luma-wiki";
        var manifestJson = """
        {
          "version": 2,
          "lumaNameOverrides": {
            "Batman™: Arkham Knight": "Batman Arkham Knight"
          }
        }
        """;
        var html = WikiRow("Batman Arkham Knight", "Luma-Batman_Arkham_Knight.zip");
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            manifestUrl => Text(manifestJson, "application/json"),
            wikiUrl => Text(html, "text/html"),
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var manifest = new UpstreamRhiManifestService(http, manifestUrl);
        var catalog = new LumaCatalogService(http, manifest, wikiUrl);
        var game = new DetectedGame { Name = "Batman™: Arkham Knight", InstallPath = "/games/batman" };

        var resolved = await catalog.ResolveAsync(game);

        Assert.NotNull(resolved);
        Assert.Equal("Batman Arkham Knight", resolved!.Name);
    }

    // w5 leftover (#190 × Luma): the Edit-game wiki name finds the Luma mod, as it does RenoDX's (upstream MatchLumaGame
    // step 1), and a changed wiki name is looked up again.
    [Fact]
    public async Task ResolveAsync_UsesTheEditGameWikiName_AndLooksAgainWhenItChanges()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(_ => Text(
            WikiRow("Batman Arkham Knight", "Luma-Batman_Arkham_Knight.zip"), "text/html")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var game = new DetectedGame { Name = "BatmanAK", InstallPath = "/games/batman" };
        Assert.Null(await catalog.ResolveAsync(game));

        game.WikiNameOverride = "Batman: Arkham Knight";
        var resolved = await catalog.ResolveAsync(game);

        Assert.Equal("Batman Arkham Knight", resolved?.Name);
        Assert.True(catalog.TryGetCached(game, out var cached));
        Assert.Equal("Batman Arkham Knight", cached?.Name);
    }

    [Fact]
    public async Task ResolveAsync_AWikiNameThatNamesNoMod_FallsBackToTheDetectedName()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(_ => Text(
            WikiRow("Nioh", "Luma-Nioh.zip"), "text/html")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var game = new DetectedGame { Name = "Nioh", InstallPath = "/games/nioh", WikiNameOverride = "Nioh Complete Edition" };

        Assert.Equal("Nioh", (await catalog.ResolveAsync(game))?.Name);
    }

    [Fact]
    public async Task ResolveAsync_DirectNormalizedMatchHandlesTrademarkAndPunctuation()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(_ => Text(
            WikiRow("Lego City: Undercover", "Luma-Lego_City_Undercover.zip"), "text/html")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var game = new DetectedGame { Name = "LEGO® City Undercover", InstallPath = "/games/lego" };

        var resolved = await catalog.ResolveAsync(game);

        Assert.NotNull(resolved);
        Assert.Equal("Lego City: Undercover", resolved!.Name);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotUseSubstringFalsePositive()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(_ => Text(
            WikiRow("Nioh", "Luma-Nioh.zip"), "text/html")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);
        var game = new DetectedGame { Name = "Nioh 3", InstallPath = "/games/nioh3" };

        var resolved = await catalog.ResolveAsync(game);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task FetchAsync_CachesWikiForSubsequentGameResolution()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        var calls = 0;
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == wikiUrl) calls++; // the release list (#94) is a second, separate request
            return Text(WikiRow("Nioh", "Luma-Nioh.zip"), "text/html");
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        _ = await catalog.ResolveAsync(new DetectedGame { Name = "Nioh", InstallPath = "/games/nioh" });
        _ = await catalog.ResolveAsync(new DetectedGame { Name = "Other", InstallPath = "/games/other" });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ParseGenericUnrealTable_ExtractsFlagsNotesVersionAndLaunchArgs()
    {
        var entries = LumaCatalogService.ParseGenericUnrealTable(GenericWiki());

        var entry = Assert.Single(entries).Value;
        Assert.Equal("Generic UE Metadata Test", entry.Name);
        Assert.True(entry.HdrSupported);
        Assert.True(entry.DlssFsrSupported);
        Assert.False(entry.DlssFsrBlocked);
        Assert.Equal("5.4.3", entry.UeVersion);
        Assert.Equal("-dx11 -NoSplash", entry.LaunchArgs);
        Assert.Contains("Launch arguments", entry.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseGenericUnrealTable_ExtractsEngineIniDetailsBySection()
    {
        var entry = Assert.Single(LumaCatalogService.ParseGenericUnrealTable(GenericWiki())).Value;

        Assert.Equal(2, entry.EngineIniKeys.Count);
        Assert.Contains(entry.EngineIniKeys, value => value.Section == "SystemSettings" && value.Key == "r.Test.One" && value.Value == "1");
        Assert.Contains(entry.EngineIniKeys, value => value.Section == "/Script/Engine.RendererSettings" && value.Key == "r.Test.Two" && value.Value == "2");
    }

    [Fact]
    public async Task ResolveGenericUnrealAsync_UsesExactNormalizedIdentityWithoutSubstringAdoption()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(_ => Text(GenericWiki(), "text/html")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        var exact = await catalog.ResolveGenericUnrealAsync(new DetectedGame { Name = "Generic UE Metadata Test™", InstallPath = "/games/exact" });
        var falsePositive = await catalog.ResolveGenericUnrealAsync(new DetectedGame { Name = "Generic UE Metadata Test 2", InstallPath = "/games/other" });

        Assert.NotNull(exact);
        Assert.Equal("Generic UE Metadata Test", exact!.Name);
        Assert.Null(falsePositive);
    }

    [Fact]
    public async Task CompletedAndGenericCatalogs_ShareOneWikiRequest()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        var calls = 0;
        var html = WikiRow("Nioh", "Luma-Nioh.zip") + GenericWiki();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == wikiUrl) calls++; // the release list (#94) is a second, separate request
            return Text(html, "text/html");
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        _ = await catalog.FetchAsync();
        _ = await catalog.FetchGenericUnrealAsync();

        Assert.Equal(1, calls);
    }

    private const string Releases = """
    [{"tag_name":"latest-741","assets":[
      {"name":"Luma-Batman_Arkham_Knight-Test.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Batman_Arkham_Knight-Test.zip"},
      {"name":"Luma-Batman_Arkham_Knight.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Batman_Arkham_Knight.zip"},
      {"name":"Luma-BioShock_Series-x32.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-BioShock_Series-x32.zip"},
      {"name":"Luma-Deus_Ex_Human_Revolution_Director.s_Cut-x32.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Deus_Ex_Human_Revolution_Director.s_Cut-x32.zip"},
      {"name":"Luma-Generic_Mod.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Generic_Mod.zip"},
      {"name":"Luma-Graphics_Analyzer-Dev.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Graphics_Analyzer-Dev.zip"},
      {"name":"Luma-Nioh.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Nioh.zip"},
      {"name":"Source code.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/archive/x.zip"}]},
     {"tag_name":"latest-740","assets":[{"name":"Luma-Older_Only.zip","browser_download_url":"https://github.com/Filoppi/Luma-Framework/releases/download/latest-740/Luma-Older_Only.zip"}]}]
    """;

    [Fact]
    public void ReleaseAssets_NameTheGameMods_OfTheNewestReleaseOnly()
    {
        var mods = LumaCatalogService.ParseReleaseMods(Releases);

        Assert.Equal(["Batman Arkham Knight", "BioShock Series", "Deus Ex Human Revolution Director's Cut", "Nioh"], mods.Select(m => m.Name));
        Assert.Equal("https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-BioShock_Series-x32.zip", mods[1].DownloadUrl);
    }

    [Fact]
    public void Merge_WikiWins_WikiRowWithoutLinkTakesTheAsset_ReleaseOnlyModsAreAdded()
    {
        var wiki = new[]
        {
            new LumaCatalogEntry("Batman: Arkham Knight", "Filoppi", "https://github.com/Filoppi/Luma-Framework/releases/latest/download/wiki.zip", "✅", "notes", null),
            new LumaCatalogEntry("Nioh", "Pumbo", null, "🚧", "wip", "features")
        };

        var merged = LumaCatalogService.MergeReleaseMods(wiki, LumaCatalogService.ParseReleaseMods(Releases));

        Assert.Equal("https://github.com/Filoppi/Luma-Framework/releases/latest/download/wiki.zip", merged[0].DownloadUrl);
        Assert.Equal(new LumaCatalogEntry("Nioh", "Pumbo", "https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-Nioh.zip", "🚧", "wip", "features"), merged[1]);
        Assert.Equal(["BioShock Series", "Deus Ex Human Revolution Director's Cut"], merged.Skip(2).Select(m => m.Name));
    }

    [Fact]
    public async Task ResolveAsync_FindsAModOnlyBuiltInTheRelease_NotOnTheWikiYet()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            wikiUrl => Text(WikiRow("Nioh", "Luma-Nioh.zip"), "text/html"),
            LumaCatalogService.ReleasesListApiUrl => Text(Releases, "application/json"),
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        var resolved = await catalog.ResolveAsync(new DetectedGame { Name = "BioShock Series", InstallPath = "/games/bioshock" });

        Assert.Equal("https://github.com/Filoppi/Luma-Framework/releases/download/latest-741/Luma-BioShock_Series-x32.zip", resolved?.DownloadUrl);
    }

    [Fact]
    public async Task ReleaseListUnreadable_TheWikiAloneIsUsed()
    {
        const string wikiUrl = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(req => req.RequestUri!.AbsoluteUri == wikiUrl
            ? Text(WikiRow("Nioh", "Luma-Nioh.zip"), "text/html")
            : throw new HttpRequestException("offline test")));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        Assert.Equal(["Nioh"], (await catalog.FetchAsync()).Select(m => m.Name));
    }

    private static string GenericWiki() => """
    <table>
      <tr><th>Name</th><th>HDR</th><th>DLSS / FSR</th><th>Notes</th><th>UE Version</th></tr>
      <tr>
        <td>Generic UE Metadata Test</td>
        <td>✅</td>
        <td>✅</td>
        <td>
          Launch arguments: <code>-dx11 -NoSplash</code>
          <details><summary>Modify Engine.ini</summary>
            <code>[SystemSettings]</code><br/>
            <code>r.Test.One=1</code><br/>
            <code>[/Script/Engine.RendererSettings]</code><br/>
            <code>r.Test.Two=2</code>
          </details>
        </td>
        <td>5.4.3</td>
      </tr>
    </table>
    """;

    private static string WikiRow(string name, string asset) => $"""
    <table>
      <tr><th>Name</th><th>Author</th><th>Download Link</th><th>Status</th><th>Special Notes</th><th>Features</th></tr>
      <tr><td>{name}</td><td>Filoppi</td><td><a href="https://github.com/Filoppi/Luma-Framework/releases/latest/download/{asset}">Download</a></td><td>✅</td><td>Named package</td><td></td></tr>
    </table>
    """;

    private static HttpResponseMessage Text(string value, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, mediaType)
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
