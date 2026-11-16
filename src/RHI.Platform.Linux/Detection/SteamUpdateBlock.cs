using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection;

/// <summary>Where a Steam game's update block stands.</summary>
public enum SteamUpdateBlockState
{
    /// <summary>Not a Steam game, or its app manifest is not where Steam keeps it (the switch is not offered).</summary>
    Unavailable,

    /// <summary>The manifest is writable: Steam updates the game as usual.</summary>
    Allowed,

    /// <summary>The manifest is read-only: Steam cannot record an update, so it does not update the game.</summary>
    Blocked
}

/// <summary>What a change of the block did; <see cref="Message"/> is the plain-English reason when it did not.</summary>
public sealed record SteamUpdateBlockResult(bool Success, SteamUpdateBlockState State, string? Message = null);

/// <summary>
/// #290 (upstream 2.9.4 "Block Steam updates"): makes a Steam game's <c>appmanifest_&lt;appid&gt;.acf</c> read-only so
/// Steam cannot update the game (mods keep working until they are updated for the new version); turning it off gives the
/// owner write access back. Linux: the manifest is in the game's Steam library (<c>…/steamapps/</c>, native or Flatpak
/// Steam), found from the game's folder (<c>steamapps/common/&lt;installdir&gt;</c>) and checked against the manifest's own
/// <c>appid</c> and <c>installdir</c>. Only the file's mode changes, never its contents. Refused while Steam is updating
/// the game (<c>StateFlags</c> update running / started / paused). The state is read from the file each time, so a
/// manifest Steam replaced reads as allowed again.
/// </summary>
public static class SteamUpdateBlock
{
    // StateFlags bits (Steam's EAppState): 256 update running, 512 update paused, 1024 update started.
    private const long UpdateInProgressFlags = 256 | 512 | 1024;

    private const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public const string NotSteamMessage = "Block Steam updates is only for Steam games whose Steam app manifest RHI can find.";
    public const string UpdatingMessage = "Steam is updating this game right now. Wait until it finishes (or cancel it in Steam), then try again.";
    public const string FailedPrefix = "The Steam app manifest could not be changed: ";

    /// <summary>The game's app manifest, or null when it is not a Steam game or the manifest is not where Steam keeps it.</summary>
    public static string? ManifestPath(DetectedGame game)
    {
        if (game.SteamAppId is not int appId || string.IsNullOrWhiteSpace(game.InstallPath)) return null;
        if (!string.Equals(game.Source, "Steam", StringComparison.OrdinalIgnoreCase)) return null;
        string install;
        try { install = Path.GetFullPath(game.InstallPath).TrimEnd('/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var common = Path.GetDirectoryName(install);
        if (common is null || !string.Equals(Path.GetFileName(common), "common", StringComparison.Ordinal)) return null;
        var steamapps = Path.GetDirectoryName(common);
        if (steamapps is null || !string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.Ordinal)) return null;
        var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
        var values = Read(manifest);
        if (values is null) return null;
        // The manifest must describe this game's folder: never touch another app's file.
        if (values.Value("appid") != appId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
        if (!string.Equals(values.Value("installdir"), Path.GetFileName(install), StringComparison.Ordinal)) return null;
        return manifest;
    }

    public static SteamUpdateBlockState State(DetectedGame game)
    {
        if (ManifestPath(game) is not { } manifest) return SteamUpdateBlockState.Unavailable;
        try
        {
            return (File.GetUnixFileMode(manifest) & UnixFileMode.UserWrite) == 0 ? SteamUpdateBlockState.Blocked : SteamUpdateBlockState.Allowed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SteamUpdateBlockState.Unavailable;
        }
    }

    /// <summary>Blocks (read-only) or allows (owner write back) Steam updates of <paramref name="game"/>.</summary>
    public static SteamUpdateBlockResult Set(DetectedGame game, bool blocked)
    {
        if (ManifestPath(game) is not { } manifest) return new(false, SteamUpdateBlockState.Unavailable, NotSteamMessage);
        var current = State(game);
        if (current == (blocked ? SteamUpdateBlockState.Blocked : SteamUpdateBlockState.Allowed)) return new(true, current);
        if (blocked && Read(manifest)?.Value("StateFlags") is { } flags && long.TryParse(flags, out var state) && (state & UpdateInProgressFlags) != 0)
            return new(false, current, UpdatingMessage);
        try
        {
            var mode = File.GetUnixFileMode(manifest);
            File.SetUnixFileMode(manifest, blocked ? mode & ~WriteBits : mode | UnixFileMode.UserWrite);
            AppLog.Warn("steam-update-block", $"{(blocked ? "Blocked" : "Allowed")} Steam updates of {game.Name}: {manifest}");
            return new(true, State(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("steam-update-block", $"Could not change {manifest}", ex);
            return new(false, current, FailedPrefix + ex.Message);
        }
    }

    private static SteamKeyValues? Read(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            var root = SteamKeyValues.Parse(File.ReadAllText(manifest));
            return root.Child("AppState");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
