using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #126: the Available HDR mods list includes RHI Unity database games (upstream 2.7.9 <c>GetAllHdrMods</c>,
/// <c>_dbUnityEntries</c>): in the RenoDX column, ✓ or 🔨 by Status, no RenoDX link (as upstream), joined by the
/// normalised name, never over a named RenoDX mod, and Luma merges into them.
/// </summary>
public sealed class AvailableHdrModsUnityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-hdr-unity-" + Guid.NewGuid().ToString("N"));

    public AvailableHdrModsUnityTests() => Directory.CreateDirectory(_root);

    private static RenoDxUnityDbEntry Unity(string name, string? status = null) => new() { Name = name, Status = status };

    [Fact]
    public void UnityGameWithoutARenoDxRow_IsListedWithATickInTheRenoDxColumn()
    {
        var entries = AvailableHdrModsCatalog.Merge([], [], [Unity("Cult of the Lamb", "Done"), Unity("Hollow Knight: Silksong", "WIP")]);

        var cult = entries.Single(e => e.Name == "Cult of the Lamb");
        Assert.Equal(HdrModAvailability.Available, cult.RenoDx);
        Assert.Equal(HdrModAvailability.None, cult.Luma);
        Assert.Null(cult.RenoDxUrl);
        Assert.Null(cult.DownloadUrl);
        Assert.Equal(HdrModAvailability.WorkInProgress, entries.Single(e => e.Name == "Hollow Knight: Silksong").RenoDx);
    }

    [Fact]
    public void NamedRenoDxMod_WinsOverTheUnityRow_AndLumaJoinsTheUnityRow()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [new RenoDxModEntry("Hades II", "https://github.com/rhi-tests/hades2.addon64", null, null)],
            [new LumaCatalogEntry("Cuphead", "Filoppi", "https://github.com/rhi-tests/cuphead.zip", "✅", null, null)],
            [Unity("Hades™ II", "WIP"), Unity("Cuphead")]);

        Assert.Equal(["Cuphead", "Hades II"], entries.Select(e => e.Name));
        var hades = entries.Single(e => e.Name == "Hades II");
        Assert.Equal(HdrModAvailability.Available, hades.RenoDx); // the named mod's status and link, not the Unity row's
        Assert.Equal("https://github.com/rhi-tests/hades2.addon64", hades.RenoDxUrl);
        var cuphead = entries.Single(e => e.Name == "Cuphead");
        Assert.Equal(HdrModAvailability.Available, cuphead.RenoDx);
        Assert.Equal(HdrModAvailability.Available, cuphead.Luma);
        Assert.Equal("https://github.com/rhi-tests/cuphead.zip", cuphead.DownloadUrl);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheUnityDatabase_AndStillListsItWhenLumaFails()
    {
        const string lumaWiki = "https://example.test/luma-wiki";
        using var http = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            RenoDxCatalogService.StructuredDbUrl => Json("""[{ "name": "Hades II", "snapshotUrl": "https://github.com/rhi-tests/hades2.addon64" }]"""),
            RenoDxUnityDatabaseService.LiveUrl => Json("""[{ "Name": "Cult of the Lamb", "Status": "Done" }]"""),
            lumaWiki => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            var url => throw new InvalidOperationException("Unexpected URL: " + url)
        }));
        var unity = new RenoDxUnityDatabaseService(http, Path.Combine(_root, "unity.json"), fallbackUrl: null);
        var catalog = new AvailableHdrModsCatalog(new RenoDxCatalogService(http), new LumaCatalogService(http, wikiUrl: lumaWiki), unity);

        var snapshot = await catalog.LoadAsync();

        Assert.Equal(["Cult of the Lamb", "Hades II"], snapshot.Entries.Select(e => e.Name));
        Assert.True(snapshot.LumaUnavailable);
    }

    [Fact]
    public void App_HandsTheUnityDatabaseToTheList()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline test")));
        var wine = new WineDllOverrideService();
        var app = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine));

        Assert.NotNull(app.RenoDxUnityDatabase);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
