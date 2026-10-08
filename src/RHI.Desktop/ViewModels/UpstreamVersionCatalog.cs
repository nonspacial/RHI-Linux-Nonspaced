using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The upstream version data the main window shows: the neural-rendering add-on version lists (DLSS5 Feeder, DX11
/// Bridge, ShortFuse) and the "latest version" of Ultimate ASI Loader (per architecture) and ShortFuse. An explicit full
/// library Refresh drops every cached latest version and reloads the version lists (upstream 2.7.9; Refs #86, #117).
/// <para>Integration hooks in <c>MainWindowViewModel</c>: one instance built in the constructor over the component
/// service; Full Refresh calls <see cref="FullRefreshAsync"/>; the UAL/ShortFuse latest-version getters go through
/// <see cref="UalLatest"/>/<see cref="ShortFuseLatest"/>; start-up and "Load versions" call
/// <see cref="NeuralVersionCatalog.EnsureAsync"/>.</para>
/// </summary>
public sealed class UpstreamVersionCatalog
{
    public UpstreamVersionCatalog(
        Func<ManagedComponentKind, Task<IReadOnlyList<GitHubReleaseVersion>>> listNeuralVersions,
        Action<NeuralVersionLists> applyNeuralVersions,
        Func<DateTime>? utcNow = null,
        TimeSpan? latestTimeToLive = null)
        : this((kind, _) => listNeuralVersions(kind), applyNeuralVersions, utcNow, latestTimeToLive)
    {
    }

    /// <param name="listNeuralVersions">Lists one add-on's versions; the token is the one the load's starter passed to
    /// <see cref="NeuralVersionCatalog.EnsureAsync"/> (its operation's Cancel, w2/vmsplit).</param>
    public UpstreamVersionCatalog(
        Func<ManagedComponentKind, CancellationToken, Task<IReadOnlyList<GitHubReleaseVersion>>> listNeuralVersions,
        Action<NeuralVersionLists> applyNeuralVersions,
        Func<DateTime>? utcNow = null,
        TimeSpan? latestTimeToLive = null)
    {
        NeuralVersions = new NeuralVersionCatalog(listNeuralVersions, applyNeuralVersions);
        UalLatest = new LatestVersionCache<PeArchitecture>(latestTimeToLive, utcNow);
        ShortFuseLatest = new LatestVersionCache<ManagedComponentKind>(latestTimeToLive, utcNow);
    }

    public NeuralVersionCatalog NeuralVersions { get; }

    /// <summary>Ultimate ASI Loader's latest release, per game architecture.</summary>
    public LatestVersionCache<PeArchitecture> UalLatest { get; }

    /// <summary>ShortFuse's latest release (key <see cref="ManagedComponentKind.ShortFuse"/>).</summary>
    public LatestVersionCache<ManagedComponentKind> ShortFuseLatest { get; }

    /// <summary>Full library Refresh: forget every latest version and reload the neural-rendering version lists.</summary>
    public Task<NeuralCatalogOutcome> FullRefreshAsync()
    {
        UalLatest.Clear();
        ShortFuseLatest.Clear();
        return NeuralVersions.EnsureAsync(forceRefresh: true);
    }
}

/// <summary>
/// A component's "latest version", kept for <see cref="DefaultTimeToLive"/>: a tray-resident session re-queries once it
/// is older than that instead of showing a stale latest until the user presses Check (UI-19, Refs #86). Concurrent
/// lookups for one key share a single request; a value fetched before <see cref="Clear"/> is never stored after it.
/// </summary>
public sealed class LatestVersionCache<TKey> where TKey : notnull
{
    /// <summary>One hour: at most one GitHub request per component and hour while RHI stays open (the unauthenticated
    /// API allows 60 an hour), and a release published meanwhile shows within the hour.</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<TKey, (string Version, DateTime StoredUtc)> _values = new();
    private readonly Dictionary<TKey, Task<string?>> _loads = new();
    private readonly TimeSpan _timeToLive;
    private readonly Func<DateTime> _utcNow;
    private int _generation;

    public LatestVersionCache(TimeSpan? timeToLive = null, Func<DateTime>? utcNow = null)
    {
        _timeToLive = timeToLive ?? DefaultTimeToLive;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The cached latest version while it is younger than the time to live, else null.</summary>
    public string? TryGetFresh(TKey key)
    {
        lock (_gate) return FreshUnsafe(key);
    }

    /// <summary>Stores a latest version learnt elsewhere (an explicit update check). Blank values are ignored.</summary>
    public void Set(TKey key, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        lock (_gate) _values[key] = (version, _utcNow());
    }

    /// <summary>Forgets every value; loads already running are not stored when they finish.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _loads.Clear();
            _generation++;
        }
    }

    /// <summary>
    /// The fresh cached value, else the result of <paramref name="load"/> (shared with a load already running for the
    /// key unless <paramref name="forceRefresh"/>); a non-blank result is cached.
    /// </summary>
    public async Task<string?> GetAsync(TKey key, Func<Task<string?>> load, bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(load);
        Task<string?>? running = null;
        int generation;
        lock (_gate)
        {
            if (!forceRefresh)
            {
                if (FreshUnsafe(key) is { } cached) return cached;
                _loads.TryGetValue(key, out running);
            }
            generation = _generation;
        }
        if (running is not null) return await running.ConfigureAwait(false);

        var task = load();
        if (!forceRefresh)
            lock (_gate)
                if (generation == _generation) _loads[key] = task;
        try
        {
            var version = await task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version))
                lock (_gate)
                    if (generation == _generation) _values[key] = (version, _utcNow());
            return version;
        }
        finally
        {
            lock (_gate)
                if (_loads.TryGetValue(key, out var active) && ReferenceEquals(active, task))
                    _loads.Remove(key);
        }
    }

    private string? FreshUnsafe(TKey key)
    {
        if (!_values.TryGetValue(key, out var entry)) return null;
        if (_utcNow() - entry.StoredUtc < _timeToLive) return entry.Version;
        _values.Remove(key);
        return null;
    }
}

/// <summary>The neural-rendering add-on version lists, newest first.</summary>
public sealed record NeuralVersionLists(
    IReadOnlyList<GitHubReleaseVersion> Dlss5Feeder,
    IReadOnlyList<GitHubReleaseVersion> Dlss5Bridge,
    IReadOnlyList<GitHubReleaseVersion> ShortFuse);

public enum NeuralCatalogOutcome
{
    /// <summary>Already loaded (or another load is still failing): nothing fetched.</summary>
    NotNeeded,
    /// <summary>Joined a load another caller started, which succeeded.</summary>
    AlreadyLoaded,
    /// <summary>Fetched and applied the lists.</summary>
    Loaded
}

/// <summary>
/// Loads the neural-rendering add-on version lists once per session; a forced load (full Refresh, "Load versions")
/// fetches them again. Concurrent callers share one load. <paramref name="list"/> gets the token of the caller that
/// started the load (cancelling it fails that load for every caller, as before the catalogue existed); a joining caller
/// stops waiting when its own token is cancelled. <paramref name="apply"/> runs on the caller's context (the UI thread
/// in the app), so it may update bound collections.
/// </summary>
public sealed class NeuralVersionCatalog(
    Func<ManagedComponentKind, CancellationToken, Task<IReadOnlyList<GitHubReleaseVersion>>> list,
    Action<NeuralVersionLists> apply)
{
    private Task? _load;

    public NeuralVersionCatalog(Func<ManagedComponentKind, Task<IReadOnlyList<GitHubReleaseVersion>>> list, Action<NeuralVersionLists> apply)
        : this((kind, _) => list(kind), apply)
    {
    }

    public bool IsLoaded { get; private set; }

    /// <summary>Throws what the load threw when this call started it (a joined load's failure is its starter's to report).</summary>
    public async Task<NeuralCatalogOutcome> EnsureAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && IsLoaded) return NeuralCatalogOutcome.NotNeeded;

        if (_load is { } running)
        {
            try { await running.WaitAsync(cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Reported by the caller that started this load; a forced refresh below starts its own.
            }
            if (!forceRefresh || IsLoaded)
                return IsLoaded ? NeuralCatalogOutcome.AlreadyLoaded : NeuralCatalogOutcome.NotNeeded;
        }

        var load = LoadAsync(cancellationToken);
        _load = load;
        try
        {
            await load;
            IsLoaded = true;
            return NeuralCatalogOutcome.Loaded;
        }
        finally
        {
            if (ReferenceEquals(_load, load)) _load = null;
        }
    }

    // No ConfigureAwait(false): apply updates bound collections on the caller's (UI) context.
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var feeder = list(ManagedComponentKind.Dlss5Feeder, cancellationToken);
        var bridge = list(ManagedComponentKind.Dlss5Dx11Bridge, cancellationToken);
        var shortFuse = list(ManagedComponentKind.ShortFuse, cancellationToken);
        await Task.WhenAll(feeder, bridge, shortFuse);
        apply(new NeuralVersionLists(feeder.Result, bridge.Result, shortFuse.Result));
    }
}
