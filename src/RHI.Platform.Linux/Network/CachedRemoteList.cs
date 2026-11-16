using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// One web list (#183 card links, #184 info dialogs, #185 new mods): downloaded under its own policy, parsed, kept in a cache file
/// (<see cref="JsonStoreKind.Cache"/>; the time of the download is recorded inside it), the last good copy reused when
/// the list cannot be fetched, retried after a pause when nothing is available. Lookups never wait for it. Full Refresh
/// downloads it again (<see cref="RefreshAsync"/>).
/// </summary>
internal sealed class CachedRemoteList<T> where T : class
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    private readonly string _name;
    private readonly string _url;
    private readonly TrustedDownloadPolicy _policy;
    private readonly Func<string, IReadOnlyList<T>> _parse;
    private readonly Func<T, string?> _nameOf;
    private readonly Func<T, bool> _usable;
    private readonly Func<string?, string> _key;
    private readonly JsonFileStore<CacheFile> _store;
    private readonly TimeSpan _freshness;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private Task? _load;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private volatile Loaded? _loaded;

    /// <param name="FreshFrom">#292: the time the copy's freshness counts from (its download, or the last failed try
    /// less the freshness plus <see cref="FailureRetryDelay"/>, so a list offline is asked again after that pause).</param>
    private sealed record Loaded(IReadOnlyList<T> Entries, IReadOnlyDictionary<string, T> ByKey, DateTimeOffset FreshFrom = default);

    public CachedRemoteList(string name, string url, TrustedDownloadPolicy policy, Func<string, IReadOnlyList<T>> parse,
        Func<T, string?> nameOf, Func<T, bool> usable, string cachePath, TimeSpan freshness, TimeProvider time, HttpClient http,
        Func<string?, string>? key = null)
    {
        _name = name;
        _url = url;
        _policy = policy;
        _parse = parse;
        _nameOf = nameOf;
        _usable = usable;
        _key = key ?? NexusGameCatalogue.NormalizeName;
        _freshness = freshness;
        _time = time;
        _http = http;
        _store = new JsonFileStore<CacheFile>(cachePath, new JsonSerializerOptions { WriteIndented = false }, JsonStoreKind.Cache,
            parse: text => JsonSerializer.Deserialize<CacheFile>(text) is { Entries: not null } parsed ? parsed : null);
    }

    public bool IsLoaded => _loaded is not null;

    public IReadOnlyList<T> Entries => _loaded?.Entries ?? [];

    /// <summary>The entry for a key (as <c>key</c> computes it from a name; first entry wins, as upstream); null until loaded.</summary>
    public T? Find(string key)
        => key.Length > 0 && _loaded is { } loaded && loaded.ByKey.TryGetValue(key, out var entry) ? entry : null;

    public void Prefetch() => _ = LoadAsync();

    public Task LoadAsync()
    {
        lock (_sync)
        {
            // #292: a loaded copy is used until it is older than the freshness, then downloaded again while it stays in use
            // (a list loaded once used to be kept for the whole session). A Full Refresh running meanwhile is not waited for.
            if (_loaded is { } loaded && _time.GetUtcNow() - loaded.FreshFrom < _freshness) return Task.CompletedTask;
            if (_load is null)
            {
                if (_time.GetUtcNow() < _retryAfter) return Task.CompletedTask;
                _load = Task.Run(() => LoadSharedAsync(refresh: false), CancellationToken.None);
            }
            return _load;
        }
    }

    /// <summary>
    /// Full Refresh: downloads the list again even when the cached copy is fresh (a load already running is joined). The
    /// loaded entries stay in use until the new copy is in; a failed download keeps them.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_sync)
        {
            if (_load is not null) return _load;
            _retryAfter = DateTimeOffset.MinValue;
            return _load = Task.Run(() => LoadSharedAsync(refresh: true), CancellationToken.None);
        }
    }

    private async Task LoadSharedAsync(bool refresh)
    {
        Loaded? loaded = null;
        try
        {
            using var timeout = new CancellationTokenSource(LoadTimeout);
            loaded = await LoadCoreAsync(refresh, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("game-links", $"{_name} list load failed", ex);
        }
        lock (_sync)
        {
            _load = null;
            if (loaded is not null) _loaded = loaded;
            else _retryAfter = _time.GetUtcNow() + FailureRetryDelay; // a loaded copy stays in use meanwhile
        }
    }

    private async Task<Loaded?> LoadCoreAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cached = ReadCache();
        if (!refresh && cached is not null && _time.GetUtcNow() - cached.FetchedAtUtc < _freshness && Index(cached.Entries!) is { } fresh)
            return fresh with { FreshFrom = cached.FetchedAtUtc };
        try
        {
            var text = await TrustedDownload.GetStringAsync(_http, _url, _policy, cancellationToken: cancellationToken).ConfigureAwait(false);
            var entries = _parse(text);
            if (entries.Count > 0 && Index(entries) is { } live)
            {
                var now = _time.GetUtcNow();
                Persist(new CacheFile { FetchedAtUtc = now, Entries = [.. live.Entries] });
                return live with { FreshFrom = now };
            }
            AppLog.Warn("game-links", $"{_name} list had no games; keeping the last good copy");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UntrustedDownloadException
                                       or TaskCanceledException or InvalidDataException or RegexMatchTimeoutException)
        {
            AppLog.Warn("game-links", $"{_name} list download failed; using the last good copy if there is one", ex);
        }
        return cached?.Entries is { Count: > 0 } stale && Index(stale) is { } kept
            ? kept with { FreshFrom = _time.GetUtcNow() - _freshness + FailureRetryDelay }
            : null;
    }

    private Loaded? Index(IReadOnlyList<T> entries)
    {
        var usable = entries.Where(entry => entry is not null && _usable(entry)).ToArray();
        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var entry in usable)
        {
            var key = _key(_nameOf(entry));
            if (key.Length > 0) byKey.TryAdd(key, entry);
        }
        return usable.Length == 0 ? null : new Loaded(usable, byKey);
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
        [JsonPropertyName("entries")] public List<T>? Entries { get; set; }
    }
}
