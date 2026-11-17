using System.Text.Json;
using RHI.Platform.Linux.Persistence;

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
/// journaled launcher write.
/// </summary>
public sealed class LauncherOptionsStore
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
}
