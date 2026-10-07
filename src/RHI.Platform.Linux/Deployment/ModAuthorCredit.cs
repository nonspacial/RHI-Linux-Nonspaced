using System.Text.RegularExpressions;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Add-on file names and mod author credits shown on the RenoDX HDR and Luma Framework rows of the game detail
/// (upstream 2.8.1). File names always come from the deployment record — what RHI actually wrote — never from the
/// catalogue, because installs can rename files.
/// </summary>
public static class ModAuthorCredit
{
    /// <summary>Upstream credits generic Luma (the Luma-Unreal_Engine framework package) to Pumbo.</summary>
    public const string LumaFrameworkAuthor = "Pumbo";

    // Upstream's built-in handle corrections (its manifest authorDisplayNames currently carries the same entry).
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oopydoopy"] = "Jon"
    };

    private static readonly Regex AuthorSeparators = new(@"\s+and\s+|[&+,]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalises a raw catalogue author field ("ShortFuse &amp; Musa", "OopyDoopy (Jon)", "Marat + Ritsu") into
    /// display names joined by ", "; null when nothing is left.
    /// </summary>
    public static string? Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var names = AuthorSeparators.Split(raw)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Select(Resolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Installed add-on file names (.addon, .addon32, .addon64) recorded for a managed component.</summary>
    public static IReadOnlyList<string> AddonFileNames(LinuxDeploymentRecord? record)
        => record is null
            ? []
            : record.Files
                .Select(file => Path.GetFileName(file.Path))
                .Where(IsAddonFileName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>RenoDX author: the resolved catalogue entry's author, else the upstream manifest author override.</summary>
    public static string? ForRenoDx(RenoDxModEntry? entry, DetectedGame game)
        => Format(entry?.Author) ?? Format(game.AuthorOverride);

    /// <summary>
    /// Luma author. An installed record wins (named packages store their catalogue author; generic downloads are
    /// the framework itself). Local drag/drop packages are not attributed. Without a record, the named catalogue
    /// match, else the generic framework.
    /// </summary>
    public static string? ForLuma(LinuxDeploymentRecord? record, LumaCatalogEntry? namedMatch)
    {
        if (record is not null)
        {
            if (Format(record.Metadata.GetValueOrDefault("LumaFramework.Author")) is { } author)
                return author;
            var generic = record.Metadata.GetValueOrDefault("LumaFramework.Generic") == "1";
            var local = record.Metadata.GetValueOrDefault(LumaFrameworkDeploymentService.MetaLocalPackage) == "1";
            return generic && !local ? LumaFrameworkAuthor : null;
        }

        return namedMatch is not null ? Format(namedMatch.Author) : LumaFrameworkAuthor;
    }

    private static string Resolve(string name)
    {
        // "OopyDoopy (Jon)" is looked up by the handle before the parenthesised alias.
        var paren = name.IndexOf('(');
        var key = paren > 0 ? name[..paren].Trim() : name;
        return DisplayNames.TryGetValue(key, out var display) ? display : name;
    }

    private static bool IsAddonFileName(string name)
        => name.EndsWith(".addon", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
}
