using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#186: upstream's legacy ReShade list and per-game pins (manifest <c>legacyReShadeAvailable</c>, <c>legacyReShadeVersions</c>).</summary>
public sealed partial class UpstreamRhiManifestService
{
    /// <summary>The version the Legacy channel offers when the manifest lists none (the fork's long-standing default).</summary>
    public const string DefaultLegacyReShadeVersion = "6.4.1";

    /// <summary>A ReShade version RHI can download from reshade.me (<c>6.4.1</c>; numbers only, 2 to 4 parts).</summary>
    public static bool IsReShadeVersion(string? value) => value is not null && ReShadeVersion().IsMatch(value.Trim());

    /// <summary>
    /// The legacy versions the manifest offers, in its order (newest first upstream), without duplicates or anything that is
    /// not a plain version; empty when the manifest lists none or cannot be read (the caller offers the default then).
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchLegacyReShadeAvailableAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return NormalizeLegacyReShadeAvailable(manifest.LegacyReShadeAvailable);
    }

    internal static IReadOnlyList<string> NormalizeLegacyReShadeAvailable(IEnumerable<string>? values)
        => (values ?? []).Where(IsReShadeVersion).Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyDictionary<string, string> NormalizeLegacyReShadeVersions(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var (game, version) in values)
            if (!string.IsNullOrWhiteSpace(game) && IsReShadeVersion(version))
                result[game.Trim()] = version.Trim();
        return result;
    }

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,4}){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReShadeVersion();
}
