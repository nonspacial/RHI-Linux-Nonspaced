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
    // Upstream credits the generic engine add-ons and Marat's UE-Extended build by name.
    public const string GenericUnrealAuthor = "ShortFuse";
    public const string GenericUnityAuthor = "Voosh";
    public const string UeExtendedAuthor = "Marat";

    // Upstream's built-in handle corrections (its manifest authorDisplayNames currently carries the same entry).
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oopydoopy"] = "Jon"
    };

    // Upstream SplitAuthors (GameCardViewModel.UI.cs): only "&" and " and " separate authors.
    private static readonly Regex AuthorSeparators = new(@"\s+and\s+|&", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalises a raw catalogue author field ("ShortFuse &amp; Musa", "OopyDoopy (Jon)") into display names joined
    /// by ", "; null when nothing is left. Like upstream, only "&amp;" and "and" separate names ("Marat + Ritsu" and
    /// "Ritsu/marat569" stay one name); a comma left dangling before a separator ("Ritsu, &amp; ShortFuse") is dropped.
    /// </summary>
    public static string? Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var names = AuthorSeparators.Split(raw)
            .Select(name => name.Trim().TrimEnd(',').TrimEnd())
            .Where(name => name.Length > 0)
            .Select(Resolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// Add-on file names (.addon, .addon32, .addon64) recorded for a managed component. When the probe found add-on
    /// files for the row (<paramref name="detected"/>), only recorded names it found are kept, so a record that no
    /// longer matches the disk does not show stale names.
    /// </summary>
    public static IReadOnlyList<string> AddonFileNames(LinuxDeploymentRecord? record, IReadOnlyCollection<string>? detected = null)
    {
        if (record is null) return [];
        var names = record.Files
            .Select(file => Path.GetFileName(file.Path))
            .Where(IsAddonFileName)
            .Distinct(StringComparer.Ordinal);
        if (detected is { Count: > 0 })
        {
            var onDisk = new HashSet<string>(detected, StringComparer.OrdinalIgnoreCase);
            names = names.Where(onDisk.Contains);
        }
        return names.ToArray();
    }

    /// <summary>
    /// RenoDX author, in upstream's order (GameCardViewModel.UI.cs AuthorList + GameInitializationService
    /// authorOverrides): Marat for the UE-Extended build; else the mod's maintainer — the RHI database author, or the
    /// engine maintainer of a generic fallback; else the manifest <c>authorOverrides</c> entry; else ShortFuse / Voosh
    /// when the add-on is the generic Unreal / Unity build. Linux credits the add-on it actually installs, so the
    /// UE-Extended and generic checks read the entry's download URL.
    /// </summary>
    public static string? ForRenoDx(RenoDxModEntry? entry, DetectedGame game)
    {
        var urls = new[] { entry?.SnapshotUrl64, entry?.SnapshotUrl32 };
        if (urls.Any(IsUeExtendedUrl)) return UeExtendedAuthor;
        return Format(entry?.Author)
               ?? Format(game.AuthorOverride)
               ?? urls.Select(GenericEngineAuthor).FirstOrDefault(author => author is not null);
    }

    /// <summary>True for Marat's UE-Extended add-on (renodx-ue-extended.addon64).</summary>
    public static bool IsUeExtendedUrl(string? url)
        => AddonUrlFileName(url).StartsWith("renodx-ue-extended.", StringComparison.OrdinalIgnoreCase);

    private static string? GenericEngineAuthor(string? url)
    {
        var file = AddonUrlFileName(url);
        if (file.StartsWith("renodx-unrealengine.", StringComparison.OrdinalIgnoreCase)) return GenericUnrealAuthor;
        if (file.StartsWith("renodx-unityengine.", StringComparison.OrdinalIgnoreCase)) return GenericUnityAuthor;
        return null;
    }

    private static string AddonUrlFileName(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "";

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
