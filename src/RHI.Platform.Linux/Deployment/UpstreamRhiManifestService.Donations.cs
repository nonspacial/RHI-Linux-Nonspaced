using System.Text.Json.Serialization;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A mod author listed in the Donate dialog: display name, what they make (may be empty) and an https donation page.</summary>
public sealed record UpstreamDonationAuthor(string Name, string Role, Uri DonationUrl)
{
    /// <summary>Button label: "Ko-fi" for ko-fi.com links, otherwise the link's host without "www.".</summary>
    public string LinkLabel => UpstreamRhiManifestService.DonationLinkLabel(DonationUrl);
}

/// <summary>
/// The Donate dialog's author list. <see cref="ManifestError"/> is set when no manifest could be loaded
/// (offline first run with no last-known-good copy); <see cref="Authors"/> is then upstream's built-in list.
/// </summary>
public sealed record UpstreamDonationList(IReadOnlyList<UpstreamDonationAuthor> Authors, string? ManifestError);

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
    // Upstream 2.8.5 GameCardViewModel.UI.cs AuthorDonationUrls: the hard-coded list the manifest is merged onto.
    // Upstream shows it when no manifest has loaded, and keeps its entries when the manifest omits an author.
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> BuiltInDonationUrls =
    [
        new("ShortFuse", "https://ko-fi.com/shortfuse"),
        new("Jon", "https://ko-fi.com/kickfister"),
        new("Forge", "https://ko-fi.com/forge87682"),
        new("Voosh", "https://ko-fi.com/notvoosh"),
        new("Musa", "https://ko-fi.com/musaqh"),
        new("Pumbo", "https://ko-fi.com/pumbo"),
        new("Nukem", "https://ko-fi.com/nukem9"),
        new("Lilium", "https://ko-fi.com/endlesslyflowering"),
        new("Bit Viper", "https://ko-fi.com/bitviper"),
    ];

    /// <summary>Upstream's built-in authors alone (no roles), as shown when no manifest is available.</summary>
    public static IReadOnlyList<UpstreamDonationAuthor> BuiltInDonationAuthors
        => BuildDonationAuthors(BuiltInDonationUrls, null, null);

    /// <summary>
    /// Upstream's built-in authors merged with the manifest's <c>donationUrls</c> (a manifest link replaces the built-in
    /// one), each with its <c>authorRoles</c> entry, sorted by name (case-insensitive, as upstream). Only absolute https
    /// links without credentials are offered. A missing manifest is not an error for the caller: the built-in list comes
    /// back with <see cref="UpstreamDonationList.ManifestError"/> set. Cancellation still throws.
    /// </summary>
    public async Task<UpstreamDonationList> FetchDonationAuthorsAsync(CancellationToken cancellationToken = default)
    {
        UpstreamManifestDocument manifest;
        try
        {
            manifest = await FetchDocumentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new UpstreamDonationList(BuiltInDonationAuthors, ex.Message);
        }
        return new UpstreamDonationList(
            BuildDonationAuthors(BuiltInDonationUrls, manifest.DonationUrls, manifest.AuthorRoles), null);
    }

    internal static IReadOnlyList<UpstreamDonationAuthor> BuildDonationAuthors(
        IEnumerable<KeyValuePair<string, string>> builtIn,
        IEnumerable<KeyValuePair<string, string>>? donationUrls,
        IEnumerable<KeyValuePair<string, string>>? authorRoles)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in authorRoles ?? [])
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(role))
                roles[name.Trim()] = role.Trim();

        // Names differing only by case are one author: the first spelling is kept and the later link wins, as a
        // case-insensitive dictionary's indexer does when upstream merges the manifest onto its built-in list.
        // (Upstream's manifest load throws on case-only duplicates inside the manifest; Linux tolerates them.)
        var links = new Dictionary<string, (string Name, string? Url)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, rawUrl) in builtIn.Concat(donationUrls ?? []))
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            links[name] = (links.TryGetValue(name, out var existing) ? existing.Name : name, rawUrl);
        }

        // The filter runs on the merged link: a manifest entry with a bad link drops that author
        // rather than silently reviving the older built-in link.
        return links.Values
            .Select(entry => TryParseHttpsLink(entry.Url) is { } url
                ? new UpstreamDonationAuthor(entry.Name, roles.GetValueOrDefault(entry.Name, ""), url)
                : null)
            .OfType<UpstreamDonationAuthor>()
            .OrderBy(author => author.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>"Ko-fi" for ko-fi.com (and its subdomains), otherwise the host without a leading "www.".</summary>
    public static string DonationLinkLabel(Uri url)
    {
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "ko-fi.com" || host.EndsWith(".ko-fi.com", StringComparison.Ordinal)) return "Ko-fi";
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
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
