using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
        long maxBytes = DefaultMaxBytes,
        IReadOnlyCollection<string>? hostPatterns = null)
    {
        Name = name;
        HostDescription = hostDescription;
        AllowedHosts = allowedHosts.Select(host => host.Trim().TrimEnd('.').ToLowerInvariant()).ToArray();
        SubdomainParents = (singleLabelSubdomainsOf ?? []).Select(parent => parent.Trim().Trim('.').ToLowerInvariant()).ToArray();
        MaxBytes = maxBytes;
        HostPatterns = (hostPatterns ?? []).ToArray();
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
    /// Whole-host patterns (regular expressions, matched against the complete lower-case host) for
    /// storage hosts that are numbered rather than fixed, e.g. GitHub Actions' artifact storage
    /// <c>productionresultssa12.blob.core.windows.net</c>. Write them narrowly: a pattern that admits a
    /// whole public suffix (<c>*.blob.core.windows.net</c>) admits every customer of that service.
    /// </summary>
    public IReadOnlyCollection<string> HostPatterns { get; init; }

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
        foreach (var pattern in HostPatterns)
            if (Regex.IsMatch(host, $"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return true;
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

/// <summary>A payload in the cache (or a fresh temp) and what its integrity rests on.</summary>
/// <param name="Path">The verified file.</param>
/// <param name="Sha256">SHA-256 of the file (upper-case hex), recorded in its stamp.</param>
/// <param name="PublishedSha256">The publisher's digest it was checked against; null when none was published.</param>
public sealed record VerifiedDownload(string Path, string Sha256, string? PublishedSha256)
{
    /// <summary>Metadata value: the file matched a digest the publisher announced.</summary>
    public const string PublishedDigest = "published-digest";

    /// <summary>Metadata value: no digest was published; HTTPS + host allow-list only, the SHA-256 was recorded.</summary>
    public const string RecordedOnly = "recorded-only";

    public bool DigestVerified => !string.IsNullOrWhiteSpace(PublishedSha256);

    /// <summary><see cref="PublishedDigest"/> or <see cref="RecordedOnly"/> (deployment record metadata).</summary>
    public string Integrity => DigestVerified ? PublishedDigest : RecordedOnly;

    /// <summary>Record metadata key: SHA-256 of the downloaded payload (zip or file) the deployment came from.</summary>
    public const string MetadataSha256Key = "Download.Sha256";

    /// <summary>Record metadata key: <see cref="Integrity"/> — what the download's integrity rests on (#26).</summary>
    public const string MetadataIntegrityKey = "Download.Integrity";

    /// <summary>Writes <see cref="MetadataSha256Key"/> and <see cref="MetadataIntegrityKey"/> into a deployment record's metadata.</summary>
    /// <param name="part">
    /// Null for the payload the deployment is made from; a name (e.g. <c>dgVoodoo</c>) for each further download the
    /// same record deploys, written as <c>Download.&lt;part&gt;.Sha256</c> / <c>Download.&lt;part&gt;.Integrity</c>.
    /// </param>
    public void RecordIn(IDictionary<string, string> metadata, string? part = null)
        => Record(metadata, Sha256, DigestVerified, part);

    /// <summary>
    /// The one way a deployment record names the download it came from (#26): the SHA-256 of the verified cache entry
    /// (the downloaded file, or the file RHI extracted from it when only that is kept) and whether a digest the
    /// publisher announced vouched for it (<see cref="PublishedDigest"/>) or HTTPS + host policy only
    /// (<see cref="RecordedOnly"/>). See <see cref="RecordIn"/> for <paramref name="part"/>.
    /// </summary>
    public static void Record(IDictionary<string, string> metadata, string sha256, bool digestVerified, string? part = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata[Sha256KeyFor(part)] = sha256;
        metadata[IntegrityKeyFor(part)] = digestVerified ? PublishedDigest : RecordedOnly;
    }

    /// <summary>Removes the keys of <paramref name="part"/> (a deployment that no longer comes from that download).</summary>
    public static void Forget(IDictionary<string, string> metadata, string? part = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Remove(Sha256KeyFor(part));
        metadata.Remove(IntegrityKeyFor(part));
    }

    public static string Sha256KeyFor(string? part) => part is null ? MetadataSha256Key : $"Download.{part}.Sha256";

    public static string IntegrityKeyFor(string? part) => part is null ? MetadataIntegrityKey : $"Download.{part}.Integrity";
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

    /// <summary>Area name of the log line written when a payload is accepted without a published digest.</summary>
    public const string UnpublishedDigestLogArea = "download-integrity";

    /// <summary>Largest metadata body (release JSON, checksum lists) read by <see cref="GetStringAsync"/>.</summary>
    public const long MetadataMaxBytes = 16L * 1024 * 1024;

    private static readonly ProductInfoHeaderValue DefaultUserAgent = ProductInfoHeaderValue.Parse(RHI.Core.RhiProductInfo.UserAgent);

    /// <summary>Parses <paramref name="url"/> and refuses it (<see cref="UntrustedDownloadException"/>) unless the policy allows it.</summary>
    public static Uri Validate(string url, TrustedDownloadPolicy policy) => policy.Validate(url);

    /// <summary>
    /// A HEAD request under the policy (requested and final URL checked). The caller disposes the response; a
    /// redirect off the list throws <see cref="UntrustedDownloadException"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> HeadAsync(HttpClient http, string url, TrustedDownloadPolicy policy, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, policy.Validate(url));
        request.Headers.UserAgent.Add(DefaultUserAgent);
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
    /// A metadata GET (release JSON, checksum list, Addons.ini) under the policy: requested and final URL
    /// checked, success status required, body capped at the smaller of the policy's cap and
    /// <see cref="MetadataMaxBytes"/>, decoded as UTF-8 (a BOM is honoured).
    /// </summary>
    public static async Task<string> GetStringAsync(
        HttpClient http,
        string url,
        TrustedDownloadPolicy policy,
        string? accept = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(http, url, policy, accept, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="GetStringAsync"/> for a caller that validates and decodes the bytes itself (a small text file it
    /// caches and hashes as served): same policy checks and cap.
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(
        HttpClient http,
        string url,
        TrustedDownloadPolicy policy,
        string? accept = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, policy.Validate(url));
        request.Headers.UserAgent.Add(DefaultUserAgent);
        if (!string.IsNullOrWhiteSpace(accept)) request.Headers.Accept.ParseAdd(accept);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        policy.ValidateFinal(response, url);
        var cap = Math.Min(policy.MaxBytes, MetadataMaxBytes);
        if (response.Content.Headers.ContentLength is long announced && announced > cap)
            throw new InvalidDataException($"{SizeLimitPrefix}{cap}{AtomicFile.SizeLimitMiddle}{announced} bytes");

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
                throw new InvalidDataException($"{SizeLimitPrefix}{cap}{AtomicFile.SizeLimitMiddle}{url}");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
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
        Action<HttpResponseMessage>? onResponse = null,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        var uri = policy.Validate(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(userAgent ?? DefaultUserAgent);
        configureRequest?.Invoke(request);
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
        => (await EnsureVerifiedAsync(http, url, cacheEntry, policy, version, publishedSha256, userAgent: userAgent,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Sha256;

    /// <summary>
    /// The one cached-payload path (#26, #42): reuses <paramref name="cacheEntry"/> only through its
    /// <see cref="CacheStamp"/> (same URL, version and published digest, content still hashing to the
    /// recorded value); otherwise downloads under <paramref name="policy"/>, verifies the published
    /// digest when there is one (a mismatch is refused, nothing is placed), runs <paramref name="validate"/>
    /// on the temporary file (PE/archive checks; throw to refuse), places it atomically and stamps it.
    /// Without a published digest the payload is accepted on HTTPS + the host policy, as upstream RHI
    /// (which verifies no digest anywhere) does; its SHA-256 is recorded in the stamp and the result and the
    /// acceptance is logged once (#26). Sources that need more (Universal MFG's SHA256SUMS.txt) check it
    /// themselves in <paramref name="validate"/>.
    /// </summary>
    public static async Task<VerifiedDownload> EnsureVerifiedAsync(
        HttpClient http,
        string url,
        string cacheEntry,
        TrustedDownloadPolicy policy,
        string? version = null,
        string? publishedSha256 = null,
        Func<string, CancellationToken, Task>? validate = null,
        ProductInfoHeaderValue? userAgent = null,
        CancellationToken cancellationToken = default,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        Validate(url, policy);
        var published = NormalizeDigest(publishedSha256);
        if (CacheStamp.TryValidate(cacheEntry, url, version, published, out var cachedSha)
            && (published is null || FileHash.HexEquals(cachedSha, published)))
            return new VerifiedDownload(cacheEntry, cachedSha, published);

        CacheStamp.Invalidate(cacheEntry);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cacheEntry))!);
        using var download = await DownloadToTempAsync(http, url, cacheEntry, policy, published, userAgent, cancellationToken,
            configureRequest: configureRequest).ConfigureAwait(false);
        if (validate is not null) await validate(download.Path, cancellationToken).ConfigureAwait(false);
        download.MoveTo(cacheEntry);
        var sha = CacheStamp.Write(cacheEntry, url, version, published);
        if (published is null) NoteAcceptedWithoutDigest(policy, url, sha);
        return new VerifiedDownload(cacheEntry, sha, published);
    }

    /// <summary>
    /// Flags (rhi.log) a download accepted without a published digest, with the SHA-256 recorded for it (#26).
    /// <see cref="EnsureVerifiedAsync"/> does this itself; callers of <see cref="DownloadToTempAsync"/> call it.
    /// </summary>
    public static void NoteAcceptedWithoutDigest(TrustedDownloadPolicy policy, string url, string sha256)
        => AppLog.Warn(UnpublishedDigestLogArea,
            $"{policy.Name}: the publisher announced no SHA-256 digest; accepted on HTTPS + host allow-list, recorded SHA-256 {sha256} for {url}");

    /// <summary>A published digest as bare hex (<c>sha256:</c> prefix and whitespace removed), or null when blank.</summary>
    public static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value["sha256:".Length..].Trim();
        return value.Length == 0 ? null : value;
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
