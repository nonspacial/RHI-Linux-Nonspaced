using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #262: KoKlusz's HDR Gaming Database README lists its games in an HTML table since November 2026 (upstream 2.9.3
/// <c>3d99906</c>); the Markdown list it used before is still read, and a download that yields no game keeps the last good copy.
/// </summary>
public sealed class HdrDatabaseReadmeTests : IDisposable
{
    private const string Discussions = "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-db-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The README as served on 2026-11-16 (138 table rows).</summary>
    private static string TodaysReadme()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hdr-gaming-database-readme-2026-11-16.md"));

    private static DetectedGame Named(string name) => new() { Name = name, InstallPath = "/games/" + name };

    [Fact]
    public void TodaysHtmlTable_GivesEveryGameWithItsDiscussion()
    {
        var entries = GameLinkCatalogue.ParseHdrDatabase(TodaysReadme());

        Assert.Equal(138, entries.Count);
        Assert.Equal(new GameLinkEntry("007 First Light", Discussions + "144"), entries[0]);
        Assert.Contains(new GameLinkEntry("Alan Wake 2", Discussions + "12"), entries);
        Assert.Contains(new GameLinkEntry("Ratchet & Clank Rift Apart", Discussions + "56"), entries); // &amp; decoded
        Assert.Contains(new GameLinkEntry("Senua's Saga Hellblade II", Discussions + "48"), entries); // curly quote straightened
        Assert.Contains(new GameLinkEntry("The Witcher 3: Wild Hunt — Remastered", Discussions + "143"), entries);
        Assert.All(entries, entry => Assert.StartsWith(Discussions, entry.Url, StringComparison.Ordinal)); // wiki, reddit, mod lists left out
    }

    [Fact]
    public void HtmlRows_KeepOnlyThisDatabasesDiscussions_AndStripTrademarks()
    {
        const string readme = """
            <table>
            <tr><td><a href="https://github.com/KoKlusz/HDR-Gaming-Database/discussions/7">Marvel’s Game™</a></td><td align="center">⭐</td></tr>
            <tr><td><a href="https://evil.example/KoKlusz/HDR-Gaming-Database/discussions/8">Elsewhere</a></td></tr>
            <tr><td><a href="https://github.com/KoKlusz/HDR-Gaming-Database/wiki/Simple-HDR-Fixes">Wiki page</a></td></tr>
            <tr><td><a href="http://github.com/KoKlusz/HDR-Gaming-Database/discussions/9">Plain http</a></td></tr>
            <tr><td><a target="_blank" HREF="https://github.com/KoKlusz/HDR-Gaming-Database/discussions/10">Other Attributes</a></td></tr>
            <tr><td><a href="https://github.com/KoKlusz/HDR-Gaming-Database/discussions/11">   </a></td></tr>
            </table>
            """;

        Assert.Equal(
        [
            new GameLinkEntry("Marvel's Game", Discussions + "7"),
            new GameLinkEntry("Other Attributes", Discussions + "10")
        ], GameLinkCatalogue.ParseHdrDatabase(readme));
    }

    [Fact]
    public void TheOldMarkdownList_IsStillRead()
    {
        const string readme = """
            * [Cyberpunk 2077™](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/5) ⭐
              * [Marvel’s Spider-Man](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/9)
            """;

        Assert.Equal(
        [
            new GameLinkEntry("Cyberpunk 2077", Discussions + "5"),
            new GameLinkEntry("Marvel's Spider-Man", Discussions + "9")
        ], GameLinkCatalogue.ParseHdrDatabase(readme));
    }

    [Fact]
    public async Task TodaysReadme_GivesTheCardItsHdrDatabasePage()
    {
        var links = new GameLinkCatalogue(new HttpClient(Serve(TodaysReadme())), Path.Combine(_root, "links"));
        await links.LoadAsync();

        Assert.Equal(Discussions + "56", links.HdrDatabasePageFor(Named("Ratchet & Clank Rift Apart")));
        Assert.Equal(Discussions + "38", links.HdrDatabasePageFor(Named("Marvel’s Spider-Man Remastered")));
        Assert.Equal(Discussions + "12", links.HdrDatabasePageFor(Named("ALAN WAKE 2")));
    }

    [Fact]
    public async Task AReadmeWithNoGame_KeepsTheLastGoodCopy()
    {
        var directory = Path.Combine(_root, "links");
        Directory.CreateDirectory(directory);
        var cacheFile = Path.Combine(directory, "hdr-gaming-database.json");
        var seeded = JsonSerializer.Serialize(new
        {
            fetchedAtUtc = DateTimeOffset.UtcNow.AddDays(-3), // stale: the README is downloaded again
            entries = new[] { new { Name = "Alan Wake 2", Url = Discussions + "12" } }
        });
        File.WriteAllText(cacheFile, seeded);

        // The next format change: a non-empty README in which no row is recognised.
        const string unknownFormat = "<div><ul><li data-game=\"Alan Wake 2\" data-thread=\"12\"></li></ul></div>\n";
        var handler = Serve(unknownFormat);
        var links = new GameLinkCatalogue(new HttpClient(handler), directory);
        await links.LoadAsync();
        await links.BackgroundRefreshes; // w14 final review: the stale copy is served while the README is asked again

        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsoluteUri == GameLinkCatalogue.HdrDatabaseReadmeUrl);
        Assert.Equal(Discussions + "12", links.HdrDatabasePageFor(Named("Alan Wake 2")));
        Assert.Equal(seeded, File.ReadAllText(cacheFile)); // the good copy is not overwritten

        // Full Refresh with the same unrecognised README keeps the loaded copy too.
        await links.RefreshAsync();
        Assert.Equal(Discussions + "12", links.HdrDatabasePageFor(Named("Alan Wake 2")));
        Assert.Equal(seeded, File.ReadAllText(cacheFile));
    }

    private static GameLinkCatalogueTests.Fake Serve(string readme)
        => new(request => request.RequestUri!.AbsoluteUri == GameLinkCatalogue.HdrDatabaseReadmeUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(readme, Encoding.UTF8, "text/plain") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}
