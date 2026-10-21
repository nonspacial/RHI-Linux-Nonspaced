using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #201 (upstream 2.8.7 <c>AuxInstallService.GacSymlink</c>, manifest <c>gacSymlinkGames</c>): ReShade for XNA Framework
/// games such as Terraria. XNA's <c>Microsoft.Xna.Framework.Graphics.dll</c> lives in the prefix's .NET assembly folder
/// (the GAC) and loads <c>d3d9.dll</c> from there, never from the game folder, so ReShade is installed as
/// <c>d3d9.dll</c> in the game folder (with ReShade.ini) and the DLL and ini are also placed in that GAC folder inside the
/// game's own prefix; the GAC ini copy alone carries <c>[INSTALL] BasePath</c>, pointing ReShade at the game folder for
/// shaders and presets (the game folder's ReShade.ini may be a file shared by every game).
/// <para>Linux deviation (headless Wine check, w7/reshade progress file): upstream symlinks the two GAC entries to the
/// game-folder files; RHI copies them, because a journaled rollback restores a tracked path as a plain file (it cannot
/// put a link back) and copies survive a library move. Updates rewrite both copies; removal deletes only the copies RHI
/// placed (a GAC ReShade.ini that changed since is kept). Wine/Proton load the GAC copy with the per-exe
/// <c>d3d9=native,builtin</c> override (Proton's wine finds no builtin d3d9 without it).</para>
/// </summary>
public static class XnaGacReShade
{
    /// <summary>The DLL name XNA's graphics assembly imports; the only name ReShade takes on an XNA game.</summary>
    public const string ProxyName = "d3d9.dll";

    /// <summary>Said after the DLL name in the install result (PT-BR presentation pair).</summary>
    public const string ModeSuffix = " (game folder and the .NET assembly folder of the prefix, for XNA)";

    /// <summary>ReShade record metadata: the GAC folder (Linux path) the install placed its copies in.</summary>
    internal const string FolderMetadataKey = "ReShade.XnaGacFolder";

    /// <summary>ReShade record metadata: SHA-256 of the GAC ReShade.ini copy RHI last wrote.</summary>
    internal const string IniSha256MetadataKey = "ReShade.XnaGacIniSha256";

    /// <summary>Row note on a listed game (PT-BR presentation pair).</summary>
    public const string RowNote =
        "XNA game: ReShade is installed as d3d9.dll into the game folder and into the .NET assembly folder of its Wine/Proton prefix, where XNA loads it. Use only with single-player/offline titles.";

    /// <summary>Row note on a native Linux build of a listed game (PT-BR presentation pair).</summary>
    public const string NativeLinuxNote =
        "This is the native Linux build of the game, which does not use XNA, so ReShade cannot be added to it. The Windows build run through Proton can take ReShade.";

    /// <summary>The prefix is unknown or not created yet (PT-BR presentation pair).</summary>
    public const string PrefixUnknownMessage =
        "ReShade for this XNA game goes into its Wine/Proton prefix, which is not set up yet; start the game once so its prefix is set up, then install ReShade.";

    /// <summary>Fixed text before the folder when the GAC folder does not exist yet (PT-BR presentation pair).</summary>
    public const string FolderMissingPrefix =
        "ReShade for this XNA game goes into the .NET assembly folder of its prefix, which does not exist yet; start the game once so its prefix is set up, then install ReShade. Folder: ";

    /// <summary>Fixed text before the manifest's path when it would lead outside the game's prefix (PT-BR presentation pair).</summary>
    public const string OutsidePrefixPrefix =
        "The XNA assembly folder the RHI manifest names would lead outside this game's prefix, so nothing was installed: ";

    /// <summary>Fixed text before the file when a file RHI did not place sits in the GAC folder (PT-BR presentation pair).</summary>
    public const string ForeignFilePrefix =
        "A file RHI did not install is already in the XNA assembly folder, so nothing was installed: ";

    /// <summary>Fixed text before the file when RHI's GAC copy changed since RHI placed it (PT-BR presentation pair).</summary>
    public const string ChangedCopyPrefix =
        "ReShade's copy in the XNA assembly folder changed since RHI placed it, so it was not replaced: ";

    /// <summary>The manifest lists the game (and it is not a native Linux build).</summary>
    public static bool IsListed(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.ManifestXnaGacFolder);

    internal static bool IsNativeLinux(DetectedGame game)
        => string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="record"/> is an XNA GAC install.</summary>
    internal static bool IsGacRecord(LinuxDeploymentRecord? record)
        => record?.Metadata.TryGetValue(FolderMetadataKey, out var folder) == true && !string.IsNullOrWhiteSpace(folder);

    /// <summary>
    /// Why the ReShade row of a listed game cannot install now (native Linux build, no prefix, no GAC folder yet, a
    /// manifest path leading outside the prefix), or null. Reads folder existence only.
    /// </summary>
    public static string? OfferRefusal(DetectedGame game)
    {
        if (IsNativeLinux(game)) return NativeLinuxNote;
        var (_, refusal) = ResolveFolder(game);
        return refusal;
    }

    /// <summary>
    /// The GAC folder (Linux path inside the game's prefix) the manifest names for <paramref name="game"/>, or the refusal:
    /// the prefix unknown or not created, a path that does not map into this prefix's <c>drive_c</c> (any <c>..</c>, a drive
    /// mapped elsewhere, a symlinked folder on the way out), or a folder that does not exist yet.
    /// </summary>
    internal static (string? Folder, string? Refusal) ResolveFolder(DetectedGame game)
    {
        var windowsPath = game.ManifestXnaGacFolder;
        if (string.IsNullOrWhiteSpace(windowsPath)) return (null, null);
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(Path.Combine(game.PrefixPath, "drive_c")))
            return (null, PrefixUnknownMessage);
        var trimmed = windowsPath.Trim();
        var driveC = Path.GetFullPath(Path.Combine(game.PrefixPath, "drive_c"));
        if (WindowsPathMapper.ToLinux(game.PrefixPath, trimmed) is not { } mapped)
            return (null, OutsidePrefixPrefix + trimmed);
        // Containment: every folder on the way, links resolved, stays inside this prefix's drive_c.
        if (!PathIdentity.IsSameOrInside(mapped, driveC) || PathIdentity.Canonicalize(mapped) == PathIdentity.Canonicalize(driveC))
            return (null, OutsidePrefixPrefix + trimmed);
        if (!Directory.Exists(mapped)) return (null, FolderMissingPrefix + mapped);
        return (mapped, null);
    }

    /// <summary>
    /// Repair after move (review 3): the GAC folder <paramref name="record"/>'s copies must be re-pointed to — (recorded,
    /// current) when the recorded folder, after <paramref name="mapGameFolder"/> (the game-folder move; null = none), is not
    /// the folder the game's prefix holds now, because the prefix moved on its own (a custom, Heroic or Lutris prefix).
    /// Null for any other record, an unlisted game, or a prefix whose GAC folder cannot be found (nothing to re-point to).
    /// <para>Re-review 1: the copies count as moved only when they really left: the recorded folder no longer holds RHI's
    /// <c>d3d9.dll</c> and the current folder does, and the two folders differ after links are resolved. A game switched
    /// to another prefix (the old one intact), a manifest naming another folder, or the same prefix reached through a
    /// symlinked spelling is not moved: the records still describe where RHI's files are. Reads file existence only.</para>
    /// </summary>
    internal static (string Old, string New)? MovedFolder(LinuxDeploymentRecord record, Func<string, string>? mapGameFolder, DetectedGame game)
    {
        if (!IsGacRecord(record) || !IsListed(game)) return null;
        var recorded = record.Metadata[FolderMetadataKey];
        if (mapGameFolder is not null) recorded = mapGameFolder(recorded);
        if (ResolveFolder(game).Folder is not { } current) return null;
        if (PathIdentity.Canonicalize(recorded) == PathIdentity.Canonicalize(current)) return null;
        if (File.Exists(Path.Combine(recorded, ProxyName)) || !File.Exists(Path.Combine(current, ProxyName))) return null;
        return (recorded, current);
    }

    /// <summary>The bitness the GAC folder declares (<c>GAC_32</c> = x86, <c>GAC_64</c> = x64), else the game's.</summary>
    internal static PeArchitecture ArchitectureOf(DetectedGame game)
    {
        var path = game.ManifestXnaGacFolder ?? "";
        if (path.Contains(@"\GAC_32\", StringComparison.OrdinalIgnoreCase) || path.Contains("/GAC_32/", StringComparison.OrdinalIgnoreCase))
            return PeArchitecture.X86;
        if (path.Contains(@"\GAC_64\", StringComparison.OrdinalIgnoreCase) || path.Contains("/GAC_64/", StringComparison.OrdinalIgnoreCase))
            return PeArchitecture.X64;
        return GameArchitecture.Of(game);
    }

    /// <summary>
    /// <paramref name="unixPath"/> as the Windows path the game sees in <paramref name="prefix"/>: the drive whose
    /// <c>dosdevices</c> link covers the longest part of it (<c>z:</c> → <c>/</c> on every Wine/Proton prefix).
    /// </summary>
    internal static string WindowsPathOf(string prefix, string unixPath)
    {
        var full = PathIdentity.Canonicalize(unixPath);
        string? bestDrive = null;
        var bestRoot = "";
        try
        {
            foreach (var link in Directory.EnumerateFileSystemEntries(Path.Combine(prefix, "dosdevices")))
            {
                var name = Path.GetFileName(link);
                if (name.Length != 2 || name[1] != ':' || !char.IsAsciiLetter(name[0])) continue;
                var target = new FileInfo(link).LinkTarget;
                if (target is null) continue;
                var root = PathIdentity.Canonicalize(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(link)!, target));
                var inside = root == "/" || full == root || full.StartsWith(root + "/", StringComparison.Ordinal);
                if (inside && root.Length > bestRoot.Length) { bestDrive = name.ToUpperInvariant(); bestRoot = root; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (bestDrive is null) return "Z:" + full.Replace('/', '\\');
        var rest = bestRoot == "/" ? full : full[bestRoot.Length..];
        return bestDrive + (rest.Length == 0 ? "\\" : rest.Replace('/', '\\'));
    }
}
