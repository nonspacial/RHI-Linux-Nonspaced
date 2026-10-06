using System.Text.Json;
using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

public sealed record GitHubReleaseAssetQuery(
    string ReleaseApiUrl,
    string? ExactAssetName = null,
    string? AssetNamePattern = null,
    string? BodyVersionPattern = null);

public sealed record GitHubResolvedReleaseAsset(
    string Version,
    string AssetUrl,
    string AssetName,
    string? Sha256);

public sealed record GitHubReleaseVersion(
    string Tag,
    string Version,
    bool Prerelease);

/// <summary>
/// Resolves a downloadable asset from either a single GitHub release object or a release-list response.
/// Asset matching can use an exact name or a regular expression, which is required for upstream RHI
/// add-ons whose release filenames change on every version. The resolver is payload-agnostic: callers
/// may consume PE add-ons, ZIP packages, shader packs, or other release assets after resolution.
/// </summary>
public sealed class GitHubReleaseAssetResolver
{
    private readonly HttpClient _http;

    public GitHubReleaseAssetResolver(HttpClient http) => _http = http;

    public Task<GitHubResolvedReleaseAsset> ResolveAsync(
        GitHubReleaseAssetQuery query,
        CancellationToken cancellationToken = default)
        => ResolveAsync(query, releaseTag: null, cancellationToken);

    public async Task<GitHubResolvedReleaseAsset> ResolveAsync(
        GitHubReleaseAssetQuery query,
        string? releaseTag,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ReleaseApiUrl))
            throw new ArgumentException("GitHub release API URL is required.", nameof(query));
        if (string.IsNullOrWhiteSpace(query.ExactAssetName) && string.IsNullOrWhiteSpace(query.AssetNamePattern))
            throw new ArgumentException("An exact asset name or asset-name pattern is required.", nameof(query));

        var requestUrl = string.IsNullOrWhiteSpace(releaseTag)
            ? NormalizeReleaseApiUrl(query.ReleaseApiUrl)
            : BuildReleaseTagApiUrl(query.ReleaseApiUrl, releaseTag);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return ResolveFromRelease(doc.RootElement, query)
                ?? throw new InvalidOperationException($"Release does not contain a matching asset ({Describe(query)}).");

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                var resolved = ResolveFromRelease(release, query);
                if (resolved is not null) return resolved;
            }
            throw new InvalidOperationException($"No release in the response contains a matching asset ({Describe(query)}).");
        }

        throw new InvalidOperationException("GitHub release API returned an unexpected JSON shape.");
    }

    public async Task<IReadOnlyList<GitHubReleaseVersion>> ListVersionsAsync(
        string releaseApiUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(releaseApiUrl))
            throw new ArgumentException("GitHub release API URL is required.", nameof(releaseApiUrl));

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildReleaseListApiUrl(releaseApiUrl));
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub releases endpoint did not return a release list.");

        var result = new List<GitHubReleaseVersion>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var prerelease = release.TryGetProperty("prerelease", out var prereleaseEl) && prereleaseEl.ValueKind == JsonValueKind.True;
            result.Add(new GitHubReleaseVersion(tag, tag.TrimStart('v', 'V'), prerelease));
        }
        // Newest first by version, not publish date, so rc10 lists above rc5 (upstream 2.7.9).
        return ReleaseVersionOrdering.NewestFirst(result);
    }

    internal static string BuildReleaseTagApiUrl(string releaseApiUrl, string releaseTag)
    {
        if (string.IsNullOrWhiteSpace(releaseTag))
            throw new ArgumentException("Release tag is required.", nameof(releaseTag));
        if (!Uri.TryCreate(releaseApiUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("Release API URL is invalid.", nameof(releaseApiUrl));

        var path = uri.AbsolutePath;
        var releasesIndex = path.IndexOf("/releases", StringComparison.OrdinalIgnoreCase);
        if (releasesIndex < 0)
            throw new ArgumentException("Release API URL must point to a GitHub releases endpoint.", nameof(releaseApiUrl));
        var releasesPath = path[..(releasesIndex + "/releases".Length)];
        var builder = new UriBuilder(uri)
        {
            Path = $"{releasesPath}/tags/{Uri.EscapeDataString(releaseTag.Trim())}",
            Query = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static string BuildReleaseListApiUrl(string releaseApiUrl)
    {
        if (!Uri.TryCreate(releaseApiUrl, UriKind.Absolute, out var uri)) return releaseApiUrl;
        var path = uri.AbsolutePath;
        var releasesIndex = path.IndexOf("/releases", StringComparison.OrdinalIgnoreCase);
        if (releasesIndex < 0) return releaseApiUrl;
        var builder = new UriBuilder(uri)
        {
            Path = path[..(releasesIndex + "/releases".Length)],
            Query = "per_page=100"
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// GitHub's /releases/latest endpoint excludes prereleases. Upstream RHI
    /// deliberately switched dynamic add-on lookups to the releases list so a published
    /// prerelease can be consumed when it is the newest release. Preserve explicit list URLs.
    /// </summary>
    internal static string NormalizeReleaseApiUrl(string releaseApiUrl)
    {
        if (!Uri.TryCreate(releaseApiUrl, UriKind.Absolute, out var uri)) return releaseApiUrl;
        if (!uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase)) return releaseApiUrl;

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath[..^"/latest".Length],
            Query = "per_page=1"
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static string ReadVersion(JsonElement release, string? bodyVersionPattern)
    {
        if (!string.IsNullOrWhiteSpace(bodyVersionPattern) && release.TryGetProperty("body", out var bodyEl))
        {
            var body = bodyEl.GetString() ?? "";
            var match = Regex.Match(body, bodyVersionPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                return match.Groups[1].Value.Trim();
        }

        var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        if (!string.IsNullOrWhiteSpace(tag) && !tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return tag.TrimStart('v', 'V');
        var name = release.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        return string.IsNullOrWhiteSpace(name) ? "latest" : name!;
    }

    private static GitHubResolvedReleaseAsset? ResolveFromRelease(JsonElement release, GitHubReleaseAssetQuery query)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !Matches(name, query)) continue;
            var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var digest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim()
                : null;
            return new GitHubResolvedReleaseAsset(
                ReadVersion(release, query.BodyVersionPattern),
                url,
                name,
                sha);
        }

        return null;
    }

    private static bool Matches(string assetName, GitHubReleaseAssetQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ExactAssetName) &&
            string.Equals(assetName, query.ExactAssetName, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(query.AssetNamePattern) &&
               Regex.IsMatch(assetName, query.AssetNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Describe(GitHubReleaseAssetQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ExactAssetName) && !string.IsNullOrWhiteSpace(query.AssetNamePattern))
            return $"{query.ExactAssetName} or /{query.AssetNamePattern}/";
        if (!string.IsNullOrWhiteSpace(query.ExactAssetName)) return query.ExactAssetName;
        return $"/{query.AssetNamePattern}/";
    }
}
