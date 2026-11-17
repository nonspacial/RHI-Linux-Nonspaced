using System.Text.RegularExpressions;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Achievements;

/// <summary>
/// w14/layouts-b (#307): the Steam client's own files on Linux — native Steam (<c>~/.local/share/Steam</c>, reached also
/// through <c>~/.steam/steam</c> and <c>~/.steam/root</c>) and Flatpak Steam (<c>~/.var/app/com.valvesoftware.Steam/…</c>).
/// Upstream reads the Windows registry (SteamPath, Language, ActiveProcess\ActiveUser); Linux Steam keeps the same values
/// in <c>registry.vdf</c> beside the client, and the users in <c>config/loginusers.vdf</c>.
/// </summary>
public sealed class SteamLocalFiles
{
    private readonly IReadOnlyList<string> _roots;
    private readonly IReadOnlyList<string> _registryFiles;

    /// <param name="roots">Steam install folders to look in (default: native and Flatpak).</param>
    /// <param name="registryFiles">registry.vdf files (default: <c>~/.steam/registry.vdf</c> and Flatpak's).</param>
    public SteamLocalFiles(IReadOnlyList<string>? roots = null, IReadOnlyList<string>? registryFiles = null)
    {
        var home = LinuxPaths.Home;
        var flatpak = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam");
        _roots = roots ??
        [
            Path.Combine(LinuxPaths.DataHome, "Steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(flatpak, ".local", "share", "Steam"),
            Path.Combine(flatpak, "data", "Steam"),
        ];
        _registryFiles = registryFiles ?? [Path.Combine(home, ".steam", "registry.vdf"), Path.Combine(flatpak, ".steam", "registry.vdf")];
    }

    /// <summary>The Steam folders that exist, each once (links resolved), in the order given.</summary>
    public IReadOnlyList<string> Roots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var root in _roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var real = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(root);
                if (seen.Add(Path.TrimEndingDirectorySeparator(real))) list.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>The Steam folder holding the game's achievement schema (the newest when several do), or null.</summary>
    public string? RootWithSchema(int appId)
        => Roots().Select(r => (Root: r, File: SchemaPath(r, appId))).Where(x => File.Exists(x.File))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.File)).Select(x => x.Root).FirstOrDefault();

    public static string StatsDir(string root) => Path.Combine(root, "appcache", "stats");

    public static string SchemaPath(string root, int appId) => Path.Combine(StatsDir(root), $"UserGameStatsSchema_{appId}.bin");

    /// <summary>Steam's display language ("english", "brazilian", …) from registry.vdf; English when unknown.</summary>
    public string Language()
    {
        foreach (var text in RegistryTexts())
            if (VdfValue(text, "language") is { Length: > 0 } language) return language;
        return "english";
    }

    /// <summary>
    /// The signed-in Steam account id: the running Steam's ActiveUser (registry.vdf, ActiveProcess), else the most recent
    /// user in loginusers.vdf (or the only one). Null when none is known.
    /// </summary>
    public uint? AccountId(string root)
    {
        foreach (var text in RegistryTexts())
            if (VdfValue(text, "ActiveUser") is { } active && uint.TryParse(active, out var id) && id > 0) return id;
        try
        {
            var vdf = Path.Combine(root, "config", "loginusers.vdf");
            return File.Exists(vdf) ? ParseLoginUsers(File.ReadAllText(vdf)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private IEnumerable<string> RegistryTexts()
    {
        foreach (var file in _registryFiles)
        {
            string? text = null;
            try { if (File.Exists(file)) text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (text is not null) yield return text;
        }
    }

    /// <summary>The first <c>"key" "value"</c> pair's value in a text VDF (case-insensitive key), or null.</summary>
    public static string? VdfValue(string vdf, string key)
    {
        var m = Regex.Match(vdf, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>The account id of the MostRecent user in loginusers.vdf text (or the only user) — upstream's rule.</summary>
    public static uint? ParseLoginUsers(string vdf)
    {
        var users = Regex.Matches(vdf, "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}");
        ulong? pick = null;
        foreach (Match m in users)
        {
            if (!ulong.TryParse(m.Groups[1].Value, out var id64)) continue;
            if (Regex.IsMatch(m.Groups[2].Value, "\"MostRecent\"\\s*\"1\"", RegexOptions.IgnoreCase)) { pick = id64; break; }
            if (users.Count == 1) pick = id64;
        }
        if (pick is not ulong v || v <= SteamId64Base) return null;
        return (uint)(v - SteamId64Base);
    }

    /// <summary>
    /// A file Steam keeps in its library cache for the game (<c>appcache/librarycache/{appid}/{name}</c>, or the older flat
    /// <c>{appid}_{name}</c>): its own artwork and, for some games, achievement icons. Null when no Steam folder has it.
    /// </summary>
    public string? LibraryCacheFile(int appId, string name)
    {
        if (name.Contains('/') || name.Contains("..", StringComparison.Ordinal)) return null;
        foreach (var root in Roots())
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            foreach (var candidate in new[] { Path.Combine(cache, appId.ToString(System.Globalization.CultureInfo.InvariantCulture), name), Path.Combine(cache, $"{appId}_{name}") })
                if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
