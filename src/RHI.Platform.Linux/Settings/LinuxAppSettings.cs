using RHI.Core.Services;
using System.Globalization;
using System.Text.Json;
using RHI.Platform.Linux.Paths;

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
    public string SettingsPath { get; }
    public string CacheRoot { get; }
    public string DataRoot { get; }
    public string ConfigRoot => Path.GetDirectoryName(SettingsPath) ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux");

    public LinuxSettingsStore(string? settingsPath = null, string? cacheRoot = null, string? dataRoot = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "settings.json");
        CacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux");
        DataRoot = dataRoot ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux");
    }

    public LinuxAppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LinuxAppSettings();
            var json = File.ReadAllText(SettingsPath);
            return (JsonSerializer.Deserialize<LinuxAppSettings>(json) ?? new LinuxAppSettings()).Normalize();
        }
        catch
        {
            return new LinuxAppSettings();
        }
    }

    public void Save(LinuxAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        Directory.CreateDirectory(ConfigRoot);
        var temp = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temp, json);
        File.Move(temp, SettingsPath, true);
    }

    public LinuxAppSettings Reset()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
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
