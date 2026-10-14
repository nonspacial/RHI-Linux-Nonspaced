using System.Text;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Settings;

public static class ReShadeGlobalSettingsApplier
{
    public static string ResolveScreenshotPath(LinuxAppSettings settings, DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(settings.ScreenshotPath)) return "";
        return settings.PerGameScreenshotFolders
            ? Path.Combine(settings.ScreenshotPath, SanitizeDirectoryName(game.Name))
            : settings.ScreenshotPath;
    }

    public static string ApplyToGame(LinuxAppSettings settings, DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        var root = game.BinaryDirectory ?? game.InstallPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Selected game binary directory is unavailable.");

        // Refs #90: nothing of an Xbox title PartingX is working on (or cannot be checked) is changed.
        var iniPath = RHI.Core.Services.XboxWriteGate.Checked(FindCaseInsensitive(root, "ReShade.ini") ?? Path.Combine(root, "ReShade.ini")); // w3/scan hook
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();

        SetValue(lines, "INPUT", "KeyOverlay", LinuxAppSettings.NormalizeHotkey(settings.OverlayHotkey, "36,0,0,0"));
        SetValue(lines, "INPUT", "KeyScreenshot", LinuxAppSettings.NormalizeHotkey(settings.ScreenshotHotkey, "44,0,0,0"));

        var screenshotPath = ResolveScreenshotPath(settings, game);
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            Directory.CreateDirectory(screenshotPath);
            SetValue(lines, "SCREENSHOT", "SavePath", ToWindowsStylePath(screenshotPath));
        }

        File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));
        return iniPath;
    }

    internal static void SetValue(List<string> lines, string section, string key, string value)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (var i = start + 1; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0 || !trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"{key}={value}";
            return;
        }
        lines.Insert(end, $"{key}={value}");
    }

    internal static string ToWindowsStylePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return Path.IsPathRooted(path) && path.StartsWith("/", StringComparison.Ordinal)
            ? "Z:" + normalized
            : normalized;
    }

    private static string SanitizeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Game" : result;
    }

    private static string? FindCaseInsensitive(string directory, string fileName)
        => Directory.EnumerateFiles(directory)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    end = j;
                    break;
                }
            }
            return (i, end);
        }
        return (-1, -1);
    }
}
