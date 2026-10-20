using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

/// <summary>Where a game shortcut goes; each only on its own explicit right-click choice.</summary>
public enum ShortcutPlace
{
    /// <summary>The desktop folder (<c>xdg-user-dir DESKTOP</c>).</summary>
    Desktop,

    /// <summary>The applications menu (<c>$XDG_DATA_HOME/applications</c>).</summary>
    ApplicationsMenu
}

public enum ShortcutOutcome
{
    /// <summary>A new shortcut was written.</summary>
    Created,

    /// <summary>RHI's earlier shortcut for this game was rewritten (same file).</summary>
    Updated,

    /// <summary>No desktop folder is configured (xdg-user-dir names the home folder itself, or none exists).</summary>
    NoDesktopFolder,

    /// <summary>A file RHI did not write already has the shortcut's name; it was left alone.</summary>
    ForeignFileInTheWay,

    /// <summary>How this RHI was started cannot be told (no executable path), so no shortcut can start it.</summary>
    NoRhiCommand,

    /// <summary>The game's key cannot be written into a desktop entry (control characters).</summary>
    UnusableKey,

    /// <summary>The file could not be written (see <see cref="ShortcutResult.Error"/>; logged).</summary>
    Failed
}

public sealed record ShortcutResult(ShortcutOutcome Outcome, string? FilePath = null, string? Error = null)
{
    public bool Succeeded => Outcome is ShortcutOutcome.Created or ShortcutOutcome.Updated;
}

/// <summary>
/// Desktop / applications-menu shortcuts that start a library game through RHI (#162; upstream 2.8.6 <c>6cc2b2d</c>
/// shortcut part, <c>27c66b7</c>). Created only when the user picks "Create shortcut" on a card (owner decision), never
/// automatically. The entry runs <c>&lt;RHI&gt; --launch &lt;GameKey&gt;</c>; a running RHI takes it over the single-instance
/// hand-off and starts the game through its normal Play path (HDR plan included). The key is the library key
/// (<c>steam:&lt;AppID&gt;</c>, <c>heroic:&lt;app&gt;</c>, else source + folder), which survives a Steam/Heroic library move
/// (#147). The file is <c>rhi-linux-game-&lt;name&gt;-&lt;key hash&gt;.desktop</c> with an <c>X-RHI-Linux-GameKey</c> line: creating
/// it again rewrites RHI's own file, and a same-named file without that line is never overwritten. Icon (#202, upstream
/// <c>ShortcutService</c>'s order): the largest icon of the game's exe (<see cref="PeIconReader"/>, written as PNG), else the
/// game's Steam library art when Steam has it, both kept in RHI's data folder (they outlive the game folder and Steam's
/// cache), else RHI's icon. An exe without a readable icon (an encrypted Game Pass exe, a native Linux binary) falls back
/// silently. RHI's icon files of shortcuts that no longer exist are removed whenever a shortcut is created.
/// </summary>
public sealed class DesktopShortcutService
{
    public const string GameKeyField = "X-RHI-Linux-GameKey";
    public const string DefaultIcon = "rhi-linux";

    private static readonly UnixFileMode MenuMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    // A launcher on the desktop must be executable for KDE/GNOME to offer to run it.
    private static readonly UnixFileMode DesktopMode = MenuMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly Func<CancellationToken, Task<string?>> _desktopFolder;
    private readonly string _applicationsFolder;
    private readonly string _iconFolder;
    private readonly Func<IReadOnlyList<string>?> _rhiCommand;
    private readonly Func<IReadOnlyList<string>> _steamRoots;

    /// <param name="desktopFolder">Test seam (default: <see cref="FindDesktopFolderAsync"/>).</param>
    /// <param name="applicationsFolder">Test seam (default: <c>$XDG_DATA_HOME/applications</c>).</param>
    /// <param name="iconFolder">Test seam (default: <c>$XDG_DATA_HOME/rhi-linux/shortcut-icons</c>).</param>
    /// <param name="rhiCommand">Test seam (default: <see cref="DesktopEntryFormat.RhiCommand"/>).</param>
    /// <param name="steamRoots">Test seam (default: <see cref="LinuxPaths.SteamRoots"/>).</param>
    public DesktopShortcutService(
        Func<CancellationToken, Task<string?>>? desktopFolder = null, string? applicationsFolder = null, string? iconFolder = null,
        Func<IReadOnlyList<string>?>? rhiCommand = null, Func<IReadOnlyList<string>>? steamRoots = null)
    {
        _desktopFolder = desktopFolder ?? FindDesktopFolderAsync;
        _applicationsFolder = applicationsFolder ?? DesktopEntryFormat.ApplicationsFolder;
        _iconFolder = iconFolder ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "shortcut-icons");
        _rhiCommand = rhiCommand ?? (() => DesktopEntryFormat.RhiCommand());
        _steamRoots = steamRoots ?? (() => LinuxPaths.SteamRoots().ToList());
    }

    public async Task<ShortcutResult> CreateAsync(DetectedGame game, string gameKey, ShortcutPlace place, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(gameKey) || gameKey.Any(char.IsControl)) return new ShortcutResult(ShortcutOutcome.UnusableKey);
        if (_rhiCommand() is not { Count: > 0 } command) return new ShortcutResult(ShortcutOutcome.NoRhiCommand);

        var folder = place == ShortcutPlace.Desktop ? await _desktopFolder(cancellationToken).ConfigureAwait(false) : _applicationsFolder;
        if (folder is null) return new ShortcutResult(ShortcutOutcome.NoDesktopFolder);
        var path = Path.Combine(folder, FileName(game.Name, gameKey));
        try
        {
            var existed = File.Exists(path);
            if (existed && !IsOurs(path, gameKey)) return new ShortcutResult(ShortcutOutcome.ForeignFileInTheWay, path);
            var icon = ExeIcon(game, gameKey) ?? SteamIcon(game, gameKey) ?? DefaultIcon;
            ForgetOtherIcons(game, gameKey, keep: icon);
            var entry = BuildEntry(game.Name, command, icon, gameKey);
            AtomicFile.WriteStoreFile(path, Encoding.UTF8.GetBytes(entry), place == ShortcutPlace.Desktop ? DesktopMode : MenuMode);
            var desktop = place == ShortcutPlace.Desktop ? folder : await _desktopFolder(cancellationToken).ConfigureAwait(false);
            RemoveUnusedIcons(desktop);
            return new ShortcutResult(existed ? ShortcutOutcome.Updated : ShortcutOutcome.Created, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shortcut", $"Could not write the shortcut {path}", ex);
            return new ShortcutResult(ShortcutOutcome.Failed, path, ex.Message);
        }
    }

    /// <summary>The entry's text (Desktop Entry Specification 1.5).</summary>
    internal static string BuildEntry(string gameName, IReadOnlyList<string> rhiCommand, string icon, string gameKey)
    {
        var name = string.IsNullOrWhiteSpace(gameName) ? gameKey : gameName.Trim();
        var exec = DesktopEntryFormat.ExecLine([.. rhiCommand, "--launch", gameKey]);
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.5",
            $"Name={DesktopEntryFormat.EscapeString(name)}",
            $"Exec={exec}",
            $"Icon={DesktopEntryFormat.EscapeString(icon)}",
            "Terminal=false",
            "Categories=Game;",
            "StartupNotify=false",
            $"{GameKeyField}={DesktopEntryFormat.EscapeString(gameKey)}",
            "");
    }

    /// <summary><c>rhi-linux-game-&lt;name slug&gt;-&lt;8 hex of the key's SHA-256&gt;.desktop</c>: one file per game, stable across renames of the card.</summary>
    internal static string FileName(string gameName, string gameKey)
    {
        var slug = Regex.Replace(gameName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        if (slug.Length == 0) slug = "game";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameKey)))[..8].ToLowerInvariant();
        return $"rhi-linux-game-{slug}-{hash}.desktop";
    }

    /// <summary>Whether the file at <paramref name="path"/> is RHI's shortcut for this key (its marker line).</summary>
    private static bool IsOurs(string path, string gameKey)
    {
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) return false;
        var marker = $"{GameKeyField}={DesktopEntryFormat.EscapeString(gameKey)}";
        return File.ReadLines(path).Any(line => line.Trim() == marker);
    }

    /// <summary>The icon file RHI keeps for this shortcut, with the given extension (one per game key, both places).</summary>
    private string IconPath(DetectedGame game, string gameKey, string extension)
        => Path.Combine(_iconFolder, FileName(game.Name, gameKey).Replace(".desktop", extension, StringComparison.Ordinal));

    /// <summary>
    /// #202: the largest icon of the game's exe as PNG in RHI's icon folder (rewritten on every creation); null when the exe
    /// is unknown or has no icon RHI can read (encrypted Game Pass exe, native binary, malformed resource) or the write failed.
    /// </summary>
    internal string? ExeIcon(DetectedGame game, string gameKey)
    {
        var exe = game.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe)) return null;
        if (PeIconReader.ReadLargestIconPng(exe) is not { } png) return null;
        var target = IconPath(game, gameKey, ".png");
        try
        {
            Directory.CreateDirectory(_iconFolder);
            AtomicFile.WriteStoreFile(target, png, IconMode);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shortcut", $"Could not write the game's icon {target}; the shortcut uses the next icon", ex);
            return null;
        }
    }

    /// <summary>Removes RHI's other icon copy of this shortcut (the Steam art after an exe icon, or both for RHI's own icon).</summary>
    private void ForgetOtherIcons(DetectedGame game, string gameKey, string keep)
    {
        foreach (var extension in IconExtensions)
        {
            var candidate = IconPath(game, gameKey, extension);
            if (candidate != keep && File.Exists(candidate)) FileCleanup.TryDelete(candidate);
        }
    }

    /// <summary>
    /// Removes RHI's icon files (<c>rhi-linux-game-*.png|.jpg</c> in RHI's own icon folder) whose shortcut is in neither the
    /// desktop nor the applications folder any more. Skipped while the desktop folder is unknown (it cannot be told).
    /// </summary>
    private void RemoveUnusedIcons(string? desktopFolder)
    {
        if (desktopFolder is null || !Directory.Exists(_iconFolder)) return;
        try
        {
            foreach (var icon in Directory.EnumerateFiles(_iconFolder, "rhi-linux-game-*"))
            {
                if (!IconExtensions.Contains(Path.GetExtension(icon), StringComparer.Ordinal)) continue;
                var entry = Path.GetFileNameWithoutExtension(icon) + ".desktop";
                if (File.Exists(Path.Combine(desktopFolder, entry)) || File.Exists(Path.Combine(_applicationsFolder, entry))) continue;
                FileCleanup.TryDelete(icon);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shortcut", $"Could not tidy the shortcut icons in {_iconFolder}", ex);
        }
    }

    private static readonly string[] IconExtensions = [".png", ".jpg"];
    private static readonly UnixFileMode IconMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// The game's Steam icon (librarycache: the small <c>&lt;sha1&gt;.jpg</c> in <c>&lt;AppID&gt;/</c>, the older
    /// <c>&lt;AppID&gt;_icon.jpg</c>, else the header art), copied into RHI's icon folder; null for non-Steam games or none found.
    /// </summary>
    internal string? SteamIcon(DetectedGame game, string gameKey)
    {
        if (game.SteamAppId is not int appId) return null;
        foreach (var root in _steamRoots())
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            var folder = Path.Combine(cache, appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var candidates = new List<string>();
            if (Directory.Exists(folder))
            {
                candidates.AddRange(Directory.EnumerateFiles(folder, "*.jpg")
                    .Where(file => Regex.IsMatch(Path.GetFileNameWithoutExtension(file), "^[0-9a-f]{40}$"))
                    .Order(StringComparer.Ordinal));
            }
            candidates.Add(Path.Combine(cache, $"{appId}_icon.jpg"));
            candidates.Add(Path.Combine(folder, "header.jpg"));
            candidates.Add(Path.Combine(cache, $"{appId}_header.jpg"));
            foreach (var source in candidates)
            {
                if (!File.Exists(source) || new FileInfo(source).Length is 0 or > 4 * 1024 * 1024) continue;
                var target = IconPath(game, gameKey, ".jpg");
                try
                {
                    Directory.CreateDirectory(_iconFolder);
                    AtomicFile.Copy(source, target);
                    return target;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("shortcut", $"Could not copy the Steam icon {source}; the shortcut uses RHI's icon", ex);
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The desktop folder: <c>xdg-user-dir DESKTOP</c>, else <c>XDG_DESKTOP_DIR</c> in <c>user-dirs.dirs</c>, else
    /// <c>~/Desktop</c> when it exists. Null when the answer is the home folder itself (no desktop configured) or missing.
    /// </summary>
    public static async Task<string?> FindDesktopFolderAsync(CancellationToken cancellationToken)
    {
        var home = Path.TrimEndingDirectorySeparator(LinuxPaths.Home);
        string? folder = null;
        if (ExternalProcess.FindOnPath("xdg-user-dir") is { } tool)
        {
            try
            {
                var result = await ExternalProcess.RunAsync(new ProcessStartInfo(tool) { ArgumentList = { "DESKTOP" } },
                    TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0) folder = result.StandardOutput.Trim();
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Warn("shortcut", "xdg-user-dir did not answer; reading user-dirs.dirs", ex);
            }
        }
        folder ??= FromUserDirs(Path.Combine(LinuxPaths.ConfigHome, "user-dirs.dirs"), home);
        folder ??= Path.Combine(home, "Desktop");
        folder = Path.TrimEndingDirectorySeparator(folder);
        return Path.IsPathRooted(folder) && folder != home && Directory.Exists(folder) ? folder : null;
    }

    /// <summary><c>XDG_DESKTOP_DIR="$HOME/…"</c> from a user-dirs.dirs file (the format xdg-user-dirs writes).</summary>
    internal static string? FromUserDirs(string file, string home)
    {
        try
        {
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
            {
                var match = Regex.Match(line, "^\\s*XDG_DESKTOP_DIR\\s*=\\s*\"(.*)\"\\s*$");
                if (!match.Success) continue;
                var value = match.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal);
                return value.StartsWith("$HOME", StringComparison.Ordinal) ? home + value[5..] : value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("shortcut", $"Could not read {file}", ex);
        }
        return null;
    }
}
