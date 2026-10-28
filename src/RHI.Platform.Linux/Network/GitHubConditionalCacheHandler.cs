using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Release-lookup cache for GitHub's REST API (DEP-16 / #35, #244), the behaviour of upstream RHI's
/// <c>GitHubETagCache</c> (2.8.5; 2.8.8 <c>feb126c</c>, <c>1b51cb2</c>) moved under the app's one
/// <see cref="HttpClient"/> so every service's release lookup gets it. For a GET to api.github.com it remembers the body
/// and validators (ETag, Last-Modified) of each successful answer and revalidates with
/// <c>If-None-Match</c>/<c>If-Modified-Since</c>; GitHub answers an unchanged resource with 304, which does not count
/// against the rate limit, and the cached body is handed back as a 200.
/// <list type="bullet">
/// <item>#244: with a <see cref="GitHubApiCacheStore"/> the answers are saved between launches (writes batched, flushed on
/// <see cref="Flush"/>/dispose). A saved answer is only ever used once GitHub confirmed it this session (304), never as
/// current without asking.</item>
/// <item>#244: overlapping identical GETs share one request (each caller gets its own response; a caller's Cancel ends
/// only its own wait), and an answer GitHub confirmed this session is reused for <see cref="FreshFor"/> without asking.</item>
/// <item>#244: every api.github.com request gives up after <see cref="DefaultApiTimeout"/> with
/// <see cref="DownloadTimedOutException"/> (a network failure: could not check, never a Cancel and never "current"). The
/// app's <see cref="HttpClient.Timeout"/> stays for payloads.</item>
/// </list>
/// When GitHub refuses (403/429 with no remaining quota, or a 5xx) and GitHub confirmed the cached body this session, that
/// body is returned and the fact is logged; a body saved by an earlier launch is returned only inside the rate-limit window
/// GitHub advertised. Until the advertised reset time requests whose answer is cached are not sent at all. Payload
/// downloads (release assets, archives) are never cached or timed here: they go through the stamped download cache.
/// Requests that carry their own Authorization header are sent as they are (never cached, shared or saved).
/// </summary>
public sealed class GitHubConditionalCacheHandler : DelegatingHandler
{
    /// <summary>Bodies larger than this are not kept (release lists are a few hundred KB).</summary>
    internal const int MaxCachedBodyBytes = 8 * 1024 * 1024;

    /// <summary>Most entries kept; the oldest is dropped beyond it.</summary>
    internal const int MaxEntries = 512;

    /// <summary>#244 (upstream <c>ApiTimeout</c>): an api.github.com request gives up after this.</summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>#244 (upstream <c>FreshFor</c>): an answer GitHub confirmed this long ago is used without asking again.</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);

    /// <summary>#244 (upstream <c>Persist</c>): saves wait this long after the last change, so a start's burst is one write.</summary>
    internal static readonly TimeSpan DefaultPersistDelay = TimeSpan.FromSeconds(2);

    private static readonly AsyncLocal<TimeSpan?> ApiTimeoutOverride = new();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private readonly GitHubApiCacheStore? _store;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeSpan _persistDelay;
    private readonly object _timerGate = new();
    private readonly object _writeGate = new();
    private readonly Timer? _persistTimer;
    private long _rateLimitedUntilTicks;
    private int _dirty;
    private bool _disposed;

    /// <param name="innerHandler">The next handler (the app: the token handler).</param>
    /// <param name="now">Test seam: the clock.</param>
    /// <param name="store">#244: where answers are saved between launches; null = this session only.</param>
    /// <param name="apiTimeout">Test seam (default <see cref="DefaultApiTimeout"/>).</param>
    /// <param name="persistDelay">Test seam (default <see cref="DefaultPersistDelay"/>).</param>
    public GitHubConditionalCacheHandler(HttpMessageHandler innerHandler, Func<DateTimeOffset>? now = null, GitHubApiCacheStore? store = null,
        TimeSpan? apiTimeout = null, TimeSpan? persistDelay = null)
        : base(innerHandler)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _store = store;
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        _persistDelay = persistDelay ?? DefaultPersistDelay;
        if (_store is null) return;
        LoadSaved(_store);
        _persistTimer = new Timer(_ => WriteIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Requests answered from the cache (304 revalidation, fresh reuse or rate-limit fallback), for tests and diagnostics.</summary>
    public int CacheHits => Volatile.Read(ref _cacheHits);
    private int _cacheHits;

    /// <summary>Test seam: the API timeout for the calling async flow only (as <c>TrustedDownload.IdleLimitForTesting</c>).</summary>
    internal static TimeSpan? ApiTimeoutForTesting
    {
        get => ApiTimeoutOverride.Value;
        set => ApiTimeoutOverride.Value = value;
    }

    private TimeSpan ApiTimeout => ApiTimeoutOverride.Value ?? _apiTimeout;

    /// <summary>Test seam: a caller joined a request already in flight (a signal to wait on, never a timing).</summary>
    internal Action<Uri>? JoinedForTesting { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || !IsApiHost(uri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsCacheable(request)) return await SendTimedAsync(request, cancellationToken).ConfigureAwait(false);

        var key = KeyFor(request);
        if (_entries.TryGetValue(key, out var cached))
        {
            if (IsFresh(cached)) return Hit(cached, request, null);
            if (IsRateLimited())
                return Hit(cached, request, "GitHub API rate limit reached; using the answer cached earlier");
        }
        return await SendSharedAsync(key, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes pending changes now (the app calls it on exit; dispose does too).</summary>
    public void Flush()
    {
        lock (_timerGate)
        {
            if (_disposed) return;
            _persistTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        WriteIfDirty();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
            lock (_timerGate)
            {
                _disposed = true;
                _persistTimer?.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    // ── Shared requests ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Joins the request already in flight for <paramref name="key"/>, or starts it. The request runs on its own token
    /// (the API timeout), so one caller's Cancel never cancels another caller's answer; it is abandoned only once every
    /// caller waiting for it has cancelled.
    /// </summary>
    private async Task<HttpResponseMessage> SendSharedAsync(string key, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Flight flight;
        var start = false;
        lock (_flights)
        {
            if (!_flights.TryGetValue(key, out flight!))
            {
                flight = new Flight();
                _flights[key] = flight;
                start = true;
            }
            flight.Waiters++;
        }
        if (start) _ = RunFlightAsync(key, flight, CloneForFlight(request));
        else JoinedForTesting?.Invoke(request.RequestUri!);

        Outcome outcome;
        try
        {
            outcome = await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Leave(key, flight);
            throw;
        }

        if (outcome.Answer is { } answer)
        {
            if (answer.FromCache) Interlocked.Increment(ref _cacheHits);
            return answer.ToResponse(request);
        }
        // An answer too big to buffer reaches one caller whole; the others ask on their own.
        if (outcome.TryClaim() is { } whole)
        {
            if (whole.RequestMessage?.RequestUri is { } final) request.RequestUri = final;
            whole.RequestMessage = request;
            return whole;
        }
        return await SendTimedAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void Leave(string key, Flight flight)
    {
        bool abandon;
        lock (_flights)
        {
            if (--flight.Waiters > 0) return;
            abandon = !flight.Completion.Task.IsCompleted;
            if (abandon && _flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
        }
        // The last caller left: stop the request, or let go of an unbuffered answer nobody will read.
        if (abandon) flight.Abort.Cancel();
        else if (flight.Completion.Task.IsCompletedSuccessfully) flight.Completion.Task.Result.TryClaim()?.Dispose();
    }

    private async Task RunFlightAsync(string key, Flight flight, HttpRequestMessage outgoing)
    {
        Outcome? outcome = null;
        Exception? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(ApiTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Abort.Token, timeout.Token);
            try
            {
                outcome = await FetchAsync(key, outgoing, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !flight.Abort.IsCancellationRequested)
            {
                throw TimedOut(outgoing, ex);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            outgoing.Dispose();
            lock (_flights)
            {
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
            }
        }

        if (failure is not null) flight.Completion.TrySetException(failure);
        else flight.Completion.TrySetResult(outcome!);
        lock (_flights)
        {
            if (flight.Waiters > 0) return;
        }
        // Every caller left before the end: nobody reads the failure, or an answer that was not buffered.
        _ = flight.Completion.Task.Exception;
        outcome?.TryClaim()?.Dispose();
    }

    /// <summary>The request itself: conditional when an answer is cached; the answer buffered so every caller gets a copy.</summary>
    private async Task<Outcome> FetchAsync(string key, HttpRequestMessage outgoing, CancellationToken token)
    {
        _entries.TryGetValue(key, out var cached);
        if (cached is not null)
        {
            if (cached.ETag is not null) outgoing.Headers.IfNoneMatch.Add(cached.ETag);
            else if (cached.LastModified is not null) outgoing.Headers.IfModifiedSince = cached.LastModified;
        }

        var response = await base.SendAsync(outgoing, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            response.Dispose();
            var confirmed = cached with { StoredAt = _now(), Checked = _now(), FromDisk = false };
            _entries[key] = confirmed;
            SchedulePersist();
            if (cached.FromDisk) AppLog.Warn("github-cache", $"GitHub API answer saved earlier is unchanged (304): {outgoing.RequestUri}");
            return new Outcome(Answer.From(confirmed));
        }

        if (!response.IsSuccessStatusCode)
        {
            NoteRateLimit(response);
            // An answer GitHub confirmed this session stands in for a refusal (#35). A saved one (an earlier launch, up to
            // 30 days old) only while GitHub's advertised rate-limit window runs: a 5xx or a 403 that is no rate limit at
            // the next start reads as could not check, never as that answer.
            if (cached is not null && IsRefusal(response.StatusCode) && (!cached.FromDisk || IsRateLimited()))
            {
                AppLog.Warn("github-cache", $"GitHub API answered {(int)response.StatusCode}; using the answer cached earlier for {outgoing.RequestUri}");
                response.Dispose();
                return new Outcome(Answer.From(cached));
            }
        }

        // #233: this read happens inside SendAsync, before any caller can wrap the body, so it runs under the idle limit
        // itself (per gap) and under the API timeout. At most MaxCachedBodyBytes + 1 bytes are held.
        var body = IdleTimeoutStream.Wrap(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), DownloadSources.GitHubApi.Name);
        byte[] read;
        try
        {
            read = await ReadUpToAsync(body, MaxCachedBodyBytes + 1, token).ConfigureAwait(false);
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            throw;
        }

        if (read.Length > MaxCachedBodyBytes)
        {
            // Too big to keep or share: one caller reads what was read so far, then the rest (still under the idle limit).
            var contentType = response.Content.Headers.ContentType;
            var lastModified = response.Content.Headers.LastModified;
            var whole = new StreamContent(new PrefixedStream(read, body));
            if (contentType is not null) whole.Headers.ContentType = contentType;
            whole.Headers.LastModified = lastModified;
            response.Content = whole;
            return new Outcome(response);
        }

        await body.DisposeAsync().ConfigureAwait(false);
        using (response)
        {
            var answer = Answer.From(response, read);
            if (response.StatusCode == HttpStatusCode.OK && IsApiHost(response.RequestMessage?.RequestUri ?? outgoing.RequestUri!))
            {
                var etag = response.Headers.ETag;
                var lastModified = response.Content.Headers.LastModified;
                if (etag is not null || lastModified is not null)
                    Store(key, new Entry(read, response.Content.Headers.ContentType, etag, lastModified, _now(), _now(), FromDisk: false));
            }
            return new Outcome(answer);
        }
    }

    /// <summary>An api.github.com request that is not cached (another method, conditional, own Authorization), under the API timeout.</summary>
    private async Task<HttpResponseMessage> SendTimedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ApiTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(request, ex);
        }
    }

    private DownloadTimedOutException TimedOut(HttpRequestMessage request, Exception inner)
    {
        AppLog.Warn("github-cache", $"GitHub API did not answer within {ApiTimeout.TotalSeconds:0.#} s: {request.RequestUri}");
        return new DownloadTimedOutException(DownloadSources.GitHubApi.Name, ApiTimeout, inner);
    }

    private static HttpRequestMessage CloneForFlight(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        return clone;
    }

    private sealed class Flight
    {
        public readonly TaskCompletionSource<Outcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Abort = new();
        public int Waiters; // under the _flights lock
    }

    /// <summary>A buffered answer every caller gets a copy of, or one response too big to buffer.</summary>
    private sealed class Outcome
    {
        private HttpResponseMessage? _whole;

        public Outcome(Answer answer) => Answer = answer;
        public Outcome(HttpResponseMessage whole) => _whole = whole;

        public Answer? Answer { get; }

        public HttpResponseMessage? TryClaim() => Interlocked.Exchange(ref _whole, null);
    }

    private sealed record Answer(
        HttpStatusCode Status, string? Reason, Version Version, KeyValuePair<string, string[]>[] Headers,
        KeyValuePair<string, string[]>[] ContentHeaders, byte[] Body, Uri? FinalUri, bool FromCache)
    {
        public static Answer From(HttpResponseMessage response, byte[] body) => new(
            response.StatusCode, response.ReasonPhrase, response.Version,
            response.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(),
            response.Content.Headers.Where(header => !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(),
            body, response.RequestMessage?.RequestUri, FromCache: false);

        public static Answer From(Entry entry)
        {
            var headers = entry.ETag is null ? [] : new[] { new KeyValuePair<string, string[]>("ETag", [entry.ETag.ToString()]) };
            var content = new List<KeyValuePair<string, string[]>>();
            if (entry.ContentType is not null) content.Add(new("Content-Type", [entry.ContentType.ToString()]));
            if (entry.LastModified is { } modified) content.Add(new("Last-Modified", [modified.ToString("R")]));
            return new(HttpStatusCode.OK, null, HttpVersion.Version11, headers, [.. content], entry.Body, FinalUri: null, FromCache: true);
        }

        /// <summary>A response of the caller's own (its own content object over the shared bytes).</summary>
        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            // As the inner handler does on a redirect: the request names where the answer came from.
            if (FinalUri is not null) request.RequestUri = FinalUri;
            var content = new ByteArrayContent(Body);
            foreach (var (name, values) in ContentHeaders) content.Headers.TryAddWithoutValidation(name, values);
            var response = new HttpResponseMessage(Status) { Content = content, RequestMessage = request, Version = Version };
            if (Reason is not null) response.ReasonPhrase = Reason;
            foreach (var (name, values) in Headers) response.Headers.TryAddWithoutValidation(name, values);
            return response;
        }
    }

    // ── Entries ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Confirmed by GitHub this session, less than <see cref="FreshFor"/> ago (a saved answer never is).</summary>
    private bool IsFresh(Entry entry)
        => entry.Checked is { } checkedAt && _now() - checkedAt < FreshFor && _now() >= checkedAt;

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
        return Answer.From(cached).ToResponse(request);
    }

    private void Store(string key, Entry entry)
    {
        _entries[key] = entry;
        if (_entries.Count > MaxEntries)
        {
            var oldest = _entries.OrderBy(pair => pair.Value.StoredAt).First().Key;
            _entries.TryRemove(oldest, out _);
        }
        SchedulePersist();
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

    /// <summary>Inside the rate-limit window GitHub advertised (403/429 with no quota left, until its reset time).</summary>
    private bool IsRateLimited() => _now().UtcTicks < Interlocked.Read(ref _rateLimitedUntilTicks);

    private static bool IsRefusal(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>HTTPS to api.github.com on the default port: the only host this handler caches or times.</summary>
    internal static bool IsApiHost(Uri uri)
        => uri is { IsAbsoluteUri: true, Scheme: "https" }
           && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort;

    private static bool IsCacheable(HttpRequestMessage request)
        => request.Method == HttpMethod.Get
           && request.Content is null
           && request.Headers.Authorization is null
           && request.Headers.IfNoneMatch.Count == 0
           && request.Headers.IfModifiedSince is null;

    /// <summary>URL plus the Accept header: GitHub varies the body by media type. Never a credential.</summary>
    private static string KeyFor(HttpRequestMessage request)
        => KeyFor(request.RequestUri!.AbsoluteUri, string.Join(",", request.Headers.Accept.Select(value => value.ToString())));

    private static string KeyFor(string url, string accept) => url + "\n" + accept;

    private sealed record Entry(byte[] Body, MediaTypeHeaderValue? ContentType, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified,
        DateTimeOffset StoredAt, DateTimeOffset? Checked, bool FromDisk);

    // ── Saved between launches (#244) ───────────────────────────────────────────────────────────────────────────────

    private void LoadSaved(GitHubApiCacheStore store)
    {
        var saved = store.Load(_now());
        foreach (var item in saved)
        {
            var uri = new Uri(item.Url);
            var entry = new Entry(Encoding.UTF8.GetBytes(item.Body!),
                item.ContentType is null ? null : MediaTypeHeaderValue.Parse(item.ContentType),
                item.ETag is null ? null : EntityTagHeaderValue.Parse(item.ETag),
                item.LastModified, item.SavedUtc, Checked: null, FromDisk: true);
            _entries.TryAdd(KeyFor(uri.AbsoluteUri, item.Accept!), entry); // newest first: a duplicate's older copy is not taken
        }
        if (saved.Count > 0) AppLog.Warn("github-cache", $"{saved.Count} GitHub API answers saved earlier were loaded; each is asked about again before use");
    }

    private void SchedulePersist()
    {
        if (_store is null) return;
        Interlocked.Exchange(ref _dirty, 1);
        lock (_timerGate) // never the write's lock: an answer is never held up by a save in progress
        {
            if (!_disposed) _persistTimer!.Change(_persistDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteIfDirty()
    {
        if (_store is null) return;
        lock (_writeGate)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                _store.Save(Snapshot());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Runs on a timer thread, where an escaping exception ends the process. A cache: the session keeps its
                // answers; the next change tries again.
                AppLog.Warn("github-cache", $"Could not save the GitHub API answers to {_store.FilePath}", ex);
            }
        }
    }

    private List<GitHubApiCacheEntry> Snapshot()
    {
        var saved = new List<GitHubApiCacheEntry>();
        foreach (var (key, entry) in _entries.ToArray().OrderByDescending(pair => pair.Value.StoredAt)) // at most MaxEntries (Store)
        {
            string body;
            try { body = StrictUtf8.GetString(entry.Body); }
            catch (DecoderFallbackException) { continue; } // not text: kept for this session only
            var split = key.IndexOf('\n', StringComparison.Ordinal);
            saved.Add(new GitHubApiCacheEntry
            {
                Url = key[..split],
                Accept = key[(split + 1)..],
                ETag = entry.ETag?.ToString(),
                LastModified = entry.LastModified,
                ContentType = entry.ContentType?.ToString(),
                SavedUtc = entry.StoredAt,
                Body = body,
            });
        }
        return saved;
    }
}
