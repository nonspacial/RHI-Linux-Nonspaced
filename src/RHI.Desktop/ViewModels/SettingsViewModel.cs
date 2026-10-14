using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The Settings dialog's view model (epic #89 unit 7): the settings snapshot it edits, Save / Reset (the shell adopts the
/// saved settings: language, view, defaults, background loop) and Clear cache (off the UI thread under the gate, #84).
/// Rows other lanes add to Settings hook in here.
/// </summary>
public sealed class SettingsViewModel : PanelViewModel
{
    private readonly MainWindowViewModel _shell;
    private readonly LinuxSettingsStore _settingsStore;

    internal SettingsViewModel(MainWindowViewModel shell, LinuxSettingsStore settingsStore) : base(shell)
    {
        _shell = shell;
        _settingsStore = settingsStore;
    }

    // w3/parity Settings rows (hooks: one member each below this line)

    public LinuxSettingsUiSnapshot GetLinuxSettingsSnapshot()
    {
        var copy = new LinuxAppSettings
        {
            UiLanguage = Host.Settings.UiLanguage,
            ReShadeChannel = Host.Settings.ReShadeChannel,
            ReShadeManageIni = Host.Settings.ReShadeManageIni,
            ScreenshotPath = Host.Settings.ScreenshotPath,
            OverlayHotkey = Host.Settings.OverlayHotkey,
            ScreenshotHotkey = Host.Settings.ScreenshotHotkey,
            PerGameScreenshotFolders = Host.Settings.PerGameScreenshotFolders,
            AddonWatchFolder = Host.Settings.AddonWatchFolder,
            AddonWatcherEnabled = Host.Settings.AddonWatcherEnabled,
            ViewMode = Host.Settings.ViewMode,
            TrayIconEnabled = Host.Settings.TrayIconEnabled,
            CloseToTray = Host.Settings.CloseToTray,
            ShowMotdOnStartup = Host.Settings.ShowMotdOnStartup,
            CacheAllShaders = Host.Settings.CacheAllShaders,
            VerboseLogging = Host.Settings.VerboseLogging,
            AutoUpdateComponents = Host.Settings.AutoUpdateComponents,
            UpdateCheckIntervalHours = Host.Settings.UpdateCheckIntervalHours,
            DefaultDlssVersion = Host.Settings.DefaultDlssVersion,
            DefaultDlssdVersion = Host.Settings.DefaultDlssdVersion,
            DefaultDlssgVersion = Host.Settings.DefaultDlssgVersion,
            DefaultDlssnrVersion = Host.Settings.DefaultDlssnrVersion,
            DefaultStreamlineVersion = Host.Settings.DefaultStreamlineVersion
        };
        return new(copy, _settingsStore.SettingsPath, _settingsStore.ConfigRoot, _settingsStore.CacheRoot, _settingsStore.DataRoot);
    }

    public string SaveLinuxSettings(LinuxAppSettings settings)
    {
        var previous = Host.Settings;
        if (TryPersist(() => _settingsStore.Save(settings), "Ui.WhatSettings", sessionOnly: false) is { } notSaved) return notSaved;
        var saved = _settingsStore.Load();
        var loopChanged = previous.AutoUpdateComponents != saved.AutoUpdateComponents || previous.UpdateCheckIntervalHours != saved.UpdateCheckIntervalHours;
        _shell.AdoptSettings(saved, restartLoop: loopChanged, runImmediate: saved.AutoUpdateComponents);
        return UiLocalizationService.Format("Vm.SettingsSaved", _settingsStore.SettingsPath);
    }

    public string ResetLinuxSettings()
    {
        LinuxAppSettings? reset = null;
        if (TryPersist(() => reset = _settingsStore.Reset(), "Ui.WhatSettings", sessionOnly: false) is { } notReset) return notReset;
        _shell.AdoptSettings(reset!, restartLoop: true, runImmediate: false);
        return UiLocalizationService.Text("Vm.SettingsRestoredLinuxDefaults");
    }

    /// <summary>
    /// Settings → Clear cache (Refs #84): the cache folder (downloaded packages, shader packs) is counted and deleted off
    /// the UI thread, holding the busy gate so no install, update or scan reads a package while it disappears. Refused
    /// while another operation runs; it cannot be cancelled part-way (a half-deleted cache would only be re-downloaded,
    /// but the count would be wrong).
    /// </summary>
    public async Task<string> PurgeLinuxCacheAsync()
    {
        using var op = BeginOperation("clear-cache", cancellable: false);
        if (op is null) return T("Vm.AnotherRHIOperationAlreadyRunning");
        int removed;
        try
        {
            removed = await Task.Run(_settingsStore.PurgeCache);
        }
        catch (Exception ex)
        {
            UiExceptionGuard.Log.TryWrite("purge cache", ex);
            return CacheNotClearedText(ex);
        }
        return removed == 0 ? UiLocalizationService.Text("Vm.RHILinuxCacheAlreadyEmpty") : UiLocalizationService.Format("Vm.FileSRemovedFromCache", removed);
    }

    /// <summary>Clear-cache failure line; the exception's text in the UI language's presentation.</summary>
    internal static string CacheNotClearedText(Exception ex)
        => UiLocalizationService.Format("Ui.CacheNotCleared", UiLocalizationService.Present(ex.Message));
}
