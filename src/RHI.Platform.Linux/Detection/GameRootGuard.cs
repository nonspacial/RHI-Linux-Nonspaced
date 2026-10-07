using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>Why a folder cannot be a game root; <see cref="None"/> means it can.</summary>
public enum GameRootProblem
{
    None,
    Missing,
    FilesystemRoot,
    HomeFolder,
    SystemFolder,
    SharedFolder,
    WinePrefix,
    SteamLibraryFolder
}

/// <summary>
/// One rule, shared by every detector and the Add/Edit game dialogs, for folders that hold far more
/// than one game: the filesystem root, the home folder and its containers, system and mount folders,
/// shared executable folders, a whole Wine prefix (or its Windows system folders) and Steam library
/// folders. Indexing any of them walks up to the file cap (200 000 files) of unrelated data, and a
/// patch would be deployed beside the wrong files.
/// </summary>
public static class GameRootGuard
{
    private static readonly string[] SystemFolders =
    [
        "/bin", "/sbin", "/lib", "/lib64", "/usr", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib64", "/usr/local",
        "/usr/local/bin", "/usr/local/sbin", "/usr/share", "/usr/games", "/usr/local/games", "/etc", "/opt", "/var",
        "/tmp", "/srv", "/boot", "/dev", "/proc", "/sys", "/run", "/media", "/mnt", "/run/media", "/home", "/root"
    ];

    // Folders directly under a prefix's drive_c that hold every installed program, not one game.
    private static readonly string[] PrefixContainers =
        ["windows", "Program Files", "Program Files (x86)", "ProgramData", "users", "Games"];

    public static bool IsAcceptable(string? path, string? home = null) => Check(path, home) == GameRootProblem.None;

    public static GameRootProblem Check(string? path, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return GameRootProblem.Missing;
        home ??= LinuxPaths.Home;
        var spellings = Spellings(path);
        var homes = string.IsNullOrWhiteSpace(home) ? [] : Spellings(home);

        if (spellings.Contains("/")) return GameRootProblem.FilesystemRoot;
        if (homes.Any(h => spellings.Contains(h) || spellings.Any(s => h.StartsWith(s + "/", StringComparison.Ordinal))))
            return GameRootProblem.HomeFolder;
        if (spellings.Any(s => SystemFolders.Contains(s, StringComparer.Ordinal) || IsMountPoint(s)))
            return GameRootProblem.SystemFolder;
        if (homes.Any(h => spellings.Any(s => IsHomeContainer(s, h))))
            return GameRootProblem.SharedFolder;
        if (LutrisGameDetector.IsWinePrefixRoot(path) || IsPrefixSystemFolder(path))
            return GameRootProblem.WinePrefix;
        if (IsSteamLibraryFolder(path)) return GameRootProblem.SteamLibraryFolder;
        return GameRootProblem.None;
    }

    // The path as given (absolute, no trailing slash) and with every symlink resolved.
    private static HashSet<string> Spellings(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 1) full = full.TrimEnd('/');
        return new HashSet<string>(StringComparer.Ordinal) { full, PathIdentity.Canonicalize(full) };
    }

    // /media/<disk>, /mnt/<disk>, /run/media/<user> and /run/media/<user>/<disk>: whole drives.
    private static bool IsMountPoint(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            2 => parts[0] is "media" or "mnt",
            3 or 4 => parts[0] == "run" && parts[1] == "media",
            _ => false
        };
    }

    private static bool IsHomeContainer(string path, string home)
    {
        string[] containers =
        [
            "bin", ".local", ".local/bin", ".local/share", ".config", ".cache", ".var", ".var/app", ".steam",
            ".wine", "Games", "Applications", "Downloads", "Desktop", "Documents"
        ];
        return containers.Any(c => path == home + "/" + c);
    }

    private static bool IsPrefixSystemFolder(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (name.Equals("drive_c", StringComparison.OrdinalIgnoreCase) && parent is not null && LutrisGameDetector.IsWinePrefixRoot(parent))
            return true;
        return parent is not null &&
               Path.GetFileName(parent).Equals("drive_c", StringComparison.OrdinalIgnoreCase) &&
               PrefixContainers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSteamLibraryFolder(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        var name = Path.GetFileName(full);
        var parentName = Path.GetFileName(Path.GetDirectoryName(full) ?? string.Empty);
        return name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
               (parentName.Equals("steamapps", StringComparison.OrdinalIgnoreCase) &&
                name is "common" or "compatdata" or "shadercache" or "downloading" or "workshop") ||
               Directory.Exists(Path.Combine(full, "steamapps", "common"));
    }
}
