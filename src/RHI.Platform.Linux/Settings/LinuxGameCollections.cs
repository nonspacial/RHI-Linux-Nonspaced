namespace RHI.Platform.Linux.Settings;

/// <summary>
/// #300 (upstream 2.9.2 <c>GameCollection</c>): a user-made list of games ("Racing", "Souls-likes"): a name and its
/// games by library key (<see cref="LinuxGameLibraryStore.GameKey(RHI.Core.Models.DetectedGame)"/>), in the order they
/// were added. A game can be in several collections. Saved in the library file in the user's order.
/// </summary>
public sealed class LinuxGameCollection
{
    public string Name { get; set; } = "";
    public List<string> Games { get; set; } = [];
}

/// <summary>Why a collection name was refused (the desktop words it).</summary>
public enum CollectionNameProblem
{
    None,
    Empty,
    TooLong,
    ControlCharacters,
    /// <summary>The name of a built-in list filter (it would read like the filter).</summary>
    BuiltInFilter,
    /// <summary>Another collection has it (any case).</summary>
    AlreadyUsed,
    /// <summary>Rename: the collection is gone.</summary>
    Missing
}

/// <summary>
/// #300: the rules for the collections list (upstream <c>GameCollectionStore</c>): names, membership, order and key
/// changes. Pure: works on the list it is given; the caller saves the library state.
/// </summary>
public static class LinuxGameCollections
{
    /// <summary>Upstream's limit.</summary>
    public const int MaxNameLength = 40;

    /// <summary>
    /// Names of the built-in list filters (upstream's set, plus the Linux tabs' own names and their PT-BR words), which a
    /// collection can't use.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "All", "All games", "Installed", "Favourites", "Favorites", "Hidden", "Detected",
        "Unreal", "Unity", "Other", "RenoDX", "Luma",
        "Todos", "Todos os jogos", "Instalados", "Favoritos", "Ocultos", "Outros"
    };

    /// <summary>
    /// Checks a name for a new collection (or for renaming <paramref name="renaming"/>): trimmed, not empty, at most
    /// <see cref="MaxNameLength"/> characters, no control characters, not a built-in filter name, not used by another
    /// collection (any case; renaming to the same name in another case is fine).
    /// </summary>
    public static CollectionNameProblem Validate(IReadOnlyList<LinuxGameCollection>? list, string? name, string? renaming, out string clean)
    {
        clean = (name ?? "").Trim();
        if (clean.Length == 0) return CollectionNameProblem.Empty;
        if (clean.Length > MaxNameLength) return CollectionNameProblem.TooLong;
        if (clean.Any(char.IsControl)) return CollectionNameProblem.ControlCharacters;
        if (ReservedNames.Contains(clean)) return CollectionNameProblem.BuiltInFilter;
        var existing = Find(list, clean);
        if (existing is not null && (renaming is null || !string.Equals(existing.Name, renaming.Trim(), StringComparison.OrdinalIgnoreCase)))
            return CollectionNameProblem.AlreadyUsed;
        return CollectionNameProblem.None;
    }

    /// <summary>The collection called <paramref name="name"/> (any case, trimmed), or null.</summary>
    public static LinuxGameCollection? Find(IReadOnlyList<LinuxGameCollection>? list, string? name)
    {
        if (list is null || string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return list.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds an empty collection at the end (with <paramref name="firstGameKey"/> in it when given).</summary>
    public static CollectionNameProblem Create(List<LinuxGameCollection> list, string? name, string? firstGameKey, out string created)
    {
        var problem = Validate(list, name, null, out created);
        if (problem != CollectionNameProblem.None) return problem;
        var collection = new LinuxGameCollection { Name = created };
        if (!string.IsNullOrWhiteSpace(firstGameKey)) collection.Games.Add(firstGameKey);
        list.Add(collection);
        return CollectionNameProblem.None;
    }

    /// <summary>Renames a collection; its games stay.</summary>
    public static CollectionNameProblem Rename(List<LinuxGameCollection> list, string oldName, string? newName, out string renamed)
    {
        renamed = "";
        var collection = Find(list, oldName);
        if (collection is null) return CollectionNameProblem.Missing;
        var problem = Validate(list, newName, collection.Name, out renamed);
        if (problem != CollectionNameProblem.None) return problem;
        collection.Name = renamed;
        return CollectionNameProblem.None;
    }

    /// <summary>Deletes a collection. The games themselves are untouched. False when it did not exist.</summary>
    public static bool Delete(List<LinuxGameCollection> list, string name)
        => Find(list, name) is { } collection && list.Remove(collection);

    /// <summary>Moves a collection <paramref name="delta"/> places (negative = left), clamped. False when it can't move.</summary>
    public static bool Move(List<LinuxGameCollection> list, string name, int delta)
    {
        var collection = Find(list, name);
        if (collection is null || delta == 0) return false;
        var from = list.IndexOf(collection);
        var to = Math.Clamp(from + delta, 0, list.Count - 1);
        if (to == from) return false;
        list.RemoveAt(from);
        list.Insert(to, collection);
        return true;
    }

    public static bool Contains(IReadOnlyList<LinuxGameCollection>? list, string name, string gameKey)
        => Find(list, name)?.Games.Contains(gameKey, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Puts a game in a collection or takes it out. True when something changed.</summary>
    public static bool SetMembership(List<LinuxGameCollection> list, string name, string gameKey, bool member)
    {
        if (string.IsNullOrWhiteSpace(gameKey) || Find(list, name) is not { } collection) return false;
        var at = collection.Games.FindIndex(k => string.Equals(k, gameKey, StringComparison.OrdinalIgnoreCase));
        if (member)
        {
            if (at >= 0) return false;
            collection.Games.Add(gameKey);
            return true;
        }
        if (at < 0) return false;
        collection.Games.RemoveAt(at);
        return true;
    }

    /// <summary>A collection's game keys (any case), or null when it does not exist.</summary>
    public static IReadOnlySet<string>? Members(IReadOnlyList<LinuxGameCollection>? list, string? name)
        => Find(list, name) is { } collection ? new HashSet<string>(collection.Games, StringComparer.OrdinalIgnoreCase) : null;

    /// <summary>
    /// A game's library key changed (an older key adopted, Repair after move): every <paramref name="oldKeys"/> member
    /// becomes <paramref name="newKey"/>, keeping its place. True when anything changed.
    /// </summary>
    public static bool ReplaceKeys(List<LinuxGameCollection>? list, IEnumerable<string> oldKeys, string newKey)
    {
        if (list is null || list.Count == 0) return false;
        var old = new HashSet<string>(oldKeys.Where(k => !string.Equals(k, newKey, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        if (old.Count == 0) return false;
        var changed = false;
        foreach (var collection in list)
        {
            if (!collection.Games.Any(old.Contains)) continue;
            collection.Games = collection.Games.Select(k => old.Contains(k) ? newKey : k).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Reading a saved list (upstream <c>Load</c>): entries with an empty, too long, built-in or repeated name are dropped,
    /// blank and repeated game keys removed. Null when nothing is left, so a library file without collections keeps the
    /// shape older builds wrote.
    /// </summary>
    public static List<LinuxGameCollection>? Normalize(IEnumerable<LinuxGameCollection?>? saved)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<LinuxGameCollection>();
        foreach (var c in saved ?? [])
        {
            var name = (c?.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl) || ReservedNames.Contains(name) || !names.Add(name))
                continue;
            var games = (c!.Games ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Add(new LinuxGameCollection { Name = name, Games = games });
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>The saved active collection's stored spelling when it still exists, else null.</summary>
    public static string? NormalizeActive(IReadOnlyList<LinuxGameCollection>? list, string? active) => Find(list, active)?.Name;
}
