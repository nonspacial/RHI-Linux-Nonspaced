using Avalonia.Controls;
using Avalonia.LogicalTree;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#296, #312): upstream 2.9.4's Settings page — eight sections in its order, Find a setting, the section
/// index, reopening on the last section, Tabs, save on change — and the Show / Hide choice for the HDR and Resolution rows.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SettingsLayoutParityTests
{
    [Fact]
    public void Sections_AreUpstreamsEight_InUpstreamsOrder_WithUpstreamsTitles()
    {
        UiLocalizationService.Apply("en-US");
        // Upstream 2.9.4 MainWindow.xaml: Tag="Section:<Key>" in this order, and the headings each section shows.
        Assert.Equal(["General", "Accounts", "ReShade", "Display", "Dlss", "Driver", "Components", "Tools"], SettingsSections.Order);
        Assert.Equal(["General", "Accounts & Data", "ReShade & RenoDX", "Display & HDR", "DLSS / Streamline", "Driver Settings", "Components", "Tools & Maintenance"],
            SettingsSections.Order.Select(SettingsSections.Title));
        foreach (var key in SettingsSections.Order)
        {
            Assert.DoesNotContain("Set.", SettingsSections.Description(key));
            Assert.DoesNotContain("Set.", SettingsSections.Title(key));
        }
    }

    [Theory]
    [InlineData("Display", "Display")]
    [InlineData("tools", "Tools")]
    [InlineData(" Dlss ", "Dlss")]
    [InlineData("Shaders/Add-ons", "General")] // an old tab name, or a section a later build dropped: General
    [InlineData("", "General")]
    [InlineData(null, "General")]
    public void Restore_OpensTheSectionLeftLast_OrGeneral(string? saved, string expected)
        => Assert.Equal(expected, SettingsSections.Restore(saved));

    private static readonly SettingsSearchEntry[] Page =
    [
        new("Updates", null, "General", "General"),
        new("Components to check", "Choose which components RHI checks for updates.", "General", "General › Updates"),
        new("Overlay key", "Opens the ReShade overlay in game.", "ReShade", "ReShade & RenoDX › Keys, screenshots and brightness"),
        new("Overlay key", "The key that opens the OptiScaler overlay in game.", "Components", "Components › ReLimiter and OptiScaler"),
        new("Browse", "Pick a folder for screenshots", "ReShade", "ReShade & RenoDX › Keys, screenshots and brightness"),
        new("HDR button on game pages", "Show or hide the HDR row on game pages.", "Display", "Display & HDR › Display"),
        new("HDR button on game pages", "the same row's control tooltip, mentioning hotkey", "Display", "Display & HDR › Display"),
    ];

    [Fact]
    public void Find_NamesFirst_ThenTooltips_OneResultPerSetting_InPageOrder()
    {
        var results = SettingsSearch.Match(Page, "overlay");
        // Both "Overlay key" rows (different cards), in page order; their tooltips also say "overlay" but they are one row each.
        Assert.Equal(["ReShade", "Components"], results.Select(r => r.Entry.SectionKey));
        Assert.All(results, r => Assert.Equal("Overlay key", r.Display));

        var byTip = Assert.Single(SettingsSearch.Match(Page, "screenshots"));
        Assert.Equal("Browse — Pick a folder for screenshots", byTip.Display); // found by its tooltip: name — tooltip

        // The label wins over its control's tooltip: one result for the HDR row, not two.
        Assert.Single(SettingsSearch.Match(Page, "HDR button"));
        Assert.Single(SettingsSearch.Match(Page, "hotkey"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Find_ABlankQuery_ShowsThePageAgain(string? query) => Assert.Empty(SettingsSearch.Match(Page, query));

    [Fact]
    public void Find_IgnoresCase_AndTrims() => Assert.Equal(2, SettingsSearch.Match(Page, "  OVERLAY KEY ").Count);

    [Fact]
    public void Find_CountLine_FollowsUpstream()
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal("No settings match \"zzz\"", SettingsSearch.CountText(0, "zzz"));
        Assert.Equal("1 setting matches \"hdr\"", SettingsSearch.CountText(1, " hdr "));
        Assert.Equal("4 settings match \"key\"", SettingsSearch.CountText(4, "key"));
        Assert.Equal("61 settings match \"e\". Showing the first 60; keep typing to narrow it down.", SettingsSearch.CountText(61, "e"));
        UiLocalizationService.Apply("pt-BR");
        try { Assert.Equal("1 configuração corresponde a \"hdr\"", SettingsSearch.CountText(1, "hdr")); }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void Excerpt_KeepsShortTexts_AndCutsLongOnesAroundTheMatch()
    {
        Assert.Equal("short", SettingsSearch.Excerpt("short", "x", 60));
        var text = new string('a', 100) + "needle" + new string('b', 100);
        var cut = SettingsSearch.Excerpt(text, "needle", 60);
        Assert.Contains("needle", cut);
        Assert.StartsWith("…", cut);
        Assert.EndsWith("…", cut);
        Assert.Equal(62, cut.Length); // 60 characters and the two ellipses
    }

    [Fact]
    public void ScrollSpy_HighlightsTheSectionAtTheTop_TheLastAtTheBottom_AndAJumpedToSection()
    {
        (string, double)[] tops = [("General", 0), ("Accounts", 800), ("ReShade", 1200), ("Tools", 3000)];
        Assert.Equal("General", SettingsScrollSpy.Highlight(tops, 0, 600, 3300));
        Assert.Equal("Accounts", SettingsScrollSpy.Highlight(tops, 700, 600, 3300)); // its top is in the top quarter
        Assert.Equal("ReShade", SettingsScrollSpy.Highlight(tops, 1150, 600, 3300));
        Assert.Equal("Tools", SettingsScrollSpy.Highlight(tops, 2700, 600, 3300)); // the bottom: a short last section
        Assert.Equal("Tools", SettingsScrollSpy.Highlight(tops, 2690, 600, 3300, "Tools", 2690)); // jumped to, it stays
        Assert.Equal("ReShade", SettingsScrollSpy.Highlight(tops, 1300, 600, 3300, "Tools", 2690)); // scrolled away since
        Assert.Null(SettingsScrollSpy.Highlight([], 0, 600, 0));
    }

    [Fact]
    public void CentreOffset_PutsTheSettingInTheMiddle_WithinTheScrollableRange()
    {
        Assert.Equal(1000 - (600 - 40) / 2.0, SettingsScrollSpy.CentreOffset(1000, 40, 600, 3000));
        Assert.Equal(0, SettingsScrollSpy.CentreOffset(100, 40, 600, 3000)); // near the top: no negative offset
        Assert.Equal(2400, SettingsScrollSpy.CentreOffset(2950, 40, 600, 3000)); // near the end: the last page
        Assert.Equal(1000, SettingsScrollSpy.CentreOffset(1000, 900, 600, 3000)); // taller than the view: its top
    }

    [Fact]
    public async Task SaveOnChange_AChangedSetting_IsSaved_WithoutASaveButton()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        var scheduled = new List<Action>();
        dialog.UseSaveScheduler(scheduled.Add);
        dialog.Composed();

        var before = Saved(fx).CacheAllShaders;
        dialog.CacheAllShaders = !dialog.CacheAllShaders;
        dialog.VerboseLogging = !dialog.VerboseLogging; // one gesture, one save
        Assert.Single(scheduled);
        Assert.Equal(before, Saved(fx).CacheAllShaders); // not yet
        scheduled[0]();
        Assert.Equal(dialog.CacheAllShaders, Saved(fx).CacheAllShaders);
        Assert.Equal(dialog.VerboseLogging, Saved(fx).VerboseLogging);

        // A preset or version choice is saved the same way.
        dialog.DefaultFgPreset.Selected = dialog.DefaultFgPreset.Items[^1];
        Assert.Equal(2, scheduled.Count);
        scheduled[1]();
        Assert.Equal(dialog.DefaultFgPreset.Value ?? 0, Saved(fx).DefaultFgPreset);
    }

    [Fact]
    public async Task SaveOnChange_AnEditThatChangesNothing_WritesNothing()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        dialog.UseSaveScheduler(run => run());
        dialog.Composed();
        dialog.Status = null;
        var before = File.Exists(fx.Settings.SettingsPath) ? File.ReadAllText(fx.Settings.SettingsPath) : null;
        dialog.SaveSoon(); // e.g. a dropdown opened and closed on the same value
        Assert.Null(dialog.Status);
        Assert.Equal(before, File.Exists(fx.Settings.SettingsPath) ? File.ReadAllText(fx.Settings.SettingsPath) : null);
        dialog.VerboseLogging = !dialog.VerboseLogging; // a real change is written
        Assert.NotNull(dialog.Status);
        Assert.Equal(dialog.VerboseLogging, Saved(fx).VerboseLogging);
    }

    [Fact]
    public async Task Reset_IsNotFollowedByAnotherSave_OfTheResetFields()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        var scheduled = new List<Action>();
        dialog.UseSaveScheduler(scheduled.Add);
        dialog.Composed();
        dialog.ResetCommand.Execute(null);
        Assert.Empty(scheduled);
        Assert.Equal(UiLocalizationService.Text("Vm.SettingsRestoredLinuxDefaults"), dialog.Status);
    }

    [Fact]
    public async Task ReopensWhereLeft_TheSectionInViewIsSavedOnClose_AndTabsIsKept()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        Assert.Equal("General", dialog.LastSection); // first time: General
        dialog.LastSection = "Components"; // the index follows the scroll
        dialog.Tabbed = true;
        dialog.Closing(null);
        var saved = Saved(fx);
        Assert.Equal("Components", saved.SettingsLastSection);
        Assert.True(saved.SettingsTabbed);

        var again = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        Assert.Equal("Components", again.LastSection);
        Assert.True(again.Tabbed);
    }

    [Fact]
    public async Task HdrAndResolutionButtons_HidingOnlyHidesTheRows_TheGamesChoiceStays()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var runtime = fx.Vm.Runtime;
        Assert.True(runtime.ShowHdrSection); // no game selected: the section applies
        Assert.True(runtime.ShowHdrRow);
        var dialog = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        Assert.True(dialog.ShowHdrLaunchButton);
        Assert.True(dialog.ShowResLaunchButton);
        var changed = new List<string?>();
        runtime.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dialog.ShowHdrLaunchButton = false;
        dialog.ShowResLaunchButton = false;
        dialog.FlushSave();

        Assert.False(Saved(fx).ShowHdrLaunchButton);
        Assert.False(Saved(fx).ShowResLaunchButton);
        Assert.False(runtime.ShowHdrRow);
        Assert.False(runtime.ShowResolutionRow);
        Assert.True(runtime.ShowHdrSection); // the launch logic still sees the section: the game's HDR choice applies
        Assert.Contains(nameof(RuntimePanelViewModel.ShowHdrRow), changed);
        Assert.Contains(nameof(RuntimePanelViewModel.ShowResolutionRow), changed);
    }

    [Fact]
    public void Store_KeepsTheNewFields_AndDefaultsShowBothRows()
    {
        using var temp = new TempDir();
        var store = new LinuxSettingsStore(Path.Combine(temp.Path, "settings.json"), Path.Combine(temp.Path, "cache"), Path.Combine(temp.Path, "data"));
        var defaults = store.Load();
        Assert.True(defaults.ShowHdrLaunchButton);
        Assert.True(defaults.ShowResLaunchButton);
        Assert.Equal("", defaults.SettingsLastSection);
        Assert.False(defaults.SettingsTabbed);
        store.Save(new LinuxAppSettings { ShowHdrLaunchButton = false, SettingsLastSection = " Tools ", SettingsTabbed = true });
        var loaded = store.Load();
        Assert.False(loaded.ShowHdrLaunchButton);
        Assert.True(loaded.ShowResLaunchButton);
        Assert.Equal("Tools", loaded.SettingsLastSection);
        Assert.True(loaded.SettingsTabbed);
    }

    [Fact]
    public async Task Page_HoldsTheEightSections_InOrder_FindLocatesRows_AndDestructiveButtonsAreRed()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var model = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        var view = new RHI.Desktop.Views.Dialogs.SettingsDialogView { DataContext = model };
        var all = view.GetLogicalDescendants().ToList();

        var sections = all.OfType<StackPanel>().Select(p => p.Tag as string).Where(t => t?.StartsWith("Section:", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(SettingsSections.Order.Select(k => "Section:" + k), sections);
        Assert.Equal(SettingsSections.Order.Select(SettingsSections.Title),
            all.OfType<Button>().Where(b => b.Name?.StartsWith("SettingsNav", StringComparison.Ordinal) == true).Select(b => b.Content as string));
        Assert.Contains(all.OfType<TextBox>(), b => b.Name == "SettingsFind");
        Assert.Contains(all.OfType<Avalonia.Controls.Primitives.ToggleButton>(), b => b.Name == "SettingsTabsToggle");

        // Find a setting: names in their sections and cards (the context line), tooltips too.
        var overlay = view.Find("Overlay key");
        Assert.Equal(["ReShade", "Components", "Components"], overlay.Select(r => r.Entry.SectionKey)); // ReShade, ReLimiter, OptiScaler
        Assert.StartsWith("ReShade & RenoDX › Keys, screenshots and brightness", overlay[0].Entry.Context);
        Assert.Contains(view.Find("Remove old Nightly"), r => r.Entry.SectionKey == "Components");
        Assert.Contains(view.Find("HDR button on game pages"), r => r.Entry.SectionKey == "Display");
        Assert.Contains(view.Find("Clear cache"), r => r.Entry.SectionKey == "Tools" && r.Entry.Target is Button);
        Assert.Contains(view.Find("Components to check"), r => r.Entry.SectionKey == "General");

        // Upstream's red destructive buttons.
        foreach (var name in new[] { "SettingsReset", "SettingsClearCache" })
            Assert.Contains("remove", all.OfType<Button>().Single(b => b.Name == name).Classes);
        // Every setting row is one layout: a name on the left (a TextBlock in column 0) and its control in column 1.
        var rows = all.OfType<Border>().Where(b => b.Classes.Contains("settingsRow") && b.Child is Grid).Select(b => (Grid)b.Child!).ToList();
        Assert.True(rows.Count > 25);
        Assert.All(rows, g => Assert.IsType<TextBlock>(g.Children[0]));
        Assert.All(rows, g => Assert.Equal(1, Grid.GetColumn(g.Children[1])));
    }

    [Fact]
    public void TooltipSweep_EveryFilterAndSection_HasItsTooltip_InBothLanguages()
    {
        // #302 (upstream 2.8.9): the library filters and the game panel's sections explain themselves.
        foreach (var filter in new[] { "All Games", "Favourites", "Installed", "Unreal", "Unity", "Other", "RenoDX", "Luma", "Hidden" })
        {
            var key = RHI.Desktop.Views.TooltipSweep.FilterTipKey(filter);
            Assert.NotNull(key);
            Assert.NotEqual(key, UiLocalizationService.TextFor("en-US", key!));
            Assert.NotEqual(key, UiLocalizationService.TextFor("pt-BR", key!));
        }
        foreach (var section in new[] { "Runtime", "DlssStreamline", "ReShadeProfile", "ManagedComponents", "Extras" })
            Assert.NotNull(RHI.Desktop.Views.TooltipSweep.SectionTipKey(section));
        Assert.Null(RHI.Desktop.Views.TooltipSweep.FilterTipKey("Collection:x")); // unknown chips keep whatever they have
    }

    [Fact]
    public async Task TooltipSweep_KeepsATooltipAControlAlreadyHas()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var panel = new StackPanel();
        var own = new Button { Classes = { "chip" }, CommandParameter = "Hidden" };
        ToolTip.SetTip(own, "mine");
        var bare = new Button { Classes = { "chip" }, CommandParameter = "Favourites" };
        panel.Children.Add(own);
        panel.Children.Add(bare);
        RHI.Desktop.Views.TooltipSweep.Apply(panel);
        Assert.Equal("mine", ToolTip.GetTip(own));
        Assert.Equal(UiLocalizationService.Text("Tip.FilterFavourites"), ToolTip.GetTip(bare));
    }

    private static LinuxAppSettings Saved(ViewModelFixture fx) => new LinuxSettingsStore(fx.Settings.SettingsPath).Load();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhi-settings-layout-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
