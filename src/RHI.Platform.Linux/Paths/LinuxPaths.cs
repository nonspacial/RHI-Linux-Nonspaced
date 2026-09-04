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
    {
        var candidates = new[]
        {
            Path.Combine(ConfigHome, "heroic"),
            Path.Combine(Home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")
        };
        return candidates.Distinct(StringComparer.Ordinal).Where(Directory.Exists);
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
