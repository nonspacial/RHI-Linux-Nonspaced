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
