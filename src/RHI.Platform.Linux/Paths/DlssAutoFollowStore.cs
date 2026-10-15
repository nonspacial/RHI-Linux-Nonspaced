using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>Why a game was skipped by a Follow newest pass and is followed again by the next one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DlssFollowHoldReason>))]
public enum DlssFollowHoldReason
{
    /// <summary>The game was running.</summary>
    Running,
    /// <summary>The app that installed the game (Xbox) was working on it, or its job list could not be read.</summary>
    InstallerBusy,
    /// <summary>The newest version could not be downloaded.</summary>
    DownloadFailed
}

/// <summary>
/// One game a pass skipped (<see cref="DlssFollowHoldReason"/>): the versions its files were on, which the next pass
/// still follows for THIS game only, and what was last reported, so a skip that repeats is not reported again.
/// </summary>
public sealed class DlssFollowHold
{
    /// <summary>The versions the game's files were on when it was skipped (older newest versions).</summary>
    [JsonPropertyName("from")]
    public List<string> From { get; set; } = [];

    /// <summary>Why it was skipped the last time.</summary>
    [JsonPropertyName("reason")]
    public DlssFollowHoldReason Reason { get; set; }

    /// <summary>The newest version the skip was reported for.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    internal DlssFollowHold Copy() => new() { From = [.. From ?? []], Reason = Reason, To = To ?? "", Unknown = Unknown };
}

/// <summary>
/// #177: what following the newest DLSS / Streamline last saw for one family (upstream <c>LastKnownNewestDlss</c> /
/// <c>LastKnownNewestStreamline</c>, one baseline per family here: the manifest's SR, RR and FG lists do not always share
/// their newest version).
/// </summary>
public sealed class DlssFollowTrack
{
    /// <summary>The newest manifest version the last completed pass followed to (the baseline).</summary>
    [JsonPropertyName("newest")]
    public string Newest { get; set; } = "";

    /// <summary>
    /// Games a pass skipped (running, their installer app busy, the download failed), under their
    /// <see cref="GameRecordIdentity"/> key: the next pass still follows those games' files from the versions held here,
    /// then forgets them. Never family-wide: another game's file on such a version (a version picked by hand, a game
    /// added later) is left alone.
    /// </summary>
    [JsonPropertyName("held")]
    public Dictionary<string, DlssFollowHold> Held { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// #177: the baselines of "Follow newest DLSS / Streamline", one per family (<see cref="Families"/>), in their own file
/// (<c>~/.local/share/rhi-linux/dlss-follow.json</c>), never in settings.json: the Settings dialog rebuilds the settings
/// from its controls on Save, which would drop them. A family missing here is seeded (not followed) at its next pass.
/// The per-game holds follow a moved game ("Repair after move").
/// </summary>
public sealed class DlssAutoFollowStore : IGameStateStore
{
    /// <summary>The families' keys, named after the manifest's lists.</summary>
    public static readonly IReadOnlyList<string> Families = ["dlss", "dlssd", "dlssg", "streamline"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<Dictionary<string, DlssFollowTrack>> _file;
    private readonly object _gate = new();

    public DlssAutoFollowStore(string? path = null)
    {
        _file = new JsonFileStore<Dictionary<string, DlssFollowTrack>>(
            path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "dlss-follow.json"), JsonOptions);
    }

    public string FilePath => _file.FilePath;

    /// <summary>The saved baselines (a copy), or null while the file cannot be read (it was copied aside; nothing moves).</summary>
    public Dictionary<string, DlssFollowTrack>? Load()
    {
        lock (_gate)
        {
            var load = _file.Load();
            return load.IsUnreadable ? null : Copy(load.Value);
        }
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(Dictionary<string, DlssFollowTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_gate) _file.Save(tracks);
    }

    /// <summary>"Repair after move" (Refs #38): a folder-keyed game's holds follow it to its new key.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(current =>
            {
                var tracks = Copy(current);
                var moved = false;
                foreach (var track in tracks.Values) moved |= move.MoveRecordEntries(track.Held);
                return moved ? tracks : null;
            });
    }

    /// <summary>Keys an unreleased earlier build wrote and nothing reads any more (family-wide <c>heldFrom</c>, re-review n3).</summary>
    internal static readonly IReadOnlyList<string> RetiredKeys = ["heldFrom"];

    private static Dictionary<string, JsonElement>? WithoutRetiredKeys(Dictionary<string, JsonElement>? unknown)
    {
        if (unknown is null || !RetiredKeys.Any(unknown.ContainsKey)) return unknown;
        var kept = new Dictionary<string, JsonElement>(unknown);
        foreach (var key in RetiredKeys) kept.Remove(key);
        return kept.Count == 0 ? null : kept;
    }

    private static Dictionary<string, DlssFollowTrack> Copy(Dictionary<string, DlssFollowTrack>? value)
    {
        var copy = JsonStoreMaps.CaseInsensitive(value);
        foreach (var key in copy.Keys.ToArray())
        {
            var track = copy[key];
            var held = new Dictionary<string, DlssFollowHold>(StringComparer.Ordinal);
            foreach (var (game, hold) in track?.Held ?? [])
                if (!string.IsNullOrWhiteSpace(game) && hold is not null) held[game] = hold.Copy();
            copy[key] = new DlssFollowTrack { Newest = track?.Newest ?? "", Held = held, Unknown = WithoutRetiredKeys(track?.Unknown) };
        }
        return copy;
    }
}
