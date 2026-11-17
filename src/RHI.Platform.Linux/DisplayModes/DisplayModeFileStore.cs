using System.Text.Json;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311: saves the display modes (<see cref="DisplayModeData"/>) in <c>~/.config/rhi-linux/display-modes.json</c> through
/// the atomic JSON store (upstream keeps them in settings.json as "DisplayModes"). A missing file gives upstream's
/// defaults (Monitor and TV, feature off); an unreadable one is copied aside, the defaults are used for the session and
/// no save goes over the user's file until it reads again.
/// </summary>
public sealed class DisplayModeFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<DisplayModeData> _file;

    public DisplayModeFileStore(string? path = null)
        => _file = new JsonFileStore<DisplayModeData>(path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "display-modes.json"), JsonOptions);

    public string FilePath => _file.FilePath;

    /// <summary>The saved data, or null when there is none or it cannot be read (<paramref name="unreadable"/> says which).</summary>
    public DisplayModeData? Load(out bool unreadable)
    {
        var load = _file.Load();
        unreadable = load.IsUnreadable;
        return load.Value;
    }

    /// <summary>Saves <paramref name="data"/>. Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(DisplayModeData data) => _file.Save(data);

    /// <summary>
    /// w14 final review M2: <paramref name="data"/> staged for a multi-file commit (a folder move, Repair after move).
    /// Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk, which cancels that commit.
    /// </summary>
    public StagedFileWrite? Stage(DisplayModeData data) => _file.Stage(_ => data);
}
