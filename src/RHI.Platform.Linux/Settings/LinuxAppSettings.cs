using System.Globalization;
using System.Text.Json;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

public sealed class LinuxAppSettings
{
    public string UiLanguage { get; set; } = DefaultUiLanguage();
    public string ReShadeChannel { get; set; } = "Stable";
    public bool ReShadeManageIni { get; set; } = true;
    public string ScreenshotPath { get; set; } = "";
    public string OverlayHotkey { get; set; } = "36,0,0,0";
    public string ScreenshotHotkey { get; set; } = "44,0,0,0";
    public bool PerGameScreenshotFolders { get; set; }
    public string AddonWatchFolder { get; set; } = "";
    public bool AddonWatcherEnabled { get; set; }
    public string ViewMode { get; set; } = "Detail";
    public bool TrayIconEnabled { get; set; }
    public bool CloseToTray { get; set; }
    public bool ShowMotdOnStartup { get; set; } = true;
    public bool CacheAllShaders { get; set; } = true;
    public bool VerboseLogging { get; set; }
    /// <summary>Opt-in silent updates for supported RHI-managed component families.</summary>
    public bool AutoUpdateComponents { get; set; }
    /// <summary>Periodic update-check cadence in hours. Normalized to the upstream-compatible 4h default when invalid.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 4;

    // DLSS / Streamline defaults. Empty means upstream-compatible "Default / don't change".
    public string DefaultDlssVersion { get; set; } = "";
    public string DefaultDlssdVersion { get; set; } = "";
    public string DefaultDlssgVersion { get; set; } = "";
    public string DefaultDlssnrVersion { get; set; } = "";
    public string DefaultStreamlineVersion { get; set; } = "";

    // w3/dllnames (#97): which OptiScaler INI template a new OptiScaler install starts from (upstream 2.8.7 "GPU Type" and
    // "DLSS Inputs"; defaults NVIDIA / on, as upstream). Set by the Settings dialog's controls.
    public string OptiScalerGpuType { get; set; } = "NVIDIA";
    public bool OptiScalerDlssInputs { get; set; } = true;
    // w5/update-policy rows
    // #177 (upstream AutoUpdateDlss / AutoUpdateStreamline, both off by default): files on the previous newest version
    // follow the manifest's newest. The baselines live in dlss-follow.json, never here.
    public bool FollowNewestDlss { get; set; }
    public bool FollowNewestStreamline { get; set; }
    // #178 (upstream GlobalSkip*Updates): components Update All and the background pass leave alone in every game.
    public bool GlobalSkipReShadeUpdates { get; set; }
    public bool GlobalSkipRenoDxUpdates { get; set; }
    public bool GlobalSkipReLimiterUpdates { get; set; }
    public bool GlobalSkipDisplayCommanderUpdates { get; set; }
    public bool GlobalSkipOptiScalerUpdates { get; set; }
    public bool GlobalSkipReFrameworkUpdates { get; set; }
    // w5/catalogue-info rows
    // w5/ini-presets rows
    // #181: ReShade's effect list as Tabs (true, upstream default) or a Tree ([OVERLAY] VariableListUseTabs).
    public bool ReShadeVariableListUseTabs { get; set; } = true;
    // #188: ReLimiter settings "Apply to all games" writes into every relimiter.ini and the template (upstream defaults).
    public string ReLimiterOsdHotkey { get; set; } = RHI.Platform.Linux.Deployment.ReLimiterIniEditor.DefaultOsdHotkey;
    public bool ReLimiterSharedPresets { get; set; }
    public bool ReLimiterDlssHooks { get; set; }
    public int ReLimiterTargetFps { get; set; }
    // #182: peak brightness written into RenoDX presets (upstream "HDR & Peak Brightness"; 0 = not set; on; presets 1-3).
    public int PeakNits { get; set; }
    public bool PeakNitsEnabled { get; set; } = true;
    public List<int> PeakNitsPresets { get; set; } = [1, 2, 3];
    // w5/shell-launch rows
    /// <summary>
    /// #187: the OptiScaler overlay key for every template and install (<c>[Menu] ShortcutKey</c>, a virtual-key code such
    /// as <c>0x2D</c>, or <c>auto</c>/<c>-1</c>); empty keeps each template's own key (the default: nothing is rewritten).
    /// </summary>
    public string OptiScalerMenuKey { get; set; } = "";
    // w5/reshade-core rows

    /// <summary>
    /// Runs on every load and save. The ReShade channel keeps only the values the channel list offers (Settings uses the
    /// same list as the ReShade profile, <c>OptionLists.ReShadeChannel</c>); a channel added there needs its row here, or
    /// the Settings default silently falls back to Stable. "NoAddons" is the w3/dllnames row (#91).
    /// </summary>
    public LinuxAppSettings Normalize()
    {
        UiLanguage = NormalizeUiLanguage(UiLanguage);
        ReShadeChannel = ReShadeChannel switch
        {
            "Nightly" => "Nightly",
            "Legacy" => "Legacy",
            "Custom" => "Custom",
            "NoAddons" => "NoAddons", // w3/dllnames (#91)
            _ => "Stable"
        };
        ScreenshotPath = ScreenshotPath?.Trim() ?? "";
        OverlayHotkey = NormalizeHotkey(OverlayHotkey, "36,0,0,0");
        ScreenshotHotkey = NormalizeHotkey(ScreenshotHotkey, "44,0,0,0");
        AddonWatchFolder = AddonWatchFolder?.Trim() ?? "";
        ViewMode = string.Equals(ViewMode, "Compact", StringComparison.OrdinalIgnoreCase) ? "Compact" : "Detail";
        if (!TrayIconEnabled) CloseToTray = false;
        DefaultDlssVersion = DefaultDlssVersion?.Trim() ?? "";
        DefaultDlssdVersion = DefaultDlssdVersion?.Trim() ?? "";
        DefaultDlssgVersion = DefaultDlssgVersion?.Trim() ?? "";
        DefaultDlssnrVersion = DefaultDlssnrVersion?.Trim() ?? "";
        DefaultStreamlineVersion = DefaultStreamlineVersion?.Trim() ?? "";
        if (UpdateCheckIntervalHours is < 1 or > 24) UpdateCheckIntervalHours = 4;
        OptiScalerGpuType = OptiScalerGpuType?.Trim().ToUpperInvariant() switch // w3/dllnames (#97)
        {
            "AMD" => "AMD",
            "INTEL" => "Intel",
            _ => "NVIDIA"
        };
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        ReLimiterOsdHotkey = RHI.Platform.Linux.Deployment.ReLimiterIniEditor.NormalizeHotkey(ReLimiterOsdHotkey); // #188
        ReLimiterTargetFps = RHI.Platform.Linux.Deployment.ReLimiterIniEditor.NormalizeTargetFps(ReLimiterTargetFps); // #188
        if (PeakNits is < 1 or > RHI.Platform.Linux.Deployment.RenoDxPeakNits.MaxNits) PeakNits = 0; // #182
        PeakNitsPresets = (PeakNitsPresets ?? [1, 2, 3]).Where(n => n is >= 1 and <= 3).Distinct().Order().ToList(); // #182
        // w5/shell-launch rows
        OptiScalerMenuKey = NormalizeOptiScalerMenuKey(OptiScalerMenuKey);
        // w5/reshade-core rows
        return this;
    }


    /// <summary>An OptiScaler overlay key as stored: auto, -1 or a virtual-key code 0x01–0xFE (upper-case hex); anything else = none.</summary>
    public static string NormalizeOptiScalerMenuKey(string? value)
    {
        var key = value?.Trim() ?? "";
        if (key.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        if (key == "-1") return "-1";
        return key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code)
               && code is > 0 and < 0xFF
            ? $"0x{code:X2}"
            : "";
    }

    public static string DefaultUiLanguage()
        => CultureInfo.CurrentUICulture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en-US";

    public static string NormalizeUiLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultUiLanguage();
        var normalized = value.Trim();
        if (normalized.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
        return "en-US";
    }

    internal static string NormalizeHotkey(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || parts.Any(part => !int.TryParse(part, out _))) return fallback;
        return string.Join(",", parts);
    }
}

public sealed class LinuxSettingsStore
{
    private readonly JsonFileStore<LinuxAppSettings> _file;

    public string SettingsPath { get; }
    public string CacheRoot { get; }
    public string DataRoot { get; }
    public string ConfigRoot => Path.GetDirectoryName(SettingsPath) ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux");

    public LinuxSettingsStore(string? settingsPath = null, string? cacheRoot = null, string? dataRoot = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "settings.json");
        CacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux");
        DataRoot = dataRoot ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux");
        _file = new JsonFileStore<LinuxAppSettings>(SettingsPath,
            parse: text => JsonSerializer.Deserialize<LinuxAppSettings>(text)?.Normalize());
    }

    /// <summary>Never throws: missing or unreadable settings yield defaults (an unreadable file is copied aside).</summary>
    public LinuxAppSettings Load() => _file.Load().Value ?? new LinuxAppSettings();

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable settings file is on disk.</summary>
    public void Save(LinuxAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        _file.Save(settings);
    }

    /// <summary>
    /// Explicit user reset. An unreadable file is copied aside before it is deleted; one that cannot
    /// even be copied is renamed aside instead, and when that fails too the reset is refused
    /// (<see cref="JsonStoreUnavailableException"/>) and the file stays untouched.
    /// </summary>
    public LinuxAppSettings Reset()
    {
        _file.ResetKeepingUnreadable();
        return new LinuxAppSettings();
    }

    public int PurgeCache()
    {
        if (!Directory.Exists(CacheRoot)) return 0;
        // Recursive delete unlinks symlinks without following them; the count must not follow them either.
        var count = SafeFileEnumeration.Files(CacheRoot, ignoreInaccessible: true).Count();
        Directory.Delete(CacheRoot, recursive: true);
        return count;
    }
}
