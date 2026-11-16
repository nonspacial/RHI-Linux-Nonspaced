using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// w14/layouts-a (#303 #304 #306): upstream 2.9.0–2.9.4's window layouts. The layout switch and the Tabs layout's tab
// (GameLayoutPresenter), the top bar's "Checked … ago" and Update All count (TopBarPresenter), the interface, top bar and
// game list sizes, and the Management section's actions. The layout fields are saved on their own (as the colour theme):
// the file's settings are read, changed and written, so a Settings save keeps the values in use.
public partial class MainWindowViewModel
{
    private GameLayoutPresenter? _layout;
    private TopBarPresenter? _topBar;

    /// <summary>#303/#304: the layout switch and the Tabs layout's remembered tab.</summary>
    public GameLayoutPresenter Layout => _layout ??= new GameLayoutPresenter(() => _settings, SaveLayoutSettings);

    /// <summary>#303: the top bar's "Checked … ago" and the Update All count.</summary>
    public TopBarPresenter TopBar => _topBar ??= new TopBarPresenter(
        () => Updates?.Now() ?? DateTimeOffset.UtcNow,
        () => Updates?.LastPass,
        () => UnattendedLibraryGames.Select(LinuxGameLibraryStore.GameKey).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Saves only the layout fields (layout, tab, sizes): the file's settings are read, changed and written; the ones in use
    /// take the change once it is on disk. The localised refusal (nothing changed), or null.
    /// </summary>
    private string? SaveLayoutSettings(Action<LinuxAppSettings> change)
    {
        var settings = _settingsStore.Load();
        change(settings);
        var refused = TryPersist(() => _settingsStore.Save(settings), "Ui.WhatSettings", sessionOnly: false, reportStatus: false);
        if (refused is not null) return refused;
        settings.Normalize();
        _settings.MainLayout = settings.MainLayout;
        _settings.DetailSelectedTab = settings.DetailSelectedTab;
        _settings.UiScale = settings.UiScale;
        _settings.TopBarScale = settings.TopBarScale;
        _settings.GameListScale = settings.GameListScale;
        return null;
    }

    // ---- #306 sizes -----------------------------------------------------------------------------------------------

    /// <summary>The interface size in percent (50–200).</summary>
    public int UiScale => _settings.UiScale;
    public int TopBarScale => _settings.TopBarScale;
    public int GameListScale => _settings.GameListScale;

    /// <summary>Raised after a size changed (the window applies it).</summary>
    public event Action? SizesChanged;

    /// <summary>Sets the interface size (Settings, Ctrl + mouse wheel). The refusal, or null.</summary>
    public string? SetUiScale(int percent) => SetSize(InterfaceSize.Clamp(percent), _settings.UiScale, s => s.UiScale = InterfaceSize.Clamp(percent));

    /// <summary>Sets the top bar size (Settings, the bottom edge's drag; double-click resets). The refusal, or null.</summary>
    public string? SetTopBarScale(int percent) => SetSize(InterfaceSize.Clamp(percent), _settings.TopBarScale, s => s.TopBarScale = InterfaceSize.Clamp(percent));

    /// <summary>Sets the game list size (Settings, the right edge's drag; double-click resets). The refusal, or null.</summary>
    public string? SetGameListScale(int percent) => SetSize(InterfaceSize.Clamp(percent), _settings.GameListScale, s => s.GameListScale = InterfaceSize.Clamp(percent));

    /// <summary>Ctrl + mouse wheel: one step larger or smaller; the status bar says the new size (upstream).</summary>
    public void StepUiScale(bool larger)
    {
        var next = InterfaceSize.Next(_settings.UiScale, larger);
        if (next == _settings.UiScale) return;
        if (SetUiScale(next) is { } refused) { StatusText = refused; return; }
        StatusText = TF("Size.Status", next);
    }

    private string? SetSize(int percent, int current, Action<LinuxAppSettings> change)
    {
        if (percent == current) return null;
        if (SaveLayoutSettings(change) is { } refused) return refused;
        OnPropertyChanged(nameof(UiScale));
        OnPropertyChanged(nameof(TopBarScale));
        OnPropertyChanged(nameof(GameListScale));
        SizesChanged?.Invoke();
        return null;
    }

    /// <summary>Settings were saved or reset: the layout and sizes show what they hold now.</summary>
    private void AdoptLayoutSettings()
    {
        // The UI language may have changed: the texts built here follow it.
        NotifyLibraryFilterSwitch();
        NotifyManagement();
        _topBar?.Tick();
        OnPropertyChanged(nameof(NavAvailableModsTip));
        _layout?.Reload();
        OnPropertyChanged(nameof(UiScale));
        OnPropertyChanged(nameof(TopBarScale));
        OnPropertyChanged(nameof(GameListScale));
        SizesChanged?.Invoke();
    }

    // ---- #303 Update All count --------------------------------------------------------------------------------------

    /// <summary>A game's analysis was published: whether any of its components has an update (the top bar's count).</summary>
    private void NoteAnalysisUpdates(DetectedGame game, GameAnalysis analysis)
        => TopBar.NoteGame(LinuxGameLibraryStore.GameKey(game), analysis.Components.Any(c => c.Availability == ComponentAvailability.UpdateAvailable));

    // ---- #304 Management (upstream Classic+ order: Change install folder, Reset install folder, Remove from RHI,
    //      Reset Overrides, Copy Report) ------------------------------------------------------------------------------

    /// <summary>Reset install folder applies: a detected game whose folders were changed with Edit game.</summary>
    public bool CanResetInstallFolder => SelectedGame is { IsManuallyAdded: false } game && !IsBusy && HasGameEdit(game);

    public string ResetInstallFolderTip => SelectedGame switch
    {
        { IsManuallyAdded: true } => T("Mgmt.ResetFolderManualTip"),
        { } game when !HasGameEdit(game) => T("Mgmt.ResetFolderNothingTip"),
        _ => T("Mgmt.ResetFolderTip"),
    };

    /// <summary>Upstream shows Reset install folder only for detected games (Tabs tiles).</summary>
    public bool ShowResetInstallFolder => SelectedGame is { IsManuallyAdded: false };

    private bool HasGameEdit(DetectedGame game) => _libraryState.GameEdits.ContainsKey(LinuxGameLibraryStore.GameKey(game));

    /// <summary>Remove from RHI for every game: a game added by hand is removed (its entry), a detected game is put on the removed list.</summary>
    public bool CanRemoveFromRhiAny => SelectedGame is { } game && (game.IsManuallyAdded ? ManualGames.CanRemoveManualGame : CanRemoveFromRhi);

    public string RemoveFromRhiAnyTip => SelectedGame is { IsManuallyAdded: true } ? T("Mgmt.RemoveManualTip") : RemoveFromRhiTip;

    public string RemoveFromRhiDesc => SelectedGame is { IsManuallyAdded: true } ? T("Mgmt.RemoveManualDesc") : T("Mgmt.RemoveDesc");

    [RelayCommand]
    private void RemoveFromRhiAny()
    {
        if (SelectedGame is not { } game) return;
        if (game.IsManuallyAdded) ManualGames.RemoveManualGameCommand.Execute(null);
        else RemoveFromRhiCommand.Execute(null);
    }

    /// <summary>Copy Report: the card menu's Copy game report, for the selected game.</summary>
    [RelayCommand]
    private Task CopyReportAsync() => SelectedGame is { } game ? Support.ReportCommand.ExecuteAsync(game) : Task.CompletedTask;

    /// <summary>Set by the window: asks before Reset Overrides (title, question, button); true = reset.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmResetOverridesAsync { get; set; }

    public bool CanResetOverrides => SelectedGame is not null && !IsBusy;

    /// <summary>
    /// #304 (upstream Management › Reset Overrides): this game's overrides go back to the defaults: the forced graphics API,
    /// Luma mode, DLL names (#196 Reset DLL names), the Update All inclusion, the NVIDIA settings and the launch arguments
    /// (w14/layouts-c). HDR and resolution on
    /// launch stay, as upstream. Nothing installed is removed; a DLL name reset renames installed files through its own
    /// journaled path. The status line says what was reset.
    /// </summary>
    [RelayCommand]
    private async Task ResetOverridesAsync()
    {
        if (SelectedGame is not { } game) return;
        if (ConfirmResetOverridesAsync is { } confirm
            && !await confirm(T("Mgmt.ResetOverridesConfirmTitle"), TF("Mgmt.ResetOverridesConfirm", game.Name), T("Mgmt.ResetOverridesButton")))
            return;
        StatusText = await ResetOverridesCoreAsync(game);
    }

    /// <summary>The reset itself (tests call it without the question); the status line.</summary>
    internal async Task<string> ResetOverridesCoreAsync(DetectedGame game)
    {
        var done = new List<string>();
        var failed = new List<string>();
        void Step(string labelKey, Func<bool> reset)
        {
            try { if (reset()) done.Add(T(labelKey)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RHI.Platform.Linux.Persistence.JsonStoreUnavailableException)
            {
                failed.Add($"{T(labelKey)} ({UiLocalizationService.Present(ex.Message)})");
            }
        }

        Step("Mgmt.ForcedApi", () =>
        {
            if (game.UserGraphicsApiOverride is null) return false;
            Runtime.SelectedGraphicsApiOverride = Runtime.GraphicsApiOverrideOptions.For(null);
            return game.UserGraphicsApiOverride is null;
        });
        Step("Mgmt.LumaMode", () =>
        {
            if (_managedComponents.GetLumaPolicy(game).UserPreference == LumaModePreference.Auto) return false;
            Runtime.SelectedLumaModePreference = Runtime.LumaModeOptions.For(LumaModePreference.Auto);
            return _managedComponents.GetLumaPolicy(game).UserPreference == LumaModePreference.Auto;
        });
        Step("Mgmt.UpdateInclusion", () =>
        {
            if (UpdatePolicy?.Inclusion is not { } inclusion || inclusion.IsUnreadable) return false;
            var excluded = inclusion.ExcludedFor(game);
            if (excluded.Count == 0) return false;
            inclusion.SetExcluded(game, excluded, []);
            return true;
        });
        Step("Mgmt.NvidiaSettings", () => Runtime.ResetNvidiaSettings(game));
        Step("Mgmt.LaunchArguments", () => // w14/layouts-c (upstream ResetAction clears LaunchArgsOverrides too)
        {
            if (LaunchArguments.Saved(game) is null) return false;
            return LaunchArguments.Save(game, null) is { } refused ? throw new IOException(refused) : true;
        });
        if (DllNamePicker.CanReset)
        {
            try
            {
                await DllNamePicker.ResetCommand.ExecuteAsync(null);
                done.Add(T("Mgmt.DllNames"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{T("Mgmt.DllNames")} ({UiLocalizationService.Present(ex.Message)})");
            }
        }

        if (failed.Count > 0)
            return TF("Mgmt.ResetOverridesPartial", game.Name, done.Count > 0 ? string.Join(", ", done) : "—", string.Join("; ", failed));
        return done.Count == 0 ? TF("Mgmt.ResetOverridesNothing", game.Name) : TF("Mgmt.ResetOverridesDone", game.Name, string.Join(", ", done));
    }

    private void NotifyManagement()
    {
        NotifyHeader();
        OnPropertyChanged(nameof(CanResetInstallFolder));
        OnPropertyChanged(nameof(ResetInstallFolderTip));
        OnPropertyChanged(nameof(ShowResetInstallFolder));
        OnPropertyChanged(nameof(CanRemoveFromRhiAny));
        OnPropertyChanged(nameof(RemoveFromRhiAnyTip));
        OnPropertyChanged(nameof(RemoveFromRhiDesc));
        OnPropertyChanged(nameof(CanResetOverrides));
    }

    // ---- #303 the game list's main filter switch and Filters button --------------------------------------------------

    /// <summary>Upstream's All | Installed | Favourites | Hidden, in its order (the library filter modes they set).</summary>
    public static readonly IReadOnlyList<(string Mode, string LabelKey)> LibraryMainFilters =
        [("All Games", "Ui.All"), ("Installed", "Ui.Installed"), ("Favourites", "Ui.Favourites"), ("Hidden", "Ui.Hidden")];

    /// <summary>The engine and mod filters behind Filters (one at a time on Linux: choosing one replaces the main filter).</summary>
    public static readonly IReadOnlyList<string> LibraryExtraFilters = ["Unreal", "Unity", "Other", "RenoDX", "Luma"];

    /// <summary>The lit segment: the main filter in use; none while a collection or an engine / mod filter is shown.</summary>
    public int LibraryMainFilterIndex
    {
        get
        {
            if (Collections.ActiveName is not null) return -1;
            for (var i = 0; i < LibraryMainFilters.Count; i++)
                if (string.Equals(LibraryMainFilters[i].Mode, LibraryFilterMode, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>A segment was clicked: its filter (which also leaves a collection, #300).</summary>
    public void ChooseLibraryMainFilter(int index)
    {
        if (index < 0 || index >= LibraryMainFilters.Count) return;
        SetLibraryFilterCommand.Execute(LibraryMainFilters[index].Mode);
    }

    /// <summary>How many filters behind Filters are on (upstream "Filters (N)").</summary>
    public int LibraryExtraFilterCount
        => Collections.ActiveName is null && LibraryExtraFilters.Contains(LibraryFilterMode, StringComparer.OrdinalIgnoreCase) ? 1 : 0;

    public string LibraryFiltersText => LibraryExtraFilterCount > 0 ? TF("Lib.FiltersCount", LibraryExtraFilterCount) : T("Lib.Filters");
    public string LibraryFiltersTip => LibraryExtraFilterCount > 0 ? TF("Lib.FiltersOnTip", LibraryExtraFilterCount) : T("Lib.FiltersTip");

    public bool LibraryFilterIsUnreal => IsExtraFilter("Unreal");
    public bool LibraryFilterIsUnity => IsExtraFilter("Unity");
    public bool LibraryFilterIsOther => IsExtraFilter("Other");
    public bool LibraryFilterIsRenoDx => IsExtraFilter("RenoDX");
    public bool LibraryFilterIsLuma => IsExtraFilter("Luma");

    private bool IsExtraFilter(string mode) => Collections.ActiveName is null && string.Equals(LibraryFilterMode, mode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Available Mods' tooltip: what is new when the New tag shows, else what the list is.</summary>
    public string NavAvailableModsTip => NewMods.IsHidden ? T("Ui.AvailableModsTip") : NewMods.ButtonTip;

    private void NotifyLibraryFilterSwitch()
    {
        OnPropertyChanged(nameof(LibraryMainFilterIndex));
        OnPropertyChanged(nameof(LibraryExtraFilterCount));
        OnPropertyChanged(nameof(LibraryFiltersText));
        OnPropertyChanged(nameof(LibraryFiltersTip));
        OnPropertyChanged(nameof(LibraryFilterIsUnreal));
        OnPropertyChanged(nameof(LibraryFilterIsUnity));
        OnPropertyChanged(nameof(LibraryFilterIsOther));
        OnPropertyChanged(nameof(LibraryFilterIsRenoDx));
        OnPropertyChanged(nameof(LibraryFilterIsLuma));
        _topBar?.LibraryChanged();
    }

    // ---- #304 the Tabs header and At a glance ------------------------------------------------------------------------

    /// <summary>The Tabs header's quiet tags: store, runner, graphics API, engine and mod author (upstream's chips).</summary>
    public IReadOnlyList<string> HeaderTags
    {
        get
        {
            if (SelectedGame is not { } game) return [];
            var tags = new List<string> { game.Source };
            if (!string.IsNullOrWhiteSpace(game.Runner)) tags.Add(game.Runner!);
            if (game.GraphicsApi != GraphicsApi.Unknown) tags.Add(ShortApi(game.GraphicsApi));
            if (!string.IsNullOrWhiteSpace(game.EngineHint)) tags.Add(game.EngineHint!);
            if (!string.IsNullOrWhiteSpace(game.AuthorOverride)) tags.Add(game.AuthorOverride!);
            return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>A graphics API as upstream's tags show it (DX12, Vulkan, OpenGL).</summary>
    public static string ShortApi(GraphicsApi api)
    {
        var name = api.ToString();
        return name.StartsWith("DirectX", StringComparison.Ordinal) ? "DX" + name["DirectX".Length..] : name;
    }

    /// <summary>The Tabs header's links: the game's Nexus Mods page and its web pages (PCGamingWiki, ultrawide fix, Ultra+ …).</summary>
    public IReadOnlyList<CardMenuItem> HeaderLinks
    {
        get
        {
            if (SelectedGame is not { } game || Parity is not { } parity) return [];
            var links = new List<CardMenuItem>();
            if (GameLinksPresenter.PageFor(game, parity.GameLinks.Games) is { } nexusUrl)
                links.Add(new("nexus-page", "Nexus", new RelayCommand(() => OpenNexusPage(nexusUrl)), Tip: nexusUrl));
            links.AddRange(parity.GameLinks.MenuItemsFor(game, OpenNexusPage));
            return links;
        }
    }

    public bool HasHeaderLinks => HeaderLinks.Count > 0;

    /// <summary>An At a glance tile's summary for a section (upstream: the collapsed header's summary).</summary>
    public string GlanceText(string section)
    {
        string Or(string text) => string.IsNullOrWhiteSpace(text) ? T("Glance.Empty") : text;
        return section switch
        {
            GamePageSections.GameOverrides => Or(DetailSectionSummaryPresenter.Text(DetailSectionSummaryPresenter.Runtime(SelectedGame))),
            GamePageSections.Extras => Or(DetailSectionSummaryPresenter.Text(DetailSectionSummaryPresenter.Extras(Extras.UalInstalled, Extras.ShortFuseInstalled, Extras.ShortFuseInstalledVersion))),
            GamePageSections.Dlss => Or(Dlss.DlssStatusSummary),
            GamePageSections.Driver => FrameRateLimit.IsShown && FrameRateLimit.Selected is { } fps ? fps.Label : T("Glance.Empty"),
            GamePageSections.NeuralRendering => Or(Runtime.NeuralRenderingRecommendation),
            GamePageSections.Management => T("Sec.ManagementNote"),
            GamePageSections.Components => Or(DetailSectionSummaryPresenter.Text(DetailSectionSummaryPresenter.ManagedComponents(Components))),
            _ => "",
        };
    }

    private void NotifyHeader()
    {
        OnPropertyChanged(nameof(HeaderTags));
        OnPropertyChanged(nameof(HeaderLinks));
        OnPropertyChanged(nameof(HasHeaderLinks));
    }

    /// <summary>The Tabs header's Launch menu after the launch command and profile: Create shortcut (upstream: desktop shortcut).</summary>
    public IReadOnlyList<CardMenuItem> LaunchMenuItems()
    {
        if (SelectedGame is not { } game || Parity is not { } parity) return [];
        return
        [
            new("create-shortcut", T("Parity.ShortcutCreate"), CardMenuItem.OpensSubmenu, SeparatorBefore: true,
                Children:
                [
                    new("shortcut-desktop", T("Parity.ShortcutOnDesktopItem"), new AsyncRelayCommand(async () => StatusText = await parity.Shortcuts.CreateAsync(game, RHI.Platform.Linux.Paths.ShortcutPlace.Desktop))),
                    new("shortcut-menu", T("Parity.ShortcutInMenuItem"), new AsyncRelayCommand(async () => StatusText = await parity.Shortcuts.CreateAsync(game, RHI.Platform.Linux.Paths.ShortcutPlace.ApplicationsMenu)))
                ],
                Tip: T("Parity.ShortcutTip")),
        ];
    }
}
