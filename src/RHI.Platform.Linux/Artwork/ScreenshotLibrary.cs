using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;

namespace RHI.Platform.Linux.Artwork;

/// <summary>Where a game's screenshots are and which ones are its own (newest first).</summary>
public sealed record ScreenshotListing(string? Folder, IReadOnlyList<FileInfo> Shots);

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.3 <c>ScreenshotLibraryService</c>): a game's ReShade screenshots for Spotlight's
/// Screenshots panel. Folder: <c>[SCREENSHOT] SavePath</c> from the game's ReShade.ini (a relative path is from the
/// game's folder), else the folder RHI would set (Settings › ReShade › Screenshot folder, per game when that is on), else
/// the game's folder. ReShade names screenshots "&lt;exe name&gt; &lt;date&gt; &lt;time&gt;", so a folder several games share is
/// filtered by the game's executable name. Linux: ReShade.ini sits beside the game's executable (the binary folder), and a
/// Windows path in it (<c>C:\…</c> inside the Wine prefix, <c>Z:\home\…</c> as RHI writes it) is mapped to the Linux
/// folder it names.
/// </summary>
public static class ScreenshotLibrary
{
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".jxr"];

    /// <summary>The game's screenshots: the folder (null when none exists) and the game's files in it, newest first.</summary>
    public static ScreenshotListing Find(DetectedGame game, string? rhiFolder, bool perGameFolders)
    {
        var gameDir = string.IsNullOrWhiteSpace(game.BinaryDirectory) ? game.InstallPath : game.BinaryDirectory;
        var folder = FindFolder(game, gameDir, rhiFolder);
        if (folder is null) return new ScreenshotListing(null, []);
        var own = (perGameFolders && SameFolder(folder, rhiFolder)) || SameFolder(folder, gameDir) || SameFolder(folder, game.InstallPath);
        return new ScreenshotListing(folder, List(folder, game.ExecutablePath ?? game.LaunchExecutablePath, own));
    }

    /// <summary>The folder the game's screenshots go to, or null when none exists.</summary>
    public static string? FindFolder(DetectedGame game, string? gameDir, string? rhiFolder)
    {
        var candidates = new List<string?>();
        foreach (var dir in new[] { gameDir, game.InstallPath }.Distinct(StringComparer.Ordinal))
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(ReadIniSavePath(dir, game.PrefixPath));
        candidates.Add(rhiFolder);
        candidates.Add(gameDir);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                if (Directory.Exists(c)) return Path.GetFullPath(c);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    /// <summary><c>[SCREENSHOT] SavePath</c> from the ReShade.ini in <paramref name="gameDir"/>, as a Linux path; null when not set.</summary>
    public static string? ReadIniSavePath(string gameDir, string? prefix)
    {
        try
        {
            var ini = Directory.Exists(gameDir)
                ? Directory.EnumerateFiles(gameDir, "reshade.ini", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault()
                : null;
            if (ini is null) return null;
            return ToLinuxFolder(ParseSavePath(File.ReadAllLines(ini)), gameDir, prefix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn("screenshots", $"ReShade.ini in {gameDir} unreadable", ex);
            return null;
        }
    }

    /// <summary>
    /// A SavePath value as a Linux folder: <c>Z:\home\me\Shots</c> → <c>/home/me/Shots</c>, <c>C:\Shots</c> → the prefix's
    /// <c>drive_c/Shots</c>, a relative path from the game's folder. Null for an empty value or a drive the prefix does not map.
    /// </summary>
    public static string? ToLinuxFolder(string? value, string gameDir, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            if (char.ToUpperInvariant(value[0]) == 'Z')
                return "/" + value[2..].Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(prefix) ? null : WindowsPathMapper.ToLinux(prefix, value);
        }
        var unix = value.Replace('\\', '/');
        return Path.IsPathRooted(unix) ? unix : Path.GetFullPath(Path.Combine(gameDir, unix));
    }

    /// <summary>SavePath in the [SCREENSHOT] section of ReShade.ini lines, or null.</summary>
    public static string? ParseSavePath(IEnumerable<string> lines)
    {
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals("[SCREENSHOT]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("SavePath", StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The game's screenshots in <paramref name="folder"/>, newest first: files starting with the exe name when there are
    /// any (ReShade's naming); otherwise every screenshot, but only in a folder that is the game's own.
    /// </summary>
    public static List<FileInfo> List(string folder, string? exePath, bool folderIsGamesOwn)
    {
        var files = new List<FileInfo>();
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
                if (Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) files.Add(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("screenshots", $"{folder} unreadable", ex);
            return files;
        }
        return Filter(files, exePath, folderIsGamesOwn).OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    public static IEnumerable<FileInfo> Filter(List<FileInfo> files, string? exePath, bool folderIsGamesOwn)
    {
        var exeName = string.IsNullOrEmpty(exePath) ? null : Path.GetFileNameWithoutExtension(exePath);
        if (!string.IsNullOrEmpty(exeName))
        {
            var mine = files.Where(f => MatchesExe(f.Name, exeName)).ToList();
            if (mine.Count > 0) return mine;
        }
        return folderIsGamesOwn ? files : [];
    }

    /// <summary>"Game 2026-11-08 21-30-00.png" belongs to Game.exe (the name, then a space, _ . - or the end).</summary>
    public static bool MatchesExe(string fileName, string exeName) =>
        fileName.StartsWith(exeName, StringComparison.OrdinalIgnoreCase)
        && (fileName.Length == exeName.Length || fileName[exeName.Length] is ' ' or '_' or '.' or '-');

    private static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
