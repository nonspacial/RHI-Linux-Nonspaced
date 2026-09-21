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
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
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
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
            return Text(html, "text/html");
        }));
        var catalog = new LumaCatalogService(http, wikiUrl: wikiUrl);

        _ = await catalog.FetchAsync();
        _ = await catalog.FetchGenericUnrealAsync();

        Assert.Equal(1, calls);
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
