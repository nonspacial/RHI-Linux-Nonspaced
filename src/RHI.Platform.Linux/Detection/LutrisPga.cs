using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection;

/// <summary>One row of Lutris' <c>pga.db</c> <c>games</c> table, keyed by its config file stem.</summary>
internal sealed record LutrisPgaGame(string ConfigPath, string? Name, string? Slug, string? Directory, bool? Installed);

internal static class LutrisPga
{
    /// <summary>
    /// <c>pga.db</c> next to each config root (<c>…/lutris/games</c> → <c>…/lutris/pga.db</c>) and in the
    /// native and Flatpak data directories; older Lutris kept configs under ~/.config and the database
    /// under ~/.local/share.
    /// </summary>
    public static IEnumerable<string> Candidates(IEnumerable<string> configRoots, string home, string? dataHome = null)
    {
        foreach (var root in configRoots)
            if (Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('/')) is { } parent)
                yield return Path.Combine(parent, "pga.db");
        if (dataHome is not null) yield return Path.Combine(dataHome, "lutris", "pga.db");
        yield return Path.Combine(home, ".local", "share", "lutris", "pga.db");
        yield return Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "pga.db");
    }

    /// <summary>Rows by config stem; the first database listing a stem wins. Unreadable databases add nothing.</summary>
    public static IReadOnlyDictionary<string, LutrisPgaGame> Load(IEnumerable<string> databases)
    {
        var byConfig = new Dictionary<string, LutrisPgaGame>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            if (!File.Exists(database) || !seen.Add(PathIdentity.Canonicalize(database))) continue;
            foreach (var row in SqliteTableReader.ReadTable(database, "games"))
            {
                if (Text(row, "configpath") is not { Length: > 0 } configPath) continue;
                var stem = Path.GetFileNameWithoutExtension(configPath);
                if (byConfig.ContainsKey(stem)) continue;
                byConfig[stem] = new LutrisPgaGame(
                    stem,
                    Text(row, "name"),
                    Text(row, "slug"),
                    Text(row, "directory"),
                    row.TryGetValue("installed", out var installed) && installed is long flag ? flag != 0 : null);
            }
        }
        return byConfig;
    }

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
