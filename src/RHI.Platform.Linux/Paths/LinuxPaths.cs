namespace RHI.Platform.Linux.Paths;

public static class LinuxPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        ?? Path.Combine(Home, ".config");
    public static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME")
        ?? Path.Combine(Home, ".local", "share");
    public static string CacheHome => Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
        ?? Path.Combine(Home, ".cache");

    public static IEnumerable<string> SteamRoots()
    {
        var candidates = new[]
        {
            Path.Combine(DataHome, "Steam"),
            Path.Combine(Home, ".steam", "steam"),
            Path.Combine(Home, ".local", "share", "Steam"),
            Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
        };
        return candidates.Distinct(StringComparer.Ordinal).Where(Directory.Exists);
    }

    public static IEnumerable<string> HeroicRoots()
        => HeroicRootCandidates(Home, ConfigHome)
            .Distinct(StringComparer.Ordinal)
            .Where(Directory.Exists);

    /// <summary>
    /// Legendary's installed.json is not guaranteed to live under ~/.config/heroic.
    /// Native Heroic historically uses ~/.config/legendary, while newer Heroic builds may
    /// keep a bundled Legendary config at heroic/legendaryConfig/legendary. Flatpak has
    /// existed in both sibling-config and bundled-config layouts as well.
    /// </summary>
    public static IEnumerable<string> HeroicLegendaryRoots()
        => HeroicLegendaryRootCandidates(Home, ConfigHome)
            .Distinct(StringComparer.Ordinal)
            .Where(Directory.Exists);

    public static IEnumerable<string> HeroicPrefixRoots()
    {
        var candidates = HeroicRoots()
            .Select(root => Path.Combine(root, "Prefixes"))
            .Concat(new[]
            {
                Path.Combine(Home, "Games", "Heroic", "Prefixes")
            });

        return candidates.Distinct(StringComparer.Ordinal).Where(Directory.Exists);
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
    {
        var candidates = new[]
        {
            Path.Combine(ConfigHome, "lutris", "games"),
            Path.Combine(DataHome, "lutris", "games"),
            Path.Combine(Home, ".var", "app", "net.lutris.Lutris", "config", "lutris", "games")
        };
        return candidates.Distinct(StringComparer.Ordinal).Where(Directory.Exists);
    }
}
