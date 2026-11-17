using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>#303 (upstream 2.9.0 <c>MainLayout</c>): the game page layouts, saved by name.</summary>
public enum MainLayout
{
    /// <summary>Classic+: the game list, and the game page as collapsible, reorderable sections.</summary>
    Classic,
    /// <summary>Tabs: the same game page one tab at a time (Overview, then a tab per section).</summary>
    Tabs,
    /// <summary>Spotlight: artwork, covers and slide-in sections. Built by the layouts-b lane (#305).</summary>
    Spotlight,
}

/// <summary>
/// w14/layouts-a (#303/#304): the game page's sections, by key, and how the layouts place them. Every layout shows the same
/// section containers (Classic+ stacks them, Tabs shows one tab at a time, Spotlight slides one in), so a section is built
/// once and a new row in it works in every layout. The keys are the ones the Linux section store saves.
/// </summary>
/// <summary>
/// The Management section's actions (by control name) and where they go. Upstream 2.9.4 orders them differently in its two
/// looks (DetailPanelBuilder.Overrides.Dxvk.cs): the Classic+ row is Change install folder, Reset install folder, Remove
/// from RHI, Reset Overrides, Copy Report, with Remove and Reset Overrides red; the Tabs tiles (Tabs and Spotlight) are
/// Change install folder, Reset Overrides, Copy Report, Reset install folder, Remove from RHI, with only Remove red.
/// </summary>
public static class ManagementActions
{
    public const string ChangeFolder = "ManagementChangeFolder";
    public const string ResetFolder = "ManagementResetFolder";
    public const string Remove = "ManagementRemove";
    public const string ResetOverrides = "ManagementResetOverrides";
    public const string CopyReport = "ManagementCopyReport";

    public static IReadOnlyList<string> Order(bool tiles) => tiles
        ? [ChangeFolder, ResetOverrides, CopyReport, ResetFolder, Remove]
        : [ChangeFolder, ResetFolder, Remove, ResetOverrides, CopyReport];

    /// <summary>Shown red (upstream's AccentRed buttons).</summary>
    public static bool IsDanger(string action, bool tiles) => action == Remove || (!tiles && action == ResetOverrides);

    /// <summary>Reset install folder for a game added by hand: greyed in the Classic+ row, left out of the tiles (upstream).</summary>
    public static bool ShowsResetFolder(bool manuallyAdded, bool tiles) => !manuallyAdded || !tiles;
}

public static class GamePageSections
{
    /// <summary>The Tabs layout's first tab (component cards and At a glance tiles); not a section.</summary>
    public const string Overview = "Overview";
    public const string Components = "ManagedComponents";
    public const string GameOverrides = "Runtime";
    public const string NeuralRendering = "NeuralRendering";
    public const string Dlss = "DlssStreamline";
    public const string Driver = "DriverSettings";
    public const string Management = "Management";
    public const string Extras = "Extras";

    /// <summary>Upstream's fixed tab order (DetailLayoutHost.FixedTabOrder), separate from Classic+'s drag order.</summary>
    public static readonly IReadOnlyList<string> TabOrder = [Overview, GameOverrides, Extras, Dlss, Driver, NeuralRendering, Management];

    /// <summary>The sections the At a glance tiles summarise (every tab after Overview), in tab order.</summary>
    public static IReadOnlyList<string> GlanceSections => TabOrder.Skip(1).ToArray();

    /// <summary>The localisation key of a section's (or tab's) title, upstream's names.</summary>
    public static string TitleKey(string key) => key switch
    {
        Overview => "Sec.Overview",
        Components => "Sec.Components",
        GameOverrides => "Sec.GameOverrides",
        NeuralRendering => "Sec.NeuralRendering",
        Dlss => "Sec.Dlss",
        Driver => "Sec.Driver",
        Management => "Sec.Management",
        Extras => "Sec.Extras",
        _ => key,
    };

    public static string Title(string key) => UiLocalizationService.Text(TitleKey(key));

    /// <summary>The sections a tab shows: Overview shows the Components section (as cards), every other tab its own section.</summary>
    public static IReadOnlyList<string> SectionsOnTab(string tab) => tab == Overview ? [Components] : [tab];

    /// <summary>The tab a section is on.</summary>
    public static string TabOf(string section) => section == Components ? Overview : section;
}

/// <summary>One choice on the top bar's layout switch.</summary>
public sealed record LayoutChoice(MainLayout Layout, string Label, string Tip, bool IsEnabled, bool IsSelected);

/// <summary>One tab of the Tabs layout's strip.</summary>
public sealed record GameTab(string Key, string Title, bool IsSelected);

/// <summary>
/// w14/layouts-a (#303/#304): the layout switch (Classic+ | Tabs | Spotlight) and the Tabs layout's remembered tab. It says
/// which sections show; the window arranges them. Spotlight is a disabled placeholder until the layouts-b lane builds it
/// (<see cref="SpotlightAvailable"/>); a saved Spotlight choice opens Classic+ meanwhile and is kept for later.
/// </summary>
public sealed partial class GameLayoutPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly Func<LinuxAppSettings> _settings;
    private readonly Func<Action<LinuxAppSettings>, string?> _save;

    /// <param name="settings">The settings in use.</param>
    /// <param name="save">Saves a change to the layout fields; the localised refusal, or null.</param>
    public GameLayoutPresenter(Func<LinuxAppSettings> settings, Func<Action<LinuxAppSettings>, string?> save)
    {
        _settings = settings;
        _save = save;
    }

    /// <summary>Whether Spotlight can be chosen (on since the layouts-b lane, #305; the tests can turn it off).</summary>
    public static bool SpotlightAvailable { get; set; } = true;

    /// <summary>Raised after the layout or the tab changed (the window re-arranges the page).</summary>
    public event Action? Changed;

    /// <summary>The saved layout.</summary>
    public MainLayout Saved => Enum.TryParse<MainLayout>(_settings().MainLayout, ignoreCase: true, out var layout) ? layout : MainLayout.Classic;

    /// <summary>The layout shown: the saved one, or Classic+ while the saved one is not available yet.</summary>
    public MainLayout Layout => Saved == MainLayout.Spotlight && !SpotlightAvailable ? MainLayout.Classic : Saved;

    public bool IsTabs => Layout == MainLayout.Tabs;

    /// <summary>w14/layouts-b (#305): Spotlight is shown.</summary>
    public bool IsSpotlight => Layout == MainLayout.Spotlight;

    /// <summary>The Tabs header and card look: Tabs, and Spotlight (its banner holds the Tabs header, upstream).</summary>
    public bool UsesTabsChrome => IsTabs || IsSpotlight;

    /// <summary>
    /// w14/layouts-b (#305): what Spotlight's side panel shows — a section's key, <see cref="GamePageSections.Overview"/> for the
    /// component cards, or a built panel's key (<see cref="SpotlightPresenter"/>); null while it is closed. Not saved.
    /// </summary>
    public string? PanelKey { get; private set; }

    /// <summary>Opens Spotlight's side panel on <paramref name="key"/> (only in Spotlight).</summary>
    public void OpenPanel(string key)
    {
        if (!IsSpotlight || PanelKey == key) return;
        PanelKey = key;
        Notify();
    }

    /// <summary>Closes Spotlight's side panel.</summary>
    public void ClosePanel()
    {
        if (PanelKey is null) return;
        PanelKey = null;
        Notify();
    }

    /// <summary>The switch's three choices, in upstream's order, with their tooltips.</summary>
    public IReadOnlyList<LayoutChoice> Choices =>
    [
        new(MainLayout.Classic, "Classic+", T("Layout.ClassicTip"), true, Layout == MainLayout.Classic),
        new(MainLayout.Tabs, "Tabs", T("Layout.TabsTip"), true, Layout == MainLayout.Tabs),
        new(MainLayout.Spotlight, "Spotlight", SpotlightAvailable ? T("Layout.SpotlightTip") : T("Layout.SpotlightSoonTip"),
            SpotlightAvailable, Layout == MainLayout.Spotlight),
    ];

    /// <summary>The switch's tooltip: every layout and what it is.</summary>
    public string SwitchTip => string.Join("\n", Choices.Select(c => $"{c.Label}: {c.Tip}"));

    /// <summary>Saves and shows <paramref name="layout"/>. A layout not available yet changes nothing. The refusal, or null.</summary>
    public string? SetLayout(MainLayout layout)
    {
        if (layout == MainLayout.Spotlight && !SpotlightAvailable) return T("Layout.SpotlightSoonTip");
        if (layout == Saved) return null;
        PanelKey = null; // the side panel is Spotlight's
        var name = layout.ToString();
        if (_save(s => s.MainLayout = name) is { } refused) return refused;
        _settings().MainLayout = name;
        Notify();
        return null;
    }

    /// <summary>The Tabs layout's tab: the remembered one, Overview when none (or an unknown one) is saved.</summary>
    public string SelectedTab => _settings().DetailSelectedTab is { Length: > 0 } saved
        ? GamePageSections.TabOrder.FirstOrDefault(t => t.Equals(GamePageSections.TabOf(saved), StringComparison.OrdinalIgnoreCase)) ?? GamePageSections.Overview
        : GamePageSections.Overview;

    /// <summary>The tab strip, in upstream's order.</summary>
    public IReadOnlyList<GameTab> Tabs
    {
        get
        {
            var selected = SelectedTab;
            return GamePageSections.TabOrder.Select(key => new GameTab(key, GamePageSections.Title(key), key == selected)).ToArray();
        }
    }

    /// <summary>Shows a tab and remembers it (upstream: RHI remembers which tab you were on). The refusal, or null.</summary>
    public string? SelectTab(string key)
    {
        var tab = GamePageSections.TabOrder.FirstOrDefault(t => t.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (tab is null || tab == SelectedTab) return null;
        var saved = tab == GamePageSections.Overview ? "" : tab;
        if (_save(s => s.DetailSelectedTab = saved) is { } refused) return refused;
        _settings().DetailSelectedTab = saved;
        Notify();
        return null;
    }

    /// <summary>
    /// Opens the tab a section is on (an At a glance tile, or a ⚙ that belongs to another tab); in Spotlight, the section in
    /// the side panel (a Tune this game tile, upstream).
    /// </summary>
    public string? ShowSection(string section)
    {
        if (!IsSpotlight) return SelectTab(GamePageSections.TabOf(section));
        OpenPanel(GamePageSections.TabOf(section));
        return null;
    }

    /// <summary>Whether a section shows: always in Classic+; in Tabs only on its tab; in Spotlight only in the side panel.</summary>
    public bool IsSectionShown(string section)
    {
        if (IsSpotlight) return PanelKey is { } open && GamePageSections.SectionsOnTab(open).Contains(section, StringComparer.OrdinalIgnoreCase);
        return !IsTabs || GamePageSections.SectionsOnTab(SelectedTab).Contains(section, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a section's header (≡, title, collapse arrow) shows: Classic+ only; the tab (or the side panel) names it.</summary>
    public bool ShowsSectionHeaders => !UsesTabsChrome;

    /// <summary>Whether the Overview tab (cards and At a glance tiles) shows.</summary>
    public bool ShowsOverview => IsTabs && SelectedTab == GamePageSections.Overview;

    /// <summary>Whether a ⚙ opens on the page, under its component (Tabs), or in its own window (Classic+, as upstream).</summary>
    public bool CogsOpenInline => IsTabs;

    /// <summary>The settings were saved or reset elsewhere: show what they hold now.</summary>
    public void Reload() => Notify();

    private void Notify()
    {
        OnPropertyChanged(string.Empty);
        Changed?.Invoke();
    }
}

/// <summary>
/// #306 (upstream 2.9.0–2.9.3 Interface size, Top bar size, Game list size): sizes in percent, 50–200 in 5% steps, as
/// Settings offers them and Ctrl + mouse wheel steps through them. Dragging an edge can land between the steps.
/// </summary>
public static class InterfaceSize
{
    public const int Min = 50, Max = 200, Normal = 100, StepPercent = 5;

    /// <summary>Every 5% from 50 to 200 (upstream <c>UiScaleOptions</c>).</summary>
    public static readonly IReadOnlyList<int> Options = Enumerable.Range(0, (Max - Min) / StepPercent + 1).Select(i => Min + i * StepPercent).ToArray();

    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>The option shown for a saved size that is off the list (a dragged 67% shows as 65%).</summary>
    public static int Nearest(int percent) => Options.OrderBy(p => Math.Abs(p - percent)).ThenBy(p => p).First();

    /// <summary>The next size up or down from <paramref name="current"/> (Ctrl + mouse wheel), which may be off the list.</summary>
    public static int Next(int current, bool larger) => larger
        ? Options.FirstOrDefault(p => p > current, Max)
        : Options.LastOrDefault(p => p < current, Min);

    /// <summary>The size an edge drag gives: the start size scaled by how far the edge moved, clamped (upstream ChromeSizes).</summary>
    public static int FromDrag(int startPercent, double startExtent, double delta)
        => startExtent <= 0 || !double.IsFinite(delta) ? Clamp(startPercent) : Clamp((int)Math.Round(startPercent * (startExtent + delta) / startExtent));

    /// <summary>A size as the Settings lists show it: "100% (normal)", "85%".</summary>
    public static string Label(int percent) => percent == Normal ? UiLocalizationService.Text("Size.Normal") : $"{percent}%";

    /// <summary>The game list's width at a size (300 px at 100%, as upstream).</summary>
    public static double GameListWidth(int percent) => 300 * Clamp(percent) / 100.0;
}
