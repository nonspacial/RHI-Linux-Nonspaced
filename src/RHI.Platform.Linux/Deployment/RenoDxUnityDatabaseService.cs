using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One game in upstream RHI's <c>database/RenoDXdb-unity.json</c> (all use the generic Unity add-on).</summary>
public sealed class RenoDxUnityDbEntry
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Upgrades")] public string? Upgrades { get; set; }
    [JsonPropertyName("Comments")] public string? Comments { get; set; }

    /// <summary>
    /// Upstream 2.7.9 <c>RenoDXDbUnityEntry.ParsedUpgrades</c>: backtick tokens read as key, optional
    /// value. Size words map to 1/2/3, "0"/"1"/"2" pass through, a bare key gets an empty value.
    /// Linux addition: pairs that are not a plain INI key/value are dropped, so a remote database
    /// cannot inject lines or sections into the game's ReShade.ini.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ParsedUpgrades()
    {
        var tokens = BacktickTokens(Upgrades);
        var result = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < tokens.Count;)
        {
            var key = tokens[i++];
            var value = "";
            if (i < tokens.Count && IsValueToken(tokens[i]))
                value = MapValue(tokens[i++]);
            if (IsSafeKey(key)) result.Add(new(key, value));
        }
        return result;
    }

    private static bool IsValueToken(string token)
        => token.Equals("Output Size", StringComparison.OrdinalIgnoreCase)
           || token.Equals("Output Ratio", StringComparison.OrdinalIgnoreCase)
           || token.Equals("Any Size", StringComparison.OrdinalIgnoreCase)
           || token is "0" or "1" or "2";

    private static string MapValue(string token) => token.ToLowerInvariant() switch
    {
        "output size" => "1",
        "output ratio" => "2",
        "any size" => "3",
        _ => token
    };

    private static bool IsSafeKey(string key)
        => key.Length is > 0 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static List<string> BacktickTokens(string? input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return tokens;
        for (var i = 0; i < input.Length;)
        {
            var start = input.IndexOf('`', i);
            if (start < 0) break;
            var end = input.IndexOf('`', start + 1);
            if (end < 0) break;
            var token = input[(start + 1)..end].Trim();
            if (token.Length > 0) tokens.Add(token);
            i = end + 1;
        }
        return tokens;
    }
}

/// <summary>Unity database keyed by game name; lookup matches upstream <c>GetDbUnityEntry</c>.</summary>
public sealed class RenoDxUnityDatabase
{
    private readonly Dictionary<string, RenoDxUnityDbEntry> _entries;

    private RenoDxUnityDatabase(Dictionary<string, RenoDxUnityDbEntry> entries) => _entries = entries;

    public int Count => _entries.Count;

    /// <summary>Parses the JSON array; returns null when it holds no named entries.</summary>
    public static RenoDxUnityDatabase? Parse(ReadOnlySpan<byte> json)
    {
        var list = JsonSerializer.Deserialize<List<RenoDxUnityDbEntry?>>(json);
        var entries = new Dictionary<string, RenoDxUnityDbEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list ?? [])
            if (!string.IsNullOrWhiteSpace(entry?.Name))
                entries[entry.Name.Trim()] = entry; // last duplicate wins, as upstream
        return entries.Count == 0 ? null : new RenoDxUnityDatabase(entries);
    }

    /// <summary>Exact name (case-insensitive), then the name without ™/®/©.</summary>
    public RenoDxUnityDbEntry? Find(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;
        if (_entries.TryGetValue(gameName, out var entry)) return entry;
        var stripped = gameName.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return stripped != gameName && _entries.TryGetValue(stripped, out entry) ? entry : null;
    }
}

/// <summary>
/// Loads <c>RenoDXdb-unity.json</c> from the URL upstream RHI 2.7.9+ reads (RankFTW/rhi-repo).
/// Upstream revalidates with an ETag every session; Linux does the same, keeps the result for
/// <see cref="DefaultFreshness"/> in memory, persists the last good copy with its ETag under the
/// cache dir and falls back to it, then to the copy pinned at RHI 2.7.9. GitHub HTTPS hosts only.
/// </summary>
public sealed class RenoDxUnityDatabaseService
{
    public const string LiveUrl =
        "https://raw.githubusercontent.com/RankFTW/rhi-repo/main/database/RenoDXdb-unity.json";
    public const string PinnedUrl =
        "https://raw.githubusercontent.com/RankFTW/RHI/94da8c3ab0d878db91588fdd2c14bcf4c3b354fd/database/RenoDXdb-unity.json";

    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private const long MaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _etagPath;
    private readonly string _liveUrl;
    private readonly string? _fallbackUrl;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RenoDxUnityDatabase? _data;
    private DateTimeOffset _loadedAt;
    private DateTimeOffset _retryAfterFailure = DateTimeOffset.MinValue;

    public RenoDxUnityDatabaseService(
        HttpClient http,
        string? cachePath = null,
        string? liveUrl = null,
        string? fallbackUrl = PinnedUrl,
        TimeSpan? freshness = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "renodx", "RenoDXdb-unity.json");
        _etagPath = _cachePath + ".etag";
        _liveUrl = RequireGitHubHttps(liveUrl ?? LiveUrl);
        _fallbackUrl = fallbackUrl is null ? null : RequireGitHubHttps(fallbackUrl);
        _freshness = freshness ?? DefaultFreshness;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal static bool IsAllowedSource(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static string RequireGitHubHttps(string url)
        => IsAllowedSource(url)
            ? url
            : throw new ArgumentException($"Unity database source must be HTTPS on raw.githubusercontent.com: {url}", nameof(url));

    /// <summary>Returns the database, or null when no source is reachable (retried after a pause).</summary>
    public async Task<RenoDxUnityDatabase?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_data is not null && _time.GetUtcNow() - _loadedAt < _freshness) return _data;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_data is not null && _time.GetUtcNow() - _loadedAt < _freshness) return _data;
            if (_time.GetUtcNow() < _retryAfterFailure) return _data;

            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                _retryAfterFailure = _time.GetUtcNow() + FailureRetryDelay;
                return _data; // keep this session's last good copy
            }
            _data = loaded;
            _loadedAt = _time.GetUtcNow();
            return _data;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RenoDxUnityDatabase?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var etag = File.Exists(_cachePath) ? ReadEtag() : null;
            var (notModified, raw, newEtag) = await DownloadAsync(_liveUrl, etag, cancellationToken).ConfigureAwait(false);
            if (notModified)
            {
                if (await ReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } cached) return cached;
                (_, raw, newEtag) = await DownloadAsync(_liveUrl, null, cancellationToken).ConfigureAwait(false);
            }
            if (raw is not null && Parse(raw) is { } live)
            {
                await PersistAsync(raw, newEtag, cancellationToken).ConfigureAwait(false);
                return live;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
        {
            // Network/HTTP failure or timeout: fall through to the last good copy.
        }

        if (await ReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } stale) return stale;
        if (string.IsNullOrWhiteSpace(_fallbackUrl) || _fallbackUrl == _liveUrl) return null;
        try
        {
            var (_, raw, _) = await DownloadAsync(_fallbackUrl, null, cancellationToken).ConfigureAwait(false);
            if (raw is not null && Parse(raw) is { } pinned)
            {
                await PersistAsync(raw, null, cancellationToken).ConfigureAwait(false); // no ETag: revalidate next time
                return pinned;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
        {
            // Both sources unavailable; installs proceed without Unity database keys.
        }
        return null;
    }

    private async Task<(bool NotModified, byte[]? Raw, string? ETag)> DownloadAsync(
        string url, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            return (true, null, etag);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new InvalidDataException("Unity database response is too large.");
        var raw = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (raw.Length > MaxBytes)
            throw new InvalidDataException("Unity database response is too large.");
        return (false, raw, response.Headers.ETag?.Tag);
    }

    private static RenoDxUnityDatabase? Parse(byte[] raw)
    {
        try { return RenoDxUnityDatabase.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private async Task<RenoDxUnityDatabase?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath)) return null;
        try { return Parse(await File.ReadAllBytesAsync(_cachePath, cancellationToken).ConfigureAwait(false)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string? ReadEtag()
    {
        try { return File.Exists(_etagPath) ? File.ReadAllText(_etagPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task PersistAsync(byte[] raw, string? etag, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(temp, raw, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _cachePath, true);
            if (string.IsNullOrWhiteSpace(etag)) File.Delete(_etagPath);
            else await File.WriteAllTextAsync(_etagPath, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { /* Persistence is optional; the loaded data stays usable. */ }
        catch (UnauthorizedAccessException) { }
    }
}
