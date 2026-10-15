using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Paths;

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
    /// Older newest versions a pass could not finish following because a game was running (or its installer app was
    /// busy with it): the next pass still follows files on these versions, then forgets them.
    /// </summary>
    [JsonPropertyName("heldFrom")]
    public List<string> HeldFrom { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// #177: the baselines of "Follow newest DLSS / Streamline", one per family (<see cref="Families"/>), in their own file
/// (<c>~/.local/share/rhi-linux/dlss-follow.json</c>), never in settings.json: the Settings dialog rebuilds the settings
/// from its controls on Save, which would drop them. A family missing here is seeded (not followed) at its next pass.
/// </summary>
public sealed class DlssAutoFollowStore
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
            if (load.IsUnreadable) return null;
            var copy = JsonStoreMaps.CaseInsensitive(load.Value);
            foreach (var key in copy.Keys.ToArray())
                copy[key] = copy[key] is { } track
                    ? new DlssFollowTrack { Newest = track.Newest ?? "", HeldFrom = [.. track.HeldFrom ?? []], Unknown = track.Unknown }
                    : new DlssFollowTrack();
            return copy;
        }
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(Dictionary<string, DlssFollowTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_gate) _file.Save(tracks);
    }
}
