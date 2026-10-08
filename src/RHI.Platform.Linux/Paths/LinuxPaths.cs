namespace RHI.Platform.Linux.Paths;

public static class LinuxPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ConfigHome => ConfigHomeFrom(Environment.GetEnvironmentVariable);
    public static string DataHome => DataHomeFrom(Environment.GetEnvironmentVariable);
    public static string CacheHome => CacheHomeFrom(Environment.GetEnvironmentVariable);
    /// <summary>XDG state dir (logs and other state that is not user data); defaults to ~/.local/state.</summary>
    public static string StateHome => StateHomeFrom(Environment.GetEnvironmentVariable);

    // The XDG base-directory spec: an empty or relative value is invalid and is ignored (the default is used), so
    // XDG_CONFIG_HOME="" never turns into a folder relative to whatever directory RHI was started from.
    internal static string ConfigHomeFrom(Func<string, string?> env) => XdgHome(env("XDG_CONFIG_HOME"), ".config");
    internal static string DataHomeFrom(Func<string, string?> env) => XdgHome(env("XDG_DATA_HOME"), ".local", "share");
    internal static string CacheHomeFrom(Func<string, string?> env) => XdgHome(env("XDG_CACHE_HOME"), ".cache");
    internal static string StateHomeFrom(Func<string, string?> env) => XdgHome(env("XDG_STATE_HOME"), ".local", "state");

    private static string XdgHome(string? value, params string[] defaultUnderHome)
        => value is { Length: > 0 } && Path.IsPathRooted(value) ? value : Path.Combine([Home, .. defaultUnderHome]);

    /// <summary>The one app log (<see cref="RHI.Core.Services.AppLog"/>): <c>$XDG_STATE_HOME/rhi-linux/logs/rhi.log</c>.</summary>
    public static string AppLogPath => Path.Combine(LogDirectory, "rhi.log");

    /// <summary>RHI's log folder: the app log, the deployment recovery log and per-launch logs live here.</summary>
    public static string LogDirectory => Path.Combine(StateHome, "rhi-linux", "logs");

    public static IEnumerable<string> SteamRoots()
        => DistinctExisting(SteamRootCandidates(Home, DataHome));

    internal static IEnumerable<string> SteamRootCandidates(string home, string dataHome)
    {
        yield return Path.Combine(dataHome, "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }

    /// <summary>
    /// Existing directories, one per real location: <c>~/.steam/steam</c> is normally a symlink to
    /// <c>~/.local/share/Steam</c>, and string de-duplication walks both. The first spelling seen is kept,
    /// so library keys built from it do not change.
    /// </summary>
    internal static IEnumerable<string> DistinctExisting(IEnumerable<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;
            if (seen.Add(RHI.Core.Services.PathIdentity.Canonicalize(candidate))) yield return candidate;
        }
    }

    public static IEnumerable<string> HeroicRoots()
        => DistinctExisting(HeroicRootCandidates(Home, ConfigHome));

    /// <summary>
    /// Legendary's installed.json is not guaranteed to live under ~/.config/heroic.
    /// Native Heroic historically uses ~/.config/legendary, while newer Heroic builds may
    /// keep a bundled Legendary config at heroic/legendaryConfig/legendary. Flatpak has
    /// existed in both sibling-config and bundled-config layouts as well.
    /// </summary>
    public static IEnumerable<string> HeroicLegendaryRoots()
        => DistinctExisting(HeroicLegendaryRootCandidates(Home, ConfigHome));

    public static IEnumerable<string> HeroicPrefixRoots()
    {
        var candidates = HeroicRoots()
            .Select(root => Path.Combine(root, "Prefixes"))
            .Concat(new[]
            {
                Path.Combine(Home, "Games", "Heroic", "Prefixes")
            });

        return DistinctExisting(candidates);
    }

    internal static IEnumerable<string> HeroicRootCandidates(string home, string configHome)
    {
        yield return Path.Combine(configHome, "heroic");
        yield return Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic");
    }

    internal static IEnumerable<string> HeroicLegendaryRootCandidates(string home, string configHome)
    {
        // Native Heroic / standalone Legendary layout.
        yield return Path.Combine(configHome, "legendary");

        // Heroic-managed Legendary layout used by newer releases.
        yield return Path.Combine(configHome, "heroic", "legendaryConfig", "legendary");

        // Flatpak sibling config layout used by older/current installations.
        yield return Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "legendary");

        // Flatpak Heroic-managed Legendary layout.
        yield return Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "legendaryConfig", "legendary");
    }

    public static IEnumerable<string> LutrisConfigRoots()
        => DistinctExisting(LutrisConfigRootCandidates(Home, ConfigHome, DataHome));

    /// <summary>
    /// Lutris game configs. The Wine detector skips every prefix these reference, so a root missing
    /// here would make that launcher's games vanish rather than merely duplicate. (A Lutris entry with
    /// no real game folder, e.g. an application behind a wrapper script, is deliberately not listed.)
    /// </summary>
    internal static IEnumerable<string> LutrisConfigRootCandidates(string home, string configHome, string dataHome)
    {
        yield return Path.Combine(configHome, "lutris", "games");
        yield return Path.Combine(dataHome, "lutris", "games");
        yield return Path.Combine(home, ".var", "app", "net.lutris.Lutris", "config", "lutris", "games");
        // Newer Lutris keeps games under its data directory; the flatpak mirrors that under data/.
        yield return Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "games");
    }
}
