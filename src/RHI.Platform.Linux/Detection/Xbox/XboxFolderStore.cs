using System.Text.Json;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>The "Xbox / Game Pass folders" setting (Refs #90, owner decision C).</summary>
public sealed class XboxFolderSettings
{
    /// <summary>
    /// Folders the user added, each one of: an installer-app state folder (holds <c>settings.json</c>), an installer-app instance
    /// folder (holds <c>state/{app}</c>), or a folder of Xodus installs. <c>$XDG_STATE_HOME/{app}</c> and
    /// <c>~/Games</c> are always searched and are not stored here.
    /// </summary>
    public List<string> Folders { get; set; } = [];
}

/// <summary>
/// Stores <see cref="XboxFolderSettings"/> in <c>$XDG_CONFIG_HOME/rhi-linux/xbox-folders.json</c> (its own file, so
/// the setting needs no change to <c>settings.json</c>'s schema). The Settings row that edits it is hosted by the main
/// window (see the w3/scan progress notes); the detector reads it on every scan.
/// </summary>
public sealed class XboxFolderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly JsonFileStore<XboxFolderSettings> _file;

    public XboxFolderStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "xbox-folders.json");
        _file = new JsonFileStore<XboxFolderSettings>(FilePath, JsonOptions,
            parse: text => JsonSerializer.Deserialize<XboxFolderSettings>(text, JsonOptions) is { } read
                ? new XboxFolderSettings { Folders = Normalize(read.Folders ?? []) }
                : null);
    }

    public string FilePath { get; }

    /// <summary>The always-searched default state folder, shown in Settings beside the user's own entries.</summary>
    public static string DefaultInstallerStateFolder => Path.Combine(LinuxPaths.StateHome, XboxInstallerApp.FolderName);

    /// <summary>The user's folders; empty when the file is missing or unreadable (an unreadable one is set aside and logged).</summary>
    public IReadOnlyList<string> Folders()
    {
        var load = _file.Load();
        if (load.IsUnreadable) AppLog.Warn("settings", $"Xbox folder list {FilePath} unreadable; set aside as {load.QuarantinePath}");
        return load.Value?.Folders ?? [];
    }

    /// <summary>Saves the list (trimmed, absolute, duplicates dropped). Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(IEnumerable<string> folders)
        => _file.Save(new XboxFolderSettings { Folders = Normalize(folders) });

    private static List<string> Normalize(IEnumerable<string> folders)
        => folders.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Trim())))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
