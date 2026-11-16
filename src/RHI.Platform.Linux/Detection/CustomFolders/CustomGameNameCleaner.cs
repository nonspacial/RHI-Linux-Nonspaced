using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Detection.CustomFolders;

/// <summary>
/// #293 (upstream 2.9.0 <c>CustomGameNameCleaner</c>, ported as is): turns a downloaded game's folder name
/// ("Hades.v1.38290-GOG", "Hollow Knight [Repack]") into a presentable game name for the custom game folders scan.
/// </summary>
public static partial class CustomGameNameCleaner
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Release groups and distribution words seen in folder names. Removed as whole words.</summary>
    private static readonly string[] TagWords =
    [
        "GOG", "GOG Galaxy", "Repack", "FitGirl", "FitGirl Repack", "DODI", "DODI Repack", "ElAmigos", "KaOs", "Xatab",
        "CODEX", "PLAZA", "SKIDROW", "RELOADED", "EMPRESS", "TENOKE", "RUNE", "FLT", "DARKSiDERS", "RAZOR1911",
        "CPY", "HOODLUM", "PROPHET", "TiNYiSO", "SiMPLEX", "DOGE", "ANOMALY", "GoldBerg", "P2P", "Chronos", "DINOByTES",
        "Portable", "DRM Free", "DRM-Free", "DRMFree", "Steam Rip", "SteamRip", "Pre-Installed", "PreInstalled",
        "Unlocked", "Cracked", "Crack", "x64", "x86", "Win64", "Win32", "Multi\\d+", "MULTi\\d+",
    ];

    private static readonly string TagAlternation = string.Join("|", TagWords.Select(w => w.Replace(" ", "[ ._-]?")));
    private static readonly Regex Brackets = new(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}", Opts);
    private static readonly Regex TrailingGroup = new(@"[-_.](" + TagAlternation + @")$", Opts);
    // Dotted abbreviations like S.T.A.L.K.E.R. keep their dots.
    private static readonly Regex DottedAbbrev = new(@"\b(?:[A-Za-z]\.){2,}[A-Za-z]?\.?", Opts);
    private static readonly Regex Version = new(@"(?<![A-Za-z0-9])v\s?\d+(?:[._]\d+)*[a-z]?(?![A-Za-z0-9])", Opts);
    private static readonly Regex BareVersion = new(@"(?<![A-Za-z0-9])\d+(?:\.\d+){2,}(?![A-Za-z0-9])", Opts);
    private static readonly Regex Build = new(@"(?<![A-Za-z0-9])(?:build|b)[\s._-]?\d{3,}(?![A-Za-z0-9])", Opts);
    private static readonly Regex Words = new(@"(?<![A-Za-z0-9])(" + TagAlternation + @")(?![A-Za-z0-9])", Opts);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\s[-–+&]\s?)+$")]
    private static partial Regex TrailingSeparators();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    /// <summary>Cleans a folder name into a game name. Returns the input trimmed if cleaning removes everything.</summary>
    public static string Clean(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "";
        var s = folderName.Trim();
        s = Brackets.Replace(s, " ");
        // "Game.v1.2-GOG" style names can end with several tags.
        for (var i = 0; i < 3; i++)
        {
            var before = s;
            s = TrailingGroup.Replace(s.Trim(), "");
            if (s == before) break;
        }
        s = DottedAbbrev.Replace(s, m => m.Value.Replace('.', '\u0001'));
        s = Version.Replace(s, " ");
        s = BareVersion.Replace(s, " ");
        s = Build.Replace(s, " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('\u0001', '.');
        s = Words.Replace(s, " ");
        // Leftover separators ("Game - ", "Game -  Deluxe") and spacing.
        s = Spaces().Replace(s, " ").Trim();
        s = TrailingSeparators().Replace(s, "").Trim();
        s = s.Trim('-', '–', '+', ' ', ',');
        s = CleanFolderName(s);
        return s.Length > 0 ? s : folderName.Trim();
    }

    /// <summary>Upstream's <c>DragDropHandler.CleanFolderName</c>: underscores, dashes and camelCase boundaries become spaces.</summary>
    public static string CleanFolderName(string name)
    {
        var cleaned = name.Replace('_', ' ').Replace('-', ' ');
        cleaned = CamelBoundary().Replace(cleaned, " ");
        return Spaces().Replace(cleaned, " ").Trim();
    }

    /// <summary>Upstream's <c>DragDropHandler.CleanUnrealExeName</c>: drops <c>-Win64-Shipping</c> and the like.</summary>
    public static string CleanUnrealExeName(string exeName)
    {
        var cleaned = Regex.Replace(exeName, @"[_-]?(Win64|WinGDK|Win32)[_-]?Shipping$", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[_-]?Shipping$", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[_-]?(Win64|WinGDK|Win32)[_-]?Test$", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[_-]?(Win64|WinGDK|Win32)$", "", RegexOptions.IgnoreCase);
        return cleaned.Trim('-', '_', ' ');
    }

    /// <summary>Folder names that say nothing about the game (the scan falls back to the exe / Unreal project name).</summary>
    public static bool IsGenericFolderName(string name)
    {
        var n = CustomGameExeScorer.Norm(name);
        return n.Length < 2 || n is "game" or "games" or "bin" or "binaries" or "win64" or "win32" or "windows"
            or "windowsnoeditor" or "app" or "client" or "pc" or "data" or "install" or "files";
    }
}
