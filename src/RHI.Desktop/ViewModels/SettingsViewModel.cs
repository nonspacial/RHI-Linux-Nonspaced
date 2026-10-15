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
    /// <summary>Settings → Accounts &amp; updates (#100 update notice, #163 GitHub token, #99 Connect Nexus account); null without them.</summary>
    public ParityFeatures? Parity => _shell.Parity;

    // w3/scan Settings row (#90): "Xbox / Game Pass folders"; null until the app composition hands the store over.
    /// <summary>The "Xbox / Game Pass folders" list; saving one rescans the Xbox and Wine detectors when idle.</summary>
    public XboxFoldersPresenter? XboxFolders { get; private set; }

    /// <summary>Hosts the Xbox folder list (one store shared with the Xbox and Wine detectors, scan nit 11).</summary>
    internal void UseXboxFolders(RHI.Platform.Linux.Detection.Xbox.XboxFolderStore store)
    {
        var presenter = new XboxFoldersPresenter(store, save => TryPersist(save, XboxFoldersPresenter.WhatKey, sessionOnly: false));
        // The Wine detector reads the same list (installer-app title prefixes are the installer app's), so both rescan.
        presenter.Changed += () => UiExceptionGuard.Forget(((ILibraryEditHost)_shell).RefreshWhenIdleAsync(XboxFolderDetectors), "xbox-folders-refresh");
        XboxFolders = presenter;
    }

    internal static readonly IReadOnlySet<string> XboxFolderDetectors =
        new HashSet<string>(StringComparer.Ordinal) { RHI.Platform.Linux.Detection.Xbox.XboxGameDetector.SourceName, "Wine" };

    // w5 lane rows: Settings hooks (one member each)
    // w5/update-policy rows
    // w5/catalogue-info rows
    // w5/ini-presets rows
    // w5/shell-launch rows
    /// <summary>#187: stores the OptiScaler overlay key alone (Apply to all games); null when saved, else the refusal line.</summary>
    internal string? SaveOptiScalerMenuKey(string key)
    {
        var copy = GetLinuxSettingsSnapshot().Settings;
        copy.OptiScalerMenuKey = key;
        if (TryPersist(() => _settingsStore.Save(copy), "Ui.WhatSettings", sessionOnly: false) is { } refused) return refused;
        _shell.AdoptSettings(_settingsStore.Load(), restartLoop: false, runImmediate: false);
        return null;
    }
    // w5/reshade-core rows

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
            DefaultStreamlineVersion = Host.Settings.DefaultStreamlineVersion,
            OptiScalerGpuType = Host.Settings.OptiScalerGpuType, // w3/dllnames hook (#97)
            OptiScalerDlssInputs = Host.Settings.OptiScalerDlssInputs, // w3/dllnames hook (#97)
            // w5/update-policy rows
            FollowNewestDlss = Host.Settings.FollowNewestDlss, // #177
            FollowNewestStreamline = Host.Settings.FollowNewestStreamline, // #177
            GlobalSkipReShadeUpdates = Host.Settings.GlobalSkipReShadeUpdates, // #178
            GlobalSkipRenoDxUpdates = Host.Settings.GlobalSkipRenoDxUpdates,
            GlobalSkipReLimiterUpdates = Host.Settings.GlobalSkipReLimiterUpdates,
            GlobalSkipDisplayCommanderUpdates = Host.Settings.GlobalSkipDisplayCommanderUpdates,
            GlobalSkipOptiScalerUpdates = Host.Settings.GlobalSkipOptiScalerUpdates,
            GlobalSkipReFrameworkUpdates = Host.Settings.GlobalSkipReFrameworkUpdates,
            // w5/catalogue-info rows
            // w5/ini-presets rows
            ReShadeVariableListUseTabs = Host.Settings.ReShadeVariableListUseTabs, // #181
            ReLimiterOsdHotkey = Host.Settings.ReLimiterOsdHotkey, // #188
            ReLimiterSharedPresets = Host.Settings.ReLimiterSharedPresets, // #188
            ReLimiterDlssHooks = Host.Settings.ReLimiterDlssHooks, // #188
            ReLimiterTargetFps = Host.Settings.ReLimiterTargetFps, // #188
            PeakNits = Host.Settings.PeakNits, // #182
            PeakNitsEnabled = Host.Settings.PeakNitsEnabled, // #182
            PeakNitsPresets = [.. Host.Settings.PeakNitsPresets], // #182
            // w5/shell-launch rows
            OptiScalerMenuKey = Host.Settings.OptiScalerMenuKey, // #187 OptiScaler overlay key
            // w5/reshade-core rows
        };
        return new(copy, _settingsStore.SettingsPath, _settingsStore.ConfigRoot, _settingsStore.CacheRoot, _settingsStore.DataRoot);
    }

    public string SaveLinuxSettings(LinuxAppSettings settings)
    {
        var previous = Host.Settings;
        if (TryPersist(() => _settingsStore.Save(settings), "Ui.WhatSettings", sessionOnly: false) is { } notSaved) return notSaved;
        var saved = _settingsStore.Load();
        var loopChanged = UpdatesViewModel.LoopSettingsChanged(previous, saved); // w5/update-policy hook (#177)
        _shell.AdoptSettings(saved, restartLoop: loopChanged, runImmediate: UpdatesViewModel.RunsImmediatelyAfterSave(previous, saved));
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
