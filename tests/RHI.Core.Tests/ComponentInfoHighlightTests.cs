using System.Collections.ObjectModel;
using System.Net;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Core.Tests;

/// <summary>
/// #210: a component row's ⓘ is highlighted when its dialog has content for the game (upstream <c>HasRealInfoContent</c>:
/// manifest note or wiki/database entry; ReLimiter / Display Commander always), worked out in the background from the shared
/// lists only, and turns highlighted when the lists arrive without the game being selected again.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ComponentInfoHighlightTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-info-highlight-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string ManifestUrl = "https://raw.githubusercontent.com/rhi-tests/manifest/main/manifest.json";

    private static HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == ManifestUrl) return ComponentInfoTests.Text(ComponentInfoTests.Manifest);
        if (url == RenoDxCatalogService.StructuredDbUrl) return ComponentInfoTests.Text(ComponentInfoTests.RenoDxDb);
        if (url == OptiScalerWikiCatalogue.CompatibilityListUrl) return ComponentInfoTests.Text(ComponentInfoTests.WikiHtml);
        if (url == ComponentInfoCatalogue.ReLimiterChangelogUrl) return ComponentInfoTests.Text(ComponentInfoTests.LimiterChangelog);
        if (url == ComponentInfoCatalogue.NexusGraphQlUrl)
            return ComponentInfoTests.Text("""{"data":{"legacyModsByDomain":{"nodes":[{"modId":50,"summary":"Replaces the tonemapper."}]}}}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private ComponentInfoCatalogue Catalogue(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new ComponentInfoCatalogue(client, new UpstreamRhiManifestService(client, ManifestUrl), new RenoDxCatalogService(client), luma: null,
            new OptiScalerWikiCatalogue(client, Path.Combine(_root, "links")), new RenoDxUnrealDatabaseService(client, Path.Combine(_root, "unreal.json")),
            unity: null, new GameLinkCatalogue(client, Path.Combine(_root, "links")));
    }

    private static DetectedGame Game(string name) => new() { Name = name, InstallPath = "/games/" + name };

    private static ComponentRowViewModel Row(ManagedComponentKind kind, ComponentAvailability availability = ComponentAvailability.Available)
        => new(new ManagedComponent { Kind = kind, DisplayName = kind.ToString(), Availability = availability }, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

    [Fact]
    public async Task Highlight_FollowsUpstreamsTiers_FromTheSharedListsOnly()
    {
        var http = new GameLinkCatalogueTests.Fake(Answer);
        var catalogue = Catalogue(http);
        var noted = Game("Plain Game");
        noted.ManifestGameNote = "Manifest says hello.";
        var cases = new (DetectedGame Game, ManagedComponentKind Kind, ComponentInfoHighlight Expected)[]
        {
            (noted, ManagedComponentKind.RenoDX, ComponentInfoHighlight.GameNotes),                       // manifest gameNotes
            (Game("Wiki Game"), ManagedComponentKind.RenoDX, ComponentInfoHighlight.GameNotes),           // the matched mod's notes / page
            (Game("Plain Game"), ManagedComponentKind.RenoDX, ComponentInfoHighlight.None),
            (Game("Nexus Only Game"), ManagedComponentKind.RenoDX, ComponentInfoHighlight.None),          // upstream: no Nexus summary for the highlight
            (Game("Luma Manifest"), ManagedComponentKind.LumaFramework, ComponentInfoHighlight.GameNotes),
            (Game("Luma Noted"), ManagedComponentKind.LumaFramework, ComponentInfoHighlight.GameNotes),
            (Game("Other Luma"), ManagedComponentKind.LumaFramework, ComponentInfoHighlight.None),
            (Game("Code Vein II"), ManagedComponentKind.OptiScaler, ComponentInfoHighlight.GameNotes),    // wiki row under optiScalerWikiNames
            (Game("Opti Manifest"), ManagedComponentKind.OptiScaler, ComponentInfoHighlight.GameNotes),
            (Game("Plain Game"), ManagedComponentKind.OptiScaler, ComponentInfoHighlight.None),
            (Game("Limited Game"), ManagedComponentKind.ReLimiter, ComponentInfoHighlight.GameNotes),
            (Game("Plain Game"), ManagedComponentKind.ReLimiter, ComponentInfoHighlight.Changelog),       // always, as upstream
            (Game("Plain Game"), ManagedComponentKind.DisplayCommander, ComponentInfoHighlight.Changelog),
            (Game("RE Game"), ManagedComponentKind.REFramework, ComponentInfoHighlight.GameNotes),
            (Game("Plain Game"), ManagedComponentKind.REFramework, ComponentInfoHighlight.None),
            (Game("RS Game"), ManagedComponentKind.ReShade, ComponentInfoHighlight.GameNotes),
            (Game("Plain Game"), ManagedComponentKind.ReShade, ComponentInfoHighlight.None),
            (Game("Wiki Game"), ManagedComponentKind.ShortFuse, ComponentInfoHighlight.None),             // no ⓘ dialog at all
        };
        foreach (var (game, kind, expected) in cases)
            Assert.True(expected == await catalogue.HighlightAsync(game, kind), $"{game.Name} / {kind}");

        // Never a per-game request: no Nexus Mods summary, no change log, no UE-Extended comment.
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsoluteUri == ComponentInfoCatalogue.ReLimiterChangelogUrl);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsoluteUri == RenoDxUnrealDatabaseService.LiveUrl);

        // Same tier as the dialog itself (the dialog's Nexus summary aside, which upstream's highlight never reads either).
        foreach (var (game, kind, expected) in cases.Where(c => c.Kind is not (ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander)
                                                               && c.Game.Name != "Nexus Only Game" && ComponentInfoCatalogue.HasInfo(c.Kind)))
            Assert.True((expected != ComponentInfoHighlight.None) == ((await catalogue.ResolveAsync(game, kind)).Source != ComponentInfoSource.Fallback),
                $"{game.Name} / {kind}");
    }

    [Fact]
    public async Task Rows_ArePlainUntilTheListsArrive_ThenHighlighted_WithoutSelectingTheGameAgain()
    {
        var server = new GameLinkCatalogueTests.HeldFake(Answer);
        var presenter = new ComponentInfoPresenter(Catalogue(server)) { UiThread = null };
        var rows = new ObservableCollection<ComponentRowViewModel>();
        DetectedGame? selected = Game("Wiki Game");
        presenter.FollowRows(rows, () => selected);
        presenter.FollowRows(rows, () => selected); // twice: still one subscription
        var renodx = Row(ManagedComponentKind.RenoDX);
        var optiscaler = Row(ManagedComponentKind.OptiScaler);
        var unsupported = Row(ManagedComponentKind.ReShade, ComponentAvailability.Unsupported);
        var changes = new List<string?>();
        renodx.InfoHighlight.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        try
        {
            rows.Add(renodx);
            rows.Add(optiscaler);
            rows.Add(unsupported);
            await server.Asked(ManifestUrl).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(renodx.InfoHighlight.IsGameSpecific); // the lists are still loading: plain
            Assert.Equal("About RenoDX for this game: notes, compatibility and links", renodx.InfoHighlight.Tip);
        }
        finally { server.Release(); }
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(renodx.InfoHighlight.IsGameSpecific); // same rows, no reselect
        Assert.Equal("Notes for this game about RenoDX: notes, compatibility and links", renodx.InfoHighlight.Tip);
        Assert.Equal([nameof(ComponentInfoHighlightState.IsGameSpecific), nameof(ComponentInfoHighlightState.Tip)], changes);
        Assert.False(optiscaler.InfoHighlight.IsGameSpecific); // the wiki has no row for this game
        Assert.False(unsupported.InfoHighlight.IsGameSpecific); // no ⓘ on a row that does not apply

        // Rebuilt rows of the same game (a new analysis) start highlighted from what the session already knows.
        rows.Clear();
        var rebuilt = Row(ManagedComponentKind.RenoDX);
        rows.Add(rebuilt);
        Assert.True(rebuilt.InfoHighlight.IsGameSpecific);
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(rebuilt.InfoHighlight.IsGameSpecific);

        UiLocalizationService.Apply("pt-BR");
        try { Assert.Equal("Notas para este jogo sobre o RenoDX: notas, compatibilidade e links", rebuilt.InfoHighlight.Tip); }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task AnAnswerForAGameNoLongerSelected_LeavesTheRowsAlone()
    {
        var server = new GameLinkCatalogueTests.HeldFake(Answer);
        var presenter = new ComponentInfoPresenter(Catalogue(server)) { UiThread = null };
        var rows = new ObservableCollection<ComponentRowViewModel>();
        DetectedGame? selected = Game("Wiki Game");
        presenter.FollowRows(rows, () => selected);
        var row = Row(ManagedComponentKind.RenoDX);
        try
        {
            rows.Add(row);
            await server.Asked(ManifestUrl).WaitAsync(TimeSpan.FromSeconds(20));
            selected = Game("Plain Game"); // another game was selected while the lists were loading
        }
        finally { server.Release(); }
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(row.InfoHighlight.IsGameSpecific);
    }

    [Fact]
    public async Task AHighlightWorkedOutOffTheUiThread_IsAppliedOnIt()
    {
        var presenter = new ComponentInfoPresenter(Catalogue(new GameLinkCatalogueTests.Fake(Answer)));
        var posted = new List<Action>();
        presenter.UiThread = (() => false, posted.Add);
        var rows = new ObservableCollection<ComponentRowViewModel>();
        presenter.FollowRows(rows, () => Game("Plain Game"));
        var row = Row(ManagedComponentKind.ReLimiter);
        rows.Add(row);
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(row.InfoHighlight.IsGameSpecific); // not yet: waiting for the UI thread
        Assert.Single(posted)();
        Assert.True(row.InfoHighlight.IsGameSpecific);
        Assert.Equal("What is new in ReLimiter from the installed version, and links", row.InfoHighlight.Tip);
    }

    [Fact]
    public async Task FullRefresh_WorksTheHighlightsOutAgain()
    {
        var presenter = new ComponentInfoPresenter(Catalogue(new GameLinkCatalogueTests.Fake(Answer))) { UiThread = null };
        var rows = new ObservableCollection<ComponentRowViewModel>();
        presenter.FollowRows(rows, () => Game("Wiki Game"));
        rows.Add(Row(ManagedComponentKind.RenoDX));
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));
        await presenter.RefreshListsAsync();
        rows.Clear();
        var afterRefresh = Row(ManagedComponentKind.RenoDX);
        rows.Add(afterRefresh);
        Assert.False(afterRefresh.InfoHighlight.IsGameSpecific); // forgotten: plain until worked out again
        await presenter.Highlighting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(afterRefresh.InfoHighlight.IsGameSpecific);
    }
}
