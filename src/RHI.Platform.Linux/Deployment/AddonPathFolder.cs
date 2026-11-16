using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Launchers;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #255 (upstream 2.8.8 <c>54ec8f2</c>): where ReShade loads add-ons from — <c>[ADDON] AddonPath</c> in the game's
/// reshade.ini, else the game's binary folder (<see cref="RenoDxDeploymentService.ResolveAddonDeployPath"/>) — and the
/// note RHI adds when an add-on it moved there leaves a same-named file in the game folder that is not RHI's.
/// </summary>
public static class AddonPathFolder
{
    /// <summary>Fixed text before the paths of add-ons left in the game folder (PT-BR presentation pair).</summary>
    public const string LeftInGameFolderPrefix =
        "Not RHI's, so left in the game folder (ReShade loads add-ons from reshade.ini's AddonPath, not from there): ";

    /// <summary>
    /// Fixed text before the AddonPath value when it names a Windows drive RHI cannot map to a Linux folder (PT-BR
    /// presentation pair). w13 final review m2.
    /// </summary>
    public const string UnmappedPrefix =
        "ReShade's AddonPath names a Windows drive RHI cannot find in this game's Wine prefix, so nothing was changed. Set AddonPath to a folder inside the prefix (C:\\…, Z:\\…) or to a relative folder: ";

    /// <summary>
    /// w13 final review m1: the update check's text (after the component's name) when RHI's add-on is recorded somewhere
    /// other than ReShade's add-on folder (AddonPath set or changed after the install) and no newer release exists: the
    /// move is the update (PT-BR presentation pair).
    /// </summary>
    public const string MoveDueSuffix =
        " is not in reshade.ini's AddonPath folder, where ReShade loads add-ons; Update moves it there.";

    /// <summary>
    /// ReShade's add-on folder for a game whose binaries are in <paramref name="binaryDirectory"/> and whose Wine prefix is
    /// <paramref name="prefixPath"/>: <c>[ADDON] AddonPath</c> in its reshade.ini (the first of a <c>;</c> list), else the
    /// binary folder. ReShade runs in Wine, so an absolute AddonPath is a Windows path: a drive path (<c>C:\…</c>,
    /// <c>Z:\home\…</c>) maps through the prefix's <c>drive_c</c> / <c>dosdevices</c> links
    /// (<see cref="WindowsPathMapper"/>); a relative one joins the binary folder; a Linux absolute path is kept. A Windows
    /// path that cannot be mapped (an unknown prefix, a drive the prefix does not link, a UNC or drive-relative path) is
    /// never joined on as a folder: <see cref="Resolution.Refusal"/> says so and <see cref="Resolution.Folder"/> stays the
    /// binary folder, so probes look there and installers refuse.
    /// </summary>
    public static Resolution Resolve(string binaryDirectory, string? prefixPath)
    {
        string? first;
        try
        {
            first = ReadAddonPath(binaryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Resolution(binaryDirectory, null);
        }
        if (string.IsNullOrWhiteSpace(first)) return new Resolution(binaryDirectory, null);
        var value = first.Trim().Trim('"');
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            var mapped = string.IsNullOrWhiteSpace(prefixPath) ? null : WindowsPathMapper.ToLinux(prefixPath, value);
            return mapped is null ? new Resolution(binaryDirectory, UnmappedPrefix + value) : new Resolution(mapped, null);
        }
        // UNC (\\server\share) and drive-relative (\Addons) paths name no Linux folder RHI can know.
        if (value.StartsWith('\\')) return new Resolution(binaryDirectory, UnmappedPrefix + value);
        if (value.StartsWith('/')) return new Resolution(value, null);
        try
        {
            return new Resolution(Path.GetFullPath(Path.Combine(binaryDirectory, value.Replace('\\', '/'))), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Resolution(binaryDirectory, UnmappedPrefix + value);
        }
    }

    /// <summary>The folder, and the refusal an installer returns instead of writing there (null when the folder is usable).</summary>
    public readonly record struct Resolution(string Folder, string? Refusal);

    private static string? ReadAddonPath(string binaryDirectory)
    {
        var ini = RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory);
        if (ini is null || !File.Exists(ini)) return null;
        var inAddon = false;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inAddon = line.Equals("[ADDON]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inAddon || !line.StartsWith("AddonPath", StringComparison.OrdinalIgnoreCase) || !line.Contains('=')) continue;
            var value = line[(line.IndexOf('=') + 1)..].Trim();
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        }
        return null;
    }

    /// <summary>
    /// w13 final review m3: true when <paramref name="a"/> and <paramref name="b"/> reach the same file — the same path
    /// once every symlinked folder is resolved, or the same device and inode (a hard link, a bind mount). An AddonPath that
    /// reaches the game folder through a link names RHI's own copy, so a move-on-update must not delete it as "the old
    /// copy" after writing the new one there.
    /// </summary>
    public static bool SameFile(string a, string b)
        => PathIdentity.AreSame(a, b)
           || (FileIdentity.TryGet(a) is { } left && FileIdentity.TryGet(b) is { } right && left == right);

    /// <summary>
    /// w13 final review nit (DLSSG's <c>EnsureFolderAsync</c> pattern): creates <paramref name="folder"/> and every missing
    /// folder above it, outermost first, each journaled before it is made (<see cref="CreatedDirectoryCompensator"/>), so a
    /// rollback or a crash before the commit removes it again once its files are gone. Every folder made is added to
    /// <paramref name="created"/> for <see cref="RemoveCreated"/> (the rollback of a scope without a journal).
    /// </summary>
    public static async Task CreateJournaledAsync(string folder, DeploymentOperation? operation, List<string> created, CancellationToken cancellationToken)
    {
        var missing = new Stack<string>();
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
             !string.IsNullOrEmpty(current) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current))
            missing.Push(current);
        while (missing.TryPop(out var path))
        {
            if (operation is not null)
                await operation.AddCompensationAsync(CreatedDirectoryCompensator.KindName, CreatedDirectoryCompensator.Describe(path), cancellationToken).ConfigureAwait(false);
            AtomicFile.CreateDirectoryDurable(path);
            created.Add(path);
        }
    }

    /// <summary>After a rollback: the folders <see cref="CreateJournaledAsync"/> made, deepest first, each only when empty; never a link.</summary>
    public static void RemoveCreated(IEnumerable<string> created)
    {
        foreach (var folder in created.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length))
            if (new DirectoryInfo(folder) is { Exists: true, LinkTarget: null }) FileCleanup.TryDeleteEmptyDirectory(folder);
    }

    /// <summary>True when ReShade's add-on folder for <paramref name="binaryDirectory"/> is not that folder itself.</summary>
    public static bool UsesAddonPath(string binaryDirectory, string deployDirectory)
        => !string.Equals(Trim(binaryDirectory), Trim(deployDirectory), StringComparison.Ordinal);

    /// <summary>
    /// " · " + <see cref="LeftInGameFolderPrefix"/> + the paths, when AddonPath is in use and a file named as one of
    /// <paramref name="fileNames"/> is still in the binary folder (RHI's own copies are gone by then); else "".
    /// </summary>
    public static string LeftInGameFolderNote(string? binaryDirectory, string deployDirectory, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(binaryDirectory) || !UsesAddonPath(binaryDirectory, deployDirectory)) return "";
        var left = new List<string>();
        foreach (var name in fileNames.Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(binaryDirectory)) break;
                left.AddRange(Directory.EnumerateFiles(binaryDirectory, name, CaseInsensitiveTopLevel));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return left.Count == 0 ? "" : " · " + LeftInGameFolderPrefix + string.Join(", ", left.Order(StringComparer.Ordinal));
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static readonly EnumerationOptions CaseInsensitiveTopLevel = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = true
    };
}
