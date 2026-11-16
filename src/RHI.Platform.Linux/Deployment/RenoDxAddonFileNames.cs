using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #287 (upstream 2.9.3 <c>870f4a9</c>, <c>AddonFileNames</c>): a browser or file manager saves a second download of an
/// add-on as <c>renodx-x (1).addon64</c> or <c>renodx-x - Copy.addon64</c>. Such a copy is the same add-on, and ReShade
/// would load it twice beside the real one.
/// </summary>
public static class RenoDxAddonFileNames
{
    // "<name> (1).addon64", "<name>(2).addon32", "<name> - Copy.addon64", "<name> - Copy (2).addon64" (upstream's pattern)
    private static readonly Regex Duplicate = new(
        @"^(?<name>.+?)(?:\s*\(\d{1,3}\)|\s+-\s+Copy(?:\s*\(\d{1,3}\))?)(?<ext>\.addon(?:64|32)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What a set-aside duplicate is renamed to: ReShade loads only <c>.addon</c>, <c>.addon32</c> and <c>.addon64</c>.</summary>
    public const string SetAsideSuffix = ".rhi-duplicate";

    /// <summary>The add-on's real name without a duplicate-download suffix ("renodx-x (1).addon64" → "renodx-x.addon64").</summary>
    public static string Canonical(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        var match = Duplicate.Match(fileName);
        return match.Success ? match.Groups["name"].Value.TrimEnd() + match.Groups["ext"].Value : fileName;
    }

    /// <summary>Whether <paramref name="fileName"/> is a duplicate-download copy of <paramref name="realName"/> (not the same name).</summary>
    public static bool IsDuplicateOf(string fileName, string realName)
        => !fileName.Equals(realName, StringComparison.OrdinalIgnoreCase)
           && Canonical(fileName).Equals(Canonical(realName), StringComparison.OrdinalIgnoreCase);

    /// <summary>A free name beside <paramref name="path"/> to set the copy aside under (never an existing file).</summary>
    public static string SetAsidePath(string path)
    {
        var candidate = path + SetAsideSuffix;
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = $"{path}{SetAsideSuffix}-{n}";
        return candidate;
    }
}
