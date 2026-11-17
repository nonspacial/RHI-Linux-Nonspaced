using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Artwork;

/// <summary>What a SteamGridDB request came back with.</summary>
public enum SteamGridDbStatus { Ok, NoKey, BadKey, NotFound, Offline, Error }

/// <summary>One SteamGridDB image: its id, full and thumbnail links, size, type, author and score.</summary>
public sealed record SteamGridDbArt(int Id, string Url, string Thumb, int Width, int Height, string Mime, string Author, int Score);

/// <summary>One page of covers or banners; <see cref="MayHaveMore"/> when the page was full.</summary>
public sealed record SteamGridDbPage(SteamGridDbStatus Status, IReadOnlyList<SteamGridDbArt> Items, bool MayHaveMore);

/// <summary>
/// w14/layouts-b (#305, upstream 853c16f, 2.9.5 beta: <c>SteamGridDbService</c>): community covers and banners from
/// SteamGridDB for Spotlight's artwork picker (game menu › Artwork › Browse covers / Browse banners). Each user brings their
/// own free API key (Settings › Accounts &amp; Data), kept in a 0600 secret file (<see cref="SecretFileStore"/>,
/// <c>~/.config/rhi-linux/steamgriddb_api.txt</c>; upstream: a text file in LocalAppData). The key is sent per request to
/// www.steamgriddb.com only (<see cref="DownloadSources.SteamGridDbApi"/>), never to the image hosts and never logged.
/// API v2: covers <c>/grids/steam/{appid}</c> or <c>/grids/game/{id}</c> (600x900, 342x482, 660x930), banners
/// <c>/heroes/…</c>, static only, no NSFW / humour / epilepsy, 0-based pages; a name search
/// (<c>/search/autocomplete/{term}</c>) when SteamGridDB does not know the AppID. Lists are cached per game for 24 h,
/// thumbnails on disk; full images are downloaded only when picked (<see cref="DownloadSources.SteamGridDbImages"/>, image
/// bytes checked). Linux deviation: WebP needs no conversion (Avalonia shows it).
/// </summary>
public sealed class SteamGridDbService
{
    public const string ApiKeyPage = "https://www.steamgriddb.com/profile/preferences/api";
    public const string ApiBase = "https://www.steamgriddb.com/api/v2";
    private const int MinFullPage = 20; // a page with fewer items is the last one (upstream)
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromHours(24);

    public static string DefaultKeyPath => Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "steamgriddb_api.txt");

    private readonly HttpClient _http;
    private readonly SecretFileStore _key;
    private readonly string _root;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _thumbGate = new(6);
    private readonly ConcurrentDictionary<string, Task<string?>> _thumbsInFlight = new(StringComparer.Ordinal);
    private readonly object _chosenLock = new();

    public SteamGridDbService(HttpClient http, SecretFileStore key, string? root = null, Func<DateTime>? utcNow = null)
    {
        _http = http;
        _key = key;
        _root = root ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "artwork", "sgdb");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The key file (Settings shows where it is).</summary>
    public string KeyPath => _key.FilePath;

    /// <summary>The saved key's store, for masking it out of a support bundle.</summary>
    public SecretFileStore KeyStore => _key;

    /// <summary>The saved API key, or null (none, or a file RHI must not use).</summary>
    public string? ApiKey => _key.Read() is { State: SecretFileState.Present, Value: { } value } ? value : null;

    public bool HasKey => ApiKey is not null;

    /// <summary>Checks <paramref name="key"/> with one small request and saves it when SteamGridDB accepts it.</summary>
    public async Task<SteamGridDbStatus> SaveKeyAsync(string? key, CancellationToken cancellationToken = default)
    {
        key = key?.Trim();
        if (!SecretFileStore.IsWellFormed(key)) return SteamGridDbStatus.BadKey;
        var status = await ValidateKeyAsync(key!, cancellationToken).ConfigureAwait(false);
        if (status != SteamGridDbStatus.Ok) return status;
        _key.Write(key!);
        AppLog.Warn("steamgriddb", "API key saved");
        return SteamGridDbStatus.Ok;
    }

    /// <summary>Removes the saved key.</summary>
    public void ClearKey()
    {
        _key.Delete();
        AppLog.Warn("steamgriddb", "API key removed");
    }

    /// <summary>Checks a key with a search (upstream: "portal"); a search with no match still means the key works.</summary>
    public async Task<SteamGridDbStatus> ValidateKeyAsync(string key, CancellationToken cancellationToken)
    {
        var (status, _) = await GetJsonAsync(key, "/search/autocomplete/portal", cancellationToken).ConfigureAwait(false);
        return status == SteamGridDbStatus.NotFound ? SteamGridDbStatus.Ok : status;
    }

    // ---- lists -----------------------------------------------------------------------------------------------------

    /// <summary>The list request for one page of a game's static covers or banners (upstream's filters).</summary>
    public static string ListPath(string target, ArtworkKind kind, int page) => kind == ArtworkKind.Cover
        ? $"/grids/{target}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false&epilepsy=false&page={page}"
        : $"/heroes/{target}?types=static&nsfw=false&humor=false&epilepsy=false&page={page}";

    /// <summary>
    /// One page (0-based) of covers or banners for a game: by Steam AppID when known, else (or when SteamGridDB does not
    /// know the AppID) by a name search. Cached for 24 h.
    /// </summary>
    public async Task<SteamGridDbPage> GetArtAsync(int? appId, string gameName, ArtworkKind kind, int page, CancellationToken cancellationToken)
    {
        if (ApiKey is not { } key) return new SteamGridDbPage(SteamGridDbStatus.NoKey, [], false);
        var kindName = kind == ArtworkKind.Cover ? "cover" : "hero";
        var cacheName = (appId is int a ? $"steam_{a}" : "name_" + Hash(gameName)) + $"_{kindName}_{page}.json";
        var cachePath = Path.Combine(_root, "lists", cacheName);
        if (TryReadCache(cachePath) is { } cached) return new SteamGridDbPage(SteamGridDbStatus.Ok, cached, cached.Count >= MinFullPage);

        var result = new SteamGridDbPage(SteamGridDbStatus.NotFound, [], false);
        if (appId is int id) result = await FetchListAsync(key, $"steam/{id}", kind, page, cancellationToken).ConfigureAwait(false);
        if (result.Status == SteamGridDbStatus.NotFound && !string.IsNullOrWhiteSpace(gameName))
        {
            var (status, gameId) = await SearchGameAsync(key, gameName, cancellationToken).ConfigureAwait(false);
            if (status is not (SteamGridDbStatus.Ok or SteamGridDbStatus.NotFound)) return new SteamGridDbPage(status, [], false);
            if (gameId is int gid) result = await FetchListAsync(key, $"game/{gid}", kind, page, cancellationToken).ConfigureAwait(false);
        }
        if (result.Status is SteamGridDbStatus.Ok or SteamGridDbStatus.NotFound) WriteCache(cachePath, result.Items);
        return result.Status == SteamGridDbStatus.NotFound ? result with { Status = SteamGridDbStatus.Ok } : result;
    }

    private async Task<SteamGridDbPage> FetchListAsync(string key, string target, ArtworkKind kind, int page, CancellationToken cancellationToken)
    {
        var (status, data) = await GetJsonAsync(key, ListPath(target, kind, page), cancellationToken).ConfigureAwait(false);
        if (status != SteamGridDbStatus.Ok || data is not { } arr) return new SteamGridDbPage(status, [], false);
        var items = ParseArt(arr);
        return new SteamGridDbPage(SteamGridDbStatus.Ok, items, items.Count >= MinFullPage);
    }

    private async Task<(SteamGridDbStatus, int?)> SearchGameAsync(string key, string gameName, CancellationToken cancellationToken)
    {
        var term = Uri.EscapeDataString(gameName.Replace('/', ' ').Replace('\\', ' ').Trim());
        var (status, data) = await GetJsonAsync(key, $"/search/autocomplete/{term}", cancellationToken).ConfigureAwait(false);
        if (status != SteamGridDbStatus.Ok || data is not { ValueKind: JsonValueKind.Array } arr) return (status, null);
        foreach (var g in arr.EnumerateArray())
            if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                return (SteamGridDbStatus.Ok, id);
        return (SteamGridDbStatus.NotFound, null);
    }

    /// <summary>
    /// The images in a reply's <c>data</c> array. An image whose link is not on SteamGridDB's image servers
    /// (<see cref="DownloadSources.SteamGridDbImages"/>) is left out; a thumbnail off them falls back to the full link.
    /// </summary>
    public static List<SteamGridDbArt> ParseArt(JsonElement arr)
    {
        var list = new List<SteamGridDbArt>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
            var url = Str(e, "url");
            if (!IsImageLink(url)) continue;
            var thumb = Str(e, "thumb");
            if (!IsImageLink(thumb)) thumb = url;
            var author = e.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object ? Str(au, "name") : "";
            list.Add(new SteamGridDbArt(id, url, thumb, Int(e, "width"), Int(e, "height"), Str(e, "mime"), author, Int(e, "score")));
        }
        return list;
    }

    /// <summary>Whether a link may be downloaded under <see cref="DownloadSources.SteamGridDbImages"/>.</summary>
    public static bool IsImageLink(string url)
    {
        try
        {
            DownloadSources.SteamGridDbImages.Validate(url);
            return true;
        }
        catch (Exception ex) when (ex is UntrustedDownloadException or UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>GET with the key; the reply's <c>data</c> element on success. 401/403 = bad key, 404 or success:false = none.</summary>
    private async Task<(SteamGridDbStatus, JsonElement?)> GetJsonAsync(string key, string path, CancellationToken cancellationToken)
    {
        HttpStatusCode? code = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var json = await TrustedDownload.GetStringAsync(_http, ApiBase + path, DownloadSources.SteamGridDbApi, "application/json", timeout.Token,
                onResponse: response => code = response.StatusCode,
                configureRequest: request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)).ConfigureAwait(false);
            return Interpret(json);
        }
        catch (HttpRequestException) when (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            AppLog.Warn("steamgriddb", $"Key refused ({(int)code!}) for {path.Split('?')[0]}");
            return (SteamGridDbStatus.BadKey, null);
        }
        catch (HttpRequestException) when (code == HttpStatusCode.NotFound)
        {
            return (SteamGridDbStatus.NotFound, null);
        }
        catch (HttpRequestException ex) when (code is not null)
        {
            AppLog.Warn("steamgriddb", $"{(int)code} for {path.Split('?')[0]}", ex);
            return (SteamGridDbStatus.Error, null);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn("steamgriddb", $"Not reached for {path.Split('?')[0]}", ex);
            return (SteamGridDbStatus.Offline, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (SteamGridDbStatus.Offline, null);
        }
        catch (OperationCanceledException)
        {
            return (SteamGridDbStatus.Error, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or UntrustedDownloadException)
        {
            AppLog.Warn("steamgriddb", $"Reply refused for {path.Split('?')[0]}", ex);
            return (SteamGridDbStatus.Error, null);
        }
    }

    /// <summary>A reply body: <c>data</c> on success, NotFound for <c>success: false</c>, Error for anything else.</summary>
    public static (SteamGridDbStatus, JsonElement?) Interpret(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (SteamGridDbStatus.Error, null);
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return (SteamGridDbStatus.NotFound, null);
            return root.TryGetProperty("data", out var data) ? (SteamGridDbStatus.Ok, data.Clone()) : (SteamGridDbStatus.Error, null);
        }
        catch (JsonException)
        {
            return (SteamGridDbStatus.Error, null);
        }
    }

    private List<SteamGridDbArt>? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path) || _utcNow() - File.GetLastWriteTimeUtc(path) > ListMaxAge) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ParseArt(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCache(string path, IReadOnlyList<SteamGridDbArt> items)
    {
        try
        {
            var json = JsonSerializer.Serialize(items.Select(a => new
            {
                id = a.Id, url = a.Url, thumb = a.Thumb, width = a.Width, height = a.Height, mime = a.Mime, score = a.Score,
                author = new { name = a.Author },
            }));
            AtomicFile.WriteStoreFile(path, Encoding.UTF8.GetBytes(json));
            File.SetLastWriteTimeUtc(path, _utcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("steamgriddb", "List cache not written", ex);
        }
    }

    // ---- images ----------------------------------------------------------------------------------------------------

    private static string Ext(string url, string mime)
    {
        var e = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => "" };
        if (e.Length > 0) return e;
        try { e = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant(); }
        catch (UriFormatException) { e = ""; }
        return e is ".png" or ".jpg" or ".jpeg" or ".webp" ? e : ".img";
    }

    /// <summary>The thumbnail on disk, downloading it once. Null on failure.</summary>
    public Task<string?> GetThumbAsync(SteamGridDbArt art, CancellationToken cancellationToken)
    {
        var dest = Path.Combine(_root, "thumbs", $"{art.Id}{Ext(art.Thumb, "")}");
        if (File.Exists(dest)) return Task.FromResult<string?>(dest);
        return _thumbsInFlight.GetOrAdd(dest, _ => Task.Run(async () =>
        {
            try
            {
                await _thumbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await DownloadAsync(art.Thumb, dest, cancellationToken).ConfigureAwait(false) ? dest : null; }
                finally { _thumbGate.Release(); }
            }
            catch (OperationCanceledException) { return null; }
            finally { _thumbsInFlight.TryRemove(dest, out Task<string?>? _); }
        }, CancellationToken.None));
    }

    /// <summary>The fixed message when a picked image could not be downloaded.</summary>
    public const string DownloadFailedMessage = "Couldn't download the image. Check your connection and try again.";

    /// <summary>Downloads the full image when it is picked. The file, or null with <see cref="DownloadFailedMessage"/>.</summary>
    public async Task<(string? Path, string? Error)> DownloadFullAsync(SteamGridDbArt art, CancellationToken cancellationToken)
    {
        var dest = Path.Combine(_root, "full", $"{art.Id}{Ext(art.Url, art.Mime)}");
        if (File.Exists(dest)) return (dest, null);
        return await DownloadAsync(art.Url, dest, cancellationToken).ConfigureAwait(false) ? (dest, null) : (null, DownloadFailedMessage);
    }

    private async Task<bool> DownloadAsync(string url, string dest, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // The image hosts get no key (it is only for the API).
            using var download = await TrustedDownload.DownloadToTempAsync(_http, url, dest, DownloadSources.SteamGridDbImages,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (download.Length < 256 || !SteamArtworkService.LooksLikeImage(download.Path))
            {
                AppLog.Warn("steamgriddb", $"Not an image: {url}");
                return false;
            }
            download.MoveTo(dest);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or UntrustedDownloadException)
        {
            AppLog.Warn("steamgriddb", $"Image {url} not downloaded", ex);
            return false;
        }
    }

    // ---- which SteamGridDB image a game uses (the picker's highlight) --------------------------------------------------

    private string ChosenPath => Path.Combine(_root, "chosen.json");

    /// <summary>Remembers that this game's custom art file came from this SteamGridDB image.</summary>
    public void RememberChoice(string gameKey, ArtworkKind kind, int artId, string customFile)
    {
        lock (_chosenLock)
        {
            var map = ReadChosen();
            map[$"{gameKey}|{kind}"] = $"{artId}|{customFile}";
            try { AtomicFile.WriteStoreFile(ChosenPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("steamgriddb", "Choice not saved", ex); }
        }
    }

    /// <summary>The SteamGridDB image id the game's current custom art came from, or null.</summary>
    public int? ChosenId(string gameKey, ArtworkKind kind, string? currentCustom)
    {
        if (currentCustom is null) return null;
        lock (_chosenLock)
        {
            if (!ReadChosen().TryGetValue($"{gameKey}|{kind}", out var v)) return null;
            var bar = v.IndexOf('|');
            if (bar <= 0 || !int.TryParse(v[..bar], System.Globalization.CultureInfo.InvariantCulture, out var id)) return null;
            return string.Equals(v[(bar + 1)..], currentCustom, StringComparison.Ordinal) ? id : null;
        }
    }

    private Dictionary<string, string> ReadChosen()
    {
        try
        {
            if (File.Exists(ChosenPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChosenPath)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Warn("steamgriddb", "Choices unreadable", ex); }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..12];
}
