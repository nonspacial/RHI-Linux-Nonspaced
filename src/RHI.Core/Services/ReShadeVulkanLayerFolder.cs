using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// Where RHI puts ReShade's Vulkan layer for a game (#92): its own folder in the prefix's
/// <c>C:\ProgramData\RHI-Linux\ReShadeVulkan\</c>. The one answer for the installer and for the component probe, which
/// counts a Vulkan-layer ReShade on disk only when this game's layer manifest is there (a DirectX game that ships its own
/// <c>vulkan-1.dll</c> beside a kept <c>ReShade.ini</c> is not ReShade).
/// </summary>
public static class ReShadeVulkanLayerFolder
{
    /// <summary>The folders below <c>drive_c</c>, outermost first; the last one holds one folder per game.</summary>
    public static readonly IReadOnlyList<string> RootParts = ["ProgramData", "RHI-Linux", "ReShadeVulkan"];

    /// <summary>The prefix folder that holds every game's layer folder (<c>…/drive_c/ProgramData/RHI-Linux/ReShadeVulkan</c>).</summary>
    public static string Root(string prefixPath) => Path.Combine([prefixPath, "drive_c", .. RootParts]);

    /// <summary>The game's folder for the layer inside its prefix, and the same folder as Windows sees it (<c>C:\…</c>).</summary>
    public static (string LinuxPath, string WindowsPath) For(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var id = Id(game);
        return (Path.Combine(Root(game.PrefixPath!), id), @"C:\" + string.Join('\\', RootParts) + @"\" + id);
    }

    /// <summary><c>&lt;exe stem&gt;-&lt;8 hex of the game key&gt;</c>: readable, and distinct per game in a shared prefix.</summary>
    public static string Id(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var stem = Path.GetFileNameWithoutExtension(game.ExecutablePath ?? game.Name ?? "game");
        var clean = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('.');
        if (clean.Length == 0) clean = "game";
        if (clean.Length > 40) clean = clean[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameRecordKey.Of(game))))[..8].ToLowerInvariant();
        return $"{clean}-{hash}";
    }

    public static string ManifestName(bool is32) => is32 ? "ReShade32.json" : "ReShade64.json";

    /// <summary>Whether RHI's layer manifest for <paramref name="game"/> sits in the game's layer folder (either bitness).</summary>
    public static bool HasManifest(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.PrefixPath)) return false;
        var folder = For(game).LinuxPath;
        return File.Exists(Path.Combine(folder, ManifestName(is32: false))) || File.Exists(Path.Combine(folder, ManifestName(is32: true)));
    }
}

/// <summary>
/// The per-game key of the deployment ledger and every per-game store (<c>steam:&lt;id&gt;</c>, <c>heroic:&lt;app&gt;</c>, else
/// the install path). <c>RHI.Platform.Linux.Settings.GameRecordIdentity.Key</c> is this; it lives here so RHI.Core can name
/// per-game folders (the Vulkan layer's) the same way.
/// </summary>
public static class GameRecordKey
{
    public static string Of(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.SteamAppId is int steamId) return $"steam:{steamId}";
        if (!string.IsNullOrWhiteSpace(game.HeroicAppName)) return "heroic:" + game.HeroicAppName.Trim();
        return Path.GetFullPath(game.InstallPath);
    }
}
