using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Xbox;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Hdr;

/// <summary>#251: whether a game can use HDR, as far as RHI's data knows.</summary>
public enum HdrSupportAnswer { Unknown, Yes, No }

/// <summary>#251: where an answer came from (worded in the section header's tooltip).</summary>
public enum HdrSupportSource
{
    /// <summary>Nothing decided it: no data, or the lookup has not finished.</summary>
    None,
    /// <summary>RHI's own curated list (<c>hdr-support-overrides.json</c>), which corrects a wrong answer for everyone.</summary>
    CuratedList,
    /// <summary>PCGamingWiki lists native HDR (<c>hdr = true</c>).</summary>
    PcGamingWiki,
    /// <summary>PCGamingWiki lists HDR as possible with a fix (<c>hdr = hackable</c>).</summary>
    PcGamingWikiHackable,
    /// <summary>The Microsoft Store product record the app that installed the game keeps lists HDR on PC.</summary>
    MicrosoftStore,
    /// <summary>PCGamingWiki has no answer for the game (no page, no field, <c>unknown</c>, <c>n/a</c>).</summary>
    PcGamingWikiUnknown,
    /// <summary>The lookup failed or could not run (offline, rate limit, timed out): nothing is known.</summary>
    Unchecked
}

/// <param name="Page">The PCGamingWiki page the answer was read from, when it came from there.</param>
public sealed record HdrSupportEvidence(HdrSupportAnswer Answer, HdrSupportSource Source, string? Page = null)
{
    /// <summary>Nothing known (no lookup yet, or no service).</summary>
    public static HdrSupportEvidence Unchecked { get; } = new(HdrSupportAnswer.Unknown, HdrSupportSource.Unchecked);
}

/// <summary>
/// #251 (owner decision: "safe, not sorry"): the HDR launch section shows for every game unless RHI's data says the game has
/// no HDR AND RHI installed no HDR mod on it (RenoDX, Luma Framework, DX9 HDR / DXVK HDR). Unknown, a failed lookup,
/// offline or a rate limit all show it.
/// </summary>
public static class HdrSupportRule
{
    /// <summary>The components that bring HDR to a game that has none of its own.</summary>
    public static IReadOnlySet<ManagedComponentKind> HdrModKinds { get; } = new HashSet<ManagedComponentKind>
    {
        ManagedComponentKind.RenoDX, ManagedComponentKind.RenoDXUpgrade, ManagedComponentKind.LumaFramework, ManagedComponentKind.DxvkHdr
    };

    /// <summary>The first HDR mod RHI installed among <paramref name="components"/> (an analysis's rows), or null.</summary>
    public static ManagedComponent? InstalledHdrMod(IEnumerable<ManagedComponent> components)
        => components.FirstOrDefault(c => HdrModKinds.Contains(c.Kind) && c.ManagedByRhi
                                          && c.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable);

    /// <param name="hdrModInstalled">Null while the game's components are not known yet (then shown).</param>
    public static bool ShowsHdrSection(HdrSupportEvidence? evidence, bool? hdrModInstalled)
        => evidence?.Answer != HdrSupportAnswer.No || hdrModInstalled != false;
}

/// <summary>
/// #251: the HDR support resolver. RHI's curated list (an embedded resource, keyed <c>steam:&lt;AppID&gt;</c>,
/// <c>heroic:&lt;app&gt;</c>, <c>xbox:&lt;StoreId&gt;</c> or <c>name:&lt;lower-case name&gt;</c>) wins; then PCGamingWiki's
/// <c>hdr</c> field (the page found as the graphics-API lookup finds it, wikitext read through <c>action=parse</c>), except
/// that — for a game the app that installed it keeps a Microsoft Store record for — that record's <c>CapabilityHDR</c>
/// attribute on PC turns a PCGW "false" or unknown into Yes (correction F3). Answers are cached under RHI's cache folder with the page name and fetch time,
/// read once per game and refreshed only after an explicit full Refresh (<see cref="MarkStale"/>); a failed lookup is
/// Unknown and never cached. Lookups run at most <see cref="MaxParallel"/> at a time, each one's work bounded in time.
/// </summary>
public sealed partial class HdrSupportService
{
    public const int MaxParallel = 2;
    public static readonly TimeSpan DefaultLookupTimeout = TimeSpan.FromSeconds(30);
    internal const string OverridesResource = "HdrSupportOverrides.json";

    private readonly PcGamingWikiGraphicsApiService? _pcgw;
    private readonly PcgwCentralDataService? _pcgwData;
    private readonly JsonFileStore<CacheFile> _cacheFile;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lookupTimeout;
    private readonly IReadOnlyDictionary<string, bool> _overrides;
    private readonly SemaphoreSlim _parallel = new(MaxParallel, MaxParallel);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, Lookup> _running = new(StringComparer.Ordinal);
    private CacheFile? _cache;
    private bool _cacheOnDisk;
    private long _staleBeforeUtcTicks;

    /// <param name="overridesJson">Test seam: the curated list's text (default: the embedded resource).</param>
    public HdrSupportService(
        PcGamingWikiGraphicsApiService? pcgw,
        PcgwCentralDataService? pcgwData = null,
        string? cachePath = null,
        string? overridesJson = null,
        TimeProvider? timeProvider = null,
        TimeSpan? lookupTimeout = null)
    {
        _pcgw = pcgw;
        _pcgwData = pcgwData;
        CachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "pcgw_hdr_cache.json");
        _cacheFile = new JsonFileStore<CacheFile>(CachePath, new JsonSerializerOptions { WriteIndented = false }, JsonStoreKind.Cache,
            parse: text => JsonSerializer.Deserialize<CacheFile>(text) is { } parsed
                ? new CacheFile { Games = JsonStoreMaps.CaseInsensitive(parsed.Games) }
                : null);
        _time = timeProvider ?? TimeProvider.System;
        _lookupTimeout = lookupTimeout ?? DefaultLookupTimeout;
        _overrides = ParseOverrides(overridesJson ?? ReadEmbeddedOverrides());
    }

    public string CachePath { get; }

    /// <summary>
    /// An explicit full Refresh: every cached answer is looked up again the next time its game is selected; until then
    /// (and when that lookup fails) the earlier answer stands.
    /// </summary>
    public void MarkStale() => Volatile.Write(ref _staleBeforeUtcTicks, _time.GetUtcNow().UtcTicks + 1);

    /// <summary>The answer known without any lookup (curated list, or a cached answer still current); null otherwise.</summary>
    public HdrSupportEvidence? Peek(DetectedGame game)
    {
        if (Curated(game) is { } curated) return curated;
        ForgetIfPurged();
        var cache = _cache;
        if (cache is null) return null;
        lock (cache)
            return cache.Games.TryGetValue(LookupKey(game), out var entry) && !IsStale(entry) ? entry.ToEvidence() : null;
    }

    /// <summary>
    /// The game's answer; never throws, a failure is Unknown. The lookup's own work is bounded by the lookup timeout; it
    /// may first wait for at most <see cref="MaxParallel"/> earlier lookups (correction F6: callers cancel the lookups of
    /// games the user has left, so the queue holds only what is still wanted). A caller that cancels gets Unknown, never
    /// cached.
    /// </summary>
    public Task<HdrSupportEvidence> ResolveAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Curated(game) is { } curated) return Task.FromResult(curated);
        var key = LookupKey(game);
        lock (_running)
        {
            // A running lookup is shared, unless its caller has left (it ends with Unknown): then this caller asks anew.
            if (_running.TryGetValue(key, out var running) && !running.Token.IsCancellationRequested) return running.Task;
            var entry = new Lookup(cancellationToken);
            entry.Task = Task.Run(async () =>
            {
                try { return await ResolveUncachedAsync(game, key, cancellationToken).ConfigureAwait(false); }
                finally
                {
                    // Correction F7 (R1): the lookup leaves the list before its task completes, so a finished lookup is
                    // never handed out again — a Refresh (MarkStale) always asks anew, with no queued clean-up to race.
                    lock (_running)
                        if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _running.Remove(key);
                }
            }, CancellationToken.None);
            _running[key] = entry;
            return entry.Task;
        }
    }

    private sealed class Lookup(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public Task<HdrSupportEvidence> Task { get; set; } = null!;
    }

    private async Task<HdrSupportEvidence> ResolveUncachedAsync(DetectedGame game, string key, CancellationToken cancellationToken)
    {
        CacheEntry? earlier = null;
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            lock (cache) cache.Games.TryGetValue(key, out earlier);
            if (earlier is not null && !IsStale(earlier)) return earlier.ToEvidence();

            await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(_lookupTimeout);
                var (found, decisive) = await LookUpAsync(game, bounded.Token).ConfigureAwait(false);
                if (found is null)
                    return earlier?.ToEvidence() ?? StoreFlag(game) ?? HdrSupportEvidence.Unchecked;
                // Correction F3 (coordinator, "safe, not sorry"): a Store yes on PC — the publisher's own listing for that build —
                // beats a PCGW "false" or unknown; only a PCGW yes needs no Store look. The curated list stays first (above).
                var evidence = found.Answer != HdrSupportAnswer.Yes && StoreFlag(game) is { } store ? store : found;
                if (decisive) await SaveAsync(key, evidence, cancellationToken).ConfigureAwait(false);
                return evidence;
            }
            finally { _parallel.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            if (!cancellationToken.IsCancellationRequested)
                AppLog.Warn("hdr-support", $"HDR support lookup for {game.Name} did not finish; the HDR section stays shown", ex);
            return earlier?.ToEvidence() ?? StoreFlag(game) ?? HdrSupportEvidence.Unchecked;
        }
    }

    /// <summary>
    /// PCGamingWiki's answer: (evidence, true) when PCGW answered (Yes, No, or a definite Unknown: no page, no field,
    /// <c>unknown</c>, <c>n/a</c>), (null, false) when it could not be asked now.
    /// </summary>
    private async Task<(HdrSupportEvidence? Evidence, bool Decisive)> LookUpAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        if (_pcgw is null) return (null, false);
        var steamAppId = game.SteamAppId ?? game.ManifestSteamAppIdOverride;
        var title = string.IsNullOrWhiteSpace(game.PcgwUrlOverride)
            ? _pcgwData?.Current?.TryLookup(game.Name, steamAppId)?.PageTitle
              ?? (string.IsNullOrWhiteSpace(game.CanonicalManifestName) ? null : _pcgwData?.Current?.TryLookup(game.CanonicalManifestName, null)?.PageTitle)
            : null;
        if (title is null)
        {
            var (resolved, missing) = await _pcgw.ResolvePageTitleAsync(game.CanonicalManifestName ?? game.Name, steamAppId, game.PcgwUrlOverride, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is null)
                return missing ? (new HdrSupportEvidence(HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown), true) : (null, false);
            title = resolved;
        }
        var (wikitext, pageMissing) = await _pcgw.FetchWikitextAsync(title, cancellationToken).ConfigureAwait(false);
        if (wikitext is null)
            return pageMissing ? (new HdrSupportEvidence(HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown, title), true) : (null, false);
        return (FromWikitext(wikitext, title), true);
    }

    /// <summary>Maps a PCGW page's <c>hdr</c> field: true/hackable/limited/always on → Yes, false → No, anything else → Unknown.</summary>
    internal static HdrSupportEvidence FromWikitext(string wikitext, string? page)
    {
        var match = HdrFieldRegex().Match(wikitext);
        // The value's words only: a reference or template right after it ("true<ref>…") is not part of it.
        var value = match.Success ? Regex.Match(match.Groups[1].Value.Trim().ToLowerInvariant(), "^[a-z/ ]*").Value.Trim() : "";
        return value switch
        {
            "true" or "always on" or "limited" => new(HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWiki, page),
            "hackable" => new(HdrSupportAnswer.Yes, HdrSupportSource.PcGamingWikiHackable, page),
            "false" => new(HdrSupportAnswer.No, HdrSupportSource.PcGamingWiki, page),
            _ => new(HdrSupportAnswer.Unknown, HdrSupportSource.PcGamingWikiUnknown, page)
        };
    }

    /// <summary>
    /// The Microsoft Store record the app that installed an Xbox game keeps in its instance's state folder
    /// (<c>catalogue/products/&lt;StoreId&gt;.json</c>, the file RHI reads the card's name from — correction F4), read-only:
    /// Yes when its <c>CapabilityHDR</c> attribute applies to PC; null otherwise (the attribute's absence says nothing: many
    /// HDR games lack it).
    /// </summary>
    internal static HdrSupportEvidence? StoreFlag(DetectedGame game)
    {
        if (game.XboxPackage is not { StoreId: { Length: > 0 } storeId, StateDir: { Length: > 0 } stateDir }) return null;
        return XboxInstallerInstance.ReadCatalogueProduct(stateDir, storeId, root =>
        {
            if (!root.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty("Attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (attribute.ValueKind != JsonValueKind.Object || !attribute.TryGetProperty("Name", out var name)
                    || name.ValueKind != JsonValueKind.String || name.GetString() != "CapabilityHDR")
                    continue;
                var onPc = !attribute.TryGetProperty("ApplicablePlatforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array
                           || platforms.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "Desktop");
                if (onPc) return new HdrSupportEvidence(HdrSupportAnswer.Yes, HdrSupportSource.MicrosoftStore);
            }
            return null;
        }, "its HDR flag is not used");
    }

    private HdrSupportEvidence? Curated(DetectedGame game)
    {
        foreach (var key in CuratedKeys(game))
            if (_overrides.TryGetValue(key, out var hdr))
                return new HdrSupportEvidence(hdr ? HdrSupportAnswer.Yes : HdrSupportAnswer.No, HdrSupportSource.CuratedList);
        return null;
    }

    internal static IEnumerable<string> CuratedKeys(DetectedGame game)
    {
        if ((game.SteamAppId ?? game.ManifestSteamAppIdOverride) is int appId and > 0) yield return "steam:" + appId;
        if (!string.IsNullOrWhiteSpace(game.HeroicAppName)) yield return "heroic:" + game.HeroicAppName;
        if (game.XboxPackage?.StoreId is { Length: > 0 } storeId) yield return "xbox:" + storeId;
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName)) yield return "name:" + game.CanonicalManifestName.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(game.Name)) yield return "name:" + game.Name.Trim().ToLowerInvariant();
    }

    /// <summary>The cache key: what the lookup is made from (the page URL, the Steam AppID, else the name).</summary>
    internal static string LookupKey(DetectedGame game)
        => !string.IsNullOrWhiteSpace(game.PcgwUrlOverride) ? "url:" + game.PcgwUrlOverride.Trim()
            : (game.SteamAppId ?? game.ManifestSteamAppIdOverride) is int appId and > 0 ? "steam:" + appId
            : "name:" + (game.CanonicalManifestName ?? game.Name ?? "").Trim().ToLowerInvariant();

    internal static IReadOnlyDictionary<string, bool> ParseOverrides(string? json)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var file = JsonSerializer.Deserialize<OverridesFile>(json);
            foreach (var (key, entry) in file?.Games ?? [])
                if (entry?.Hdr is bool hdr && !string.IsNullOrWhiteSpace(key)) result[key.Trim()] = hdr;
        }
        catch (JsonException ex)
        {
            AppLog.Warn("hdr-support", "The curated HDR support list is unreadable; PCGamingWiki answers alone", ex);
        }
        return result;
    }

    private static string? ReadEmbeddedOverrides()
    {
        using var stream = typeof(HdrSupportService).Assembly.GetManifestResourceStream(OverridesResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private bool IsStale(CacheEntry entry) => entry.FetchedAt.UtcTicks < Volatile.Read(ref _staleBeforeUtcTicks);

    /// <summary>
    /// Correction N1: Settings → Clear cache deletes the cache file, and the answers held in memory go with it (otherwise
    /// they would be used, and written back by the next save). A file that could never be saved is not a purge.
    /// </summary>
    private void ForgetIfPurged()
    {
        if (_cache is null || !Volatile.Read(ref _cacheOnDisk) || File.Exists(CachePath)) return;
        _cacheGate.Wait();
        try
        {
            if (_cache is null || !_cacheOnDisk || File.Exists(CachePath)) return;
            _cache = null;
            _cacheOnDisk = false;
        }
        finally { _cacheGate.Release(); }
    }

    private async Task<CacheFile> LoadCacheAsync(CancellationToken cancellationToken)
    {
        ForgetIfPurged();
        if (_cache is { } loaded) return loaded;
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is { } again) return again;
            var load = _cacheFile.Load();
            Volatile.Write(ref _cacheOnDisk, load.Value is not null);
            return _cache = load.Value ?? new CacheFile();
        }
        finally { _cacheGate.Release(); }
    }

    /// <summary>Test seam: runs after the save loaded the cache and before it takes the cache gate (re-review n1).</summary>
    internal Action? BeforeSaveGateForTesting { get; set; }

    private async Task SaveAsync(string key, HdrSupportEvidence evidence, CancellationToken cancellationToken)
    {
        await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        BeforeSaveGateForTesting?.Invoke();
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // w12 follow-up (re-review hdr-wsi n1): a Clear cache between the load above and this gate purged the answers; the
            // purge is asked again here and the save writes into the cache as it is now, never the pre-purge copy.
            if (_cache is not null && _cacheOnDisk && !File.Exists(CachePath))
            {
                _cache = null;
                _cacheOnDisk = false;
            }
            var cache = _cache ??= _cacheFile.Load().Value ?? new CacheFile();
            lock (cache)
            {
                cache.Games[key] = new CacheEntry { Answer = evidence.Answer, Source = evidence.Source, Page = evidence.Page, FetchedAt = _time.GetUtcNow() };
                _cacheFile.Save(cache);
            }
            Volatile.Write(ref _cacheOnDisk, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("hdr-support", $"HDR support cache not saved: {CachePath}", ex); // the in-memory copy still serves
        }
        finally { _cacheGate.Release(); }
    }

    private sealed class CacheFile
    {
        public Dictionary<string, CacheEntry> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheEntry
    {
        [JsonConverter(typeof(JsonStringEnumConverter))] public HdrSupportAnswer Answer { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))] public HdrSupportSource Source { get; set; }
        public string? Page { get; set; }
        public DateTimeOffset FetchedAt { get; set; }

        public HdrSupportEvidence ToEvidence() => new(Answer, Source, Page);
    }

    private sealed class OverridesFile
    {
        [JsonPropertyName("games")] public Dictionary<string, OverrideEntry?>? Games { get; set; }
    }

    private sealed class OverrideEntry
    {
        [JsonPropertyName("hdr")] public bool? Hdr { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    // The Video settings infobox row: "|hdr = true" (the "|hdr notes" row is a different parameter).
    [GeneratedRegex(@"^\s*\|\s*hdr\s*=[ \t]*([^\r\n|}]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HdrFieldRegex();
}
