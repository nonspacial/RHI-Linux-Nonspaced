using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Release-lookup cache for GitHub's REST API (DEP-16 / #35), the behaviour of upstream RHI 2.8.5's
/// <c>GitHubETagCache</c> moved under the app's one <see cref="HttpClient"/> so every service's release
/// lookup gets it: for a GET to api.github.com it remembers the body and validators (ETag,
/// Last-Modified) of each successful answer for the session and revalidates with
/// <c>If-None-Match</c>/<c>If-Modified-Since</c>; GitHub answers an unchanged resource with 304, which
/// does not count against the rate limit, and the cached body is handed back as a 200. Like upstream it
/// keeps nothing on disk. When GitHub refuses (403/429 with no remaining quota, or a 5xx) and a body is
/// cached, the cached body is returned and the fact is logged; until the advertised reset time requests
/// whose answer is cached are not sent at all. Payload downloads (release assets, archives) are never
/// cached here: they go through the stamped download cache.
/// </summary>
public sealed class GitHubConditionalCacheHandler : DelegatingHandler
{
    /// <summary>Bodies larger than this are not kept (release lists are a few hundred KB).</summary>
    internal const int MaxCachedBodyBytes = 8 * 1024 * 1024;

    /// <summary>Most entries kept; the oldest is dropped beyond it.</summary>
    internal const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private long _rateLimitedUntilTicks;

    public GitHubConditionalCacheHandler(HttpMessageHandler innerHandler, Func<DateTimeOffset>? now = null)
        : base(innerHandler)
        => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Requests answered from the cache (304 revalidation or rate-limit fallback), for tests and diagnostics.</summary>
    public int CacheHits => Volatile.Read(ref _cacheHits);
    private int _cacheHits;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsCacheable(request)) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var key = KeyFor(request);
        _entries.TryGetValue(key, out var cached);
        if (cached is not null && _now().UtcTicks < Interlocked.Read(ref _rateLimitedUntilTicks))
            return Hit(cached, request, "GitHub API rate limit reached; using the answer cached earlier this session");

        if (cached is not null)
        {
            if (cached.ETag is not null) request.Headers.IfNoneMatch.Add(cached.ETag);
            else if (cached.LastModified is not null) request.Headers.IfModifiedSince = cached.LastModified;
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            response.Dispose();
            return Hit(cached, request, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            NoteRateLimit(response);
            if (cached is not null && IsRefusal(response.StatusCode))
            {
                AppLog.Warn("github-cache", $"GitHub API answered {(int)response.StatusCode}; using the answer cached earlier this session for {request.RequestUri}");
                response.Dispose();
                return Hit(cached, request, null);
            }
            return response;
        }

        if (response.StatusCode != HttpStatusCode.OK || response.Content is null) return response;
        var etag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified;
        if (etag is null && lastModified is null) return response;
        if (response.Content.Headers.ContentLength is long announced && announced > MaxCachedBodyBytes) return response;

        // #233: this read happens inside SendAsync, before any caller can wrap the body, so it runs under the idle limit
        // itself (per gap, never a total deadline): a GitHub answer that stops sending data ends with
        // DownloadStalledException, not after the whole HttpClient.Timeout. At most MaxCachedBodyBytes + 1 bytes are held.
        var contentType = response.Content.Headers.ContentType;
        var body = IdleTimeoutStream.Wrap(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), DownloadSources.GitHubApi.Name);
        byte[] read;
        try
        {
            read = await ReadUpToAsync(body, MaxCachedBodyBytes + 1, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            throw;
        }

        HttpContent replacement;
        if (read.Length <= MaxCachedBodyBytes)
        {
            await body.DisposeAsync().ConfigureAwait(false);
            Store(key, new Entry(read, contentType, etag, lastModified, _now()));
            // The body was read; hand the caller an equivalent buffered response.
            replacement = new ByteArrayContent(read);
        }
        else
        {
            // Too big to keep: the caller reads what was read so far, then the rest (still under the idle limit).
            replacement = new StreamContent(new PrefixedStream(read, body));
        }
        if (contentType is not null) replacement.Headers.ContentType = contentType;
        replacement.Headers.LastModified = lastModified;
        response.Content = replacement;
        return response;
    }

    private static async Task<byte[]> ReadUpToAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(limit, 81920)];
        using var collected = new MemoryStream();
        while (collected.Length < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - collected.Length);
            var count = await body.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            collected.Write(buffer, 0, count);
        }
        return collected.ToArray();
    }

    /// <summary>Bytes already read, then the rest of the body.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            return await rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) rest.Dispose();
            base.Dispose(disposing);
        }
    }

    private HttpResponseMessage Hit(Entry cached, HttpRequestMessage request, string? logLine)
    {
        Interlocked.Increment(ref _cacheHits);
        if (logLine is not null) AppLog.Warn("github-cache", $"{logLine}: {request.RequestUri}");
        var content = new ByteArrayContent(cached.Body);
        if (cached.ContentType is not null) content.Headers.ContentType = cached.ContentType;
        content.Headers.LastModified = cached.LastModified;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        if (cached.ETag is not null) response.Headers.ETag = cached.ETag;
        return response;
    }

    private void Store(string key, Entry entry)
    {
        _entries[key] = entry;
        if (_entries.Count <= MaxEntries) return;
        var oldest = _entries.OrderBy(pair => pair.Value.StoredAt).First().Key;
        _entries.TryRemove(oldest, out _);
    }

    private void NoteRateLimit(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return;
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) || remaining.FirstOrDefault() != "0") return;
        var until = _now().AddMinutes(1);
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var epoch))
            until = DateTimeOffset.FromUnixTimeSeconds(epoch);
        Interlocked.Exchange(ref _rateLimitedUntilTicks, until.UtcTicks);
    }

    private static bool IsRefusal(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsCacheable(HttpRequestMessage request)
        => request.Method == HttpMethod.Get
           && request.Content is null
           && request.RequestUri is { Scheme: "https" } uri
           && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort
           && request.Headers.IfNoneMatch.Count == 0
           && request.Headers.IfModifiedSince is null;

    /// <summary>URL plus the Accept header: GitHub varies the body by media type.</summary>
    private static string KeyFor(HttpRequestMessage request)
        => request.RequestUri!.AbsoluteUri + "\n" + string.Join(",", request.Headers.Accept.Select(value => value.ToString()));

    private sealed record Entry(byte[] Body, MediaTypeHeaderValue? ContentType, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified, DateTimeOffset StoredAt);
}
