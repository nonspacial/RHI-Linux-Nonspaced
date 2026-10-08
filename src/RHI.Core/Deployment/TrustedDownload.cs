using System.Net.Http.Headers;
using RHI.Core.Services;

namespace RHI.Core.Deployment;

/// <summary>
/// Where a download may come from and how large it may be — the one download allow-list. Rules: absolute URI,
/// HTTPS, default port, no user-info, a DNS host (no IP literal) equal to an allowed host or exactly one DNS label
/// under an allowed parent domain (<c>marat569.github.io</c>, never <c>a.b.github.io</c> or bare <c>github.io</c>).
/// Checked on the requested URL before any request and on the final URL after redirects, before the body is read.
/// </summary>
public sealed record TrustedDownloadPolicy
{
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Cap for a single add-on/DLL download (real ones are a few MB).</summary>
    public const long AddonMaxBytes = 256L * 1024 * 1024;

    public TrustedDownloadPolicy(
        string name,
        string hostDescription,
        IReadOnlyCollection<string> allowedHosts,
        IReadOnlyCollection<string>? singleLabelSubdomainsOf = null,
        long maxBytes = DefaultMaxBytes)
    {
        Name = name;
        HostDescription = hostDescription;
        AllowedHosts = allowedHosts.Select(host => host.Trim().TrimEnd('.').ToLowerInvariant()).ToArray();
        SubdomainParents = (singleLabelSubdomainsOf ?? []).Select(parent => parent.Trim().Trim('.').ToLowerInvariant()).ToArray();
        MaxBytes = maxBytes;
    }

    /// <summary>What is being downloaded ("RenoDX add-on"); part of the refusal message.</summary>
    public string Name { get; init; }

    /// <summary>The trusted hosts in plain words ("GitHub or GitHub Pages"); part of the refusal message.</summary>
    public string HostDescription { get; init; }

    public IReadOnlyCollection<string> AllowedHosts { get; init; }

    /// <summary>Parent domains under which exactly one more DNS label is trusted (GitHub Pages: <c>github.io</c>).</summary>
    public IReadOnlyCollection<string> SubdomainParents { get; init; }

    public long MaxBytes { get; init; }

    /// <summary>
    /// Fixed text before the refused link in <see cref="UntrustedDownloadException"/>; one per policy, each matched by a
    /// PT-BR presentation pair.
    /// </summary>
    public string RefusalPrefix => $"Download refused: {Name} links must be HTTPS on a trusted host ({HostDescription}). Refused link: ";

    private static readonly string[] GitHubReleaseHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>GitHub release downloads: github.com redirects to one of the asset CDNs.</summary>
    public static readonly TrustedDownloadPolicy GitHubReleases = new("GitHub release", "GitHub", GitHubReleaseHosts);

    /// <summary>GitHub release assets and raw files (a manifest may point at either).</summary>
    public static readonly TrustedDownloadPolicy GitHub = new(
        "GitHub", "GitHub", [.. GitHubReleaseHosts, "raw.githubusercontent.com"], maxBytes: AddonMaxBytes);

    /// <summary>
    /// RenoDX add-ons. Derived from upstream RHI 2.8.5 data (docs/RenoDXdb.json, manifest.json snapshotOverrides,
    /// ModInstallService fallbacks) and the live RenoDX wiki/rhi-repo database: every add-on link is HTTPS on
    /// github.com (release downloads redirect to release-assets/objects.githubusercontent.com), on a mod
    /// author's GitHub Pages site (<c>&lt;author&gt;.github.io</c>; new authors appear in the live database) or on
    /// renodx.com, where clshortfuse.github.io redirects.
    /// </summary>
    public static readonly TrustedDownloadPolicy RenoDxAddons = new(
        "RenoDX add-on", "GitHub, GitHub Pages or renodx.com",
        [.. GitHubReleaseHosts, "raw.githubusercontent.com", "renodx.com"], ["github.io"], AddonMaxBytes);

    /// <summary>ReShade add-ons from crosire's Addons.ini and the upstream manifest: GitHub and GitHub Pages.</summary>
    public static readonly TrustedDownloadPolicy ReShadeAddons = new(
        "ReShade add-on", "GitHub or GitHub Pages",
        [.. GitHubReleaseHosts, "raw.githubusercontent.com"], ["github.io"], AddonMaxBytes);

    public bool IsTrusted(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Allows(uri);

    /// <summary>HTTPS on the default port, no user-info, a DNS host on the list (or one label under a listed parent).</summary>
    public bool Allows(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (AllowedHosts.Contains(host, StringComparer.Ordinal)) return true;
        foreach (var parent in SubdomainParents)
        {
            if (!host.EndsWith("." + parent, StringComparison.Ordinal)) continue;
            var label = host[..^(parent.Length + 1)];
            if (label.Length > 0 && !label.Contains('.')) return true;
        }
        return false;
    }

    /// <summary>Parses <paramref name="url"/>; throws <see cref="UntrustedDownloadException"/> unless it is trusted.</summary>
    public Uri Validate(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && Allows(uri)) return uri;
        throw new UntrustedDownloadException(url, this);
    }

    /// <summary>Checks the URI a response actually came from (after redirects); without redirect information, the request.</summary>
    public void ValidateFinal(HttpResponseMessage response, string requestedUrl)
    {
        var final = response.RequestMessage?.RequestUri;
        if (final is null) { Validate(requestedUrl); return; }
        if (!Allows(final)) throw new UntrustedDownloadException(final.AbsoluteUri, this);
    }
}

/// <summary>
/// A download link (requested or reached by a redirect) is not HTTPS on one of the policy's hosts. Nothing was
/// fetched; the message is the policy's fixed <see cref="TrustedDownloadPolicy.RefusalPrefix"/> followed by the link.
/// </summary>
public sealed class UntrustedDownloadException : Exception
{
    public UntrustedDownloadException(string? url, TrustedDownloadPolicy policy)
        : base(policy.RefusalPrefix + url)
    {
        Url = url;
        PolicyName = policy.Name;
    }

    public string? Url { get; }
    public string PolicyName { get; }
}

/// <summary>
/// The deployment layer's download path: HTTPS only, host allow-list checked on the requested URL
/// and on the final URL after redirects, size cap, optional published SHA-256, written through
/// <see cref="AtomicFile"/> and, for cache entries, reused only through <see cref="CacheStamp"/>.
/// </summary>
public static class TrustedDownload
{
    /// <summary>Fixed start of the announced-size refusal (PT-BR presentation pair key); the cap and size follow.</summary>
    public const string SizeLimitPrefix = "The download is larger than the size limit (";

    /// <summary>Parses <paramref name="url"/> and refuses it (<see cref="UntrustedDownloadException"/>) unless the policy allows it.</summary>
    public static Uri Validate(string url, TrustedDownloadPolicy policy) => policy.Validate(url);

    /// <summary>
    /// A HEAD request under the policy (requested and final URL checked). The caller disposes the response; a
    /// redirect off the list throws <see cref="UntrustedDownloadException"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> HeadAsync(HttpClient http, string url, TrustedDownloadPolicy policy, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, policy.Validate(url));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            policy.ValidateFinal(response, url);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into a verified temporary file beside <paramref name="targetPath"/>.
    /// The caller places it (<see cref="AtomicFile.MoveIntoPlace"/>) or disposes it to delete it.
    /// <paramref name="onResponse"/> sees the validated response (status, final URL, validators) before the body is read.
    /// </summary>
    public static async Task<TempDownload> DownloadToTempAsync(
        HttpClient http,
        string url,
        string targetPath,
        TrustedDownloadPolicy policy,
        string? expectedSha256 = null,
        ProductInfoHeaderValue? userAgent = null,
        CancellationToken cancellationToken = default,
        Action<HttpResponseMessage>? onResponse = null)
    {
        var uri = policy.Validate(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (userAgent is not null) request.Headers.UserAgent.Add(userAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        policy.ValidateFinal(response, url);
        onResponse?.Invoke(response);
        if (response.Content.Headers.ContentLength is long announced && announced > policy.MaxBytes)
            throw new InvalidDataException($"{SizeLimitPrefix}{policy.MaxBytes}{AtomicFile.SizeLimitMiddle}{announced} bytes");

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
