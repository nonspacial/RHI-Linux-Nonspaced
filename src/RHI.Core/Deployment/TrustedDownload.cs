using System.Net.Http.Headers;
using RHI.Core.Services;

namespace RHI.Core.Deployment;

/// <summary>Where a download may come from and how large it may be.</summary>
/// <param name="AllowedHosts">Exact host names (case-insensitive) for the requested URL and for the final URL after redirects.</param>
public sealed record TrustedDownloadPolicy(IReadOnlyCollection<string> AllowedHosts, long MaxBytes = TrustedDownloadPolicy.DefaultMaxBytes)
{
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>GitHub release downloads: github.com redirects to one of the asset CDNs.</summary>
    public static readonly TrustedDownloadPolicy GitHubReleases = new(
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"]);

    public bool Allows(Uri uri)
        => uri.IsAbsoluteUri
           && uri.Scheme == Uri.UriSchemeHttps
           && AllowedHosts.Any(host => string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The deployment layer's download path: HTTPS only, host allow-list checked on the requested URL
/// and on the final URL after redirects, size cap, optional published SHA-256, written through
/// <see cref="AtomicFile"/> and, for cache entries, reused only through <see cref="CacheStamp"/>.
/// </summary>
public static class TrustedDownload
{
    /// <summary>Parses <paramref name="url"/> and refuses it unless the policy allows it.</summary>
    public static Uri Validate(string url, TrustedDownloadPolicy policy)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Refusing a non-HTTPS download URL: {url}");
        if (!policy.Allows(uri))
            throw new InvalidDataException($"Unexpected download host: {uri.Host}");
        return uri;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into a verified temporary file beside <paramref name="targetPath"/>.
    /// The caller places it (<see cref="AtomicFile.MoveIntoPlace"/>) or disposes it to delete it.
    /// </summary>
    public static async Task<TempDownload> DownloadToTempAsync(
        HttpClient http,
        string url,
        string targetPath,
        TrustedDownloadPolicy policy,
        string? expectedSha256 = null,
        ProductInfoHeaderValue? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        var uri = Validate(url, policy);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (userAgent is not null) request.Headers.UserAgent.Add(userAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!policy.Allows(finalUri))
            throw new InvalidDataException($"Download of {uri.Host}{uri.AbsolutePath} was redirected to an unexpected host: {finalUri.Host}");
        if (response.Content.Headers.ContentLength is long announced && announced > policy.MaxBytes)
            throw new InvalidDataException($"Download is larger than the {policy.MaxBytes}-byte limit: {announced} bytes.");

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var staged = await AtomicFile.StageStreamAsync(body, targetPath, expectedSha256, null, policy.MaxBytes, cancellationToken).ConfigureAwait(false);
        return new TempDownload(staged);
    }

    /// <summary>
    /// Returns <paramref name="cacheEntry"/> after making sure it holds a stamped download of
    /// <paramref name="url"/>: a valid stamp is reused as is, anything else is invalidated, downloaded
    /// again, verified against <paramref name="publishedSha256"/> when given, placed atomically and stamped.
    /// </summary>
    /// <returns>The entry's SHA-256 (upper-case hex).</returns>
    public static async Task<string> EnsureCachedAsync(
        HttpClient http,
        string url,
        string cacheEntry,
        TrustedDownloadPolicy policy,
        string? version = null,
        string? publishedSha256 = null,
        ProductInfoHeaderValue? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        Validate(url, policy);
        if (CacheStamp.TryValidate(cacheEntry, url, version, publishedSha256, out var cachedSha))
            return cachedSha;

        CacheStamp.Invalidate(cacheEntry);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheEntry))!);
        using var download = await DownloadToTempAsync(http, url, cacheEntry, policy, publishedSha256, userAgent, cancellationToken).ConfigureAwait(false);
        download.MoveTo(cacheEntry);
        return CacheStamp.Write(cacheEntry, url, version, publishedSha256);
    }
}

/// <summary>A downloaded, verified temporary file; deleted on dispose unless it was moved into place.</summary>
public sealed class TempDownload : IDisposable
{
    private bool _placed;

    internal TempDownload(StagedFile staged) => Staged = staged;

    public StagedFile Staged { get; }
    public string Path => Staged.Path;
    public string Sha256 => Staged.Sha256;
    public long Length => Staged.Length;

    public void MoveTo(string target)
    {
        AtomicFile.MoveIntoPlace(Staged.Path, target);
        _placed = true;
    }

    public void Dispose()
    {
        if (!_placed) FileCleanup.TryDelete(Staged.Path);
    }
}
