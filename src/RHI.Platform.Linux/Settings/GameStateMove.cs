using RHI.Core.Models;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>
/// A store holding per-game entries that "Repair after move" carries over to the game's current
/// key and folder (Refs #38). Every participant is staged first and written in one
/// <see cref="StagedFileCommit"/>, so either all of them follow the game or none does.
/// </summary>
public interface IGameStateStore
{
    /// <summary>
    /// This store's part of <paramref name="move"/>, or null when it holds nothing to move. Writes
    /// nothing. Throws <see cref="IOException"/> (e.g. <see cref="JsonStoreUnavailableException"/>)
    /// when its file cannot be read, which cancels the whole repair.
    /// </summary>
    StagedFileWrite? StageMove(GameStateMove move);

    /// <summary>
    /// Paths (folders or files) this store's entries for <paramref name="game"/> still name although they no longer
    /// exist — the game moved, with no RHI records to show it (e.g. an Edit game patch target). "Repair after move"
    /// maps them onto the game's current folder and re-points them only when every mapped path exists (Refs #147).
    /// Reads only; none by default.
    /// </summary>
    IReadOnlyList<string> MovedPaths(DetectedGame game) => [];
}

/// <summary>
/// What moved when a game was repaired after a move: the folders (old root → new root), the
/// per-game keys the stores used at the old location and the keys the game has now.
/// <list type="bullet">
/// <item>Record keys (<see cref="GameRecordIdentity"/>): only folder-keyed games change key
/// (store-keyed <c>steam:</c>/<c>heroic:</c> keys survive a move); entries may carry a
/// <c>|suffix</c>.</item>
/// <item>Library keys (<see cref="LinuxGameLibraryStore.GameKey(DetectedGame)"/>): only folder-keyed games
/// change key (Steam/Heroic keys survive a move, Refs #147); the keys builds before #147 wrote (folder
/// included) are old keys of every game.</item>
/// </list>
/// An entry already stored under the new key is the user's newer choice and wins over the old one.
/// </summary>
public sealed class GameStateMove
{
    private readonly IReadOnlyList<(string OldRoot, string NewRoot)> _roots;

    public GameStateMove(DetectedGame game, IEnumerable<(string OldRoot, string NewRoot)> roots, IEnumerable<string> oldRecordKeys)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        _roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r.OldRoot) && !string.IsNullOrWhiteSpace(r.NewRoot))
            .Select(r => (Trim(r.OldRoot), Trim(r.NewRoot)))
            .Distinct()
            // Deepest first, so a nested root maps by its own pair.
            .OrderByDescending(r => r.Item1.Length)
            .ToArray();
        NewRecordKey = GameRecordIdentity.Key(game);
        OldRecordKeys = oldRecordKeys
            .Where(k => !string.IsNullOrWhiteSpace(k) && !string.Equals(k, NewRecordKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        NewLibraryKey = LinuxGameLibraryStore.GameKey(game);
        var identity = string.IsNullOrWhiteSpace(game.LibraryIdentityPath) ? game.InstallPath : game.LibraryIdentityPath;
        OldLibraryKeys = string.IsNullOrWhiteSpace(identity) ? [] : _roots
            .Select(r => Rebase(identity, r.NewRoot, r.OldRoot))
            .OfType<string>()
            .SelectMany(old => new[]
            {
                LinuxGameLibraryStore.GameKey(game, old), LinuxGameLibraryStore.LegacyGameKey(game.Source, old, game.SteamAppId),
                LinuxGameLibraryStore.ResolvedFolderKey(game, old) // this branch's earlier symlink-resolved folder key
            })
            .OfType<string>()
            .Append(LinuxGameLibraryStore.LegacyGameKey(game)) // the current folder's pre-#147 key, if not yet adopted
            .Where(k => !string.Equals(k, NewLibraryKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DetectedGame Game { get; }
    public string NewRecordKey { get; }
    public IReadOnlyList<string> OldRecordKeys { get; }
    public string NewLibraryKey { get; }
    public IReadOnlyList<string> OldLibraryKeys { get; }

    /// <summary>A path under one of the old roots, mapped to the same place under its new root; anything else unchanged.</summary>
    public string MapPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        foreach (var (oldRoot, newRoot) in _roots)
            if (Rebase(path, oldRoot, newRoot) is { } mapped) return mapped;
        return path;
    }

    /// <summary>The record key (or key plus <c>|suffix</c>) <paramref name="key"/> becomes, or null when it is not an old key.</summary>
    public string? MovedRecordKey(string key)
    {
        foreach (var old in OldRecordKeys)
        {
            if (string.Equals(key, old, StringComparison.OrdinalIgnoreCase)) return NewRecordKey;
            if (key.Length > old.Length && key[old.Length] == '|' && key.StartsWith(old, StringComparison.OrdinalIgnoreCase))
                return NewRecordKey + key[old.Length..];
        }
        return null;
    }

    /// <summary>Moves per-game entries from old record keys to the new one. True when anything changed.</summary>
    public bool MoveRecordEntries<T>(IDictionary<string, T> map) => MoveEntries(map, MovedRecordKey, null);

    /// <summary>Moves library entries from old library keys, rebasing each value with <paramref name="rebase"/>.</summary>
    public bool MoveLibraryEntries<T>(IDictionary<string, T> map, Func<T, T>? rebase = null)
        => MoveEntries(map, MovedLibraryKey, rebase);

    /// <summary>Moves a library flag (favourite, hidden) from an old library key. True when anything changed.</summary>
    public bool MoveLibraryFlag(ISet<string> set)
    {
        var changed = false;
        foreach (var old in OldLibraryKeys)
        {
            var present = set.FirstOrDefault(k => string.Equals(k, old, StringComparison.OrdinalIgnoreCase));
            if (present is null) continue;
            set.Remove(present);
            set.Add(NewLibraryKey);
            changed = true;
        }
        return changed;
    }

    /// <summary>A list of game keys (opt-ins) with old record keys replaced by the new one; null when unchanged.</summary>
    public string[]? MoveRecordKeyList(IReadOnlyCollection<string>? keys)
    {
        if (keys is null || !keys.Any(k => MovedRecordKey(k) is not null)) return null;
        return keys.Select(k => MovedRecordKey(k) ?? k)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    private string? MovedLibraryKey(string key)
        => OldLibraryKeys.Any(old => string.Equals(key, old, StringComparison.OrdinalIgnoreCase)) ? NewLibraryKey : null;

    private static bool MoveEntries<T>(IDictionary<string, T> map, Func<string, string?> moved, Func<T, T>? rebase)
    {
        var changed = false;
        foreach (var key in map.Keys.ToArray())
        {
            if (moved(key) is not { } target) continue;
            var value = map[key];
            map.Remove(key);
            if (!map.ContainsKey(target)) map[target] = rebase is null ? value : rebase(value);
            changed = true;
        }
        return changed;
    }

    private static string Trim(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } t ? t : "/";

    /// <summary>Lexical rebase of <paramref name="path"/> from <paramref name="from"/> to <paramref name="to"/>; null when it is not under <paramref name="from"/>.</summary>
    private static string? Rebase(string path, string from, string to)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, from, StringComparison.Ordinal)) return to;
        var prefix = from.EndsWith(Path.DirectorySeparatorChar) ? from : from + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? Path.Combine(to, full[prefix.Length..]) : null;
    }
}
