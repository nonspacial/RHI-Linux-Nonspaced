using System.Xml.Linq;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-a (#303 #304 #306): upstream 2.9.0–2.9.4's window layouts. The layout switch (Classic+ | Tabs | Spotlight),
/// the Tabs layout's fixed tab order and remembered tab, the sections and their order, the top bar's Help menu, "Checked …
/// ago" and Update All count, the interface / top bar / game list sizes, the game list's main filter switch, and the
/// Management section's actions.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LayoutsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-layouts-{Guid.NewGuid():N}");

    public LayoutsTests()
    {
        UiLocalizationService.Apply("en-US");
        // w14/layouts-b (#305) turned Spotlight on; these tests keep checking the switch's "not available" path.
        GameLayoutPresenter.SpotlightAvailable = false;
    }

    public void Dispose()
    {
        GameLayoutPresenter.SpotlightAvailable = true;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private (GameLayoutPresenter Layout, LinuxSettingsStore Store, LinuxAppSettings Settings) NewLayout(LinuxAppSettings? start = null)
    {
        var store = new LinuxSettingsStore(Path.Combine(_root, "config", "settings.json"), Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        var settings = start ?? new LinuxAppSettings();
        store.Save(settings);
        string? Save(Action<LinuxAppSettings> change)
        {
            var copy = store.Load();
            change(copy);
            store.Save(copy);
            return null;
        }
        return (new GameLayoutPresenter(() => settings, Save), store, settings);
    }

    // ---- #303 the layout switch ------------------------------------------------------------------------------------

    [Fact]
    public void Switch_OffersClassicPlusTabsSpotlight_InUpstreamsOrder_ClassicPlusByDefault_SpotlightAPlaceholder()
    {
        var (layout, _, _) = NewLayout();
        Assert.Equal(["Classic+", "Tabs", "Spotlight"], layout.Choices.Select(c => c.Label));
        Assert.Equal(MainLayout.Classic, layout.Layout);
        Assert.True(layout.Choices[0].IsSelected);
        var spotlight = layout.Choices[2];
        Assert.False(spotlight.IsEnabled);
        Assert.Equal("Spotlight comes in a later update of RHI Linux.", spotlight.Tip);
        Assert.Contains("Classic+: Every section of the game page in one long page", layout.SwitchTip);
        Assert.Contains("Tabs: An Overview, then one tab per section", layout.SwitchTip);
    }

    [Fact]
    public void ChoosingTabs_ShowsAndSavesIt_SpotlightIsRefusedWhileAPlaceholder()
    {
        var (layout, store, _) = NewLayout();
        var changed = 0;
        layout.Changed += () => changed++;

        Assert.Null(layout.SetLayout(MainLayout.Tabs));
        Assert.Equal("Tabs", store.Load().MainLayout);
        Assert.True(layout.IsTabs);
        Assert.Equal(1, changed);

        Assert.Equal("Spotlight comes in a later update of RHI Linux.", layout.SetLayout(MainLayout.Spotlight));
        Assert.Equal("Tabs", store.Load().MainLayout);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ASavedSpotlight_OpensClassicPlus_UntilSpotlightIsAvailable_AndIsKept()
    {
        var (layout, store, _) = NewLayout(new LinuxAppSettings { MainLayout = "Spotlight" });
        Assert.Equal(MainLayout.Spotlight, layout.Saved);
        Assert.Equal(MainLayout.Classic, layout.Layout);
        Assert.Equal("Spotlight", store.Load().MainLayout);

        GameLayoutPresenter.SpotlightAvailable = true; // the layouts-b lane (#305)
        Assert.Equal(MainLayout.Spotlight, layout.Layout);
        Assert.True(layout.Choices[2].IsEnabled);
    }

    [Theory]
    [InlineData("tabs", "Tabs")]
    [InlineData("Classic", "Classic")]
    [InlineData("Grid", "Classic")]
    [InlineData(null, "Classic")]
    public void MainLayout_IsNormalised(string? saved, string expected)
        => Assert.Equal(expected, new LinuxAppSettings { MainLayout = saved! }.Normalize().MainLayout);

    // ---- #304 Tabs -------------------------------------------------------------------------------------------------

    [Fact]
    public void Tabs_FollowUpstreamsFixedOrder_AndNames()
    {
        var (layout, _, _) = NewLayout();
        Assert.Equal(["Overview", "Game Overrides", "Extras", "DLSS / Streamline", "Driver Settings", "Neural Rendering", "Management"],
            layout.Tabs.Select(t => t.Title));
        Assert.Equal(GamePageSections.Overview, layout.SelectedTab);
        UiLocalizationService.Apply("pt-BR");
        Assert.Equal(["Visão geral", "Substituições do jogo", "Extras", "DLSS / Streamline", "Configurações do driver", "Renderização Neural", "Gerenciamento"],
            layout.Tabs.Select(t => t.Title));
    }

    [Fact]
    public void TheTabYouWereOn_IsRemembered()
    {
        var (layout, store, settings) = NewLayout(new LinuxAppSettings { MainLayout = "Tabs" });
        Assert.Null(layout.SelectTab(GamePageSections.Extras));
        Assert.Equal("Extras", store.Load().DetailSelectedTab);

        var again = new GameLayoutPresenter(() => store.Load(), _ => null);
        Assert.Equal(GamePageSections.Extras, again.SelectedTab);
        Assert.True(again.Tabs.Single(t => t.Key == GamePageSections.Extras).IsSelected);

        layout.SelectTab(GamePageSections.Overview);
        Assert.Equal("", store.Load().DetailSelectedTab);
        Assert.Equal("", settings.DetailSelectedTab);
    }

    [Theory]
    [InlineData("", GamePageSections.Overview)]
    [InlineData("Nonsense", GamePageSections.Overview)]
    [InlineData("ManagedComponents", GamePageSections.Overview)] // Overview shows the components
    [InlineData("management", GamePageSections.Management)]
    public void ASavedTab_OpensItsTab_OrOverview(string saved, string expected)
    {
        var (layout, _, _) = NewLayout(new LinuxAppSettings { MainLayout = "Tabs", DetailSelectedTab = saved });
        Assert.Equal(expected, layout.SelectedTab);
    }

    [Fact]
    public void ClassicPlus_ShowsEverySection_Tabs_OnlyTheTabsOwn()
    {
        var (layout, _, _) = NewLayout();
        Assert.All(DetailSectionLayoutStore.DefaultSectionOrder, key => Assert.True(layout.IsSectionShown(key)));
        Assert.True(layout.ShowsSectionHeaders);
        Assert.False(layout.CogsOpenInline);

        layout.SetLayout(MainLayout.Tabs);
        Assert.Equal([GamePageSections.Components], DetailSectionLayoutStore.DefaultSectionOrder.Where(layout.IsSectionShown));
        Assert.True(layout.ShowsOverview);
        Assert.False(layout.ShowsSectionHeaders);
        Assert.True(layout.CogsOpenInline);

        layout.ShowSection(GamePageSections.Driver); // an At a glance tile
        Assert.Equal(GamePageSections.Driver, layout.SelectedTab);
        Assert.Equal([GamePageSections.Driver], DetailSectionLayoutStore.DefaultSectionOrder.Where(layout.IsSectionShown));
        Assert.False(layout.ShowsOverview);
    }

    [Fact]
    public void EveryTabAfterOverview_IsASection_AndHasAnAtAGlanceTile()
    {
        Assert.Equal(GamePageSections.TabOrder.Skip(1), GamePageSections.GlanceSections);
        Assert.All(GamePageSections.GlanceSections, key => Assert.Contains(key, DetailSectionLayoutStore.DefaultSectionOrder));
        Assert.Equal(DetailSectionLayoutStore.DefaultSectionOrder.OrderBy(k => k),
            GamePageSections.TabOrder.SelectMany(GamePageSections.SectionsOnTab).OrderBy(k => k));
    }

    // ---- #303/#304 the sections and their order --------------------------------------------------------------------

    [Fact]
    public void Sections_AreUpstreamsSeven_InClassicPlusDefaultOrder()
    {
        Assert.Equal(["Components", "Game Overrides", "Neural Rendering", "DLSS / Streamline", "Driver Settings", "Management", "Extras"],
            DetailSectionLayoutStore.DefaultSectionOrder.Select(GamePageSections.Title));
    }

    [Fact]
    public void AnOrderNeverDragged_OpensInUpstreamsOrder()
    {
        var normalised = DetailSectionLayoutStore.Normalize(new DetailSectionLayoutSettings
        {
            SectionOrder = [.. DetailSectionLayoutStore.PreviousDefaultSectionOrder]
        });
        Assert.Equal(DetailSectionLayoutStore.DefaultSectionOrder, normalised.SectionOrder);
    }

    [Fact]
    public void ADraggedOrder_IsKept_TheReShadeProfileBecomesNeuralRendering_NewSectionsGoWhereUpstreamPutsThem()
    {
        var normalised = DetailSectionLayoutStore.Normalize(new DetailSectionLayoutSettings
        {
            SectionOrder = ["Extras", "ReShadeProfile", "Runtime", "ManagedComponents", "DlssStreamline"],
            CollapsedSections = ["ReShadeProfile"]
        });
        Assert.Equal(["Extras", "NeuralRendering", "Runtime", "ManagedComponents", "DlssStreamline", "DriverSettings", "Management"],
            normalised.SectionOrder);
        Assert.Equal(["NeuralRendering"], normalised.CollapsedSections);
    }

    // ---- #303 the top bar -----------------------------------------------------------------------------------------

    [Fact]
    public void HelpMenu_HasUpstreamsEntries_InItsOrderAndGroups()
    {
        Assert.Equal(
            ["Quick Start", "Guide", "Discord", "|", "RenoDX Wiki", "Luma Wiki", "RHI Linux GitHub", "RHI GitHub", "ReLimiter GitHub",
             "|", "Patch notes", "Message of the day", "Update log", "About", "Donate"],
            TopBarPresenter.HelpMenu.SelectMany(i => i.SeparatorBefore ? new[] { "|", i.Text } : [i.Text]));
        Assert.Equal("https://github.com/clshortfuse/renodx/wiki/Mods", TopBarPresenter.HelpMenu.Single(i => i.Action == HelpAction.RenoDxWiki).Url);
        Assert.All(TopBarPresenter.HelpMenu.Where(i => i.Url is not null), i => Assert.StartsWith("https://", i.Url));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(18, "18m ago")]
    [InlineData(125, "2h 5m ago")]
    [InlineData(60 * 24 * 3 + 7, "3d ago")]
    public void Checked_SaysHowLongAgo_AsUpstream(int minutes, string expected)
        => Assert.Equal(expected, TopBarPresenter.Ago(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Checked_IsEmptyBeforeTheFirstCheck_ThenFollowsTheClock()
    {
        DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? last = null;
        var bar = new TopBarPresenter(() => now, () => last, () => []);
        Assert.Equal("", bar.CheckedText);
        last = now.AddMinutes(-18);
        Assert.Equal("Checked 18m ago", bar.CheckedText);
        UiLocalizationService.Apply("pt-BR");
        Assert.Equal("Verificado há 18min", bar.CheckedText);
    }

    [Fact]
    public void UpdateAllCount_CountsGamesWithUpdates_ThatUpdateAllCovers_AndStartsAgainAfterUpdateAll()
    {
        var covered = new HashSet<string> { "a", "b" };
        var bar = new TopBarPresenter(() => DateTimeOffset.UtcNow, () => null, () => covered);
        Assert.False(bar.HasUpdateCount);
        Assert.Equal("Updates the components RHI installed across your library (hidden games are left out).", bar.UpdateAllTip);

        bar.NoteGame("a", hasUpdate: true);
        bar.NoteGame("hidden", hasUpdate: true); // not covered (hidden)
        Assert.Equal(1, bar.UpdateCount);
        Assert.Equal("1", bar.UpdateCountText);
        bar.NoteGame("b", hasUpdate: true);
        Assert.Equal(2, bar.UpdateCount);
        bar.NoteGame("b", hasUpdate: false); // updated since
        Assert.Equal(1, bar.UpdateCount);
        Assert.StartsWith("1 game(s) have updates.", bar.UpdateAllTip); // w14/layouts-c: the library-wide check, not only games seen

        bar.UpdateAllRan();
        Assert.Equal(0, bar.UpdateCount);
        Assert.False(bar.HasUpdateCount);
    }

    // ---- #306 sizes -------------------------------------------------------------------------------------------------

    [Fact]
    public void Sizes_Are50To200_In5PercentSteps()
    {
        Assert.Equal(31, InterfaceSize.Options.Count);
        Assert.Equal(50, InterfaceSize.Options[0]);
        Assert.Equal(200, InterfaceSize.Options[^1]);
        Assert.All(InterfaceSize.Options.Zip(InterfaceSize.Options.Skip(1)), p => Assert.Equal(5, p.Second - p.First));
        Assert.Equal("100% (normal)", InterfaceSize.Label(100));
        Assert.Equal("85%", InterfaceSize.Label(85));
    }

    [Theory]
    [InlineData(100, true, 105)]
    [InlineData(100, false, 95)]
    [InlineData(67, true, 70)]   // a dragged size steps to the next one on the list
    [InlineData(67, false, 65)]
    [InlineData(200, true, 200)]
    [InlineData(50, false, 50)]
    public void CtrlWheel_StepsToTheNextSize(int current, bool larger, int expected)
        => Assert.Equal(expected, InterfaceSize.Next(current, larger));

    [Theory]
    [InlineData(100, 50, 25, 150)]  // the top bar dragged half its height taller
    [InlineData(100, 300, -150, 50)]
    [InlineData(150, 300, 600, 200)] // clamped
    [InlineData(120, 0, 40, 120)]    // nothing measured yet: unchanged
    public void DraggingAnEdge_ScalesBySizeMoved(int start, double extent, double delta, int expected)
        => Assert.Equal(expected, InterfaceSize.FromDrag(start, extent, delta));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(10, 50)]
    [InlineData(500, 200)]
    [InlineData(67, 67)]
    public void SavedSizes_AreClamped(int saved, int expected)
    {
        var settings = new LinuxAppSettings { UiScale = saved, TopBarScale = saved, GameListScale = saved }.Normalize();
        Assert.Equal((expected, expected, expected), (settings.UiScale, settings.TopBarScale, settings.GameListScale));
        Assert.Equal(65, InterfaceSize.Nearest(67));
        Assert.Equal(300 * 1.5, InterfaceSize.GameListWidth(150));
    }

    // ---- view-model wiring ------------------------------------------------------------------------------------------

    private static DetectedGame Game(string root, string name, bool manual = false) => new()
    {
        Name = name, InstallPath = Directory.CreateDirectory(Path.Combine(root, name)).FullName, Source = manual ? "Manual" : "Steam",
        IsManuallyAdded = manual, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12,
    };

    private static DetectedGame Library(ViewModelFixture fx, string name) => fx.Vm.AllLibraryGames.Single(g => g.Name == name);

    [Fact]
    public async Task Sizes_AreSavedOnTheirOwn_CtrlWheelSaysTheNewSize_AndASettingsSaveKeepsThem()
    {
        using var fx = await ViewModelFixture.StartAsync([Game(_root, "Sized Game")]);
        var raised = 0;
        fx.Vm.SizesChanged += () => raised++;

        fx.Vm.StepUiScale(larger: true);
        Assert.Equal(105, fx.Vm.UiScale);
        Assert.StartsWith("Interface size 105%", fx.Vm.StatusText); // a pending notice may follow
        Assert.Null(fx.Vm.SetTopBarScale(80));
        Assert.Null(fx.Vm.SetGameListScale(130));
        Assert.Equal(3, raised);
        var saved = fx.Settings.Load();
        Assert.Equal((105, 80, 130), (saved.UiScale, saved.TopBarScale, saved.GameListScale));

        fx.Vm.Layout.SetLayout(MainLayout.Tabs);
        fx.Vm.Layout.SelectTab(GamePageSections.Management);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        var written = dialog.Read();
        Assert.Equal(("Tabs", "Management", 105, 80, 130),
            (written.MainLayout, written.DetailSelectedTab, written.UiScale, written.TopBarScale, written.GameListScale));
    }

    [Fact]
    public async Task GameListSwitch_LightsTheMainFilter_NoneForACollectionOrAFilterBehindFilters()
    {
        using var fx = await ViewModelFixture.StartAsync([Game(_root, "Switch Game")]);
        Assert.Equal(["All", "Installed", "Favourites", "Hidden"], MainWindowViewModel.LibraryMainFilters.Select(f => UiLocalizationService.Text(f.LabelKey)));
        Assert.Equal(0, fx.Vm.LibraryMainFilterIndex);
        Assert.Equal("Filters", fx.Vm.LibraryFiltersText);

        fx.Vm.ChooseLibraryMainFilter(2);
        Assert.Equal("Favourites", fx.Vm.LibraryFilterMode);
        Assert.Equal(2, fx.Vm.LibraryMainFilterIndex);

        fx.Vm.SetLibraryFilterCommand.Execute("Unreal");
        Assert.Equal(-1, fx.Vm.LibraryMainFilterIndex);
        Assert.True(fx.Vm.LibraryFilterIsUnreal);
        Assert.Equal("Filters (1)", fx.Vm.LibraryFiltersText);
        Assert.Equal("Filters (1 on): engine and mod filters, and your saved searches", fx.Vm.LibraryFiltersTip);

        fx.Vm.ChooseLibraryMainFilter(0);
        Assert.Equal("Filters", fx.Vm.LibraryFiltersText);
        Assert.False(fx.Vm.LibraryFilterIsUnreal);

        Assert.True(fx.Vm.Collections.Create("Racing", fx.Vm.SelectedGame).Done);
        fx.Vm.Collections.ToggleCommand.Execute("Racing");
        Assert.Equal(-1, fx.Vm.LibraryMainFilterIndex);
    }

    [Fact]
    public async Task Management_ResetInstallFolder_OnlyForADetectedGameWithAChangedFolder_RemoveFromRhiForEveryGame()
    {
        using var fx = await ViewModelFixture.StartAsync([Game(_root, "Detected Game"), Game(_root, "Hand Added", manual: true)]);
        fx.Vm.SelectedGame = Library(fx, "Detected Game");
        Assert.False(fx.Vm.CanResetInstallFolder);
        Assert.True(fx.Vm.ShowResetInstallFolder);
        Assert.Equal("This game uses the folder RHI detected; there is nothing to reset.", fx.Vm.ResetInstallFolderTip);
        Assert.True(fx.Vm.CanRemoveFromRhiAny);
        Assert.Equal("Take this game out of RHI for good; undo in Settings", fx.Vm.RemoveFromRhiDesc);

        fx.Vm.SelectedGame = Library(fx, "Hand Added");
        Assert.False(fx.Vm.CanResetInstallFolder);
        Assert.False(fx.Vm.ShowResetInstallFolder);
        Assert.Equal("You added this game yourself, so there's no detected folder to go back to. Use Change install folder.", fx.Vm.ResetInstallFolderTip);
        Assert.True(fx.Vm.CanRemoveFromRhiAny);
        Assert.Equal("Remove this game you added yourself", fx.Vm.RemoveFromRhiDesc);
    }

    [Fact]
    public async Task ResetOverrides_PutsTheForcedApiAndUpdateInclusionBack_AndSaysWhat()
    {
        using var fx = await ViewModelFixture.StartAsync([Game(_root, "Override Game")]);
        var game = Library(fx, "Override Game");
        fx.Vm.SelectedGame = game;
        Assert.Equal("Override Game had no overrides.", await fx.Vm.ResetOverridesCoreAsync(game));

        fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.ForDisplay("DirectX 11");
        Assert.Equal(GraphicsApi.DirectX11, game.UserGraphicsApiOverride);
        var inclusion = fx.Vm.UpdatePolicy?.Inclusion;
        inclusion?.SetExcluded(game, [ManagedComponentKind.ReShade], [ManagedComponentKind.ReShade]);

        var line = await fx.Vm.ResetOverridesCoreAsync(game);

        Assert.Null(game.UserGraphicsApiOverride);
        Assert.Null(new GraphicsApiOverrideStore(Path.Combine(fx.ConfigDir, "rhi-linux", "game-api-overrides.json")).Get(game));
        if (inclusion is not null)
        {
            Assert.Empty(inclusion.ExcludedFor(game));
            Assert.Equal("Override Game: overrides reset (forced API, Update All inclusion).", line);
        }
        else Assert.Equal("Override Game: overrides reset (forced API).", line);
        Assert.Equal("Override Game had no overrides.", await fx.Vm.ResetOverridesCoreAsync(game));
    }

    // ---- the window ------------------------------------------------------------------------------------------------

    private static XDocument MainWindowXaml()
        => XDocument.Load(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml"));

    private static string? Name(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    [Fact]
    public void TopBar_HasUpstreamsItems_InItsOrder_AndTheOldToolbarButtonsAreGone()
    {
        var row = MainWindowXaml().Descendants().Single(e => Name(e) == "ToolbarRow");
        Assert.Equal(
            ["LibraryToggleButton", "TopBarBrand", "NavAvailableMods", "NavShadersAddons", "TopBarCheckedText", "TopBarRefresh", "TopBarUpdateAll",
             "LayoutSwitch", "TopBarHelp", "ToolbarOverflowButton", "TopBarSettings"],
            row.Elements().Select(Name));
        var xaml = MainWindowXaml().ToString();
        foreach (var gone in new[] { "Ui.BatchDlss", "ViewToggleText", "Links_Click", "Ui.QuickStart}", "Ui.Motd}", "Ui.Donate}" })
            Assert.DoesNotContain(gone, xaml);
    }

    [Fact]
    public void EverySection_HasItsContainerBodyAndToggle_AndManagementHasUpstreamsActionsInOrder()
    {
        var named = MainWindowXaml().Descendants().Where(e => Name(e) is not null).ToDictionary(e => Name(e)!);
        foreach (var key in DetailSectionLayoutStore.DefaultSectionOrder)
        {
            Assert.Equal(key, (string?)named[key + "Section"].Attribute("Tag"));
            Assert.True(named.ContainsKey(key + "SectionBody"), key);
            Assert.True(named.ContainsKey(key + "SectionToggle"), key);
        }
        Assert.Equal(["ManagementChangeFolder", "ManagementResetFolder", "ManagementRemove", "ManagementResetOverrides", "ManagementCopyReport"],
            named["ManagementActions"].Elements().Select(Name));
        // Remove from RHI left the Game settings ⚙ (Management holds it, as upstream); Block Steam updates stays there.
        Assert.DoesNotContain("RemoveFromRhi", File.ReadAllText(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "GameSettingsView.cs")));
    }

    [Fact]
    public void TheDisplayModeSwitch_SitsBesideHdrAndResolution_InClassicPlusAndTabs()
    {
        // Upstream 2.9.3/2.9.4: the display mode buttons sit beside the HDR and resolution buttons in every layout (#311).
        var named = MainWindowXaml().Descendants().Where(e => Name(e) is not null).ToDictionary(e => Name(e)!);
        var classic = named["DisplayModeSwitch"];
        Assert.Contains(classic, named["RuntimeSectionBody"].Descendants());
        Assert.Equal("11", (string?)classic.Attribute("Grid.Row")); // the row under the resolution row (10)
        var tabs = named["TabsDisplayModeSwitch"];
        Assert.Contains(tabs, named["TabsHeader"].Descendants());
        var row = named["TabsResolution"].Parent!;
        Assert.Contains(tabs, row.Descendants());
        Assert.Contains(named["TabsHdrMode"], row.Descendants());
        Assert.Equal(["DisplayModeSwitch", "TabsDisplayModeSwitch"], MainWindow.SwitchHosts);
    }
}
