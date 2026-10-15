using System.Net;
using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #197: the Available HDR mods list also shows the RenoDX mods the RHI database lists without an RHI download (published
/// only on Nexus Mods or Discord, linked there) and the games of RHI's UE-Extended database (<c>RenoDXdb-unreal.json</c>),
/// in upstream 2.8.7 <c>GetAllHdrMods</c>'s order: named mods, UE-Extended, Unity, then Luma merged in.
/// </summary>
public sealed class AvailableHdrModsLeftoversTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-197-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static RenoDxUnrealDbEntry Unreal(string name, string? status = null) => new() { Name = name, Status = status };

    [Fact]
    public void ModsWithoutAnRhiDownload_LinkTheirNexusPage_ElseTheirDiscord()
    {
        var entries = AvailableHdrModsCatalog.Merge(
        [
            new RenoDxModEntry("Nexus Game", null, null, null, NexusUrl: "https://www.nexusmods.com/nexusgame/mods/1"),
            new RenoDxModEntry("Discord Game", null, null, null, IsWorkInProgress: true, DiscordUrl: "https://discord.gg/abc"),
            new RenoDxModEntry("Both Game", "https://github.com/rhi-tests/both.addon64", null, null, NexusUrl: "https://www.nexusmods.com/both/mods/2")
        ], []).ToDictionary(e => e.Name);

        Assert.Equal(HdrModAvailability.Available, entries["Nexus Game"].RenoDx);
        Assert.Equal("https://www.nexusmods.com/nexusgame/mods/1", entries["Nexus Game"].DownloadUrl);
        Assert.Equal(HdrModAvailability.WorkInProgress, entries["Discord Game"].RenoDx);
        Assert.Equal("https://discord.gg/abc", entries["Discord Game"].DownloadUrl);
        Assert.Equal("https://github.com/rhi-tests/both.addon64", entries["Both Game"].DownloadUrl); // RHI's download first, as upstream
    }

    [Fact]
    public void UeExtendedGames_JoinTheRenoDxColumn_BeforeUnityRows_AndNeverOverANamedMod()
    {
        var entries = AvailableHdrModsCatalog.Merge(
            [new RenoDxModEntry("Hogwarts Legacy", "https://github.com/rhi-tests/hl.addon64", null, null)],
            [new LumaCatalogEntry("Abiotic Factor", "Filoppi", "https://github.com/rhi-tests/af.zip", "✅", null, null)],
            [new RenoDxUnityDbEntry { Name = "Both Engines", Status = "Done" }],
            [Unreal("A Way Out", "WIP"), Unreal("Abiotic Factor", "Done"), Unreal("Hogwarts Legacy™", "WIP"), Unreal("Both Engines", "WIP")])
            .ToDictionary(e => e.Name);

        Assert.Equal(["A Way Out", "Abiotic Factor", "Both Engines", "Hogwarts Legacy"], entries.Keys.Order());
        Assert.Equal(HdrModAvailability.WorkInProgress, entries["A Way Out"].RenoDx);
        Assert.Null(entries["A Way Out"].RenoDxUrl); // no RenoDX link, as upstream
        Assert.Equal(HdrModAvailability.Available, entries["Abiotic Factor"].RenoDx);
        Assert.Equal(HdrModAvailability.Available, entries["Abiotic Factor"].Luma); // Luma merges into the UE-Extended row
        Assert.Equal("https://github.com/rhi-tests/af.zip", entries["Abiotic Factor"].DownloadUrl);
        Assert.Equal(HdrModAvailability.Available, entries["Hogwarts Legacy"].RenoDx); // the named mod's status and link
        Assert.Equal("https://github.com/rhi-tests/hl.addon64", entries["Hogwarts Legacy"].RenoDxUrl);
        Assert.Equal(HdrModAvailability.WorkInProgress, entries["Both Engines"].RenoDx); // UE-Extended first, then Unity
    }

    [Fact]
    public async Task LoadAsync_ReadsTheWholeDatabaseAndTheUeExtendedDatabase()
    {
        const string lumaWiki = "https://raw.githubusercontent.com/rhi-tests/luma/main/wiki.html";
        var handler = new GameLinkCatalogueTests.Fake(request => request.RequestUri!.AbsoluteUri switch
        {
            RenoDxCatalogService.StructuredDbUrl => Text("""
                [{"name": "Hades II", "status": "Done", "snapshotUrl": "https://github.com/rhi-tests/hades2.addon64"},
                 {"name": "A Plague Tale: Requiem", "status": "Done", "nexusUrl": "https://www.nexusmods.com/aplaguetalerequiem/mods/50"}]
                """),
            RenoDxUnrealDatabaseService.LiveUrl => Text("""[{"Name": "Abiotic Factor", "Status": "Done", "Method": "ini"}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        var http = new HttpClient(handler);
        var catalog = new AvailableHdrModsCatalog(new RenoDxCatalogService(http), new LumaCatalogService(http, wikiUrl: lumaWiki),
            unreal: new RenoDxUnrealDatabaseService(http, Path.Combine(_root, "unreal.json")));

        var snapshot = await catalog.LoadAsync();

        Assert.Equal(["A Plague Tale: Requiem", "Abiotic Factor", "Hades II"], snapshot.Entries.Select(e => e.Name));
        Assert.Equal("https://www.nexusmods.com/aplaguetalerequiem/mods/50", snapshot.Entries[0].DownloadUrl);
        Assert.False(snapshot.RenoDxUnavailable);
        Assert.True(snapshot.LumaUnavailable);
    }

    [Fact]
    public void TheApp_PassesTheUeExtendedDatabaseToTheList()
    {
        using var http = new HttpClient(new GameLinkCatalogueTests.Fake(_ => throw new HttpRequestException("offline test")));
        var wine = new WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        Assert.NotNull(services.RenoDxUnrealDatabase);
        Assert.Same(services.RenoDxUnrealDatabase, services.HdrModsCatalog!.UeExtendedDatabase);
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
