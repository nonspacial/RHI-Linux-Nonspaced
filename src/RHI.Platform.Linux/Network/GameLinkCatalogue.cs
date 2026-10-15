using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Network;

/// <summary>One game of a community list: its name as the list spells it and the page RHI opens for it.</summary>
public sealed record GameLinkEntry(string Name, string Url);

/// <summary>
/// The web pages a library card links to (#183, upstream <c>PcgwService</c>, <c>UltraPlusService</c>,
/// <c>UltrawideFixService</c>, <c>HdrDatabaseService</c>, <c>SteamAppIdResolver</c>): PCGamingWiki, Ultra+, an
/// ultrawide fix (Lyall on Codeberg, RoseTheFlower, p1xel8ted) and the HDR Gaming Database. The manifest's
/// <c>*UrlOverrides</c> come first, as upstream.
/// <para>Every list loads once in the background (<see cref="Prefetch"/>), is kept 24 h in RHI's cache folder and never
/// blocks a lookup: until a list has loaded, only the manifest answers for it. A failed load keeps the last good copy
/// and is retried after a pause. Every download goes through its own <see cref="DownloadSources"/> policy with a size
/// cap; parsing is text-only with time-limited patterns.</para>
/// <para>PCGamingWiki: the manifest's page, then upstream's <c>pcgw_data.json</c> (by name or Steam AppID), then pages
/// found earlier (cached), then a background lookup (<see cref="ResolvePcgwInBackground"/>) by Steam AppID — the
/// manifest's, the card's, a <c>steam_appid.txt</c> in the game folder, else Steam's store search — and PCGW's own
/// search. That lookup shares PCGW's request gate (one request a second, pause on 429/503) with the graphics-API lookup.</para>
/// </summary>
public sealed partial class GameLinkCatalogue
{
    public const string UltraPlusGamesUrl = "https://theultraplace.com/games/";
    public const string LyallReposUrl = "https://codeberg.org/api/v1/users/Lyall/repos?limit=50";
    public const string RoseReadmeUrl = "https://raw.githubusercontent.com/RoseTheFlower/UltrawideIndex/main/README.md";
    public const string P1xelReadmeUrl = "https://raw.githubusercontent.com/p1xel8ted/UltrawideFixes/main/README.md";
    public const string HdrDatabaseReadmeUrl = "https://raw.githubusercontent.com/KoKlusz/HDR-Gaming-Database/main/README.md";
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(24);
    /// <summary>How long "PCGamingWiki has no page for this game" is trusted before RHI asks again.</summary>
    public static readonly TimeSpan PcgwMissFreshness = TimeSpan.FromDays(7);
    private static readonly TimeSpan SteamSearchGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SteamSearchPause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SteamSearchTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly PcgwCentralDataService? _pcgwData;
    private readonly PcGamingWikiGraphicsApiService? _pcgwSearch;
    private readonly Func<CancellationToken, Task<UpstreamLibraryPolicySnapshot?>>? _policy;
    private readonly GameLinkList _ultraPlus;
    private readonly GameLinkList _lyall;
    private readonly GameLinkList _rose;
    private readonly GameLinkList _p1xel;
    private readonly GameLinkList _hdrDatabase;
    private readonly JsonFileStore<PcgwLinkCache> _pcgwStore;
    private readonly object _pcgwSync = new();
    private PcgwLinkCache? _pcgwCache;
    private readonly Dictionary<string, Task> _pcgwLookups = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _steamGate = new(1, 1);
    private long _steamPausedUntilTicks;

    public GameLinkCatalogue(
        HttpClient http,
        string? cacheDirectory = null,
        PcgwCentralDataService? pcgwData = null,
        PcGamingWikiGraphicsApiService? pcgwSearch = null,
        Func<CancellationToken, Task<UpstreamLibraryPolicySnapshot?>>? policy = null,
        TimeSpan? freshness = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _time = timeProvider ?? TimeProvider.System;
        _pcgwData = pcgwData;
        _pcgwSearch = pcgwSearch;
        _policy = policy;
        var directory = cacheDirectory ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "links");
        var fresh = freshness ?? DefaultFreshness;
        _ultraPlus = new("Ultra+", UltraPlusGamesUrl, DownloadSources.UltraPlusGameList, ParseUltraPlus, Path.Combine(directory, "ultraplus-games.json"), fresh, _time, http);
        _lyall = new("Lyall ultrawide fixes", LyallReposUrl, DownloadSources.UltrawideFixList, ParseLyall, Path.Combine(directory, "uwfix-lyall.json"), fresh, _time, http);
        _rose = new("RoseTheFlower ultrawide fixes", RoseReadmeUrl, DownloadSources.CommunityGameList, ParseRose, Path.Combine(directory, "uwfix-rose.json"), fresh, _time, http);
        _p1xel = new("p1xel8ted ultrawide fixes", P1xelReadmeUrl, DownloadSources.CommunityGameList, ParseP1xel, Path.Combine(directory, "uwfix-p1xel8ted.json"), fresh, _time, http);
        _hdrDatabase = new("HDR Gaming Database", HdrDatabaseReadmeUrl, DownloadSources.CommunityGameList, ParseHdrDatabase, Path.Combine(directory, "hdr-gaming-database.json"), fresh, _time, http,
            HdrDatabaseKey);
        _pcgwStore = new JsonFileStore<PcgwLinkCache>(Path.Combine(directory, "pcgw-links.json"), new JsonSerializerOptions { WriteIndented = false },
            JsonStoreKind.Cache, parse: text => JsonSerializer.Deserialize<PcgwLinkCache>(text) is { } parsed
                ? new PcgwLinkCache
                {
                    Version = parsed.Version,
                    Pages = JsonStoreMaps.CaseInsensitive(parsed.Pages),
                    Misses = JsonStoreMaps.CaseInsensitive(parsed.Misses),
                    AppIds = JsonStoreMaps.CaseInsensitive(parsed.AppIds)
                }
                : null);
    }

    /// <summary>Starts (or joins) every list's background load. Never blocks, never faults.</summary>
    public void Prefetch()
    {
        foreach (var list in Lists) list.Prefetch();
    }

    /// <summary>Every list's load (never faults). Tests await it; the app calls <see cref="Prefetch"/>.</summary>
    public Task LoadAsync() => Task.WhenAll(Lists.Select(list => list.LoadAsync()));

    private IEnumerable<GameLinkList> Lists => [_ultraPlus, _lyall, _rose, _p1xel, _hdrDatabase];

    /// <summary>True once the Ultra+ list (live or the last good copy) is loaded for this session.</summary>
    public bool UltraPlusLoaded => _ultraPlus.IsLoaded;

    /// <summary>Every game the Ultra+ list names (its own spelling), for the new-mods notice; empty until loaded.</summary>
    public IReadOnlyList<GameLinkEntry> UltraPlusGames => _ultraPlus.Entries;

    /// <summary>The game's Ultra+ page: the manifest's, else the Ultra+ list's, else null.</summary>
    public string? UltraPlusPageFor(DetectedGame? game)
        => game is null ? null : SafePage(game.UltraPlusUrlOverride) ?? _ultraPlus.Find(NexusGameCatalogue.NormalizeName(game.Name));

    /// <summary>The game's ultrawide fix: the manifest's, then Lyall's, RoseTheFlower's, p1xel8ted's (upstream's order).</summary>
    public string? UltrawideFixPageFor(DetectedGame? game)
    {
        if (game is null) return null;
        if (SafePage(game.UwFixUrlOverride) is { } manifest) return manifest;
        var key = NexusGameCatalogue.NormalizeName(game.Name);
        return _lyall.Find(key) ?? _rose.Find(key) ?? _p1xel.Find(key);
    }

    /// <summary>The game's HDR analysis thread on the HDR Gaming Database, else null.</summary>
    public string? HdrDatabasePageFor(DetectedGame? game) => game is null ? null : _hdrDatabase.Find(HdrDatabaseKey(game.Name));

    /// <summary>
    /// The game's PCGamingWiki page from what is known now (manifest, <c>pcgw_data.json</c>, pages found earlier), else
    /// null. Never asks the network: call <see cref="ResolvePcgwInBackground"/> to look a missing page up.
    /// </summary>
    public string? PcgwPageFor(DetectedGame? game)
    {
        if (game is null) return null;
        if (SafePage(game.PcgwUrlOverride) is { } manifest) return manifest;
        if (_pcgwData?.Current?.TryLookup(game.Name, SteamAppIdOf(game)) is { } central) return PcgwWikiUrl(central.PageTitle);
        var key = NexusGameCatalogue.NormalizeName(game.Name);
        if (key.Length == 0) return null;
        var cache = PcgwCache();
        lock (_pcgwSync) return cache.Pages.TryGetValue(key, out var page) ? page : null;
    }

    /// <summary>
    /// Looks the game's PCGamingWiki page up in the background when nothing knows it yet (never blocks; at most one lookup
    /// per game at a time; a definite "no page" is kept for <see cref="PcgwMissFreshness"/>).
    /// </summary>
    public void ResolvePcgwInBackground(DetectedGame? game) => _ = ResolvePcgwAsync(game);

    /// <summary>The background lookup itself (never faults). Tests await it.</summary>
    public Task ResolvePcgwAsync(DetectedGame? game)
    {
        if (game is null || PcgwPageFor(game) is not null) return Task.CompletedTask;
        var key = NexusGameCatalogue.NormalizeName(game.Name);
        if (key.Length == 0 || IsFreshMiss(key)) return Task.CompletedTask;
        var snapshot = new PcgwLookup(game.Name.Trim(), key, SteamAppIdOf(game), game.InstallPath, game.BinaryDirectory);
        lock (_pcgwSync)
        {
            if (_pcgwLookups.TryGetValue(key, out var running)) return running;
            var lookup = Task.Run(() => LookUpPcgwAsync(snapshot), CancellationToken.None);
            _pcgwLookups[key] = lookup;
            return lookup;
        }
    }

    private sealed record PcgwLookup(string Name, string Key, int? SteamAppId, string? InstallPath, string? BinaryDirectory);

    private async Task LookUpPcgwAsync(PcgwLookup game)
    {
        try
        {
            UpstreamLibraryPolicySnapshot? policy = null;
            try { policy = _policy is null ? null : await _policy(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Warn("game-links", "Manifest unavailable for the PCGamingWiki lookup; using its defaults", ex);
            }
            ApplyCacheVersion(policy?.PcgwUrlCacheVersion ?? 0);
            // pcgw_data.json covers most games: wait for its shared load (it never faults) before asking the network.
            var central = _pcgwData is null ? null : await _pcgwData.GetAsync().ConfigureAwait(false);
            if (central?.TryLookup(game.Name, game.SteamAppId) is { } byName)
            {
                Remember(cache => cache.Pages[game.Key] = PcgwWikiUrl(byName.PageTitle));
                return;
            }
            var appId = game.SteamAppId ?? CachedAppId(game.Key) ?? SteamAppIdFile(game.InstallPath) ?? SteamAppIdFile(game.BinaryDirectory)
                        ?? await SearchSteamStoreAsync(game.Name).ConfigureAwait(false);
            if (appId is { } found)
            {
                Remember(cache => cache.AppIds[game.Key] = found);
                if (central?.TryLookup(game.Name, found) is { } byAppId)
                {
                    Remember(cache => cache.Pages[game.Key] = PcgwWikiUrl(byAppId.PageTitle));
                    return;
                }
                if (policy?.PcgwUseAppId == true)
                {
                    Remember(cache => cache.Pages[game.Key] = PcgwAppIdUrl(found));
                    return;
                }
            }
            if (_pcgwSearch is null) return;
            var (page, missing) = await _pcgwSearch.SearchPageAsync(game.Name).ConfigureAwait(false);
            if (SafePage(page) is { } safe) Remember(cache => cache.Pages[game.Key] = safe);
            else if (missing) Remember(cache => cache.Misses[game.Key] = _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            // Never faults: the card menu simply has no PCGamingWiki entry for this game yet.
            AppLog.Warn("game-links", $"PCGamingWiki page lookup failed for {game.Name}", ex);
        }
        finally
        {
            lock (_pcgwSync) _pcgwLookups.Remove(game.Key);
        }
    }

    private static int? SteamAppIdOf(DetectedGame game) => game.ManifestSteamAppIdOverride ?? game.SteamAppId;

    /// <summary>PCGamingWiki's page URL for a page title (spaces become underscores, as upstream).</summary>
    public static string PcgwWikiUrl(string pageTitle)
        => "https://www.pcgamingwiki.com/wiki/" + pageTitle.Trim().Replace(' ', '_')
            .Replace("%", "%25", StringComparison.Ordinal).Replace("?", "%3F", StringComparison.Ordinal).Replace("#", "%23", StringComparison.Ordinal);

    /// <summary>PCGamingWiki's redirect from a Steam AppID to its page (used when the manifest says it works).</summary>
    public static string PcgwAppIdUrl(int appId) => $"https://www.pcgamingwiki.com/api/appid.php?appid={appId}";

    /// <summary>A link RHI may hand to the desktop opener: https, default port, no credentials; else null.</summary>
    public static string? SafePage(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Length > 0
            ? uri.AbsoluteUri
            : null;
    }

    // ── PCGamingWiki link cache ─────────────────────────────────────────────────────────────────────────────────

    private PcgwLinkCache PcgwCache()
    {
        lock (_pcgwSync)
        {
            if (_pcgwCache is not null) return _pcgwCache;
            try { _pcgwCache = _pcgwStore.Load().Value; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("game-links", "PCGamingWiki link cache unreadable; starting empty", ex);
            }
            return _pcgwCache ??= new PcgwLinkCache();
        }
    }

    private bool IsFreshMiss(string key)
    {
        var cache = PcgwCache();
        lock (_pcgwSync)
            return cache.Misses.TryGetValue(key, out var at) && _time.GetUtcNow() - at < PcgwMissFreshness;
    }

    private int? CachedAppId(string key)
    {
        var cache = PcgwCache();
        lock (_pcgwSync) return cache.AppIds.TryGetValue(key, out var id) && id > 0 ? id : null;
    }

    /// <summary>Upstream <c>CheckManifestCacheVersion</c>: a manifest asking for a newer cache version drops found pages and misses.</summary>
    private void ApplyCacheVersion(int version)
    {
        var cache = PcgwCache();
        lock (_pcgwSync)
        {
            if (version <= cache.Version) return;
            cache.Version = version;
            cache.Pages.Clear();
            cache.Misses.Clear();
        }
        Persist();
    }

    private void Remember(Action<PcgwLinkCache> change)
    {
        var cache = PcgwCache();
        lock (_pcgwSync) change(cache);
        Persist();
    }

    private void Persist()
    {
        PcgwLinkCache copy;
        var cache = PcgwCache();
        lock (_pcgwSync)
            copy = new PcgwLinkCache
            {
                Version = cache.Version,
                Pages = new(cache.Pages, StringComparer.OrdinalIgnoreCase),
                Misses = new(cache.Misses, StringComparer.OrdinalIgnoreCase),
                AppIds = new(cache.AppIds, StringComparer.OrdinalIgnoreCase)
            };
        try
        {
            if (Path.GetDirectoryName(_pcgwStore.FilePath) is { Length: > 0 } directory) AtomicFile.CreateDirectoryDurable(directory);
            lock (_pcgwStore) _pcgwStore.Save(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Optional: the pages stay known for this session.
            AppLog.Warn("game-links", $"PCGamingWiki link cache not saved: {_pcgwStore.FilePath}", ex);
        }
    }

    /// <summary>On disk: cache version, normalised name → page, → time of a definite miss, → Steam AppID.</summary>
    private sealed class PcgwLinkCache
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("pages")] public Dictionary<string, string> Pages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("misses")] public Dictionary<string, DateTimeOffset> Misses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("appIds")] public Dictionary<string, int> AppIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ── Steam AppID ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first line of <c>steam_appid.txt</c> in a folder, as a positive number; else null.</summary>
    public static int? SteamAppIdFile(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var path = Path.Combine(directory, "steam_appid.txt");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64) return null;
            var first = File.ReadLines(path).FirstOrDefault()?.Trim();
            return int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("game-links", $"steam_appid.txt unreadable: {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Upstream <c>SteamAppIdResolver.SearchSteamStoreAsync</c>: Steam's store search for the name, one request a second,
    /// the first result whose normalised name equals the game's. A 429 pauses searches; any failure is "not found".
    /// </summary>
    private async Task<int?> SearchSteamStoreAsync(string name)
    {
        if (_time.GetUtcNow().UtcTicks < Volatile.Read(ref _steamPausedUntilTicks)) return null;
        await _steamGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow().UtcTicks < Volatile.Read(ref _steamPausedUntilTicks)) return null;
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&l=english&cc=US";
            using var timeout = new CancellationTokenSource(SteamSearchTimeout);
            try
            {
                var raw = await TrustedDownload.GetBytesAsync(_http, url, DownloadSources.SteamStoreSearch, "application/json", timeout.Token).ConfigureAwait(false);
                return MatchSteamSearch(name, raw);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Volatile.Write(ref _steamPausedUntilTicks, (_time.GetUtcNow() + SteamSearchPause).UtcTicks);
                AppLog.Warn("game-links", "Steam store search rate-limited; paused", ex);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UntrustedDownloadException
                                           or TaskCanceledException or InvalidDataException)
            {
                AppLog.Warn("game-links", $"Steam store search failed for {name}", ex);
                return null;
            }
        }
        finally
        {
            await Task.Delay(SteamSearchGap, _time, CancellationToken.None).ConfigureAwait(false);
            _steamGate.Release();
        }
    }

    /// <summary>The AppID of the first store-search item whose normalised name equals <paramref name="name"/>'s.</summary>
    public static int? MatchSteamSearch(string name, ReadOnlySpan<byte> raw)
    {
        var wanted = NexusGameCatalogue.NormalizeName(name);
        if (wanted.Length == 0) return null;
        using var document = JsonDocument.Parse(raw.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var appId) || appId <= 0) continue;
            if (item.TryGetProperty("name", out var itemName) && itemName.ValueKind == JsonValueKind.String
                && NexusGameCatalogue.NormalizeName(itemName.GetString()) == wanted)
                return appId;
        }
        return null;
    }

    // ── List parsers (text only, bounded patterns) ──────────────────────────────────────────────────────────────

    /// <summary>Ultra+ game cards: <c>&lt;a href="/games/slug/" class="game-card"…&gt;…&lt;h3&gt;Name&lt;/h3&gt;</c>.</summary>
    public static IReadOnlyList<GameLinkEntry> ParseUltraPlus(string html)
    {
        var result = new List<GameLinkEntry>();
        foreach (Match match in UltraPlusCard().Matches(html))
        {
            var name = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            var path = match.Groups[1].Value.Trim();
            if (name.Length == 0 || path.Contains("..", StringComparison.Ordinal)) continue;
            if (SafePage("https://theultraplace.com" + path) is { } url) result.Add(new(name, url));
        }
        return result;
    }

    /// <summary>Lyall's Codeberg repositories with releases whose description names the game ("A fix for X that …").</summary>
    public static IReadOnlyList<GameLinkEntry> ParseLyall(string json)
    {
        var result = new List<GameLinkEntry>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Codeberg repository list is not an array");
        foreach (var repo in document.RootElement.EnumerateArray())
        {
            if (repo.ValueKind != JsonValueKind.Object) continue;
            var name = String(repo, "name");
            var description = String(repo, "description");
            var url = SafePage(String(repo, "html_url"));
            var hasReleases = repo.TryGetProperty("has_releases", out var releases) && releases.ValueKind == JsonValueKind.True;
            if (!hasReleases || url is null || string.IsNullOrEmpty(description)) continue;
            if (string.Equals(name, "UltrawidePatches", StringComparison.OrdinalIgnoreCase)) continue;
            if (!new Uri(url).Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase)) continue;
            var match = LyallDescription().Match(description);
            if (!match.Success) continue;
            var game = AlsoForGames().Replace(match.Groups[1].Value, "").Replace("**", "", StringComparison.Ordinal).Trim();
            if (game.Length > 0) result.Add(new(game, url));
        }
        return result;
    }

    /// <summary>RoseTheFlower's index: a game heading, then its release tag or its "Download page and instructions" link.</summary>
    public static IReadOnlyList<GameLinkEntry> ParseRose(string markdown)
    {
        const string releaseBase = "https://github.com/RoseTheFlower/UltrawideIndex/releases/tag/";
        var result = new List<GameLinkEntry>();
        string? game = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var header = RoseHeader().Match(line);
                game = header.Success ? header.Groups[1].Value.Trim() : null;
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var sub = line[3..].Trim();
                var header = RoseHeader().Match("# " + sub);
                game = header.Success ? header.Groups[1].Value.Trim() : sub;
                continue;
            }
            if (game is null) continue;
            var tag = RoseReleaseTag().Match(line);
            if (tag.Success)
            {
                if (SafePage(releaseBase + tag.Groups[1].Value) is { } page) result.Add(new(game, page));
                game = null;
                continue;
            }
            var download = RoseDownloadLink().Match(line);
            if (!download.Success) continue;
            var url = download.Groups[1].Value.Trim();
            if (url.StartsWith("/../../../", StringComparison.Ordinal))
                url = "https://github.com/RoseTheFlower/" + url["/../../../".Length..].Replace("#readme", "", StringComparison.Ordinal);
            else if (url.StartsWith("/../../", StringComparison.Ordinal))
                url = "https://github.com/RoseTheFlower/UltrawideIndex/" + url["/../../".Length..];
            if (SafePage(url) is { } link && !link.Contains("/../", StringComparison.Ordinal)) result.Add(new(game, link));
            game = null;
        }
        return result;
    }

    /// <summary>p1xel8ted's index: <c>## [Game](https://github.com/p1xel8ted/UltrawideFixes/releases/tag/…)</c>.</summary>
    public static IReadOnlyList<GameLinkEntry> ParseP1xel(string markdown)
    {
        var result = new List<GameLinkEntry>();
        foreach (Match match in P1xelHeader().Matches(markdown))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length > 0 && SafePage(match.Groups[2].Value) is { } url) result.Add(new(name, url));
        }
        return result;
    }

    /// <summary>The HDR Gaming Database README: <c>* [Game](https://github.com/KoKlusz/HDR-Gaming-Database/discussions/N)</c>.</summary>
    public static IReadOnlyList<GameLinkEntry> ParseHdrDatabase(string markdown)
    {
        var result = new List<GameLinkEntry>();
        foreach (var raw in markdown.Split('\n'))
        {
            var match = HdrDatabaseEntry().Match(raw);
            if (!match.Success) continue;
            var name = StraightQuotes(StripTrademarks(match.Groups[1].Value.Trim()));
            if (name.Length > 0) result.Add(new(name, match.Groups[2].Value.Trim()));
        }
        return result;
    }

    /// <summary>Upstream matches the HDR Gaming Database by name without ™/®/© and with straight quotes, ignoring case.</summary>
    private static string HdrDatabaseKey(string? name) => StraightQuotes(StripTrademarks(name ?? "")).ToLowerInvariant();

    private static string StripTrademarks(string name)
        => name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal).Trim();

    private static string StraightQuotes(string s)
        => s.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"');

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private const int PatternTimeoutMs = 2000;

    [GeneratedRegex(@"<a\s+href=""(/games/[^""]+)""\s+class=""game-card""[^>]*>.*?<h3>([^<]+)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline, PatternTimeoutMs)]
    private static partial Regex UltraPlusCard();

    [GeneratedRegex(@"(?:An ASI plugin|A fix|A BepInEx plugin) for (.+?) that ", RegexOptions.IgnoreCase, PatternTimeoutMs)]
    private static partial Regex LyallDescription();

    [GeneratedRegex(@"\s*\(and [^)]*\)", RegexOptions.IgnoreCase, PatternTimeoutMs)]
    private static partial Regex AlsoForGames();

    [GeneratedRegex(@"^#\s+(.+?)(?:\s+(?:ultrawide|widescreen|super ultrawide|multi-monitor|improvements|FOV|black bars|resolutions|better ultrawide|cutscenes|field of view))", RegexOptions.IgnoreCase, PatternTimeoutMs)]
    private static partial Regex RoseHeader();

    [GeneratedRegex(@"releases/tag/(\S+)\)", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex RoseReleaseTag();

    [GeneratedRegex(@"\[Download page and instructions\]\(([^)]+)\)", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex RoseDownloadLink();

    [GeneratedRegex(@"^##\s+\[([^\]]+)\]\((https://github\.com/p1xel8ted/UltrawideFixes/releases/tag/[^)]+)\)", RegexOptions.Multiline, PatternTimeoutMs)]
    private static partial Regex P1xelHeader();

    [GeneratedRegex(@"^\s*\*\s*\[([^\]]+)\]\((https://github\.com/KoKlusz/HDR-Gaming-Database/discussions/\d+)\)", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex HdrDatabaseEntry();
}

/// <summary>
/// One web list behind a card link (#183): downloaded under its own policy, parsed, kept 24 h in a cache file
/// (<see cref="JsonStoreKind.Cache"/>; the time of the download is recorded inside it), the last good copy reused when
/// the list cannot be fetched, retried after a pause when nothing is available. Lookups never wait for it.
/// </summary>
internal sealed class GameLinkList
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    private readonly string _name;
    private readonly string _url;
    private readonly TrustedDownloadPolicy _policy;
    private readonly Func<string, IReadOnlyList<GameLinkEntry>> _parse;
    private readonly Func<string?, string> _key;
    private readonly JsonFileStore<CacheFile> _store;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private Task? _load;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private volatile Loaded? _loaded;

    private sealed record Loaded(IReadOnlyList<GameLinkEntry> Entries, IReadOnlyDictionary<string, string> ByKey);

    public GameLinkList(string name, string url, TrustedDownloadPolicy policy, Func<string, IReadOnlyList<GameLinkEntry>> parse,
        string cachePath, TimeSpan freshness, TimeProvider time, HttpClient http, Func<string?, string>? key = null)
    {
        _name = name;
        _url = url;
        _policy = policy;
        _parse = parse;
        _key = key ?? NexusGameCatalogue.NormalizeName;
        _freshness = freshness;
        _time = time;
        _http = http;
        _store = new JsonFileStore<CacheFile>(cachePath, new JsonSerializerOptions { WriteIndented = false }, JsonStoreKind.Cache,
            parse: text => JsonSerializer.Deserialize<CacheFile>(text) is { Entries: not null } parsed ? parsed : null);
    }

    public bool IsLoaded => _loaded is not null;

    public IReadOnlyList<GameLinkEntry> Entries => _loaded?.Entries ?? [];

    /// <summary>The page for a key (as <c>key</c> computes it from a name; first entry wins, as upstream); null until loaded.</summary>
    public string? Find(string key)
        => key.Length > 0 && _loaded is { } loaded && loaded.ByKey.TryGetValue(key, out var url) ? url : null;

    public void Prefetch() => _ = LoadAsync();

    public Task LoadAsync()
    {
        lock (_sync)
        {
            if (_loaded is not null) return Task.CompletedTask;
            if (_load is null)
            {
                if (_time.GetUtcNow() < _retryAfter) return Task.CompletedTask;
                _load = Task.Run(LoadSharedAsync, CancellationToken.None);
            }
            return _load;
        }
    }

    private async Task LoadSharedAsync()
    {
        Loaded? loaded = null;
        try
        {
            using var timeout = new CancellationTokenSource(LoadTimeout);
            loaded = await LoadCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("game-links", $"{_name} list load failed", ex);
        }
        lock (_sync)
        {
            _load = null;
            if (loaded is not null) _loaded = loaded;
            else _retryAfter = _time.GetUtcNow() + FailureRetryDelay;
        }
    }

    private async Task<Loaded?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var cached = ReadCache();
        if (cached is not null && _time.GetUtcNow() - cached.FetchedAtUtc < _freshness && Index(cached.Entries!) is { } fresh) return fresh;
        try
        {
            var text = await TrustedDownload.GetStringAsync(_http, _url, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
            var entries = _parse(text);
            if (entries.Count > 0)
            {
                Persist(new CacheFile { FetchedAtUtc = _time.GetUtcNow(), Entries = [.. entries] });
                return Index(entries);
            }
            AppLog.Warn("game-links", $"{_name} list had no games; keeping the last good copy");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UntrustedDownloadException
                                       or TaskCanceledException or InvalidDataException or RegexMatchTimeoutException)
        {
            AppLog.Warn("game-links", $"{_name} list download failed; using the last good copy if there is one", ex);
        }
        return cached?.Entries is { Count: > 0 } stale ? Index(stale) : null;
    }

    private Loaded? Index(IReadOnlyList<GameLinkEntry> entries)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || GameLinkCatalogue.SafePage(entry.Url) is not { } url) continue;
            var key = _key(entry.Name);
            if (key.Length > 0) byKey.TryAdd(key, url);
        }
        return byKey.Count == 0 ? null : new Loaded(entries.Where(e => e is not null).ToArray(), byKey);
    }

    private CacheFile? ReadCache()
    {
        try { return _store.Load().Value; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("game-links", $"Cached {_name} list unreadable: {_store.FilePath}", ex);
            return null;
        }
    }

    private void Persist(CacheFile file)
    {
        try
        {
            if (Path.GetDirectoryName(_store.FilePath) is { Length: > 0 } directory) AtomicFile.CreateDirectoryDurable(directory);
            _store.Save(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("game-links", $"{_name} list not cached: {_store.FilePath}", ex);
        }
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("fetchedAtUtc")] public DateTimeOffset FetchedAtUtc { get; set; }
        [JsonPropertyName("entries")] public List<GameLinkEntry>? Entries { get; set; }
    }
}
