using System.Text.Json.Serialization;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A mod author listed in the Donate dialog: display name, what they make (may be empty) and an https donation page.</summary>
public sealed record UpstreamDonationAuthor(string Name, string Role, Uri DonationUrl);

internal sealed partial class UpstreamManifestDocument
{
    [JsonPropertyName("donationUrls")]
    public Dictionary<string, string>? DonationUrls { get; set; }

    // Upstream 2.8.2+: what each author is known for, keyed by the same display name as donationUrls.
    [JsonPropertyName("authorRoles")]
    public Dictionary<string, string>? AuthorRoles { get; set; }
}

public sealed partial class UpstreamRhiManifestService
{
    /// <summary>
    /// Authors from the manifest's <c>donationUrls</c> with their <c>authorRoles</c> entry, sorted by name
    /// (case-insensitive, as upstream). Only absolute https links are offered; entries with any other link are dropped.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamDonationAuthor>> FetchDonationAuthorsAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        return BuildDonationAuthors(manifest.DonationUrls, manifest.AuthorRoles);
    }

    private static IReadOnlyList<UpstreamDonationAuthor> BuildDonationAuthors(
        IReadOnlyDictionary<string, string>? donationUrls,
        IReadOnlyDictionary<string, string>? authorRoles)
    {
        if (donationUrls is null || donationUrls.Count == 0) return [];

        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in authorRoles ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(role))
                roles[name.Trim()] = role.Trim();

        // Keys differing only by case collapse to one author; the later entry wins, as upstream's merge does.
        var authors = new Dictionary<string, UpstreamDonationAuthor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, rawUrl) in donationUrls)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name) || TryParseHttpsLink(rawUrl) is not { } url) continue;
            authors[name] = new UpstreamDonationAuthor(name, roles.GetValueOrDefault(name, ""), url);
        }

        return authors.Values
            .OrderBy(author => author.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Uri? TryParseHttpsLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri;
    }
}
