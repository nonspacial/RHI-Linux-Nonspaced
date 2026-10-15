using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
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
        DefaultDlss = Choice(shell.Dlss.DlssSrVersions, settings.DefaultDlssVersion);
        DefaultDlssd = Choice(shell.Dlss.DlssRrVersions, settings.DefaultDlssdVersion);
        DefaultDlssg = Choice(shell.Dlss.DlssFgVersions, settings.DefaultDlssgVersion);
        DefaultDlssnr = Choice(shell.Dlss.DlssNrVersions, settings.DefaultDlssnrVersion);
        DefaultStreamline = Choice(shell.Dlss.StreamlineVersions, settings.DefaultStreamlineVersion);
        CanApplyToGame = shell.HasSelectedGame;
        saveLabel = T("Ui.Save");
        resetLabel = T("Ui.ResetSettings");

        static VersionChoice Choice(IEnumerable<string> versions, string current) => new("", versions, current);
    }

    public LinuxSettingsUiSnapshot Snapshot { get; }
    public LinuxAppSettings Settings => Snapshot.Settings;
    public string WindowTitle => T("Ui.SettingsWindowTitle");
    internal MainWindowViewModel Shell => _shell;
    internal Func<string, string?> OpenFolder { get; }
    internal Func<string, Task<string?>> PickFolder { get; }
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

    [ObservableProperty] private string? status;
    [ObservableProperty] private string saveLabel;
    [ObservableProperty] private string resetLabel;
    [ObservableProperty] private bool canClearCache = true;
    public bool CanApplyToGame { get; }

    /// <summary>Saved or reset: the window follows the view mode, add-on watcher and tray icon.</summary>
    public event Action? Applied;

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
            CacheAllShaders = CacheAllShaders,
            VerboseLogging = VerboseLogging,
            AutoUpdateComponents = AutoUpdateComponents,
            UpdateCheckIntervalHours = 4,
            DefaultDlssVersion = DefaultDlss.Value,
            DefaultDlssdVersion = DefaultDlssd.Value,
            DefaultDlssgVersion = DefaultDlssg.Value,
            DefaultDlssnrVersion = DefaultDlssnr.Value,
            DefaultStreamlineVersion = DefaultStreamline.Value,
        };
        foreach (var rows in _hosted) rows.WriteTo(settings);
        return settings;
    }

    [RelayCommand]
    private void Save()
    {
        Status = _shell.Settings.SaveLinuxSettings(Read());
        SaveLabel = T("Ui.Save");
        ResetLabel = T("Ui.ResetSettings");
        Applied?.Invoke();
    }

    [RelayCommand]
    private void Reset()
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
        CacheAllShaders = defaults.CacheAllShaders;
        VerboseLogging = defaults.VerboseLogging;
        AutoUpdateComponents = defaults.AutoUpdateComponents;
        DefaultDlss.Clear();
        DefaultDlssd.Clear();
        DefaultDlssg.Clear();
        DefaultDlssnr.Clear();
        DefaultStreamline.Clear();
        foreach (var rows in _hosted) rows.Reset(defaults);
        Status = _shell.Settings.ResetLinuxSettings();
        Applied?.Invoke();
    }

    /// <summary>Saves, then writes the ReShade capture and hotkey defaults into the selected game's ReShade.ini.</summary>
    [RelayCommand]
    private void ApplyToGame()
    {
        try
        {
            var current = Read();
            _shell.Settings.SaveLinuxSettings(current);
            Applied?.Invoke();
            Status = _shell.ReShadeProfile.ApplyGlobalReShadeSettingsToSelectedGame(current);
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
