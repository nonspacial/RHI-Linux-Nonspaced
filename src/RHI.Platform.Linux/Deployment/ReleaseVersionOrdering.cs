using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Newest-first ordering for GitHub release tags (upstream RHI 2.7.9): the numeric base version
/// decides first, then the release-candidate number, so <c>rc10</c> sorts above <c>rc5</c> and a
/// stable release sorts above every <c>rc</c> of the same base. Other pre-release suffixes rank
/// below any numbered rc, as upstream does.
/// </summary>
public static class ReleaseVersionOrdering
{
    // Anchored at the end so a product name such as "renodx-dlss5-…" never wins over the version.
    private static readonly Regex VersionAtEnd = new(
        @"(\d+(?:\.\d+)*)(?:[-_.]?([A-Za-z]+)[-_.]?(\d*))?$",
        RegexOptions.CultureInvariant);

    public static (Version Base, int PreRelease) Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (new Version(0, 0), 0);
        var match = VersionAtEnd.Match(text.Trim());
        if (!match.Success) return (new Version(0, 0), 0);

        var parts = match.Groups[1].Value.Split('.')
            .Take(4)
            .Select(part => int.TryParse(part, out var n) ? n : 0)
            .ToList();
        while (parts.Count < 2) parts.Add(0);
        var baseVersion = parts.Count switch
        {
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };

        if (!match.Groups[2].Success) return (baseVersion, int.MaxValue);
        var preRelease = match.Groups[2].Value.Equals("rc", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(match.Groups[3].Value, out var rc)
            ? rc
            : 0;
        return (baseVersion, preRelease);
    }

    public static IReadOnlyList<GitHubReleaseVersion> NewestFirst(IEnumerable<GitHubReleaseVersion> versions)
        => versions
            .Select(version => (Version: version, Key: Key(version.Version)))
            .OrderByDescending(item => item.Key.Base)
            .ThenByDescending(item => item.Key.PreRelease)
            .Select(item => item.Version)
            .ToArray();
}
