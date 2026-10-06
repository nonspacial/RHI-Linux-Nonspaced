using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Detection;

/// <summary>One game in upstream RHI's centralised <c>database/pcgw_data.json</c>.</summary>
public sealed class PcgwCentralEntry
{
    [JsonPropertyName("steam_appid")] public int SteamAppId { get; set; }
    [JsonPropertyName("dx9")] public bool Dx9 { get; set; }
    [JsonPropertyName("dx10")] public bool Dx10 { get; set; }
    [JsonPropertyName("dx11")] public bool Dx11 { get; set; }
    [JsonPropertyName("dx12")] public bool Dx12 { get; set; }
    [JsonPropertyName("vulkan")] public bool Vulkan { get; set; }
    [JsonPropertyName("opengl")] public bool OpenGL { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }

    /// <summary>APIs the entry lists for Windows, highest renderer priority first.</summary>
    public IReadOnlyList<GraphicsApi> GraphicsApis()
    {
        var apis = new List<GraphicsApi>(6);
        if (Dx12) apis.Add(GraphicsApi.DirectX12);
        if (Vulkan) apis.Add(GraphicsApi.Vulkan);
        if (Dx11) apis.Add(GraphicsApi.DirectX11);
        if (Dx10) apis.Add(GraphicsApi.DirectX10);
        if (OpenGL) apis.Add(GraphicsApi.OpenGL);
        if (Dx9) apis.Add(GraphicsApi.DirectX9);
        return apis;
    }
}

/// <summary>
/// In-memory PCGamingWiki data, keyed by PCGW page title. Lookup order matches upstream RHI 2.7.7:
/// name override, exact title, Steam AppID, then apostrophe and trademark-symbol normalisation.
/// </summary>
public sealed class PcgwCentralData
{
    private readonly Dictionary<string, PcgwCentralEntry> _games;
    private readonly Dictionary<string, string> _nameOverrides;
    private readonly Dictionary<int, string> _appIdIndex = new();

    private PcgwCentralData(Dictionary<string, PcgwCentralEntry> games, Dictionary<string, string> nameOverrides)
    {
        _games = games;
        _nameOverrides = nameOverrides;
        foreach (var (title, entry) in games)
            if (entry.SteamAppId > 0)
                _appIdIndex[entry.SteamAppId] = title;
    }

    public int Count => _games.Count;

    /// <summary>Parses pcgw_data.json; returns null when the document has no games.</summary>
    public static PcgwCentralData? Parse(ReadOnlySpan<byte> json)
    {
        var file = JsonSerializer.Deserialize<PcgwCentralFile>(json);
        if (file?.Games is not { Count: > 0 } games)
            return null;
        // Indexer inserts: case-variant duplicate titles must not throw (last one wins, as upstream).
        var byTitle = new Dictionary<string, PcgwCentralEntry>(games.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (title, entry) in games)
            if (entry is not null) byTitle[title] = entry;
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, title) in file.NameOverrides ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(title)) overrides[name] = title;
        return new PcgwCentralData(byTitle, overrides);
    }

    public (string PageTitle, PcgwCentralEntry Entry)? TryLookup(string? gameName, int? steamAppId)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return LookupAppId(steamAppId);

        if (LookupName(gameName) is { } byName) return byName;
        if (LookupAppId(steamAppId) is { } byAppId) return byAppId;

        // Steam uses a straight apostrophe where PCGW titles use U+2019.
        var curly = gameName.Replace('\'', '\u2019');
        if (!curly.Equals(gameName, StringComparison.Ordinal) && LookupName(curly) is { } byCurly)
            return byCurly;

        // Store names may carry ™/®/© that PCGW titles omit.
        var stripped = gameName.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        if (stripped.Equals(gameName, StringComparison.Ordinal))
            return null;
        if (LookupName(stripped) is { } byStripped) return byStripped;
        var strippedCurly = stripped.Replace('\'', '\u2019');
        return strippedCurly.Equals(stripped, StringComparison.Ordinal) ? null : LookupName(strippedCurly);
    }

    private (string, PcgwCentralEntry)? LookupName(string name)
    {
        if (_nameOverrides.TryGetValue(name, out var mapped) && _games.TryGetValue(mapped, out var mappedEntry))
            return (mapped, mappedEntry);
        return _games.TryGetValue(name, out var entry) ? (name, entry) : null;
    }

    private (string, PcgwCentralEntry)? LookupAppId(int? steamAppId)
        => steamAppId is > 0
           && _appIdIndex.TryGetValue(steamAppId.Value, out var title)
           && _games.TryGetValue(title, out var entry)
            ? (title, entry)
            : null;

    /// <summary>Strips a Cargo namespace (<c>Engine:Unreal_Engine_4</c>) and underscores.</summary>
    public static string? NormaliseEngineName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[(colon + 1)..];
        s = s.Replace('_', ' ').Trim();
        return s.Length == 0 ? null : s;
    }

    private sealed class PcgwCentralFile
    {
        [JsonPropertyName("name_overrides")] public Dictionary<string, string>? NameOverrides { get; set; }
        [JsonPropertyName("games")] public Dictionary<string, PcgwCentralEntry>? Games { get; set; }
    }
}

/// <summary>
/// Loads upstream RHI's single-file PCGamingWiki database once per session, replacing per-game
/// PCGW requests. Fetched at runtime (not bundled): the file changes several times a week and is
/// about 5 MB. A fresh disk copy is used without any request; an older copy is revalidated with
/// its ETag. Any failure falls back to the last good copy, then to the copy pinned at RHI 2.7.9.
/// </summary>
public sealed class PcgwCentralDataService
{
    public const string LiveDataUrl =
        "https://raw.githubusercontent.com/RankFTW/RHI/main/database/pcgw_data.json";
    public const string PinnedDataUrl =
        "https://raw.githubusercontent.com/RankFTW/RHI/94da8c3ab0d878db91588fdd2c14bcf4c3b354fd/database/pcgw_data.json";

    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _etagPath;
    private readonly string _liveUrl;
    private readonly string? _fallbackUrl;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PcgwCentralData? _data;
    private DateTimeOffset _retryAfterFailure = DateTimeOffset.MinValue;

    public PcgwCentralDataService(
        HttpClient http,
        string? cachePath = null,
        string? liveUrl = null,
        string? fallbackUrl = PinnedDataUrl,
        TimeSpan? freshness = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "pcgw_data.json");
        _etagPath = _cachePath + ".etag";
        _liveUrl = liveUrl ?? LiveDataUrl;
        _fallbackUrl = fallbackUrl;
        _freshness = freshness ?? DefaultFreshness;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns the loaded data, or null when no source is reachable (retried after a pause).</summary>
    public async Task<PcgwCentralData?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_data is not null) return _data;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_data is not null) return _data;
            if (_time.GetUtcNow() < _retryAfterFailure) return null;

            _data = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_data is null)
                _retryAfterFailure = _time.GetUtcNow() + FailureRetryDelay;
            return _data;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PcgwCentralData?> LoadAsync(CancellationToken cancellationToken)
    {
        var cacheAge = File.Exists(_cachePath)
            ? _time.GetUtcNow() - File.GetLastWriteTimeUtc(_cachePath)
            : (TimeSpan?)null;
        if (cacheAge < _freshness && await ReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } fresh)
            return fresh;

        try
        {
            var etag = cacheAge is null ? null : ReadEtag();
            var (notModified, raw, newEtag) = await DownloadAsync(_liveUrl, etag, cancellationToken).ConfigureAwait(false);
            if (notModified)
            {
                var cached = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    TryTouchCache();
                    return cached;
                }
                // 304 against a vanished/corrupt cache: fetch unconditionally once.
                (_, raw, newEtag) = await DownloadAsync(_liveUrl, null, cancellationToken).ConfigureAwait(false);
            }

            if (raw is not null && Parse(raw) is { } live)
            {
                await PersistAsync(raw, newEtag, cancellationToken).ConfigureAwait(false);
                return live;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Network/HTTP/parse failure: fall through to the last good copy.
        }

        if (await ReadCacheAsync(cancellationToken).ConfigureAwait(false) is { } stale)
            return stale;

        if (string.IsNullOrWhiteSpace(_fallbackUrl) || _fallbackUrl == _liveUrl)
            return null;
        try
        {
            var (_, raw, _) = await DownloadAsync(_fallbackUrl, null, cancellationToken).ConfigureAwait(false);
            if (raw is not null && Parse(raw) is { } pinned)
            {
                // No ETag: the next session revalidates against the live file.
                await PersistAsync(raw, null, cancellationToken).ConfigureAwait(false);
                return pinned;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Both sources unavailable; detection carries on without PCGW data.
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
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            return (true, null, etag);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return (false, raw, response.Headers.ETag?.Tag);
    }

    private static PcgwCentralData? Parse(byte[] raw)
    {
        try { return PcgwCentralData.Parse(raw); }
        catch (JsonException) { return null; }
    }

    private async Task<PcgwCentralData?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath)) return null;
        try
        {
            return Parse(await File.ReadAllBytesAsync(_cachePath, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private string? ReadEtag()
    {
        try { return File.Exists(_etagPath) ? File.ReadAllText(_etagPath).Trim() : null; }
        catch { return null; }
    }

    private void TryTouchCache()
    {
        try { File.SetLastWriteTimeUtc(_cachePath, _time.GetUtcNow().UtcDateTime); }
        catch { /* Freshness only; a failed touch just means an earlier revalidation. */ }
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
            File.SetLastWriteTimeUtc(_cachePath, _time.GetUtcNow().UtcDateTime);
            if (string.IsNullOrWhiteSpace(etag)) File.Delete(_etagPath);
            else await File.WriteAllTextAsync(_etagPath, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Persistence is optional; the loaded data stays usable this session.
        }
    }
}
