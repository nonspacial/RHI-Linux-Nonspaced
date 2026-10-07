using RHI.Core.Models;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// The per-game key used by the deployment ledger and every per-game preference store. It is
/// tied to the store's own app id where there is one, so it survives moving the game to another
/// library or drive: Steam AppID (<c>steam:&lt;id&gt;</c>), then Heroic's app name
/// (<c>heroic:&lt;app&gt;</c>, covering Epic, GOG and Amazon). Games without a store id keep the
/// full install path, as before; a moved one is found again by "Repair after move".
/// Older builds keyed Heroic games by install path; <see cref="LegacyKeys"/> lists those keys so
/// stores can still read, and migrate, entries written before (Refs #38).
/// </summary>
public static class GameRecordIdentity
{
    public static string Key(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.SteamAppId is int steamId) return $"steam:{steamId}";
        if (!string.IsNullOrWhiteSpace(game.HeroicAppName)) return "heroic:" + game.HeroicAppName.Trim();
        return Path.GetFullPath(game.InstallPath);
    }

    /// <summary>Keys older builds wrote for this game that differ from <see cref="Key"/> (empty when unchanged).</summary>
    public static IReadOnlyList<string> LegacyKeys(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.SteamAppId is not null || string.IsNullOrWhiteSpace(game.HeroicAppName)) return [];
        return string.IsNullOrWhiteSpace(game.InstallPath) ? [] : [Path.GetFullPath(game.InstallPath)];
    }

    /// <summary>True when <paramref name="key"/> is this game's key or one an older build wrote for it.</summary>
    public static bool Matches(string? key, DetectedGame game)
        => key is not null && (key == Key(game) || LegacyKeys(game).Contains(key, StringComparer.Ordinal));

    /// <summary>True for keys that name a store app id rather than a folder.</summary>
    public static bool IsStoreKey(string? key)
        => key is not null && (key.StartsWith("steam:", StringComparison.Ordinal) || key.StartsWith("heroic:", StringComparison.Ordinal));

    /// <summary>Reads a per-game entry under the current key, falling back to a legacy key.</summary>
    public static bool TryGet<T>(IReadOnlyDictionary<string, T> map, DetectedGame game, out T value, string suffix = "")
    {
        if (map.TryGetValue(Key(game) + suffix, out value!)) return true;
        foreach (var legacy in LegacyKeys(game))
            if (map.TryGetValue(legacy + suffix, out value!)) return true;
        value = default!;
        return false;
    }

    /// <summary>Writes a per-game entry under the current key and drops legacy copies (migrates on write).</summary>
    public static void Set<T>(IDictionary<string, T> map, DetectedGame game, T value, string suffix = "")
    {
        Remove(map, game, suffix);
        map[Key(game) + suffix] = value;
    }

    /// <summary>Removes the per-game entry under the current and every legacy key.</summary>
    public static void Remove<T>(IDictionary<string, T> map, DetectedGame game, string suffix = "")
    {
        map.Remove(Key(game) + suffix);
        foreach (var legacy in LegacyKeys(game)) map.Remove(legacy + suffix);
    }
}
