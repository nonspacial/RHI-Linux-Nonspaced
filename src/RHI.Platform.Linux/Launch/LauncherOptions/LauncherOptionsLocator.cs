using System.Globalization;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>Where a game's launcher keeps its launch options: the launcher, its files, and the game's id there.</summary>
public sealed record LauncherOptionsTarget(LauncherKind Kind, IReadOnlyList<string> Files, int? SteamAppId = null, string? HeroicAppName = null);

/// <summary>
/// #318: finds the launcher file that holds a game's launch options.
/// <list type="bullet">
/// <item>Steam (a Steam game, not a non-Steam shortcut): <c>&lt;steam&gt;/userdata/&lt;account&gt;/config/localconfig.vdf</c> of
/// the Steam the game's launch uses (Flatpak Steam for <c>flatpak run com.valvesoftware.Steam</c>, else the native one), for
/// every account that lists the app, else the most recent account (<c>config/loginusers.vdf</c> <c>MostRecent</c>), else the
/// only account;</item>
/// <item>Lutris (a game with its own Lutris entry, not a game started through a store client's entry): the entry's
/// <c>games/&lt;slug&gt;-&lt;id&gt;.yml</c> in Lutris' native or Flatpak folders;</item>
/// <item>Heroic: <c>GamesConfig/&lt;appName&gt;.json</c> in native or Flatpak Heroic (it is there once the game's settings were
/// saved in Heroic).</item>
/// </list>
/// Anything else (a launcher that only opens, the Xbox app, Bottles, itch, a command the user typed) has no target.
/// </summary>
public sealed partial class LauncherOptionsLocator
{
    private readonly Func<IEnumerable<string>> _steamRoots;
    private readonly Func<IEnumerable<string>> _lutrisRoots;
    private readonly Func<IEnumerable<string>> _heroicRoots;

    public LauncherOptionsLocator(Func<IEnumerable<string>>? steamRoots = null, Func<IEnumerable<string>>? lutrisRoots = null,
        Func<IEnumerable<string>>? heroicRoots = null)
    {
        _steamRoots = steamRoots ?? LinuxPaths.SteamRoots;
        _lutrisRoots = lutrisRoots ?? LinuxPaths.LutrisConfigRoots;
        _heroicRoots = heroicRoots ?? LinuxPaths.HeroicRoots;
    }

    /// <summary>The launcher that starts <paramref name="game"/> when it is one RHI can write to (no file needed yet), else null.</summary>
    public static LauncherKind? KindOf(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (LinuxGameLauncher.IsUserCommand(game) || game.XboxPackage is not null || !string.IsNullOrWhiteSpace(game.ParentLauncherName)) return null;
        if (string.Equals(game.Source, SteamGameDetector.ShortcutSource, StringComparison.OrdinalIgnoreCase)) return null;
        return LaunchHandoffClassifier.Classify(game) switch
        {
            LaunchHandoff.Steam when game.SteamAppId is > 0 => LauncherKind.Steam,
            LaunchHandoff.Lutris when LutrisSlug(game.LaunchCommand) is not null => LauncherKind.Lutris,
            LaunchHandoff.Heroic when SafeAppName(game.HeroicAppName ?? game.EpicAppName) is not null => LauncherKind.Heroic,
            _ => null
        };
    }

    /// <summary>The files that hold <paramref name="game"/>'s launch options, or null (not a launcher RHI writes to, or no file).</summary>
    public LauncherOptionsTarget? Locate(DetectedGame game)
    {
        return KindOf(game) switch
        {
            LauncherKind.Steam => Steam(game),
            LauncherKind.Lutris => Lutris(game),
            LauncherKind.Heroic => Heroic(game),
            _ => null
        };
    }

    // ── Steam ─────────────────────────────────────────────────────────────────────────────

    private LauncherOptionsTarget? Steam(DetectedGame game)
    {
        var appId = game.SteamAppId!.Value;
        var flatpak = (game.LaunchCommand ?? "").Contains("com.valvesoftware.Steam", StringComparison.Ordinal);
        var roots = Distinct(_steamRoots()).Where(r => IsFlatpakSteam(r) == flatpak).ToList();
        var files = new List<string>();
        foreach (var root in roots)
        {
            var users = Users(root);
            var listing = users.Where(u => ListsApp(u.File, appId)).Select(u => u.File).ToList();
            if (listing.Count == 0 && MostRecentAccount(root) is { } recent && users.FirstOrDefault(u => u.Account == recent) is { File: { } recentFile })
                listing.Add(recentFile);
            if (listing.Count == 0 && users.Count == 1) listing.Add(users[0].File);
            files.AddRange(listing);
        }
        return files.Count == 0 ? null : new LauncherOptionsTarget(LauncherKind.Steam, files, SteamAppId: appId);
    }

    private static bool IsFlatpakSteam(string root)
        => (root.Replace('\\', '/').TrimEnd('/') + "/").Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal)
           || (PathIdentity.Canonicalize(root) + "/").Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);

    private static List<(string Account, string File)> Users(string root)
    {
        var result = new List<(string, string)>();
        var userdata = Path.Combine(root, "userdata");
        try
        {
            if (!Directory.Exists(userdata)) return result;
            foreach (var dir in Directory.EnumerateDirectories(userdata).Order(StringComparer.Ordinal))
            {
                var account = Path.GetFileName(dir);
                // "0" and "anonymous" are Steam's own, not a signed-in account.
                if (!ulong.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) continue;
                var file = Path.Combine(dir, "config", SteamLocalConfig.FileName);
                if (File.Exists(file)) result.Add((account, file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("launcher-options", $"Steam userdata {userdata} not readable", ex);
        }
        return result;
    }

    private static bool ListsApp(string file, int appId)
    {
        try { return SteamLocalConfig.Read(File.ReadAllBytes(file), appId).AppListed; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
    }

    /// <summary>The account id (SteamID64 − 76561197960265728) of <c>loginusers.vdf</c>'s MostRecent user, or null.</summary>
    internal static string? MostRecentAccount(string root)
    {
        var file = Path.Combine(root, "config", "loginusers.vdf");
        try
        {
            if (!File.Exists(file)) return null;
            var users = SteamKeyValues.Parse(File.ReadAllText(file)).Child("users");
            if (users is null) return null;
            foreach (var (steamId, user) in users.Children)
            {
                if (user.Value("MostRecent") != "1" || !ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out var id64)) continue;
                const ulong Base = 76561197960265728UL;
                return id64 > Base ? (id64 - Base).ToString(CultureInfo.InvariantCulture) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // ── Lutris ────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"lutris:(?://)?rungame/(?<slug>[A-Za-z0-9._-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RunGame();

    internal static string? LutrisSlug(string? command)
        => command is not null && RunGame().Match(command) is { Success: true } m ? m.Groups["slug"].Value : null;

    private LauncherOptionsTarget? Lutris(DetectedGame game)
    {
        var slug = LutrisSlug(game.LaunchCommand)!;
        // A card that had to tell same-folder entries apart carries its own config file.
        if (game.LibraryIdentityPath is { } identity && identity.EndsWith(".yml", StringComparison.Ordinal) && File.Exists(identity))
            return new LauncherOptionsTarget(LauncherKind.Lutris, [identity]);
        var roots = Distinct(_lutrisRoots()).ToList();
        var pga = LutrisPga.Load(LutrisPga.Candidates(roots, LinuxPaths.Home, LinuxPaths.DataHome));
        var matches = new List<string>();
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.yml").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var fileSlug = pga.TryGetValue(stem, out var row) && row.Slug is { } s ? s : LutrisGameDetector.ResolveLutrisSlug(stem);
                if (row?.Installed == false) continue;
                if (string.Equals(fileSlug, slug, StringComparison.Ordinal)) matches.Add(file);
            }
        }
        // Two entries with the same slug: RHI cannot tell which one Lutris starts, so it writes to neither.
        return matches.Count == 1 ? new LauncherOptionsTarget(LauncherKind.Lutris, matches) : null;
    }

    // ── Heroic ────────────────────────────────────────────────────────────────────────────

    private static string? SafeAppName(string? appName)
        => string.IsNullOrWhiteSpace(appName) || appName.Contains('/') || appName.Contains('\0') || appName is "." or ".." ? null : appName;

    private LauncherOptionsTarget? Heroic(DetectedGame game)
    {
        var appName = SafeAppName(game.HeroicAppName ?? game.EpicAppName)!;
        var files = Distinct(_heroicRoots())
            .Select(root => Path.Combine(root, HeroicGameArgs.Folder, appName + ".json"))
            .Where(File.Exists)
            .ToList();
        return files.Count == 0 ? null : new LauncherOptionsTarget(LauncherKind.Heroic, files, HeroicAppName: appName);
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
            if (Directory.Exists(root) && seen.Add(PathIdentity.Canonicalize(root))) yield return root;
    }
}
