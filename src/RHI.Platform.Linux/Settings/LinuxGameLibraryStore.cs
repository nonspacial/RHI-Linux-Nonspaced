using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.Settings;

public sealed class LinuxManualGameEntry
{
    public string Name { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? PrefixPath { get; set; }
    public string? LaunchCommand { get; set; }
    public string? Runner { get; set; }
    public GraphicsApi? UserGraphicsApiOverride { get; set; }
}

public sealed class LinuxGameEditOverride
{
    /// <summary>Optional UI-only launcher/platform badge override. "Automatic" or empty returns to inference.</summary>
    public string? LaunchProvider { get; set; }

    /// <summary>Confirmed game executable used for runtime detection and game-local patches.</summary>
    public string? PatchExecutablePath { get; set; }

    /// <summary>Confirmed game root used for indexing, manifests and local component discovery.</summary>
    public string? GameRootPath { get; set; }

    /// <summary>Confirmed binary directory used for proxy/add-on deployment.</summary>
    public string? BinaryDirectory { get; set; }

    public LinuxGameEditOverride Normalize()
    {
        LaunchProvider = string.IsNullOrWhiteSpace(LaunchProvider) ||
                         LaunchProvider.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ||
                         LaunchProvider.Equals("Automático", StringComparison.OrdinalIgnoreCase)
            ? null
            : LaunchProvider.Trim();
        PatchExecutablePath = NormalizeOptionalPath(PatchExecutablePath);
        GameRootPath = NormalizeOptionalPath(GameRootPath);
        BinaryDirectory = NormalizeOptionalPath(BinaryDirectory);
        return this;
    }

    public bool HasPatchTarget =>
        !string.IsNullOrWhiteSpace(PatchExecutablePath) &&
        !string.IsNullOrWhiteSpace(GameRootPath) &&
        !string.IsNullOrWhiteSpace(BinaryDirectory);

    private static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())); }
        catch { return value.Trim(); }
    }
}

public sealed class LinuxGameLibraryState
{
    public HashSet<string> FavouriteGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HiddenGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LinuxManualGameEntry> ManualGames { get; set; } = [];

    /// <summary>
    /// Per-game user authority for launcher badge presentation and the separate game-local patch target.
    /// Keys use the launcher/library identity, so a patch root change does not orphan the override itself.
    /// </summary>
    public Dictionary<string, LinuxGameEditOverride> GameEdits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-game HDR launch orchestration. This stores only RHI launch preferences, never runner binaries/configuration.</summary>
    public Dictionary<string, LinuxHdrGameProfile> HdrProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FilterMode { get; set; } = "All Games";

    public LinuxGameLibraryState Normalize()
    {
        FavouriteGames = new HashSet<string>(FavouriteGames ?? [], StringComparer.OrdinalIgnoreCase);
        HiddenGames = new HashSet<string>(HiddenGames ?? [], StringComparer.OrdinalIgnoreCase);
        ManualGames ??= [];
        GameEdits = new Dictionary<string, LinuxGameEditOverride>(GameEdits ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in GameEdits.Keys.ToArray())
        {
            var normalized = (GameEdits[key] ?? new LinuxGameEditOverride()).Normalize();
            if (!normalized.HasPatchTarget && string.IsNullOrWhiteSpace(normalized.LaunchProvider))
                GameEdits.Remove(key);
            else
                GameEdits[key] = normalized;
        }
        HdrProfiles = new Dictionary<string, LinuxHdrGameProfile>(HdrProfiles ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in HdrProfiles.Keys.ToArray()) HdrProfiles[key] = (HdrProfiles[key] ?? new LinuxHdrGameProfile()).Normalize();
        ManualGames = ManualGames
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.InstallPath))
            .Select(entry =>
            {
                entry.Name = entry.Name.Trim();
                entry.InstallPath = NormalizePath(entry.InstallPath);
                entry.ExecutablePath = NormalizeOptionalPath(entry.ExecutablePath);
                entry.PrefixPath = NormalizeOptionalPath(entry.PrefixPath);
                entry.LaunchCommand = string.IsNullOrWhiteSpace(entry.LaunchCommand) ? null : entry.LaunchCommand.Trim();
                entry.Runner = string.IsNullOrWhiteSpace(entry.Runner) ? "Manual Windows" : entry.Runner.Trim();
                return entry;
            })
            .GroupBy(entry => ManualIdentity(entry), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(FilterMode);
        return this;
    }

    private static string? NormalizeOptionalPath(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizePath(value);

    private static string NormalizePath(string value)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())); }
        catch { return value.Trim(); }
    }

    private static string ManualIdentity(LinuxManualGameEntry entry)
        => $"{entry.Name}|{entry.InstallPath}|{entry.ExecutablePath}";
}

/// <summary>
/// XDG-native persisted library state. This deliberately stays separate from settings.json:
/// settings are global preferences, while this file owns library membership and per-game visibility state.
/// </summary>
public sealed class LinuxGameLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string LibraryPath { get; }

    public LinuxGameLibraryStore(string? libraryPath = null)
    {
        LibraryPath = libraryPath ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "game-library.json");
    }

    public LinuxGameLibraryState Load()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return new LinuxGameLibraryState();
            return (JsonSerializer.Deserialize<LinuxGameLibraryState>(File.ReadAllText(LibraryPath), JsonOptions)
                    ?? new LinuxGameLibraryState()).Normalize();
        }
        catch
        {
            return new LinuxGameLibraryState();
        }
    }

    public void Save(LinuxGameLibraryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
        var temp = LibraryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, LibraryPath, overwrite: true);
    }

    public static string GameKey(DetectedGame game)
    {
        var source = (game.Source ?? "").Trim().ToLowerInvariant();
        var identityPath = string.IsNullOrWhiteSpace(game.LibraryIdentityPath)
            ? game.InstallPath
            : game.LibraryIdentityPath;
        string path;
        try { path = Path.GetFullPath(identityPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(); }
        catch { path = (identityPath ?? "").Trim().ToLowerInvariant(); }
        var app = game.SteamAppId is int id ? $"steam:{id}" : "nosteam";
        return $"{source}|{app}|{path}";
    }

    public static string NormalizeFilterMode(string? value) => value?.Trim() switch
    {
        "Favourites" => "Favourites",
        "Installed" => "Installed",
        "Unreal" => "Unreal",
        "Unity" => "Unity",
        "Other" => "Other",
        "RenoDX" => "RenoDX",
        "Luma" => "Luma",
        "Hidden" => "Hidden",
        _ => "All Games"
    };
}
