using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Artwork;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Achievements;

/// <summary>One achievement as RHI shows it (upstream <c>Achievement</c>).</summary>
public sealed record Achievement(string ApiName, string Name, string Description, bool Hidden, bool Unlocked, DateTime? UnlockedUtc, string? Icon, string? IconGray)
{
    /// <summary>Percentage of all players who have it, or null when unknown.</summary>
    public double? Percent { get; init; }

    /// <summary>
    /// Shown as "Hidden achievement" without details: a locked hidden one, unless the user chose to see hidden details;
    /// still masked when the store gave no name for it (upstream).
    /// </summary>
    public bool IsMasked(bool showHidden) => Hidden && !Unlocked && (!showHidden || string.IsNullOrWhiteSpace(Name));
}

/// <summary>A game's achievements: unlocked first (newest first), then locked in the game's order (upstream).</summary>
public sealed record AchievementSet(int AppId, IReadOnlyList<Achievement> Items)
{
    public int Total => Items.Count;
    public int Unlocked => Items.Count(a => a.Unlocked);
    /// <summary>Whole percent, rounded down like Steam's.</summary>
    public int PercentDone => Total == 0 ? 0 : (int)Math.Floor(Unlocked * 100.0 / Total);
    public bool HasRarity => Items.Any(a => a.Percent.HasValue);
    /// <summary>The newest unlock, or null.</summary>
    public Achievement? Latest => Items.FirstOrDefault(a => a.Unlocked);
}

/// <summary>
/// w14/layouts-b (#307, upstream 2.9.2/2.9.3 <c>SteamAchievementService</c>): the selected Steam game's achievements, read
/// from Steam's own files on this PC (no sign-in, API key or public profile):
/// <c>appcache/stats/UserGameStatsSchema_{appid}.bin</c> (names, descriptions, icons, hidden flag) and
/// <c>UserGameStats_{account}_{appid}.bin</c> (unlocked bits and times), both binary KeyValues (<see cref="SteamBinaryKv"/>),
/// under native or Flatpak Steam (<see cref="SteamLocalFiles"/>). Rarity is Steam's public, keyless
/// <c>GetGlobalAchievementPercentagesForApp</c> (as upstream; <see cref="DownloadSources.SteamWebApi"/>), cached a day. Icons
/// come from Steam's library cache when it has them, else Steam's CDN (<see cref="DownloadSources.SteamArtwork"/>), cached.
/// Used only while Settings › Achievements › Steam is on.
/// </summary>
public sealed class SteamAchievementService
{
    private static readonly TimeSpan RarityMaxAge = TimeSpan.FromDays(1);
    // Newer games use 40-character hash names; older Valve games readable ones. Plain names only (a path and a URL).
    private static readonly Regex IconName = new(@"^[A-Za-z0-9_-]{1,100}\.(jpg|jpeg|png)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SteamLocalFiles _steam;
    private readonly HttpClient? _http;
    private readonly string _cacheRoot;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, Task<string?>> _iconsInFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task<IReadOnlyDictionary<string, double>?>> _rarityInFlight = new();

    public SteamAchievementService(SteamLocalFiles? steam = null, HttpClient? http = null, string? cacheRoot = null, Func<DateTime>? utcNow = null)
    {
        _steam = steam ?? new SteamLocalFiles();
        _http = http;
        _cacheRoot = cacheRoot ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "achievements");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The game's achievements from Steam's files (with any rarity already known), or null when Steam has no readable schema
    /// for it (not played on this PC, or no achievements). File reads: call it off the UI thread.
    /// </summary>
    public AchievementSet? Load(int appId)
    {
        if (_steam.RootWithSchema(appId) is not { } root) return null;
        try
        {
            var schema = SteamBinaryKv.Parse(File.ReadAllBytes(SteamLocalFiles.SchemaPath(root, appId)));
            var statsPath = FindStatsFile(root, appId);
            var stats = statsPath is null ? null : SteamBinaryKv.Parse(File.ReadAllBytes(statsPath));
            var set = Build(appId, schema, stats, _steam.Language());
            return set is null ? null : WithRarity(set, PeekRarity(appId));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("achievements", $"Steam's achievement files for {appId} unreadable", ex);
            return null;
        }
    }

    /// <summary>When Steam last wrote the game's achievement files (the live refresh compares it), or null.</summary>
    public DateTime? LastChange(int appId)
    {
        if (_steam.RootWithSchema(appId) is not { } root) return null;
        var schema = File.GetLastWriteTimeUtc(SteamLocalFiles.SchemaPath(root, appId));
        return FindStatsFile(root, appId) is { } stats && File.GetLastWriteTimeUtc(stats) > schema ? File.GetLastWriteTimeUtc(stats) : schema;
    }

    /// <summary>The signed-in account's stats file; the newest one for the game when the account is not known (upstream).</summary>
    private string? FindStatsFile(string root, int appId)
    {
        var dir = SteamLocalFiles.StatsDir(root);
        if (_steam.AccountId(root) is uint account)
        {
            var path = Path.Combine(dir, $"UserGameStats_{account}_{appId}.bin");
            return File.Exists(path) ? path : null;
        }
        return Directory.EnumerateFiles(dir, $"UserGameStats_*_{appId}.bin").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>
    /// The list from parsed schema and stats trees (upstream <c>Build</c>). Schema: {appid} → stats → {block} → bits → {bit} →
    /// name, display → name / desc (per language), hidden, icon, icon_gray. Stats: cache → {block} → data (the unlocked
    /// bitfield), AchievementTimes → {bit} = unix time. Null when there is none.
    /// </summary>
    public static AchievementSet? Build(int appId, Dictionary<string, object> schema, Dictionary<string, object>? stats, string language)
    {
        var root = schema.TryGetValue(appId.ToString(CultureInfo.InvariantCulture), out var r) && r is Dictionary<string, object> rd
            ? rd
            : schema.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
        if (root is null || Child(root, "stats") is not { } blocks) return null;
        var cache = stats is null ? null : Child(stats, "cache");

        var unlocked = new List<(Achievement A, long Order)>();
        var locked = new List<(Achievement A, long Order)>();
        foreach (var (blockKey, blockObj) in blocks.OrderBy(kv => NumKey(kv.Key)))
        {
            if (blockObj is not Dictionary<string, object> block || Child(block, "bits") is not { } bits) continue;
            var userBlock = cache is null ? null : Child(cache, blockKey);
            var mask = userBlock is not null && userBlock.TryGetValue("data", out var d) ? ToMask(d) : 0;
            var times = userBlock is null ? null : Child(userBlock, "AchievementTimes");
            foreach (var (bitKey, bitObj) in bits.OrderBy(kv => NumKey(kv.Key)))
            {
                if (bitObj is not Dictionary<string, object> bit) continue;
                if (!int.TryParse(bitKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitIndex) || bitIndex is < 0 or > 63) continue;
                var display = Child(bit, "display");
                var apiName = bit.TryGetValue("name", out var n) && n is string ns ? ns : $"{blockKey}_{bitKey}";
                var name = Localised(display, "name", language) ?? apiName;
                var desc = Localised(display, "desc", language) ?? "";
                var hidden = display is not null && display.TryGetValue("hidden", out var h) && ToLong(h) != 0;
                var isUnlocked = (mask & (1UL << bitIndex)) != 0;
                DateTime? when = null;
                if (isUnlocked && times is not null && times.TryGetValue(bitKey, out var t) && ToLong(t) > 0)
                    when = DateTimeOffset.FromUnixTimeSeconds(ToLong(t)).UtcDateTime;
                var a = new Achievement(apiName, name, desc, hidden, isUnlocked, when, IconFile(display, "icon"), IconFile(display, "icon_gray"));
                (isUnlocked ? unlocked : locked).Add((a, NumKey(blockKey) * 64 + bitIndex));
            }
        }
        if (unlocked.Count + locked.Count == 0) return null;
        var items = unlocked.OrderByDescending(x => x.A.UnlockedUtc ?? DateTime.MinValue).ThenBy(x => x.Order)
            .Concat(locked.OrderBy(x => x.Order)).Select(x => x.A).ToList();
        return new AchievementSet(appId, items);
    }

    private static Dictionary<string, object>? Child(Dictionary<string, object> node, string key)
        => node.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    private static long NumKey(string key) => long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : long.MaxValue / 128;

    private static long ToLong(object v) => v switch
    {
        int i => i,
        long l => l,
        ulong u => (long)u,
        float f => (long)f,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => 0,
    };

    // The bitfield is a signed int32: keep its bits, never sign-extended into the upper half (upstream).
    private static ulong ToMask(object v) => v switch
    {
        int i => (uint)i,
        ulong u => u,
        long l => (ulong)l,
        _ => (ulong)ToLong(v),
    };

    /// <summary>A display field in Steam's language, English, or any language (some games store a plain string).</summary>
    private static string? Localised(Dictionary<string, object>? display, string field, string language)
    {
        if (display is null || !display.TryGetValue(field, out var v)) return null;
        if (v is string s) return s.Length > 0 ? s : null;
        if (v is not Dictionary<string, object> langs) return null;
        foreach (var key in new[] { language, "english" })
            if (langs.TryGetValue(key, out var x) && x is string xs && xs.Length > 0) return xs;
        return langs.Where(kv => !string.Equals(kv.Key, "token", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).OfType<string>().FirstOrDefault(x => x.Length > 0);
    }

    private static string? IconFile(Dictionary<string, object>? display, string field)
        => display is not null && display.TryGetValue(field, out var v) && v is string s && IconName.IsMatch(s) ? s : null;

    // ---- rarity ----------------------------------------------------------------------------------------------------

    /// <summary>Steam's keyless global percentages link (the one upstream calls).</summary>
    public static string RarityUrl(int appId)
        => $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?gameid={appId}";

    private string RarityPath(int appId) => Path.Combine(_cacheRoot, appId.ToString(CultureInfo.InvariantCulture), "rarity.json");

    /// <summary>Adds rarity to a set; the same set when there is none.</summary>
    public static AchievementSet WithRarity(AchievementSet set, IReadOnlyDictionary<string, double>? rarity)
        => rarity is null || rarity.Count == 0 ? set
            : set with { Items = set.Items.Select(a => rarity.TryGetValue(a.ApiName, out var p) ? a with { Percent = p } : a).ToList() };

    /// <summary>The rarity already on disk and fresh (no download).</summary>
    public IReadOnlyDictionary<string, double>? PeekRarity(int appId) => ReadRarityFile(appId, RarityMaxAge);

    /// <summary>The game's global unlock percentages (downloaded at most once a day); an older copy when offline; else null.</summary>
    public Task<IReadOnlyDictionary<string, double>?> GetRarityAsync(int appId, CancellationToken cancellationToken)
    {
        if (PeekRarity(appId) is { } known) return Task.FromResult<IReadOnlyDictionary<string, double>?>(known);
        if (_http is null) return Task.FromResult(ReadRarityFile(appId, null));
        return _rarityInFlight.GetOrAdd(appId, id => Task.Run(async () =>
        {
            try { return await FetchRarityAsync(id, cancellationToken).ConfigureAwait(false); }
            finally { _rarityInFlight.TryRemove(id, out Task<IReadOnlyDictionary<string, double>?>? _); }
        }, CancellationToken.None));
    }

    private async Task<IReadOnlyDictionary<string, double>?> FetchRarityAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await TrustedDownload.GetStringAsync(_http!, RarityUrl(appId), DownloadSources.SteamWebApi, "application/json", cancellationToken).ConfigureAwait(false);
            if (ParseRarity(json) is not { } map) return ReadRarityFile(appId, null);
            try
            {
                AtomicFile.WriteStoreFile(RarityPath(appId), Encoding.UTF8.GetBytes(json));
                File.SetLastWriteTimeUtc(RarityPath(appId), _utcNow());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("achievements", $"Rarity for {appId} not cached", ex); }
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or UntrustedDownloadException)
        {
            AppLog.Warn("achievements", $"Rarity for {appId} not read", ex);
        }
        catch (OperationCanceledException) { return null; }
        return ReadRarityFile(appId, null); // offline: an older copy is better than nothing (upstream)
    }

    private IReadOnlyDictionary<string, double>? ReadRarityFile(int appId, TimeSpan? maxAge)
    {
        try
        {
            var path = RarityPath(appId);
            if (!File.Exists(path)) return null;
            if (maxAge is { } age && _utcNow() - File.GetLastWriteTimeUtc(path) >= age) return null;
            return ParseRarity(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>GetGlobalAchievementPercentagesForApp's JSON: API name → percent (a string or a number, clamped 0–100).</summary>
    public static IReadOnlyDictionary<string, double>? ParseRarity(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("achievementpercentages", out var ap)
                || !ap.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var a in list.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name || !a.TryGetProperty("percent", out var p)) continue;
                double value;
                if (p.ValueKind == JsonValueKind.Number) value = p.GetDouble();
                else if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) value = s;
                else continue;
                map[name] = Math.Clamp(value, 0, 100);
            }
            return map;
        }
        catch (JsonException) { return null; }
    }

    // ---- icons -----------------------------------------------------------------------------------------------------

    private string IconPath(int appId, string file) => Path.Combine(_cacheRoot, appId.ToString(CultureInfo.InvariantCulture), "icons", file);

    /// <summary>The icon on disk (Steam's library cache, else RHI's cache), or null.</summary>
    public string? TryGetCachedIcon(int appId, string? file)
    {
        if (file is null || !IconName.IsMatch(file)) return null;
        if (_steam.LibraryCacheFile(appId, file) is { } steams) return steams;
        var path = IconPath(appId, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The icon file, downloading it once from Steam's CDN when no copy is on disk. Null on failure.</summary>
    public Task<string?> GetIconAsync(int appId, string? file, CancellationToken cancellationToken)
    {
        if (file is null || !IconName.IsMatch(file)) return Task.FromResult<string?>(null);
        if (TryGetCachedIcon(appId, file) is { } cached) return Task.FromResult<string?>(cached);
        if (_http is null) return Task.FromResult<string?>(null);
        var key = $"{appId}/{file}";
        return _iconsInFlight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try { return await DownloadIconAsync(appId, file, cancellationToken).ConfigureAwait(false); }
            finally { _iconsInFlight.TryRemove(key, out Task<string?>? _); }
        }, CancellationToken.None));
    }

    /// <summary>Where an icon is on Steam's CDN (upstream's two links).</summary>
    public static IReadOnlyList<string> IconLinks(int appId, string file) =>
    [
        $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{file}",
        $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{appId}/{file}",
    ];

    private async Task<string?> DownloadIconAsync(int appId, string file, CancellationToken cancellationToken)
    {
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            var dest = IconPath(appId, file);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            foreach (var url in IconLinks(appId, file))
            {
                try
                {
                    using var download = await TrustedDownload.DownloadToTempAsync(_http!, url, dest, DownloadSources.SteamArtwork,
                        cancellationToken: cancellationToken, onResponse: SteamArtworkService.RequireImage).ConfigureAwait(false);
                    if (download.Length < 100 || !SteamArtworkService.LooksLikeImage(download.Path)) continue;
                    download.MoveTo(dest);
                    return dest;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or UntrustedDownloadException)
                {
                    AppLog.Warn("achievements", $"Icon {url} not downloaded", ex);
                }
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Valve's binary KeyValues (appcache/stats files; upstream <c>SteamBinaryKv</c>). Types: 0 subtree, 1 string,
/// 2/4/6 int32, 3 float, 7 uint64, 8/11 end of subtree. Keys case-insensitive. Throws <see cref="FormatException"/> on bad
/// data; nesting is limited so a damaged file cannot recurse without end.
/// </summary>
public static class SteamBinaryKv
{
    private const int MaxDepth = 32;

    public static Dictionary<string, object> Parse(byte[] data)
    {
        var pos = 0;
        return ReadNode(data, ref pos, 0);
    }

    private static Dictionary<string, object> ReadNode(byte[] b, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("KeyValues nested too deeply");
        var node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (pos >= b.Length) return node; // a file may end without the last end marker
            var type = b[pos++];
            if (type is 8 or 11) return node;
            var key = ReadString(b, ref pos);
            node[key] = type switch
            {
                0 => ReadNode(b, ref pos, depth + 1),
                1 => ReadString(b, ref pos),
                2 or 4 or 6 => (object)Read(b, ref pos, 4, BitConverter.ToInt32),
                3 => Read(b, ref pos, 4, BitConverter.ToSingle),
                7 => Read(b, ref pos, 8, BitConverter.ToUInt64),
                _ => throw new FormatException($"Unknown KeyValues type {type} at {pos - 1}"),
            };
        }
    }

    private static string ReadString(byte[] b, ref int pos)
    {
        var end = Array.IndexOf(b, (byte)0, pos);
        if (end < 0) throw new FormatException("Unterminated KeyValues string");
        var s = Encoding.UTF8.GetString(b, pos, end - pos);
        pos = end + 1;
        return s;
    }

    private static T Read<T>(byte[] b, ref int pos, int size, Func<byte[], int, T> read)
    {
        if (pos + size > b.Length) throw new FormatException("Truncated KeyValues value");
        var v = read(b, pos);
        pos += size;
        return v;
    }
}
