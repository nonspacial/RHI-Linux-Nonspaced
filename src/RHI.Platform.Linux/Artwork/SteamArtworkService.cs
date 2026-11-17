using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Artwork;

/// <summary>Which Spotlight artwork: the wide banner over the game page, or the tall cover on the shelf.</summary>
public enum ArtworkKind
{
    /// <summary>The banner (Steam <c>library_hero</c>).</summary>
    Hero,
    /// <summary>The cover (Steam <c>library_600x900</c>).</summary>
    Cover,
}

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.0–2.9.4 <c>SteamArtworkService</c>): Steam artwork for the Spotlight layout (banner,
/// covers) for any game that is also on Steam, whatever store it is owned on, and the user's own cover and banner. Images
/// are downloaded once into <c>~/.cache/rhi-linux/artwork/{appid}/</c>, only while Spotlight is in use (the view asks), and
/// only through <see cref="DownloadSources.SteamArtwork"/> (Steam's CDN hosts, 16 MiB cap, an image's first bytes checked);
/// a miss is remembered for a week. Newer games use hashed asset paths only Steam's store API knows
/// (<see cref="DownloadSources.SteamWebApi"/>, asked after a CDN miss, once per game a session). Custom art is a copy of the
/// chosen file in <c>~/.local/share/rhi-linux/artwork/custom/</c>: the user's own file is never moved or changed.
/// </summary>
public sealed class SteamArtworkService
{
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    /// <summary>Largest image RHI copies in as custom art (upstream sets none; 32 MiB keeps a mistaken pick out).</summary>
    public const long CustomMaxBytes = 32L * 1024 * 1024;

    /// <summary>The image file types a custom cover or banner may be (upstream's file picker filter).</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly string _customRoot;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(4); // parallel downloads, as upstream
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task<IReadOnlyDictionary<string, string>?>> _assetLookups = new();

    private readonly Func<int, ArtworkKind, string?> _steamLibraryCache;

    /// <param name="steamLibraryCache">
    /// Linux addition: the Steam client's own copy of the art (<c>appcache/librarycache</c>), used before any download; the
    /// default looks in native and Flatpak Steam (<see cref="Achievements.SteamLocalFiles"/>).
    /// </param>
    public SteamArtworkService(HttpClient http, string? cacheRoot = null, string? customRoot = null, Func<DateTime>? utcNow = null,
        Func<int, ArtworkKind, string?>? steamLibraryCache = null)
    {
        _steamLibraryCache = steamLibraryCache ?? DefaultSteamLibraryCache;
        _http = http;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "artwork");
        _customRoot = customRoot ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "artwork", "custom");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The Steam AppID whose artwork a game uses (upstream <c>ResolveCommunityAppId</c>): the manifest's override, the Steam
    /// library's id, then PCGamingWiki's data by name (store suffixes, editions and years handled by
    /// <see cref="PcgwCentralData.TryLookup"/>); a demo or playtest without its own entry uses the full game's. Null when
    /// none is known (the cover shows the name, the banner stays plain).
    /// </summary>
    public static int? ResolveAppId(DetectedGame game, PcgwCentralData? pcgw)
    {
        if (game.ManifestSteamAppIdOverride is > 0) return game.ManifestSteamAppIdOverride;
        if (game.SteamAppId is > 0) return game.SteamAppId;
        if (pcgw is null) return null;
        var name = game.DisplayName();
        if (pcgw.TryLookup(name, null)?.Entry.SteamAppId is > 0 and var byName) return byName;
        return StoreNameSuffix.StripTrial(name) is { } fullGame && pcgw.TryLookup(fullGame, null)?.Entry.SteamAppId is > 0 and var full ? full : null;
    }

    private static string FileName(ArtworkKind kind) => kind == ArtworkKind.Hero ? "hero.jpg" : "cover.jpg";

    private string LocalPath(int appId, ArtworkKind kind) => Path.Combine(_cacheRoot, appId.ToString(System.Globalization.CultureInfo.InvariantCulture), FileName(kind));

    private string MissPath(int appId, ArtworkKind kind) => LocalPath(appId, kind) + ".miss";

    // ---- custom artwork (Artwork › Set custom cover / banner; SteamGridDB; Screenshots › Set as banner) ----------------

    private string CustomDir(string gameKey)
    {
        var safe = new string(gameKey.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        if (safe.Length > 60) safe = safe[..60];
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(gameKey.ToLowerInvariant())))[..8];
        return Path.Combine(_customRoot, $"{safe}_{hash}");
    }

    private static string CustomPrefix(ArtworkKind kind) => kind == ArtworkKind.Hero ? "hero-" : "cover-";

    /// <summary>The game's own cover or banner (custom art wins over Steam's), or null.</summary>
    public string? TryGetCustom(string gameKey, ArtworkKind kind)
    {
        try
        {
            var dir = CustomDir(gameKey);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, CustomPrefix(kind) + "*")
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("artwork", $"Custom art for {gameKey} unreadable", ex);
            return null;
        }
    }

    /// <summary>
    /// Copies <paramref name="sourceFile"/> in as the game's custom cover or banner (a new name each time, so an image shown
    /// for the old file is never reused) and removes RHI's previous copy. The user's file is only read. Returns the copy, or
    /// null with the reason in <paramref name="error"/> (<see cref="NotAnImageMessage"/>, a size or I/O message).
    /// </summary>
    public string? SetCustom(string gameKey, ArtworkKind kind, string sourceFile, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(sourceFile);
            if (!info.Exists) { error = LinuxDesktopOpener.FileMissingPrefix + sourceFile; return null; }
            if (info.Length > CustomMaxBytes) { error = TooLargeMessage; return null; }
            var ext = info.Extension.ToLowerInvariant();
            if (!ImageExtensions.Contains(ext) || !LooksLikeImage(sourceFile)) { error = NotAnImageMessage; return null; }
            var dir = CustomDir(gameKey);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{CustomPrefix(kind)}{_utcNow().Ticks}{ext}");
            var tmp = dest + ".tmp";
            File.Copy(sourceFile, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, _utcNow());
            DeleteCustom(gameKey, kind, keep: dest);
            AppLog.Warn("artwork", $"Custom {kind} for {gameKey} set from {sourceFile}");
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("artwork", $"Custom {kind} for {gameKey} not saved", ex);
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The fixed part of the message for a file that is not a JPEG, PNG, WebP or BMP image.</summary>
    public const string NotAnImageMessage = "The file is not a JPEG, PNG, WebP or BMP image.";

    /// <summary>The message for a file over <see cref="CustomMaxBytes"/>.</summary>
    public const string TooLargeMessage = "The image is larger than 32 MB.";

    /// <summary>Use Steam cover / banner: removes RHI's copy, so Steam's art (or the plain tile) shows again.</summary>
    public void ClearCustom(string gameKey, ArtworkKind kind) => DeleteCustom(gameKey, kind, keep: null);

    private void DeleteCustom(string gameKey, ArtworkKind kind, string? keep)
    {
        var dir = CustomDir(gameKey);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, CustomPrefix(kind) + "*").ToList())
        {
            if (keep is not null && string.Equals(f, keep, StringComparison.Ordinal)) continue;
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("artwork", $"Old custom art {f} not removed", ex);
            }
        }
    }

    /// <summary>Whether a file starts like a JPEG, PNG, WebP, GIF or BMP image.</summary>
    public static bool LooksLikeImage(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[12];
            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return LooksLikeImage(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether bytes start like a JPEG, PNG, WebP, GIF or BMP image.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> head)
        => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF
           || head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
           || head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)
           || head.Length >= 4 && head[..4].SequenceEqual("GIF8"u8)
           || head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M';

    // ---- Steam art -------------------------------------------------------------------------------------------------

    private static readonly Achievements.SteamLocalFiles SteamFiles = new();

    private static string? DefaultSteamLibraryCache(int appId, ArtworkKind kind)
        => SteamFiles.LibraryCacheFile(appId, kind == ArtworkKind.Hero ? "library_hero.jpg" : "library_600x900.jpg");

    /// <summary>The art on disk — Steam's own library cache, else RHI's download — or null. Cheap (file checks only).</summary>
    public string? TryGetCached(int appId, ArtworkKind kind)
    {
        if (_steamLibraryCache(appId, kind) is { } steams && LooksLikeImage(steams)) return steams;
        var path = LocalPath(appId, kind);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The local file for this artwork, downloading it once if needed. Null when Steam has none (or offline).</summary>
    public Task<string?> GetAsync(int appId, ArtworkKind kind, CancellationToken cancellationToken)
    {
        if (TryGetCached(appId, kind) is { } cached) return Task.FromResult<string?>(cached);
        var key = $"{appId}:{kind}";
        return _inFlight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try { return await DownloadAsync(appId, kind, cancellationToken).ConfigureAwait(false); }
            finally { _inFlight.TryRemove(key, out Task<string?>? _); }
        }, CancellationToken.None));
    }

    /// <summary>The fixed CDN links tried first (upstream's order).</summary>
    public static IReadOnlyList<string> CdnLinks(int appId, ArtworkKind kind)
    {
        var asset = kind == ArtworkKind.Hero ? "library_hero.jpg" : "library_600x900.jpg";
        return
        [
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{asset}",
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/{asset}",
        ];
    }

    private async Task<string?> DownloadAsync(int appId, ArtworkKind kind, CancellationToken cancellationToken)
    {
        var miss = MissPath(appId, kind);
        try
        {
            if (File.Exists(miss) && _utcNow() - File.GetLastWriteTimeUtc(miss) < MissRetry) return null;
        }
        catch (IOException ex) { AppLog.Warn("artwork", $"Miss marker for {appId} unreadable", ex); }

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            var dest = LocalPath(appId, kind);
            foreach (var url in CdnLinks(appId, kind))
                if (await TryDownloadAsync(url, dest, cancellationToken).ConfigureAwait(false)) return dest;

            // Newer games use hashed asset paths only the store API knows. The wide header is the banner's last resort;
            // covers without one show the game's name instead (upstream).
            var assets = await _assetLookups.GetOrAdd(appId, id => LookupAssetsAsync(id, cancellationToken)).ConfigureAwait(false);
            var names = kind == ArtworkKind.Hero ? new[] { "library_hero", "header" } : new[] { "library_capsule" };
            if (assets is not null)
                foreach (var name in names)
                    if (assets.TryGetValue(name, out var url) && await TryDownloadAsync(url, dest, cancellationToken).ConfigureAwait(false))
                        return dest;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(miss)!);
                File.WriteAllText(miss, _utcNow().ToString("O"));
                File.SetLastWriteTimeUtc(miss, _utcNow());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("artwork", $"Miss for {appId} not recorded", ex);
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        finally { _gate.Release(); }
    }

    private async Task<bool> TryDownloadAsync(string url, string dest, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var download = await TrustedDownload.DownloadToTempAsync(_http, url, dest, DownloadSources.SteamArtwork,
                cancellationToken: cancellationToken, onResponse: RequireImage).ConfigureAwait(false);
            if (download.Length < 1024 || !LooksLikeImage(download.Path)) return false;
            download.MoveTo(dest);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or UntrustedDownloadException)
        {
            AppLog.Warn("artwork", $"Steam art {url} not downloaded", ex);
            return false;
        }
    }

    /// <summary>Refuses an answer that is not an image (a CDN's HTML error page).</summary>
    internal static void RequireImage(HttpResponseMessage response)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (response.IsSuccessStatusCode && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not an image ({type}): {response.RequestMessage?.RequestUri}");
    }

    /// <summary>The store API's link for a game's assets (no key needed).</summary>
    public static string AssetLookupUrl(int appId)
    {
        var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
        return "https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input);
    }

    private async Task<IReadOnlyDictionary<string, string>?> LookupAssetsAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await TrustedDownload.GetStringAsync(_http, AssetLookupUrl(appId), DownloadSources.SteamWebApi,
                "application/json", cancellationToken).ConfigureAwait(false);
            return ParseAssets(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or UntrustedDownloadException)
        {
            AppLog.Warn("artwork", $"Steam store assets for {appId} not read", ex);
            return null;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>
    /// IStoreBrowseService/GetItems' answer: asset name (library_capsule, library_hero, header) → full link on Steam's CDN.
    /// Null when the answer has no assets. Names with a path separator or a link outside the CDN are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseAssets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("store_items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0
                || !items[0].TryGetProperty("assets", out var assets)
                || !assets.TryGetProperty("asset_url_format", out var fmt) || fmt.GetString() is not { Length: > 0 } format)
                return null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "library_capsule", "library_hero", "header" })
                if (assets.TryGetProperty(name, out var file) && file.GetString() is { Length: > 0 } f && !f.Contains("..", StringComparison.Ordinal))
                    result[name] = "https://shared.akamai.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", f, StringComparison.Ordinal);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
