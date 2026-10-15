using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #184: the component rows' ⓘ dialogs — upstream's three tiers (manifest note, wiki/database content, general text), the
/// OptiScaler wiki's compatibility rows under <c>optiScalerWikiNames</c>, Luma's <c>lumaGameNotes</c>, the HDR Gaming
/// Database link, the Nexus Mods summary of a mod published only there, and ReLimiter / Display Commander release notes.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ComponentInfoTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-component-info-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal const string WikiHtml = """
        <html><head><script>alert('never')</script></head><body>
        <table role="table"><thead><tr><th>Game</th><th align="center">Compatibility</th><th align="center">Upscaler <br>Inputs</th>
          <th align="center">OptiPatcher <br>Support</th><th>Notes</th><th align="center">Images</th></tr></thead>
        <tbody>
          <tr><td><a href="007-First-Light">007 First Light</a></td><td>✅</td><td>DLSS, FSR3.1/4</td><td></td>
              <td>FSR3.1 inputs <strong>work</strong> with <code>Non-Linear</code> on</td><td><a href="https://github.com/user-attachments/assets/1"><img src="x.png"></a></td></tr>
          <tr><td>Code Vein 2</td><td>⚠️</td><td>XeSS</td><td>✨</td><td></td><td></td></tr>
          <tr><td><a href="https://evil.example/page">Broken Game</a></td><td>❌</td><td></td><td></td><td>Crashes</td><td></td></tr>
          <tr><td><a href="../../escape">Escape Game</a></td><td>✔️</td><td>DLSS</td><td></td><td></td><td></td></tr>
          <tr><td>007 First Light</td><td>❌</td><td></td><td></td><td>duplicate row</td><td></td></tr>
          <tr><td>Assassin’s Creed Shadows</td><td>?</td><td>DLSS</td><td></td><td></td><td></td></tr>
        </tbody></table>
        <table><tr><th>Setting</th><th>Value</th></tr><tr><td>Not a game</td><td>x</td></tr></table>
        </body></html>
        """;

    [Fact]
    public void OptiScalerWiki_FindsColumnsByHeading_AndKeepsTextAndWikiPagesOnly()
    {
        var entries = OptiScalerWikiCatalogue.Parse(WikiHtml).ToDictionary(e => e.GameName);
        Assert.Equal(["007 First Light", "Code Vein 2", "Broken Game", "Escape Game", "Assassin's Creed Shadows"], entries.Keys);
        var first = entries["007 First Light"];
        Assert.Equal(OptiScalerWikiCatalogue.Working, first.Status);
        Assert.Equal(["DLSS", "FSR3.1/4"], first.Upscalers); // the Upscaler column, not upstream's fixed 4th column (OptiPatcher now)
        Assert.Equal("FSR3.1 inputs work with Non-Linear on", first.Notes); // text only
        Assert.Equal("https://github.com/optiscaler/OptiScaler/wiki/007-First-Light", first.DetailPageUrl);
        Assert.Equal(OptiScalerWikiCatalogue.Partial, entries["Code Vein 2"].Status);
        Assert.Null(entries["Code Vein 2"].Notes);
        Assert.Equal(OptiScalerWikiCatalogue.NotWorking, entries["Broken Game"].Status);
        Assert.Null(entries["Broken Game"].DetailPageUrl); // another host is never offered
        Assert.Null(entries["Escape Game"].DetailPageUrl);
        Assert.Equal(OptiScalerWikiCatalogue.Working, entries["Escape Game"].Status);
        Assert.Equal(OptiScalerWikiCatalogue.Unknown, entries["Assassin's Creed Shadows"].Status);
        Assert.Equal("Alone in the Dark (2024)", OptiScalerWikiCatalogue.WikiNameFor("Alone in the Dark", "Alone in the Dark (2024)"));
        Assert.Equal("Borderlands 4", OptiScalerWikiCatalogue.WikiNameFor("Borderlands® 4", null));
    }

    [Fact]
    public void Changelog_StartsAtTheInstalledVersion_AndKeepsThreeEntries()
    {
        const string changelog = """
            # Changelog
            ## 3.3.5
            - **Fixed** idle timer
            ## v3.3.4 (2026-10-01)
            - Move OSD keybind
            ## 3.3.3
            - Default OSD preset
            ## 3.3.2
            - Older
            ## 3.3.1
            - Oldest
            """;
        Assert.Equal("v3.3.4 (2026-10-01)\n- Move OSD keybind\n\n3.3.3\n- Default OSD preset\n\n3.3.2\n- Older",
            ComponentInfoCatalogue.ChangelogEntries(changelog, "3.3.4.0", 3)!.ReplaceLineEndings("\n"));
        Assert.StartsWith("3.3.5\n- Fixed idle timer", ComponentInfoCatalogue.ChangelogEntries(changelog, "9.9", 3)!.ReplaceLineEndings("\n"));
        Assert.Null(ComponentInfoCatalogue.ChangelogEntries("no versions here", null, 3));
        Assert.DoesNotContain("3.3.1", ComponentInfoCatalogue.ChangelogEntries(changelog, "3.3.4", 3));
    }

    private const string Manifest = """
        {"version": 3,
         "lumaGameNotes": {"Luma Noted": {"notes": "Switch to DX11 with -dx11", "notesUrl": "https://www.nexusmods.com/luma/mods/1", "notesUrlLabel": "Luma Nexus"}},
         "lumaGameInfo": {"Luma Manifest": {"notes": "Luma manifest note"}},
         "optiScalerWikiNames": {"Code Vein II": "Code Vein 2"},
         "optiScalerGameInfo": {"Opti Manifest": {"notes": "OptiScaler manifest note", "notesUrl": "http://insecure.example/x"}},
         "relimiterGameInfo": {"Limited Game": {"notes": "Use the in-game cap", "notesUrl": "https://www.nexusmods.com/limited/mods/3", "notesUrlLabel": "Read"}},
         "displayCommanderGameInfo": {"DC Game": {"notes": "DC note"}},
         "reframeworkGameInfo": {"RE Game": {"notes": "REF note"}}
        }
        """;

    private const string RenoDxDb = """
        [{"name": "Wiki Game", "status": "Done", "author": "A", "snapshotUrl": "https://github.com/a/b/releases/download/snapshot/renodx-wiki.addon64",
          "discussionUrl": "https://github.com/clshortfuse/renodx/discussions/9", "notes": "Turn off in-game sharpening."},
         {"name": "Nexus Only Game", "status": "Done", "author": "B", "snapshotUrl": null, "nexusUrl": "https://www.nexusmods.com/nexusonly/mods/50"},
         {"name": "Discord Only Game", "status": "WIP", "author": "C", "discordUrl": "https://discord.gg/abc"},
         {"name": "Banana Game", "status": "Done", "author": "D", "nexusUrl": "https://gamebanana.com/mods/123"}]
        """;

    private const string Unreal = """
        [{"Name": "Wiki Game", "Status": "Done", "Method": "ini", "Comments": "Set r.HDR.EnableHDROutput=1"},
         {"Name": "Native Game", "Status": "Done", "Method": "native", "Comments": "Needs testing"}]
        """;

    private const string LimiterChangelog = "# Changelog\n## 3.3.5\n- Newest\n## 3.3.4\n- Installed one\n## 3.3.3\n- Older\n## 3.3.2\n- Oldest\n";

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed record Services(ComponentInfoCatalogue Catalogue, GameLinkCatalogueTests.Fake Http, GameLinkCatalogue Links);

    private Services Build(bool offline = false, string? hdrMarkdown = null)
    {
        var http = new GameLinkCatalogueTests.Fake(request =>
        {
            if (offline) throw new HttpRequestException("offline");
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json") return Text(Manifest);
            if (url == RenoDxCatalogService.StructuredDbUrl) return Text(RenoDxDb);
            if (url == RenoDxUnrealDatabaseService.LiveUrl) return Text(Unreal);
            if (url == OptiScalerWikiCatalogue.CompatibilityListUrl) return Text(WikiHtml);
            if (url == ComponentInfoCatalogue.ReLimiterChangelogUrl) return Text(LimiterChangelog);
            if (url == GameLinkCatalogue.HdrDatabaseReadmeUrl && hdrMarkdown is not null) return Text(hdrMarkdown);
            if (url == ComponentInfoCatalogue.NexusGraphQlUrl && request.Method == HttpMethod.Post)
                return Text("""{"data":{"legacyModsByDomain":{"nodes":[{"modId":50,"summary":"Replaces the game's HDR tonemapper."}]}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new HttpClient(http);
        var manifest = new UpstreamRhiManifestService(client, "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json");
        var links = new GameLinkCatalogue(client, Path.Combine(_root, "links"));
        return new Services(new ComponentInfoCatalogue(client, manifest, new RenoDxCatalogService(client), luma: null,
            new OptiScalerWikiCatalogue(client, Path.Combine(_root, "links")), new RenoDxUnrealDatabaseService(client, Path.Combine(_root, "unreal.json")),
            unity: null, links), http, links);
    }

    private static DetectedGame Game(string name) => new() { Name = name, InstallPath = "/games/" + name };

    [Fact]
    public async Task RenoDx_ManifestNoteWins_ThenTheWikiNotesWithTheDatabaseComment_ThenGeneralText()
    {
        var services = Build();
        var noted = Game("Wiki Game");
        noted.ManifestGameNote = "  Manifest says hello.  ";
        noted.ManifestGameNoteUrl = "https://www.nexusmods.com/x/mods/1";
        noted.ManifestGameNoteUrlLabel = "Nexus Mods Page";
        var manifest = await services.Catalogue.ResolveAsync(noted, ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentInfoSource.Manifest, manifest.Source);
        Assert.Equal(["Manifest says hello."], manifest.Texts);
        Assert.False(manifest.GeneralText);
        Assert.Equal(new ComponentInfoLink(ComponentInfoLinkKind.Notes, "https://www.nexusmods.com/x/mods/1", "Nexus Mods Page"), Assert.Single(manifest.Links));

        var wiki = await services.Catalogue.ResolveAsync(Game("Wiki Game"), ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentInfoSource.Wiki, wiki.Source);
        Assert.Equal(["Turn off in-game sharpening.", "Set r.HDR.EnableHDROutput=1"], wiki.Texts);
        Assert.False(wiki.GeneralText);
        Assert.Equal([new ComponentInfoLink(ComponentInfoLinkKind.WikiPage, "https://github.com/clshortfuse/renodx/discussions/9")], wiki.Links);

        var plain = await services.Catalogue.ResolveAsync(Game("Plain Game"), ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentInfoSource.Fallback, plain.Source);
        Assert.True(plain.GeneralText);
        Assert.Empty(plain.Texts);
    }

    [Fact]
    public async Task RenoDx_NativeHdrGame_GetsTheUeExtendedNote_NotTheWiki()
    {
        var services = Build();
        var native = Game("Native Game");
        native.NativeHdrByManifest = true;
        native.UeExtendedByManifest = true;
        var info = await services.Catalogue.ResolveAsync(native, ManagedComponentKind.RenoDX);
        Assert.True(info.NativeHdr);
        Assert.False(info.GeneralText);
        Assert.Equal(["Needs testing"], info.Texts);
        Assert.Equal(ComponentInfoSource.Wiki, info.Source); // upstream highlights native-HDR games

        var nativeWithMod = Game("Wiki Game");
        nativeWithMod.NativeHdrByManifest = true;
        var both = await services.Catalogue.ResolveAsync(nativeWithMod, ManagedComponentKind.RenoDX);
        Assert.True(both.NativeHdr);
        Assert.Equal(["Set r.HDR.EnableHDROutput=1"], both.Texts); // the wiki's technical notes do not apply to native HDR
        Assert.Empty(both.Links);
    }

    [Fact]
    public void NotesRhiWritesItself_AreNotShownAsGameNotes()
    {
        Assert.True(ComponentInfoCatalogue.IsRhiOwnNote("Upstream RHI manifest snapshot override · Unreal"));
        Assert.True(ComponentInfoCatalogue.IsRhiOwnNote("Generic RenoDX Unity addon"));
        Assert.False(ComponentInfoCatalogue.IsRhiOwnNote("Turn off in-game sharpening."));
    }

    [Fact]
    public async Task Databases_ListEveryNamedMod_AndFindUnrealGamesWithoutTrademarks()
    {
        var listing = RenoDxCatalogService.ParseStructuredListing("""
            [{"name": "A", "snapshotUrl": "https://github.com/a/b/releases/download/snapshot/a.addon64"},
             {"name": "B", "nexusUrl": "http://www.nexusmods.com/b/mods/1", "discordUrl": "https://discord.gg/b"},
             {"name": "C", "snapshotUrl": "https://example.org/page.html", "nexusUrl": "https://www.nexusmods.com/c/mods/2", "status": "WIP"}]
            """).ToDictionary(e => e.Name);
        Assert.Equal(["A", "B", "C"], listing.Keys);
        Assert.Null(listing["B"].NexusUrl); // http dropped
        Assert.Equal("https://discord.gg/b", listing["B"].DiscordUrl);
        Assert.Null(listing["C"].SnapshotUrl64); // not an add-on
        Assert.True(listing["C"].IsWorkInProgress);

        var unreal = new RenoDxUnrealDatabaseService(new HttpClient(new GameLinkCatalogueTests.Fake(_ => Text(Unreal))), Path.Combine(_root, "u.json"));
        Assert.Equal("Set r.HDR.EnableHDROutput=1", (await unreal.FindAsync("WIKI GAME™"))!.Comments);
        Assert.Null(await unreal.FindAsync("Other"));
        Assert.Equal(2, (await unreal.GetAsync()).Count);
    }

    [Fact]
    public async Task RenoDx_AModPublishedOnlyOnNexus_ShowsItsNexusSummaryAndLink_AndADiscordOnlyModItsDiscord()
    {
        var services = Build();
        var nexus = await services.Catalogue.ResolveAsync(Game("Nexus Only Game"), ManagedComponentKind.RenoDX);
        Assert.Equal(["Replaces the game's HDR tonemapper."], nexus.Texts);
        Assert.Equal([new ComponentInfoLink(ComponentInfoLinkKind.ModPage, "https://www.nexusmods.com/nexusonly/mods/50")], nexus.Links);
        var query = Assert.Single(services.Http.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("api.nexusmods.com", query.RequestUri!.Host);
        Assert.False(query.Headers.Contains("apikey")); // a public query: no account key is sent

        var discord = await services.Catalogue.ResolveAsync(Game("Discord Only Game"), ManagedComponentKind.RenoDX);
        Assert.True(discord.GeneralText);
        Assert.Equal([new ComponentInfoLink(ComponentInfoLinkKind.Discord, "https://discord.gg/abc")], discord.Links);
    }

    [Fact]
    public async Task RenoDx_AModPageOnGameBanana_IsNamedGameBanana_NotNexusMods()
    {
        var services = Build();
        var banana = await services.Catalogue.ResolveAsync(Game("Banana Game"), ManagedComponentKind.RenoDX);
        Assert.Equal([new ComponentInfoLink(ComponentInfoLinkKind.ModPage, "https://gamebanana.com/mods/123")], banana.Links);
        Assert.DoesNotContain(services.Http.Requests, r => r.Method == HttpMethod.Post); // no Nexus summary for a GameBanana page
        var nexus = await services.Catalogue.ResolveAsync(Game("Nexus Only Game"), ManagedComponentKind.RenoDX);

        Assert.Equal(["Mod page on GameBanana"], ComponentInfoPresenter.Compose(banana, "Banana Game", "RenoDX").Links.Select(l => l.Label));
        Assert.Equal(["Mod page on Nexus Mods"], ComponentInfoPresenter.Compose(nexus, "Nexus Only Game", "RenoDX").Links.Select(l => l.Label));
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal(["Página do mod no GameBanana"], ComponentInfoPresenter.Compose(banana, "Banana Game", "RenoDX").Links.Select(l => l.Label));
            Assert.Equal(["Página do mod no Nexus Mods"], ComponentInfoPresenter.Compose(nexus, "Nexus Only Game", "RenoDX").Links.Select(l => l.Label));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task Links_OpenOnlyTheirKindsSites()
    {
        var listing = RenoDxCatalogService.ParseStructuredListing("""
            [{"name": "Wrong Sites", "nexusUrl": "https://discord.gg/x", "discordUrl": "https://www.nexusmods.com/w/mods/1", "discussionUrl": "https://www.nexusmods.com/w/mods/2"},
             {"name": "Right Sites", "nexusUrl": "https://gamebanana.com/mods/9", "discordUrl": "https://discord.com/channels/1/2", "discussionUrl": "https://github.com/clshortfuse/renodx/discussions/3"}]
            """).ToDictionary(e => e.Name);
        Assert.Null(listing["Wrong Sites"].NexusUrl);
        Assert.Null(listing["Wrong Sites"].DiscordUrl);
        Assert.Null(listing["Wrong Sites"].DiscussionUrl);
        Assert.Equal("https://gamebanana.com/mods/9", listing["Right Sites"].NexusUrl);
        Assert.Equal("https://discord.com/channels/1/2", listing["Right Sites"].DiscordUrl);
        Assert.Equal("https://github.com/clshortfuse/renodx/discussions/3", listing["Right Sites"].DiscussionUrl);
        Assert.Null(RenoDxCatalogService.ParseStructuredDatabase("""
            [{"name": "W", "snapshotUrl": "https://github.com/x/y/releases/download/snapshot/w.addon64", "discussionUrl": "https://discord.gg/w"}]
            """).Single().DiscussionUrl);
        var wiki = RenoDxCatalogService.ParseWiki("""
            <table><tr><th>Name</th><th>Mod</th><th>Status</th><th>Notes</th></tr>
            <tr><td><a href="https://www.nexusmods.com/elsewhere/mods/1">Elsewhere Game</a></td><td><a href="https://github.com/a/b/releases/download/snapshot/renodx-e.addon64">x</a></td><td>✅</td><td></td></tr>
            </table>
            """);
        Assert.Null(Assert.Single(wiki).DiscussionUrl);

        // A manifest note's link: the community sites only.
        var services = Build();
        var noted = Game("Noted Elsewhere");
        noted.ManifestGameNote = "Note";
        noted.ManifestGameNoteUrl = "https://evil.example/x";
        Assert.Empty((await services.Catalogue.ResolveAsync(noted, ManagedComponentKind.RenoDX)).Links);
        noted.ManifestGameNoteUrl = "https://store.steampowered.com/app/1";
        Assert.Equal("https://store.steampowered.com/app/1", Assert.Single((await services.Catalogue.ResolveAsync(noted, ManagedComponentKind.RenoDX)).Links).Url);

        Assert.Same(LinkHosts.ModPage, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.ModPage));
        Assert.Same(LinkHosts.Discord, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.Discord));
        Assert.Same(LinkHosts.HdrDatabase, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.HdrDatabase));
        Assert.Same(LinkHosts.Wiki, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.WikiPage));
        Assert.Same(LinkHosts.Releases, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.Releases));
        Assert.Same(LinkHosts.ManifestNote, ComponentInfoCatalogue.HostsFor(ComponentInfoLinkKind.Notes));
    }

    [Fact]
    public async Task RenoDxAndLuma_LinkTheHdrGamingDatabase_WhichMakesGeneralTextCountAsGameContent()
    {
        var services = Build(hdrMarkdown: "* [Plain Game](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/77)\n");
        await services.Links.LoadAsync();
        var info = await services.Catalogue.ResolveAsync(Game("Plain Game"), ManagedComponentKind.RenoDX);
        Assert.Equal(ComponentInfoSource.Wiki, info.Source);
        Assert.Equal(new ComponentInfoLink(ComponentInfoLinkKind.HdrDatabase, "https://github.com/KoKlusz/HDR-Gaming-Database/discussions/77"), Assert.Single(info.Links));
        var luma = await services.Catalogue.ResolveAsync(Game("Plain Game"), ManagedComponentKind.LumaFramework);
        Assert.Contains(luma.Links, l => l.Kind == ComponentInfoLinkKind.HdrDatabase);
        var opti = await services.Catalogue.ResolveAsync(Game("Plain Game"), ManagedComponentKind.OptiScaler);
        Assert.DoesNotContain(opti.Links, l => l.Kind == ComponentInfoLinkKind.HdrDatabase); // RenoDX and Luma only
    }

    [Fact]
    public async Task Luma_UsesLumaGameInfo_ThenLumaGameNotesWithTheirLink()
    {
        var services = Build();
        var manifest = await services.Catalogue.ResolveAsync(Game("Luma Manifest"), ManagedComponentKind.LumaFramework);
        Assert.Equal(ComponentInfoSource.Manifest, manifest.Source);
        Assert.Equal(["Luma manifest note"], manifest.Texts);

        var noted = await services.Catalogue.ResolveAsync(Game("Luma Noted"), ManagedComponentKind.LumaFramework);
        Assert.Equal(ComponentInfoSource.Wiki, noted.Source);
        Assert.Equal(["Switch to DX11 with -dx11"], noted.Texts);
        Assert.Equal(new ComponentInfoLink(ComponentInfoLinkKind.Notes, "https://www.nexusmods.com/luma/mods/1", "Luma Nexus"), Assert.Single(noted.Links));

        var canonical = Game("LUMA NOTED (Steam)");
        canonical.CanonicalManifestName = "Luma Noted"; // the manifest's own spelling of the game
        Assert.Equal(["Switch to DX11 with -dx11"], (await services.Catalogue.ResolveAsync(canonical, ManagedComponentKind.LumaFramework)).Texts);
        Assert.True((await services.Catalogue.ResolveAsync(Game("Other Luma"), ManagedComponentKind.LumaFramework)).GeneralText);
    }

    [Fact]
    public async Task OptiScaler_ReadsTheWikiUnderTheManifestsWikiName_AndKeepsOnlyHttpsManifestLinks()
    {
        var services = Build();
        var info = await services.Catalogue.ResolveAsync(Game("Code Vein II"), ManagedComponentKind.OptiScaler);
        Assert.Equal(ComponentInfoSource.Wiki, info.Source);
        Assert.Equal("Code Vein 2", info.Compatibility!.GameName);
        Assert.Null(info.Fsr4Compatibility);
        Assert.False(info.GeneralText);
        var trademark = await services.Catalogue.ResolveAsync(Game("007 First Light™"), ManagedComponentKind.OptiScaler);
        Assert.Equal([new ComponentInfoLink(ComponentInfoLinkKind.WikiPage, "https://github.com/optiscaler/OptiScaler/wiki/007-First-Light")], trademark.Links);
        var quotes = await services.Catalogue.ResolveAsync(Game("Assassin's Creed Shadows"), ManagedComponentKind.OptiScaler);
        Assert.NotNull(quotes.Compatibility);

        var manifest = await services.Catalogue.ResolveAsync(Game("Opti Manifest"), ManagedComponentKind.OptiScaler);
        Assert.Equal(["OptiScaler manifest note"], manifest.Texts);
        Assert.Empty(manifest.Links); // http link dropped
    }

    [Fact]
    public async Task ReLimiterAndDisplayCommander_ShowReleaseNotesFromTheInstalledVersion_ElseTheirDescription()
    {
        var services = Build();
        var info = await services.Catalogue.ResolveAsync(Game("Any Game"), ManagedComponentKind.ReLimiter, "3.3.4");
        Assert.StartsWith("3.3.4", info.Changelog);
        Assert.Contains("3.3.2", info.Changelog);
        Assert.DoesNotContain("3.3.5", info.Changelog);
        Assert.False(info.GeneralText);
        Assert.Equal(new ComponentInfoLink(ComponentInfoLinkKind.Releases, ComponentInfoCatalogue.ReLimiterReleasesUrl), Assert.Single(info.Links));

        var dc = await services.Catalogue.ResolveAsync(Game("Any Game"), ManagedComponentKind.DisplayCommander, "1.0");
        Assert.Null(dc.Changelog); // its change log is not served here
        Assert.True(dc.GeneralText);
        Assert.Equal(ComponentInfoCatalogue.DisplayCommanderReleasesUrl, Assert.Single(dc.Links).Url);

        var limited = await services.Catalogue.ResolveAsync(Game("Limited Game"), ManagedComponentKind.ReLimiter, "3.3.4");
        Assert.Equal(ComponentInfoSource.Manifest, limited.Source);
        Assert.Equal(["Use the in-game cap"], limited.Texts);
        Assert.Equal(["https://www.nexusmods.com/limited/mods/3", ComponentInfoCatalogue.ReLimiterReleasesUrl], limited.Links.Select(l => l.Url));
        Assert.Equal(["DC note"], (await services.Catalogue.ResolveAsync(Game("DC Game"), ManagedComponentKind.DisplayCommander)).Texts);
        Assert.Equal(["REF note"], (await services.Catalogue.ResolveAsync(Game("RE Game"), ManagedComponentKind.REFramework)).Texts);
    }

    [Fact]
    public async Task EverySourceOffline_StillGivesTheGeneralText()
    {
        var services = Build(offline: true);
        foreach (var kind in new[] { ManagedComponentKind.RenoDX, ManagedComponentKind.LumaFramework, ManagedComponentKind.OptiScaler,
                     ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander, ManagedComponentKind.REFramework, ManagedComponentKind.ReShade })
        {
            var info = await services.Catalogue.ResolveAsync(Game("Wiki Game"), kind, "3.3.4");
            Assert.True(info.GeneralText, kind.ToString());
            Assert.Empty(info.Texts);
        }
    }

    [Fact]
    public void Dialog_IsWordedInTheUiLanguage()
    {
        var info = new ComponentInfo(ManagedComponentKind.OptiScaler, ComponentInfoSource.Wiki, ["Remote text"], true, true,
            new OptiScalerCompatEntry("G", OptiScalerWikiCatalogue.Partial, ["DLSS", "XeSS"], "Note", null),
            new OptiScalerCompatEntry("G", OptiScalerWikiCatalogue.Working, [], null, null), "1.0\n- x",
            [new(ComponentInfoLinkKind.WikiPage, "https://github.com/optiscaler/OptiScaler/wiki/G"), new(ComponentInfoLinkKind.Notes, "https://x.example/n")]);
        var view = ComponentInfoPresenter.Compose(info, "Game", "OptiScaler");
        Assert.Equal("OptiScaler for Game", view.Title);
        Assert.Equal(
        [
            UiLocalizationService.Text("Cat.InfoNativeHdr"), "OptiScaler compatibility: working with issues", "Upscaler inputs: DLSS, XeSS", "Note",
            "FSR4 compatibility: working", "Upscaler inputs: none listed", "Remote text", "Release notes", "1.0\n- x",
            UiLocalizationService.Text("Cat.InfoAboutOptiScaler")
        ], view.Blocks.Select(b => b.Text));
        Assert.True(view.Blocks[1].IsHeading);
        Assert.Equal(["View wiki page", "Open link"], view.Links.Select(l => l.Label));

        UiLocalizationService.Apply("pt-BR");
        try
        {
            var pt = ComponentInfoPresenter.Compose(info, "Jogo", "OptiScaler");
            Assert.Equal("OptiScaler em Jogo", pt.Title);
            Assert.Equal("Compatibilidade com o OptiScaler: funciona com problemas", pt.Blocks[1].Text);
            Assert.Equal("Ver página no wiki", pt.Links[0].Label);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Theory]
    [InlineData(ManagedComponentKind.RenoDX, ComponentAvailability.Available, true)]
    [InlineData(ManagedComponentKind.LumaFramework, ComponentAvailability.Installed, true)]
    [InlineData(ManagedComponentKind.OptiScaler, ComponentAvailability.UpdateAvailable, true)]
    [InlineData(ManagedComponentKind.ReLimiter, ComponentAvailability.Available, true)]
    [InlineData(ManagedComponentKind.DisplayCommander, ComponentAvailability.Available, true)]
    [InlineData(ManagedComponentKind.REFramework, ComponentAvailability.Available, true)]
    [InlineData(ManagedComponentKind.ReShade, ComponentAvailability.Installed, true)]
    [InlineData(ManagedComponentKind.RenoDX, ComponentAvailability.Unsupported, false)] // a row that does not apply: no ⓘ
    [InlineData(ManagedComponentKind.ShortFuse, ComponentAvailability.Available, false)]
    [InlineData(ManagedComponentKind.Dlss, ComponentAvailability.Installed, false)]
    public void InfoButton_OnlyOnUpstreamsInfoComponents_WhereTheRowApplies(ManagedComponentKind kind, ComponentAvailability availability, bool expected)
    {
        var row = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = kind.ToString(), Availability = availability },
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));
        Assert.Equal(expected, row.HasInfo);
        Assert.Equal($"About {kind} for this game: notes, compatibility and links", row.InfoTip);
    }

    [Fact]
    public async Task Manifest_BindsLumaGameNotes_OptiScalerWikiNames_AndEachComponentsGameInfo()
    {
        var client = new HttpClient(new GameLinkCatalogueTests.Fake(_ => Text(Manifest)));
        var policy = await new UpstreamRhiManifestService(client, "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json").FetchLibraryPolicyAsync();
        Assert.Equal("Switch to DX11 with -dx11", policy.LumaGameNotes["luma noted"].Notes);
        Assert.Equal("Luma Nexus", policy.LumaGameNotes["Luma Noted"].UrlLabel);
        Assert.Equal("Code Vein 2", policy.OptiScalerWikiNames["code vein ii"]);
        Assert.Equal("Use the in-game cap", policy.ComponentGameNotes[ComponentNoteKeys.ReLimiter]["Limited Game"].Notes);
        Assert.Equal("DC note", policy.ComponentGameNotes[ComponentNoteKeys.DisplayCommander]["DC Game"].Notes);
        Assert.Equal("REF note", policy.ComponentGameNotes[ComponentNoteKeys.ReFramework]["RE Game"].Notes);
        Assert.Equal("OptiScaler manifest note", policy.ComponentGameNotes[ComponentNoteKeys.OptiScaler]["Opti Manifest"].Notes);
        Assert.Equal("Luma manifest note", policy.ComponentGameNotes[ComponentNoteKeys.Luma]["Luma Manifest"].Notes);
        Assert.Contains("Code Vein II", policy.KnownGameNames);
    }

    [Fact]
    public async Task Downloads_StayOnTheirHosts_AndAPostIsCapped()
    {
        Assert.True(DownloadSources.OptiScalerWiki.IsTrusted(OptiScalerWikiCatalogue.CompatibilityListUrl));
        Assert.False(DownloadSources.OptiScalerWiki.IsTrusted("https://raw.githubusercontent.com/optiscaler/x"));
        Assert.Equal(8L * 1024 * 1024, DownloadSources.OptiScalerWiki.MaxBytes);
        Assert.True(DownloadSources.ComponentNotes.IsTrusted(ComponentInfoCatalogue.ReLimiterChangelogUrl));
        Assert.True(DownloadSources.ComponentNotes.IsTrusted(RenoDxUnrealDatabaseService.LiveUrl));
        Assert.False(DownloadSources.ComponentNotes.IsTrusted("https://github.com/RankFTW/ReLimiter"));
        await Assert.ThrowsAsync<UntrustedDownloadException>(() => TrustedDownload.PostJsonAsync(new HttpClient(new GameLinkCatalogueTests.Fake(_ => Text("{}"))),
            "https://api.nexusmods.com.evil.example/v2/graphql", DownloadSources.NexusApi, "{}"));
        var redirected = new GameLinkCatalogueTests.Fake(_ =>
        {
            var response = Text("{}");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://evil.example/graphql");
            return response;
        });
        await Assert.ThrowsAsync<UntrustedDownloadException>(() => TrustedDownload.PostJsonAsync(new HttpClient(redirected),
            ComponentInfoCatalogue.NexusGraphQlUrl, DownloadSources.NexusApi, "{}"));
        var big = new GameLinkCatalogueTests.Fake(_ => Text(new string('x', 17 * 1024 * 1024)));
        await Assert.ThrowsAsync<InvalidDataException>(() => TrustedDownload.PostJsonAsync(new HttpClient(big), ComponentInfoCatalogue.NexusGraphQlUrl, DownloadSources.NexusApi, "{}"));
    }

    [Fact]
    public void TheApp_WiresTheInfoDialogs()
    {
        using var http = new HttpClient(new GameLinkCatalogueTests.Fake(_ => throw new HttpRequestException("offline test")));
        var wine = new WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        Assert.NotNull(services.Parity!.ComponentInfo);
    }
}
