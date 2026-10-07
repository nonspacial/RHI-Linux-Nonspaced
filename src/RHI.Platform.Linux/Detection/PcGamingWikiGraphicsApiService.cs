using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Cache-backed PCGamingWiki API-table fallback used only when the local runtime probe
/// cannot identify a graphics API with stronger evidence. Hits are cached for good; definite misses
/// (no page, no Windows renderer row) are cached for <see cref="DefaultMissTtl"/>, so an unknown game
/// does not cost two requests on every selection. The cache lives in RHI-Linux's cache folder, which
/// Settings → Purge cache clears (Refs #54).
/// </summary>
public sealed partial class PcGamingWikiGraphicsApiService
{
    /// <summary>Pause applied after a timeout/network error, or a 429 without a usable Retry-After.</summary>
    internal static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinPause = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(15);

    /// <summary>PCGW documents 60 requests/minute; one request per second stays under it.</summary>
    private static readonly TimeSpan DefaultMinRequestGap = TimeSpan.FromSeconds(1);

    /// <summary>How long a definite miss (no page / no Windows renderer) is trusted before asking again.</summary>
    public static readonly TimeSpan DefaultMissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly TimeProvider _time;
    private readonly TimeSpan _minRequestGap;
    private readonly TimeSpan _missTtl;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private CacheFile? _cache;
    private long _pausedUntilUtcTicks;

    public PcGamingWikiGraphicsApiService(
        HttpClient http,
        string? cachePath = null,
        TimeProvider? timeProvider = null,
        TimeSpan? minRequestGap = null,
        TimeSpan? missTtl = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "pcgw_graphics_api_cache.json");
        _time = timeProvider ?? TimeProvider.System;
        _minRequestGap = minRequestGap ?? DefaultMinRequestGap;
        _missTtl = missTtl ?? DefaultMissTtl;
    }

    internal string CachePath => _cachePath;

    /// <summary>True while PCGW requests are paused after a rate limit or failure; expires on its own.</summary>
    public bool IsPaused => _time.GetUtcNow().UtcTicks < Volatile.Read(ref _pausedUntilUtcTicks);

    public async Task<GraphicsApiDetection> ResolveAndFetchAsync(
        string gameName,
        int? steamAppId,
        string? pageUrlOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(pageUrlOverride))
            return await FetchAsync(gameName, pageUrlOverride, cancellationToken).ConfigureAwait(false);

        if (steamAppId is int appId && appId > 0)
        {
            var appIdUrl = $"https://www.pcgamingwiki.com/api/appid.php?appid={appId}";
            return await FetchAsync(gameName, appIdUrl, cancellationToken).ConfigureAwait(false);
        }

        var searchKey = "search:" + gameName.Trim().ToLowerInvariant();
        if (await IsFreshMissAsync(searchKey, cancellationToken).ConfigureAwait(false))
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki has no page for {gameName} (cached)" };

        var (pageUrl, outcome) = await ResolveByOpenSearchAsync(gameName, cancellationToken).ConfigureAwait(false);
        if (outcome == FetchOutcome.Missing)
            await StoreMissAsync(searchKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(pageUrl)
            ? GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki page not resolved for {gameName}" }
            : await FetchAsync(gameName, pageUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphicsApiDetection> FetchAsync(
        string gameName,
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidatePageUrl(pageUrl, out var uri))
            return GraphicsApiDetection.Unknown with { Evidence = "PCGamingWiki URL rejected by safety policy" };

        var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        string[]? cachedNames;
        lock (cache) cache.Hits.TryGetValue(uri.AbsoluteUri, out cachedNames);
        if (cachedNames is not null)
            return FromNames(cachedNames, $"PCGamingWiki API table: {string.Join(", ", cachedNames)}");
        if (await IsFreshMissAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false))
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki lists no Windows renderer for {gameName} (cached)" };

        var (html, outcome) = await GetTextAsync(uri, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (outcome == FetchOutcome.Missing)
        {
            await StoreMissAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki has no page for {gameName}" };
        }
        if (html is null)
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki unavailable for {gameName}; not cached, retried later" };

        var apis = ParseWindowsApis(html);
        if (apis.Count == 0)
        {
            await StoreMissAsync(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            return GraphicsApiDetection.Unknown with { Evidence = $"PCGamingWiki API table contained no Windows renderer for {gameName}" };
        }

        var names = apis.Select(static api => api.ToString()).ToArray();
        await StoreAsync(uri.AbsoluteUri, names, cancellationToken).ConfigureAwait(false);
        return FromApis(apis, $"PCGamingWiki API table: {string.Join(", ", apis)}");
    }

    internal static IReadOnlyList<GraphicsApi> ParseWindowsApis(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var tableMatch = ApiTableRegex().Match(html);
        if (!tableMatch.Success)
            return [];

        var found = new HashSet<GraphicsApi>();
        foreach (Match row in TableRowRegex().Matches(tableMatch.Groups[1].Value))
        {
            var rowHtml = row.Groups[1].Value;
            var abbr = AbbrRegex().Match(rowHtml);
            if (abbr.Success)
            {
                var platform = WebUtility.HtmlDecode(StripTags(abbr.Groups[1].Value));
                if (!platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var text = WebUtility.HtmlDecode(StripTags(rowHtml));
            AddApis(text, found);
        }

        return found
            .OrderByDescending(static api => Priority(api))
            .ThenBy(static api => api.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<(string? Url, FetchOutcome Outcome)> ResolveByOpenSearchAsync(string gameName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return (null, FetchOutcome.Missing);

        var url = new Uri($"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(gameName)}&limit=5&format=json");
        var (json, outcome) = await GetTextAsync(url, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (json is null)
            return (null, outcome);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 4)
                return (null, FetchOutcome.Missing);

            var urls = doc.RootElement[3];
            if (urls.ValueKind != JsonValueKind.Array)
                return (null, FetchOutcome.Missing);

            foreach (var candidate in urls.EnumerateArray())
            {
                var value = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
                if (value is not null && TryValidatePageUrl(value, out var safe))
                    return (safe.AbsoluteUri, FetchOutcome.Ok);
            }
            return (null, FetchOutcome.Missing);
        }
        catch (JsonException ex)
        {
            // A changed/garbled search response is not a definite miss: log it, do not cache it.
            AppLog.Warn("pcgw", $"PCGamingWiki search response unreadable for {gameName}", ex);
            return (null, FetchOutcome.Unavailable);
        }
    }

    private enum FetchOutcome { Ok, Missing, Unavailable }

    /// <summary>
    /// Sends one PCGW request through the shared gate (serialised, minimum gap between requests).
    /// Returns null when paused, on a non-success status, or on a timeout/network failure. 429 and
    /// 503 pause all PCGW traffic for the server's Retry-After (clamped); timeouts and network
    /// errors pause for <see cref="DefaultPause"/>. The pause expires, so the session recovers.
    /// </summary>
    private async Task<(string? Text, FetchOutcome Outcome)> GetTextAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsPaused)
            return (null, FetchOutcome.Unavailable);

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sent = false;
        try
        {
            // Another caller may have tripped the pause while this one queued.
            if (IsPaused)
                return (null, FetchOutcome.Unavailable);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(RhiProductInfo.UserAgent);
            sent = true;
            try
            {
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    linked.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    Pause(ParseRetryAfter(response, _time.GetUtcNow()) ?? DefaultPause);
                    return (null, FetchOutcome.Unavailable);
                }
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    return (null, FetchOutcome.Missing);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn("pcgw", $"PCGamingWiki answered {(int)response.StatusCode} for {uri.AbsolutePath}");
                    return (null, FetchOutcome.Unavailable);
                }
                return (await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false), FetchOutcome.Ok);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn("pcgw", $"PCGamingWiki timed out for {uri.AbsolutePath}; pausing requests", ex);
                Pause(DefaultPause);
                return (null, FetchOutcome.Unavailable);
            }
            catch (HttpRequestException ex)
            {
                AppLog.Warn("pcgw", $"PCGamingWiki unreachable for {uri.AbsolutePath}; pausing requests", ex);
                Pause(DefaultPause);
                return (null, FetchOutcome.Unavailable);
            }
        }
        finally
        {
            if (sent && _minRequestGap > TimeSpan.Zero)
                await Task.Delay(_minRequestGap, _time, CancellationToken.None).ConfigureAwait(false);
            _requestGate.Release();
        }
    }

    private void Pause(TimeSpan requested)
    {
        var until = _time.GetUtcNow() + ClampPause(requested);
        Volatile.Write(ref _pausedUntilUtcTicks, until.UtcTicks);
    }

    /// <summary>Bounds a pause so a bogus Retry-After neither disables PCGW for hours nor hot-loops.</summary>
    internal static TimeSpan ClampPause(TimeSpan requested)
        => requested < MinPause ? MinPause : requested > MaxPause ? MaxPause : requested;

    /// <summary>Reads Retry-After (delta-seconds or HTTP-date); null when absent.</summary>
    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
            return delta;
        if (header?.Date is DateTimeOffset date)
            return date > now ? date - now : TimeSpan.Zero;
        return null;
    }

    /// <summary>On-disk cache: page URL → Windows renderer names, and lookup key → time of a definite miss.</summary>
    private sealed class CacheFile
    {
        public Dictionary<string, string[]> Hits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTimeOffset> Misses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<CacheFile> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
            return _cache;

        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
                return _cache;

            var loaded = new CacheFile();
            if (File.Exists(_cachePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
                    var parsed = JsonSerializer.Deserialize<CacheFile>(json);
                    if (parsed is not null)
                    {
                        loaded.Hits = new Dictionary<string, string[]>(parsed.Hits ?? new(), StringComparer.OrdinalIgnoreCase);
                        loaded.Misses = new Dictionary<string, DateTimeOffset>(parsed.Misses ?? new(), StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A cache only: start empty and say why; the next store rewrites it.
                    AppLog.Warn("pcgw", $"PCGamingWiki cache unreadable, starting empty: {_cachePath}", ex);
                }
            }
            return _cache = loaded;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<bool> IsFreshMissAsync(string key, CancellationToken cancellationToken)
    {
        var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        lock (cache)
            return cache.Misses.TryGetValue(key, out var at) && _time.GetUtcNow() - at < _missTtl;
    }

    private Task StoreMissAsync(string key, CancellationToken cancellationToken)
        => UpdateCacheAsync(cache => cache.Misses[key] = _time.GetUtcNow(), cancellationToken);

    private Task StoreAsync(string pageUrl, string[] names, CancellationToken cancellationToken)
        => UpdateCacheAsync(cache =>
        {
            cache.Hits[pageUrl] = names;
            cache.Misses.Remove(pageUrl);
        }, cancellationToken);

    private async Task UpdateCacheAsync(Action<CacheFile> update, CancellationToken cancellationToken)
    {
        var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temp = null;
        try
        {
            string json;
            lock (cache)
            {
                update(cache);
                json = JsonSerializer.Serialize(cache);
            }
            var parent = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            temp = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json), CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _cachePath, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory cache still serves this session.
            AppLog.Warn("pcgw", $"PCGamingWiki cache not saved: {_cachePath}", ex);
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("pcgw", $"Temp file left behind: {temp}", ex); }
            }
            _cacheGate.Release();
        }
    }

    private static GraphicsApiDetection FromNames(IReadOnlyList<string> names, string evidence)
    {
        var apis = names
            .Select(static name => Enum.TryParse<GraphicsApi>(name, ignoreCase: true, out var api) ? api : GraphicsApi.Unknown)
            .Where(static api => api != GraphicsApi.Unknown)
            .Distinct()
            .OrderByDescending(static api => Priority(api))
            .ToArray();
        return apis.Length == 0 ? GraphicsApiDetection.Unknown : FromApis(apis, evidence);
    }

    private static GraphicsApiDetection FromApis(IReadOnlyList<GraphicsApi> apis, string evidence)
        => new(apis[0], apis, GraphicsApiConfidence.Medium, evidence);

    private static bool TryValidatePageUrl(string pageUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsed)
            || !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = parsed.IdnHost;
        if (!host.Equals("pcgamingwiki.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".pcgamingwiki.com", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    private static void AddApis(string text, ISet<GraphicsApi> apis)
    {
        var normalized = Regex.Replace(text, @"\s+", " ");
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*12\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX12);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*11\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX11);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*10(?:\.1)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX10);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*9(?:\.0c)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX9);
        if (Regex.IsMatch(normalized, @"\b(?:Direct3D|DirectX)\s*8(?:\.1)?\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.DirectX8);
        if (Regex.IsMatch(normalized, @"\bVulkan\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.Vulkan);
        if (Regex.IsMatch(normalized, @"\bOpenGL\b", RegexOptions.IgnoreCase)) apis.Add(GraphicsApi.OpenGL);
    }

    private static int Priority(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => 7,
        GraphicsApi.Vulkan => 6,
        GraphicsApi.DirectX11 => 5,
        GraphicsApi.DirectX10 => 4,
        GraphicsApi.OpenGL => 3,
        GraphicsApi.DirectX9 => 2,
        GraphicsApi.DirectX8 => 1,
        _ => 0
    };

    private static string StripTags(string input) => HtmlTagRegex().Replace(input, " ");

    [GeneratedRegex("<table\\b[^>]*\\bid\\s*=\\s*[\\\"']table-api[\\\"'][^>]*>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ApiTableRegex();

    [GeneratedRegex("<tr\\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex("<abbr\\b[^>]*\\btitle\\s*=\\s*[\\\"']([^\\\"']*)[\\\"'][^>]*>.*?</abbr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AbbrRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
