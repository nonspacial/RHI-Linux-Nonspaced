using System.Text.Json;
using System.Globalization;
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

    public LinuxAppSettings Normalize()
    {
        UiLanguage = NormalizeUiLanguage(UiLanguage);
        ReShadeChannel = ReShadeChannel switch
        {
            "Nightly" => "Nightly",
            "Legacy" => "Legacy",
            "Custom" => "Custom",
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
        return this;
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

    /// <summary>Explicit user reset. An unreadable file is still copied aside before it is deleted.</summary>
    public LinuxAppSettings Reset()
    {
        _file.Load();
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        return new LinuxAppSettings();
    }

    public int PurgeCache()
    {
        if (!Directory.Exists(CacheRoot)) return 0;
        var count = Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(CacheRoot, recursive: true);
        return count;
    }
}
