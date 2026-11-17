using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Rows another lane put into Settings (code-built controls the view hosts): how they write their values into the
/// settings being saved, and how they go back to the defaults on Reset.
/// </summary>
public sealed record SettingsRowsHost(Action<LinuxAppSettings> WriteTo, Action<LinuxAppSettings> Reset);

/// <summary>
/// The Settings dialog's form (epic #89: was built in the window's code-behind). Holds the core fields of every tab,
/// reads them into <see cref="LinuxAppSettings"/> (plus whatever the hosted lane rows write, <see cref="Host"/>) and
/// runs Save, Reset settings, Apply ReShade defaults to the selected game, Clear cache and the folder buttons through
/// the shell's <see cref="SettingsViewModel"/>. <see cref="Applied"/> tells the window to follow the saved view mode,
/// add-on watcher and tray icon.
/// </summary>
public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly MainWindowViewModel _shell;
    private readonly List<SettingsRowsHost> _hosted = [];

    /// <param name="openFolder">Opens (creating) a folder; returns the line to show when it could not, else null.</param>
    internal SettingsDialogViewModel(MainWindowViewModel shell, Func<string, string?> openFolder, Func<string, Task<string?>> pickFolder,
        Func<string, Task<bool>>? copyText = null)
    {
        _shell = shell;
        OpenFolder = openFolder;
        PickFolder = pickFolder;
        CopyText = copyText ?? (_ => Task.FromResult(false));
        Snapshot = shell.Settings.GetLinuxSettingsSnapshot();
        var settings = Snapshot.Settings;
        // Labels and values come from the one mapping the view model uses (OptionLists, Refs #87).
        uiLanguage = OptionLists.UiLanguage.Display(settings.UiLanguage);
        reShadeChannel = OptionLists.ReShadeChannel.Display(settings.ReShadeChannel);
        manageIni = settings.ReShadeManageIni;
        screenshotPath = settings.ScreenshotPath;
        perGameScreenshots = settings.PerGameScreenshotFolders;
        addonWatchFolder = settings.AddonWatchFolder;
        cacheAllShaders = settings.CacheAllShaders;
        verboseLogging = settings.VerboseLogging;
        autoUpdateComponents = settings.AutoUpdateComponents;
        addonWatcherEnabled = settings.AddonWatcherEnabled;
        viewMode = OptionLists.ViewMode.Display(settings.ViewMode);
        trayIconEnabled = settings.TrayIconEnabled;
        closeToTray = settings.CloseToTray;
        closeToTrayEnabled = settings.TrayIconEnabled;
        showMotdOnStartup = settings.ShowMotdOnStartup;
        showHdrLaunchButton = settings.ShowHdrLaunchButton; // #312
        showResLaunchButton = settings.ShowResLaunchButton;
        lastSection = SettingsSections.Restore(settings.SettingsLastSection); // #296
        tabbed = settings.SettingsTabbed;
        DefaultDlss = Choice(shell.Dlss.DlssSrVersions, settings.DefaultDlssVersion);
        DefaultDlssd = Choice(shell.Dlss.DlssRrVersions, settings.DefaultDlssdVersion);
        DefaultDlssg = Choice(shell.Dlss.DlssFgVersions, settings.DefaultDlssgVersion);
        DefaultDlssnr = Choice(shell.Dlss.DlssNrVersions, settings.DefaultDlssnrVersion);
        DefaultStreamline = Choice(shell.Dlss.StreamlineVersions, settings.DefaultStreamlineVersion);
        // #215: the default presets, from the manifest's lists (the NVIDIA settings' own labels; a saved value the
        // manifest no longer lists stays shown).
        var presets = DlssPresetCatalogue.Current;
        var defaultsTip = T("Dp.DefaultPresetsTip");
        DefaultSrPreset = new(T("Nv.SrPreset"), T("Nv.SrPresetTip") + "\n" + defaultsTip, presets.SuperResolution, settings.DefaultSrPreset, doNotChange: false);
        DefaultRrPreset = new(T("Nv.RrPreset"), T("Nv.RrPresetTip") + "\n" + defaultsTip, presets.RayReconstruction, settings.DefaultRrPreset, doNotChange: false);
        DefaultFgPreset = new(T("Nv.FgPreset"), T("Nv.FgPresetTip") + "\n" + defaultsTip, presets.FrameGeneration, settings.DefaultFgPreset, doNotChange: false);
        DefaultPresetsTip = defaultsTip;
        CanApplyToGame = shell.HasSelectedGame;
        saveLabel = T("Ui.Save");
        resetLabel = T("Ui.ResetSettings");

        static VersionChoice Choice(IEnumerable<string> versions, string current) => new("", versions, current);

        // #296 (upstream 2.8.9): Settings saves as soon as a setting changes; there is no Save button. The version and
        // preset choices are objects of their own, so they report their changes here too.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && AutoSaved.Contains(name)) SaveSoon();
        };
        foreach (var choice in new[] { DefaultDlss, DefaultDlssd, DefaultDlssg, DefaultDlssnr, DefaultStreamline })
            choice.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VersionChoice.Selected)) SaveSoon(); };
        foreach (var preset in new[] { DefaultSrPreset, DefaultRrPreset, DefaultFgPreset })
            preset.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PresetChoice.Selected)) SaveSoon(); };
    }

    // The form's own settings (not Status, labels or the page position): a change to one of these is saved.
    private static readonly HashSet<string> AutoSaved =
    [
        nameof(UiLanguage), nameof(ReShadeChannel), nameof(ManageIni), nameof(ScreenshotPath), nameof(PerGameScreenshots),
        nameof(AddonWatchFolder), nameof(CacheAllShaders), nameof(VerboseLogging), nameof(AutoUpdateComponents),
        nameof(AddonWatcherEnabled), nameof(ViewMode), nameof(TrayIconEnabled), nameof(CloseToTray), nameof(ShowMotdOnStartup),
        nameof(ShowHdrLaunchButton), nameof(ShowResLaunchButton), nameof(Tabbed),
    ];

    private Action<Action>? _schedule;
    private bool _savePending;
    private bool _scheduled;
    private int _suppressSaves;

    /// <summary>
    /// How a save is put off (the view coalesces the changes of one gesture, e.g. a combo box and the rows it updates, into
    /// one save a moment later). Unset (no view), a change waits for <see cref="FlushSave"/> or Save.
    /// </summary>
    public void UseSaveScheduler(Action<Action> schedule) => _schedule = schedule;

    /// <summary>A setting changed (the form's own fields, or a hosted row through the view): it is saved shortly.</summary>
    public void SaveSoon()
    {
        if (_suppressSaves > 0) return;
        _savePending = true;
        if (_scheduled || _schedule is null) return;
        _scheduled = true;
        _schedule(() =>
        {
            _scheduled = false;
            FlushSave();
        });
    }

    /// <summary>
    /// Saves a pending change now (also when Settings closes, so nothing typed is lost). Nothing is written when the form
    /// still holds what was saved last (the view reports every edit gesture, also those that change no setting).
    /// </summary>
    public void FlushSave()
    {
        if (!_savePending) return;
        _savePending = false;
        var current = Read();
        if (Fingerprint(current) == _savedFingerprint) return;
        Save(current);
    }

    private string? _savedFingerprint;

    /// <summary>The view has built its rows: what they show now is what is saved (an edit that changes nothing writes nothing).</summary>
    public void Composed() => _savedFingerprint = Fingerprint(Read());

    private static string Fingerprint(LinuxAppSettings settings)
        => System.Text.Json.JsonSerializer.Serialize(Clone(settings).Normalize());

    private static LinuxAppSettings Clone(LinuxAppSettings settings)
        => System.Text.Json.JsonSerializer.Deserialize<LinuxAppSettings>(System.Text.Json.JsonSerializer.Serialize(settings)) ?? new LinuxAppSettings();

    /// <summary>#296: the section in view, saved so Settings reopens there (upstream SettingsLastSection).</summary>
    [ObservableProperty] private string lastSection;
    /// <summary>#296 (upstream 2.9.3): one section at a time, with a tab strip.</summary>
    [ObservableProperty] private bool tabbed;
    /// <summary>#312 (upstream 2.9.3): Display &amp; HDR › the HDR and Resolution rows on game pages, Show or Hide.</summary>
    [ObservableProperty] private bool showHdrLaunchButton;
    [ObservableProperty] private bool showResLaunchButton;

    /// <summary>Settings is closing: a pending change is saved, and the section in view is remembered when it changed.</summary>
    public void Closing(string? sectionInView)
    {
        var key = sectionInView is null ? LastSection : SettingsSections.Restore(sectionInView);
        var moved = !string.Equals(key, SettingsSections.Restore(_shell.Settings.GetLinuxSettingsSnapshot().Settings.SettingsLastSection), StringComparison.Ordinal);
        LastSection = key;
        if (moved) _savePending = true;
        FlushSave();
    }

    public LinuxSettingsUiSnapshot Snapshot { get; }
    public LinuxAppSettings Settings => Snapshot.Settings;
    public string WindowTitle => T("Ui.SettingsWindowTitle");
    internal MainWindowViewModel Shell => _shell;
    internal Func<string, string?> OpenFolder { get; }
    internal Func<string, Task<string?>> PickFolder { get; }
    /// <summary>#296: DLSS / Streamline › Batch deploy opens the DLSS batch (set by the window; null = not offered).</summary>
    public Action? OpenDlssBatch { get; set; }
    /// <summary>Copies text to the clipboard (Support tab); false when the session has none.</summary>
    internal Func<string, Task<bool>> CopyText { get; }

    public IReadOnlyList<string> UiLanguages { get; } = OptionLists.UiLanguage.Items;
    public IReadOnlyList<string> ReShadeChannels { get; } = OptionLists.ReShadeChannel.Items;
    public IReadOnlyList<string> ViewModes { get; } = OptionLists.ViewMode.Items;

    [ObservableProperty] private string? uiLanguage;
    [ObservableProperty] private string? reShadeChannel;
    [ObservableProperty] private bool manageIni;
    [ObservableProperty] private string? screenshotPath;
    [ObservableProperty] private bool perGameScreenshots;
    [ObservableProperty] private string? addonWatchFolder;
    [ObservableProperty] private bool cacheAllShaders;
    [ObservableProperty] private bool verboseLogging;
    [ObservableProperty] private bool autoUpdateComponents;
    [ObservableProperty] private bool addonWatcherEnabled;
    [ObservableProperty] private string? viewMode;
    [ObservableProperty] private bool trayIconEnabled;
    [ObservableProperty] private bool closeToTray;
    [ObservableProperty] private bool closeToTrayEnabled;
    [ObservableProperty] private bool showMotdOnStartup;

    public VersionChoice DefaultDlss { get; }
    public VersionChoice DefaultDlssd { get; }
    public VersionChoice DefaultDlssg { get; }
    public VersionChoice DefaultDlssnr { get; }
    public VersionChoice DefaultStreamline { get; }

    /// <summary>#215: the default presets Quick Apply and the DLSS batch copy into a game's NVIDIA settings.</summary>
    public PresetChoice DefaultSrPreset { get; }
    public PresetChoice DefaultRrPreset { get; }
    public PresetChoice DefaultFgPreset { get; }
    public string DefaultPresetsTip { get; }

    [ObservableProperty] private string? status;
    [ObservableProperty] private string saveLabel;
    [ObservableProperty] private string resetLabel;
    [ObservableProperty] private bool canClearCache = true;
    public bool CanApplyToGame { get; }

    /// <summary>Saved or reset: the window follows the view mode, add-on watcher and tray icon.</summary>
    public event Action? Applied;

    /// <summary>
    /// A view is about to host its rows (<see cref="Host"/>). Rows belong to the view that built them: when a different
    /// view composes this model (its template rebuilt), the earlier view's rows are dropped, so Save never writes and Reset
    /// never touches rows that are no longer shown, and nothing is registered twice.
    /// </summary>
    public void BeginHosting(object view)
    {
        if (ReferenceEquals(_hostingView, view)) return;
        _hostingView = view;
        _hosted.Clear();
    }

    private object? _hostingView;

    /// <summary>Hosts a lane's rows: they write their values on Save and go back to the defaults on Reset.</summary>
    public void Host(SettingsRowsHost rows) => _hosted.Add(rows);

    /// <summary>The tray box is what the user clicks: off also clears and locks "Hide to tray when closing".</summary>
    public void TrayIconClicked()
    {
        CloseToTrayEnabled = TrayIconEnabled;
        if (!TrayIconEnabled) CloseToTray = false;
    }

    /// <summary>The ReShade tab's screenshot fields (#181's INI rows read them before Settings is saved).</summary>
    public void WriteReShadeFields(LinuxAppSettings current)
    {
        current.ScreenshotPath = ScreenshotPath ?? "";
        current.PerGameScreenshotFolders = PerGameScreenshots;
    }

    /// <summary>Everything the dialog shows, as the settings Save stores.</summary>
    public LinuxAppSettings Read()
    {
        var settings = new LinuxAppSettings
        {
            UiLanguage = OptionLists.UiLanguage.Value(UiLanguage),
            ReShadeChannel = OptionLists.ReShadeChannel.Value(ReShadeChannel),
            ReShadeManageIni = ManageIni,
            ScreenshotPath = ScreenshotPath ?? "",
            PerGameScreenshotFolders = PerGameScreenshots,
            AddonWatchFolder = AddonWatchFolder ?? "",
            AddonWatcherEnabled = AddonWatcherEnabled,
            ViewMode = OptionLists.ViewMode.Value(ViewMode),
            TrayIconEnabled = TrayIconEnabled,
            CloseToTray = TrayIconEnabled && CloseToTray,
            ShowMotdOnStartup = ShowMotdOnStartup,
            ShowHdrLaunchButton = ShowHdrLaunchButton, // #312
            ShowResLaunchButton = ShowResLaunchButton,
            SettingsLastSection = LastSection, // #296
            SettingsTabbed = Tabbed,
            WhatsNewNavHidden = Settings.WhatsNewNavHidden, // #309: What's New's own choices, kept as they are
            WhatsNewFontSize = Settings.WhatsNewFontSize,
            ColourTheme = _shell.Theme.ThemeId, // #301: the theme is saved as it changes; the save keeps the one in use
            CustomThemeColours = _shell.Theme.SavedCustomColours,
            GlobalFpsLimit = Settings.GlobalFpsLimit, // #310: the view's FPS Limit row writes its value over this
            MainLayout = _shell.Layout.Saved.ToString(), // w14/layouts-a (#303/#306): saved as they change, kept as in use
            DetailSelectedTab = _shell.Layout.SelectedTab == GamePageSections.Overview ? "" : _shell.Layout.SelectedTab,
            UiScale = _shell.UiScale,
            TopBarScale = _shell.TopBarScale,
            GameListScale = _shell.GameListScale,
            // w14/layouts-b rows (#305 #307 #308): Spotlight's sizes and places are saved as they change, kept as in use; the
            // achievements and controller switches are the Achievements and General rows' (hosted rows write over these).
            SpotlightHeroHeight = _shell.SpotlightSettings.SpotlightHeroHeight,
            SpotlightCoverScale = _shell.SpotlightSettings.SpotlightCoverScale,
            SpotlightShelfAtBottom = _shell.SpotlightSettings.SpotlightShelfAtBottom,
            SpotlightShelfPlain = _shell.SpotlightSettings.SpotlightShelfPlain,
            SteamAchievements = _shell.SpotlightSettings.SteamAchievements,
            AchievementsShowHidden = _shell.SpotlightSettings.AchievementsShowHidden,
            ControllerSupport = _shell.SpotlightSettings.ControllerSupport,
            CacheAllShaders = CacheAllShaders,
            VerboseLogging = VerboseLogging,
            AutoUpdateComponents = AutoUpdateComponents,
            UpdateCheckIntervalHours = 4,
            DefaultDlssVersion = DefaultDlss.Value,
            DefaultDlssdVersion = DefaultDlssd.Value,
            DefaultDlssgVersion = DefaultDlssg.Value,
            DefaultDlssnrVersion = DefaultDlssnr.Value,
            DefaultStreamlineVersion = DefaultStreamline.Value,
            DefaultSrPreset = DefaultSrPreset.Value ?? DlssPresetCatalogue.Default, // #215
            DefaultRrPreset = DefaultRrPreset.Value ?? DlssPresetCatalogue.Default,
            DefaultFgPreset = DefaultFgPreset.Value ?? DlssPresetCatalogue.Default,
        };
        foreach (var rows in _hosted) rows.WriteTo(settings);
        return settings;
    }

    [RelayCommand]
    private void Save() => Save(Read());

    private void Save(LinuxAppSettings settings)
    {
        _savePending = false;
        _savedFingerprint = Fingerprint(settings);
        Status = _shell.Settings.SaveLinuxSettings(settings);
        SaveLabel = T("Ui.Save");
        ResetLabel = T("Ui.ResetSettings");
        Applied?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        _suppressSaves++;
        try { ResetForm(); }
        finally { _suppressSaves--; }
        _savePending = false;
        Status = _shell.Settings.ResetLinuxSettings();
        Applied?.Invoke();
    }

    private void ResetForm()
    {
        var defaults = new LinuxAppSettings();
        UiLanguage = OptionLists.UiLanguage.Display(defaults.UiLanguage);
        ReShadeChannel = OptionLists.ReShadeChannel.Display(defaults.ReShadeChannel);
        ManageIni = defaults.ReShadeManageIni;
        ScreenshotPath = defaults.ScreenshotPath;
        PerGameScreenshots = defaults.PerGameScreenshotFolders;
        AddonWatchFolder = defaults.AddonWatchFolder;
        AddonWatcherEnabled = defaults.AddonWatcherEnabled;
        ViewMode = OptionLists.ViewMode.Display(defaults.ViewMode);
        TrayIconEnabled = defaults.TrayIconEnabled;
        CloseToTray = defaults.CloseToTray;
        CloseToTrayEnabled = defaults.TrayIconEnabled;
        ShowMotdOnStartup = defaults.ShowMotdOnStartup;
        ShowHdrLaunchButton = defaults.ShowHdrLaunchButton; // #312
        ShowResLaunchButton = defaults.ShowResLaunchButton;
        CacheAllShaders = defaults.CacheAllShaders;
        VerboseLogging = defaults.VerboseLogging;
        AutoUpdateComponents = defaults.AutoUpdateComponents;
        DefaultDlss.Clear();
        DefaultDlssd.Clear();
        DefaultDlssg.Clear();
        DefaultDlssnr.Clear();
        DefaultStreamline.Clear();
        DefaultSrPreset.Clear(); // #215
        DefaultRrPreset.Clear();
        DefaultFgPreset.Clear();
        foreach (var rows in _hosted) rows.Reset(defaults);
    }

    /// <summary>Saves, then writes the ReShade capture and hotkey defaults into the selected game's ReShade.ini.</summary>
    [RelayCommand]
    private async Task ApplyToGame()
    {
        try
        {
            var current = Read();
            _shell.Settings.SaveLinuxSettings(current);
            Applied?.Invoke();
            Status = await _shell.ReShadeProfile.ApplyGlobalReShadeSettingsToSelectedGameAsync(current, _shell.IniPresets.Service); // w8/reshade-ini (#206): journaled
        }
        catch (Exception ex) { Status = TF("Win.CouldNotApplyReShadeDefaults", UiLocalizationService.Present(ex.Message)); }
    }

    [RelayCommand]
    private Task ClearCache() => UiExceptionGuard.RunAsync(async () =>
    {
        CanClearCache = false;
        try { Status = await _shell.Settings.PurgeLinuxCacheAsync(); }
        finally { CanClearCache = true; }
    }, "Settings_Click");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowseScreenshots() => UiExceptionGuard.RunAsync(async () =>
    {
        var chosen = await PickFolder(T("Win.SelectScreenshotFolder"));
        if (!string.IsNullOrWhiteSpace(chosen)) ScreenshotPath = chosen;
    }, "Settings_Click");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowseWatchFolder() => UiExceptionGuard.RunAsync(async () =>
    {
        var chosen = await PickFolder(T("Win.SelectWatchedAddFolder"));
        if (!string.IsNullOrWhiteSpace(chosen)) AddonWatchFolder = chosen;
    }, "Settings_Click");

    [RelayCommand]
    private void OpenConfiguration() => Open(Snapshot.ConfigRoot);

    [RelayCommand]
    private void OpenCache() => Open(Snapshot.CacheRoot);

    [RelayCommand]
    private void OpenData() => Open(Snapshot.DataRoot);

    private void Open(string path)
    {
        if (OpenFolder(path) is { } error) Status = error;
    }
}
