using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// Which games have a Nexus Mods page (#174). Opening a game page needs no account, so the card menu offers it for
/// every game Nexus has a page for, found in this order: the RHI manifest's <c>nexusUrlOverrides</c> (applied by the
/// caller), RHI's built-in table of store names Nexus spells differently (Steam's "The Elder Scrolls V: Skyrim Special
/// Edition" is Nexus's "Skyrim Special Edition"), then Nexus's public game list (<see cref="GamesListUrl"/>, as
/// upstream RHI reads it: one ~2 MB file, kept 24 h under the cache folder) by normalised name.
/// <para>The list loads once in the background (<see cref="Prefetch"/>) and never blocks a lookup: until it has
/// loaded, only the manifest and the built-in table answer. A failed load keeps the last good copy on disk and is
/// retried after a pause.</para>
/// </summary>
public sealed partial class NexusGameCatalogue
{
    public const string GamesListUrl = "https://data.nexusmods.com/file/nexus-data/games.json";
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Steam app IDs whose store name does not match Nexus's game name and is not unique by name alone
    /// (the 2019/2020 Resident Evil remakes share their names with the originals).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> KnownSteamApps = new Dictionary<int, string>
    {
        [489830] = "skyrimspecialedition",
        [72850] = "skyrim",
        [22330] = "oblivion",
        [2623190] = "oblivionremastered",
        [22320] = "morrowind",
        [22370] = "fallout3",
        [377160] = "fallout4",
        [22380] = "newvegas",
        [1449560] = "metroexodus",
        [729040] = "borderlandsgotyenhanced",
        [374320] = "darksouls3",
        [335300] = "darksouls2",
        [236430] = "darksouls2",
        [211420] = "darksouls",
        [47810] = "dragonage",
        [814380] = "sekiro",
        [883710] = "residentevil22019",
        [952060] = "residentevil32020",
    };

    /// <summary>
    /// Store names (normalised as <see cref="NormalizeName"/> does) that Nexus files under another name, for every
    /// store (GOG, Xbox and Epic spell some of these the same way Steam does).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["theelderscrollsvskyrimspecialedition"] = "skyrimspecialedition",
        ["theelderscrollsvskyrimanniversaryedition"] = "skyrimspecialedition",
        ["skyrimanniversaryedition"] = "skyrimspecialedition",
        ["theelderscrollsvskyrim"] = "skyrim",
        ["theelderscrollsvskyrimlegendaryedition"] = "skyrim",
        ["theelderscrollsivoblivion"] = "oblivion",
        ["theelderscrollsivoblivionremastered"] = "oblivionremastered",
        ["theelderscrollsivobliviongameoftheyearedition"] = "oblivion",
        ["theelderscrollsivobliviongameoftheyeareditiondeluxe"] = "oblivion",
        ["theelderscrollsiiimorrowind"] = "morrowind",
        ["theelderscrollsiiimorrowindgameoftheyearedition"] = "morrowind",
        ["fallout3gameoftheyearedition"] = "fallout3",
        ["fallout4gameoftheyearedition"] = "fallout4",
        ["metroexodusenhancededition"] = "metroexodus",
        ["borderlandsgotyenhanced"] = "borderlandsgotyenhanced",
        ["darksoulsiii"] = "darksouls3",
        ["darksoulsii"] = "darksouls2",
        ["darksoulsiischolarofthefirstsin"] = "darksouls2",
        ["darksoulspreparetodieedition"] = "darksouls",
        ["sekiroshadowsdietwicegotyedition"] = "sekiro",
        ["dragonageoriginsultimateedition"] = "dragonage",
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private Task? _load;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private volatile IReadOnlyDictionary<string, string>? _byName;

    public NexusGameCatalogue(HttpClient http, string? cachePath = null, TimeSpan? freshness = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "nexus_games.json");
        _freshness = freshness ?? DefaultFreshness;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True once Nexus's game list (live or the last good copy) is loaded for this session.</summary>
    public bool IsLoaded => _byName is not null;

    /// <summary>Starts (or joins) the background load; does nothing after a failure until the retry pause ends.</summary>
    public void Prefetch() => _ = LoadAsync();

    /// <summary>The background load (never faults). Tests await it; the app calls <see cref="Prefetch"/>.</summary>
    public Task LoadAsync()
    {
        lock (_sync)
        {
            if (_byName is not null) return Task.CompletedTask;
            if (_load is null)
            {
                if (_time.GetUtcNow() < _retryAfter) return Task.CompletedTask;
                _load = Task.Run(LoadSharedAsync, CancellationToken.None);
            }
            return _load;
        }
    }

    /// <summary>
    /// The Nexus Mods page of a game known by name or Steam app ID (built-in table first, then Nexus's list), or null.
    /// </summary>
    public string? PageFor(string? name, int? steamAppId) => KnownPageFor(name, steamAppId) ?? ListedPageFor(name);

    /// <summary>The page RHI's built-in table names for a game whose store name Nexus spells differently, or null.</summary>
    public static string? KnownPageFor(string? name, int? steamAppId)
    {
        if (steamAppId is { } id && KnownSteamApps.TryGetValue(id, out var byApp)) return PageUrl(byApp);
        foreach (var candidate in NameCandidates(name))
            if (KnownNames.TryGetValue(candidate, out var byName)) return PageUrl(byName);
        return null;
    }

    private string? ListedPageFor(string? name)
    {
        if (_byName is not { } listed) return null;
        foreach (var candidate in NameCandidates(name))
            if (listed.TryGetValue(candidate, out var url)) return url;
        return null;
    }

    /// <summary>A game page URL for a Nexus game domain (as Nexus spells it).</summary>
    public static string PageUrl(string domain) => "https://www.nexusmods.com/" + domain;

    /// <summary>Upstream's name normalisation: no ™/®/©, no diacritics, lower case, letters and digits only.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal);
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return NotAlphanumeric().Replace(sb.ToString().ToLowerInvariant(), "");
    }

    /// <summary>The game's name, then the name without a trailing release year ("Broken Sword 3 (2003)").</summary>
    private static IEnumerable<string> NameCandidates(string? name)
    {
        var whole = NormalizeName(name);
        if (whole.Length == 0) yield break;
        yield return whole;
        if (name is not null && TrailingYear().Replace(name, "") is var trimmed && trimmed.Length != name.Length
            && NormalizeName(trimmed) is { Length: > 0 } withoutYear)
            yield return withoutYear;
    }

    /// <summary>
    /// Normalised name → game page from Nexus's list; only https www.nexusmods.com pages one path segment deep are kept
    /// (first entry wins, as upstream). Throws <see cref="JsonException"/> on a document that is not the list.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(ReadOnlySpan<byte> raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(raw.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Nexus game list is not an array");
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty("nexusmods_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String) continue;
            var key = NormalizeName(nameValue.GetString());
            if (key.Length == 0 || GamePage(urlValue.GetString()) is not { } page) continue;
            map.TryAdd(key, page);
        }
        return map;
    }

    /// <summary>The URL when it is an https www.nexusmods.com game page (one path segment, no query), else null.</summary>
    public static string? GamePage(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Host is not ("www.nexusmods.com" or "nexusmods.com") || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 1 && DomainName().IsMatch(segments[0]) ? PageUrl(segments[0]) : null;
    }

    private async Task LoadSharedAsync()
    {
        IReadOnlyDictionary<string, string>? loaded = null;
        try
        {
            using var timeout = new CancellationTokenSource(LoadTimeout);
            loaded = await LoadCoreAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never faults: the card menu keeps the manifest and built-in pages.
            AppLog.Warn("nexus-games", "Nexus game list load failed", ex);
        }
        lock (_sync)
        {
            _load = null;
            if (loaded is not null) _byName = loaded;
            else _retryAfter = _time.GetUtcNow() + FailureRetryDelay;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var age = File.Exists(_cachePath) ? _time.GetUtcNow() - File.GetLastWriteTimeUtc(_cachePath) : (TimeSpan?)null;
        if (age < _freshness && ReadCache() is { } fresh) return fresh;
        try
        {
            var raw = await TrustedDownload.GetBytesAsync(_http, GamesListUrl, DownloadSources.NexusGameList, "application/json", cancellationToken)
                .ConfigureAwait(false);
            var live = Parse(raw);
            if (live.Count > 0)
            {
                await PersistAsync(raw, cancellationToken).ConfigureAwait(false);
                return live;
            }
            AppLog.Warn("nexus-games", "Nexus game list had no games; keeping the last good copy");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UntrustedDownloadException
                                       or TaskCanceledException or InvalidDataException)
        {
            AppLog.Warn("nexus-games", "Nexus game list download failed; using the last good copy if there is one", ex);
        }
        return ReadCache();
    }

    private IReadOnlyDictionary<string, string>? ReadCache()
    {
        if (!File.Exists(_cachePath)) return null;
        try
        {
            var map = Parse(File.ReadAllBytes(_cachePath));
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("nexus-games", $"Cached Nexus game list unreadable: {_cachePath}", ex);
            return null;
        }
    }

    private async Task PersistAsync(byte[] raw, CancellationToken cancellationToken)
    {
        try
        {
            if (Path.GetDirectoryName(_cachePath) is { Length: > 0 } directory) AtomicFile.CreateDirectoryDurable(directory);
            await AtomicFile.WriteAllBytesAsync(_cachePath, raw, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(_cachePath, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is optional; the list stays loaded for this session.
            AppLog.Warn("nexus-games", $"Nexus game list not cached: {_cachePath}", ex);
        }
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"\s*\(\d{4}\)\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._'-]*$")]
    private static partial Regex DomainName();
}
