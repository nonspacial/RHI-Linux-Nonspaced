using System.Text.Json;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>#318: the launchers whose own launch options RHI can write a game's arguments into.</summary>
public enum LauncherKind
{
    Steam,
    Lutris,
    Heroic
}

/// <summary>
/// One launcher file RHI wrote a game's arguments into: the options before RHI's write (<see cref="OriginalPresent"/> false:
/// there was no such key), exactly what RHI wrote, and exactly the part RHI added (empty: the options already held the
/// arguments, so RHI added nothing).
/// </summary>
public sealed class LauncherOptionsFileRecord
{
    public string Path { get; set; } = "";
    public bool OriginalPresent { get; set; }
    public string? Original { get; set; }
    public string Written { get; set; } = "";
    public string Added { get; set; } = "";
}

/// <summary>A game whose arguments are in its launcher's options: which launcher, the arguments (RHI's text) and the files.</summary>
public sealed class LauncherOptionsRecord
{
    public LauncherKind Launcher { get; set; }
    /// <summary>RHI's launch arguments (Game Overrides' text, Windows rules) the launcher copy was made from.</summary>
    public string Arguments { get; set; } = "";
    /// <summary>The journaled write that made this record (its commit point).</summary>
    public string OperationId { get; set; } = "";
    /// <summary>The game's Steam AppID (Steam) or app name (Heroic): how its entry in the files is found.</summary>
    public int? SteamAppId { get; set; }
    public string? HeroicAppName { get; set; }
    public List<LauncherOptionsFileRecord> Files { get; set; } = [];
}

/// <summary>Everything in <c>launcher-options.json</c>: the records by library key.</summary>
public sealed class LauncherOptionsData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, LauncherOptionsRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// #318: <c>~/.config/rhi-linux/launcher-options.json</c> (atomic JSON store; an unreadable file is set aside and never saved
/// over): which games' arguments RHI put into their launcher's options, and what it changed. It is the commit point of each
/// journaled launcher write. A per-game store (<see cref="IGameStateStore"/>, w14 last fixes): a record follows its game to
/// a new library key — an install folder move and Repair after move stage it with their commit, and a scan moves it from the
/// keys the game had before (<see cref="AdoptOlderKeys"/>).
/// </summary>
public sealed class LauncherOptionsStore : IGameStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly JsonFileStore<LauncherOptionsData> _file;

    public LauncherOptionsStore(string path) => _file = new JsonFileStore<LauncherOptionsData>(path, JsonOptions);

    public string FilePath => _file.FilePath;

    /// <summary>The saved records (empty when there are none or the file cannot be read).</summary>
    public LauncherOptionsData Load()
    {
        var data = _file.Load().Value ?? new LauncherOptionsData();
        // A deserialised dictionary is case-sensitive; library keys are not.
        data.Games = new Dictionary<string, LauncherOptionsRecord>(data.Games ?? [], StringComparer.OrdinalIgnoreCase);
        return data;
    }

    public LauncherOptionsRecord? Get(string key) => Load().Games.GetValueOrDefault(key);

    /// <summary>Saves the records. Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(LauncherOptionsData data) => _file.Save(data);

    /// <summary>Library keys of every game with a launcher record.</summary>
    public IReadOnlyList<string> GameKeys => Load().Games.Keys.ToList();

    /// <summary>
    /// "Repair after move" and an install folder move: the game's record under its old key moves to the new one. A record
    /// already under the new key stays, and so does the old one (it still names what RHI wrote, so it is never dropped).
    /// </summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        return _file.Stage(current =>
        {
            if (current is null) return null;
            current.Games = new Dictionary<string, LauncherOptionsRecord>(current.Games ?? [], StringComparer.OrdinalIgnoreCase);
            return Move(current, [(move.NewLibraryKey, move.OldLibraryKeys)]) ? current : null;
        });
    }

    /// <summary>
    /// A scan: records under keys a game had before (older builds, merged duplicates, another spelling of its folder) move
    /// to its current key, one pair per game. A record already under the current key is the newer one and stays (the older
    /// one is left). True when anything moved (saved). Throws <see cref="JsonStoreUnavailableException"/> while an
    /// unreadable file is on disk.
    /// </summary>
    public bool AdoptOlderKeys(IEnumerable<(string CurrentKey, IReadOnlyList<string> OlderKeys)> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var data = Load();
        if (!Move(data, games)) return false;
        Save(data);
        return true;
    }

    private static bool Move(LauncherOptionsData data, IEnumerable<(string CurrentKey, IReadOnlyList<string> OlderKeys)> games)
    {
        var moved = false;
        foreach (var (current, older) in games)
        foreach (var old in older)
        {
            if (string.Equals(old, current, StringComparison.OrdinalIgnoreCase) || data.Games.ContainsKey(current)) continue;
            if (!data.Games.Remove(old, out var record)) continue;
            data.Games[current] = record;
            moved = true;
        }
        return moved;
    }
}
