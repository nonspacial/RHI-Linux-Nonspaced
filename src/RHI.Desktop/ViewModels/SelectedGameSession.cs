using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// What the panels show about the selected game that needs disk access: deployment records, files next to the
/// executable, the executable's architecture, RHI backups of DLSS/Streamline DLLs, the Luma policy and the HDR host.
/// Worked out off the UI thread with the rest of the analysis, so property getters never touch the disk (Refs #71).
/// </summary>
public sealed record SelectionFacts(
    LinuxDeploymentRecord? UalRecord,
    IReadOnlyList<string> UalProxyNames,
    string UalRecommendedProxy,
    IReadOnlySet<string> UalProxyFilesPresent,
    bool ShortFuseManaged,
    LinuxDeploymentRecord? ShortFuseRecord,
    bool ShortFuseAutoConfigEnabled,
    bool ShortFuseZzzLoadOrderInEffect,
    bool Dlss5FeederFullManaged,
    bool HasNativeDlssForNeuralRendering,
    PeArchitecture Architecture,
    bool ExecutableExists,
    int DlssSrBackups,
    int DlssRrBackups,
    int DlssFgBackups,
    int DlssNrBackups,
    int StreamlineBackups,
    LumaPolicySnapshot LumaPolicy,
    LinuxHdrHostSnapshot HdrHost,
    bool OrphanedProtonPrefix,
    bool CanInstallWithWinetricks)
{
    public static SelectionFacts Compute(
        DetectedGame game,
        DlssDetectionResult dlss,
        LinuxManagedComponentService managed,
        LinuxHdrLaunchProfileService hdr)
    {
        var proxyNames = managed.GetUltimateAsiLoaderProxyNames(game).ToArray();
        var binaryDirectory = game.BinaryDirectory ?? Path.GetDirectoryName(game.ExecutablePath ?? "");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(binaryDirectory))
            foreach (var name in proxyNames)
                if (File.Exists(Path.Combine(binaryDirectory, name))) present.Add(name);
        return new SelectionFacts(
            managed.GetUltimateAsiLoaderRecord(game),
            proxyNames,
            managed.GetUltimateAsiLoaderRecommendedProxy(game),
            present,
            managed.IsShortFuseManaged(game),
            managed.GetShortFuseRecord(game),
            managed.GetShortFuseAutoConfigEnabled(game),
            managed.GetShortFuseZzzLoadOrderInEffect(game),
            managed.IsDlss5FeederFullManaged(game),
            managed.HasNativeDlssForNeuralRendering(game, dlss.DlssPaths.Count > 0),
            GameArchitecture.Of(game),
            !string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath),
            Backups(dlss.DlssPaths),
            Backups(dlss.DlssdPaths),
            Backups(dlss.DlssgPaths),
            Backups(dlss.DlssnrPaths),
            Backups(dlss.StreamlineFolders
                .SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name)))
                .Distinct(StringComparer.Ordinal)),
            managed.GetLumaPolicy(game),
            hdr.ProbeHost(),
            D3DCompilerToolInstaller.IsOrphanedProtonPrefix(game),
            D3DCompilerToolInstaller.CanInstallWithWinetricks(game));
    }

    /// <summary>How many of <paramref name="targets"/> have an RHI <c>.original</c> backup beside them.</summary>
    private static int Backups(IEnumerable<string> targets)
        => targets.Count(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
}

/// <summary>
/// One immutable analysis of a game: its file index, component rows, DLSS detection, disk facts and the runtime fields
/// the probe worked out. Published by <see cref="SelectedGameSession"/>; every panel reads the selected game's.
/// </summary>
public sealed record GameAnalysis(
    IReadOnlyList<string> Files,
    IReadOnlyList<ManagedComponent> Components,
    DlssDetectionResult Dlss,
    SelectionFacts Facts,
    GameRuntimeFacts Runtime);

public enum AnalysisOutcomeKind { Ready, Cached, NoPatchTarget, Failed }

/// <summary>How an analysis of the selected game ended (the view model turns it into the status line).</summary>
public sealed record AnalysisOutcome(
    DetectedGame Game,
    AnalysisOutcomeKind Kind,
    int FileCount = 0,
    long ElapsedMilliseconds = 0,
    bool Truncated = false,
    Exception? Error = null);

/// <summary>
/// Unit 1 of the view-model split (epic #89): the selected game's analysis. Owns the selection generation, the
/// bounded probe cache (<see cref="DefaultCapacity"/> games, least recently used dropped first; Refs #74, #60),
/// re-probe and invalidation (Refs #72, #83), and publishes one immutable <see cref="GameAnalysis"/> worked out off
/// the UI thread on a working copy of the game (Refs #82, #71). UI thread only, apart from the work it starts.
/// </summary>
public sealed class SelectedGameSession
{
    /// <summary>
    /// Games whose analysis (including the full file index) is kept. Measured (SelectedGameSessionTests): browsing 60
    /// games of 3,000 paths (~125 characters each) retains 6.2 MiB, about 270 bytes per path for the 8 kept; the old
    /// unbounded cache kept all 60 (~48 MiB) for the whole session. The game count alone does not bound memory (an index
    /// may hold up to <see cref="GameFileIndexer.DefaultMaxFiles"/> paths), so <see cref="DefaultMaxCachedPaths"/> bounds
    /// it too.
    /// </summary>
    public const int DefaultCapacity = 8;

    /// <summary>
    /// Total file-index paths the cache keeps (~27 MiB at the measured 270 bytes per path). Older analyses are dropped
    /// first; the newest is always kept, so one game with a capped 200,000-path index (~54 MiB) stays cached alone.
    /// </summary>
    public const long DefaultMaxCachedPaths = 100_000;

    private readonly IGameRuntimeProbe _probe;
    private readonly LinuxManagedComponentService _managed;
    private readonly LinuxDlssDefaultsService _dlssDefaults;
    private readonly LinuxHdrLaunchProfileService _hdr;
    private readonly LruCache<string, GameAnalysis> _cache;
    private CancellationTokenSource? _cts;
    private int _generation;

    public SelectedGameSession(
        IGameRuntimeProbe probe,
        LinuxManagedComponentService managed,
        LinuxDlssDefaultsService dlssDefaults,
        LinuxHdrLaunchProfileService hdr,
        int capacity = DefaultCapacity,
        long maxCachedPaths = DefaultMaxCachedPaths)
    {
        _probe = probe;
        _managed = managed;
        _dlssDefaults = dlssDefaults;
        _hdr = hdr;
        _cache = new LruCache<string, GameAnalysis>(capacity, weigh: analysis => analysis.Files.Count, maxWeight: maxCachedPaths);
    }

    /// <summary>The game the session analyses (the selected game), or null.</summary>
    public DetectedGame? Game { get; private set; }

    /// <summary>The selected game's published analysis; null while it is being worked out or when nothing is selected.</summary>
    public GameAnalysis? Analysis { get; private set; }

    /// <summary>True while the selected game is being analysed.</summary>
    public bool IsProbeBusy { get; private set; }

    /// <summary>How many analyses the cache holds (tests).</summary>
    public int CachedCount => _cache.Count;

    /// <summary>How many file-index paths the cached analyses hold together (tests).</summary>
    public long CachedPaths => _cache.Weight;

    /// <summary><see cref="Game"/>, <see cref="Analysis"/> or <see cref="IsProbeBusy"/> changed.</summary>
    public event Action? Changed;

    /// <summary>An analysis of the selected game ended (not raised when it was cancelled or superseded).</summary>
    public event Action<AnalysisOutcome>? Completed;

    /// <summary>The cached analysis of <paramref name="game"/>, if any.</summary>
    public bool TryGetCached(DetectedGame game, out GameAnalysis analysis) => _cache.TryGet(Key(game), out analysis!);

    /// <summary>The cached file index of <paramref name="game"/>, or null (bulk operations re-index on demand).</summary>
    public IReadOnlyList<string>? CachedFiles(DetectedGame game) => TryGetCached(game, out var analysis) ? analysis.Files : null;

    public void Invalidate(DetectedGame game) => _cache.Remove(Key(game));

    public void InvalidateAll() => _cache.Clear();

    /// <summary>Stops the running analysis; its result is never published.</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _generation++;
    }

    /// <summary>
    /// Makes <paramref name="game"/> the analysed game: a cached analysis is published at once (and reported as
    /// <see cref="AnalysisOutcomeKind.Cached"/>), otherwise an analysis starts in the background.
    /// </summary>
    public void Select(DetectedGame? game)
    {
        Cancel();
        Game = game;
        Analysis = null;
        IsProbeBusy = false;
        if (game is null)
        {
            Changed?.Invoke();
            return;
        }
        if (TryGetCached(game, out var cached))
        {
            Publish(game, cached);
            Completed?.Invoke(new AnalysisOutcome(game, AnalysisOutcomeKind.Cached));
            return;
        }
        UiExceptionGuard.Forget(AnalyzeAsync(game), "AnalyzeSelectedGameAsync");
    }

    /// <summary>Drops <paramref name="game"/>'s analysis and, when it is the analysed game, works it out again.</summary>
    public Task ReprobeAsync(DetectedGame game)
    {
        Invalidate(game);
        if (!ReferenceEquals(Game, game)) return Task.CompletedTask;
        Cancel();
        Analysis = null;
        return AnalyzeAsync(game);
    }

    /// <summary>
    /// The one path for operations that may have changed files in any game (Update All, the background pass, DLSS
    /// batch): every cached analysis is dropped and the selected game is always analysed again, so its component rows
    /// never wait on an analysis that is not running (Refs #72).
    /// </summary>
    public Task InvalidateAllAndReprobeAsync()
    {
        InvalidateAll();
        return Game is { } game ? ReprobeAsync(game) : Task.CompletedTask;
    }

    /// <summary>
    /// Works out the component rows again from the cached file index for the game's current state (a changed API
    /// override, Luma preference or OptiScaler setting) and publishes them; false when nothing is cached.
    /// </summary>
    public bool Recompose(DetectedGame game, Func<SelectionFacts, SelectionFacts>? updateFacts = null)
    {
        if (!TryGetCached(game, out var cached)) return false;
        var components = ComponentProbe.Probe(game, cached.Files);
        _managed.Decorate(game, components);
        var refreshed = cached with { Components = components, Facts = updateFacts?.Invoke(cached.Facts) ?? cached.Facts };
        _cache.Set(Key(game), refreshed);
        if (ReferenceEquals(Game, game)) Publish(game, refreshed);
        return true;
    }

    private async Task AnalyzeAsync(DetectedGame game)
    {
        var generation = ++_generation;
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;
        IsProbeBusy = true;
        Changed?.Invoke();
        var timer = Stopwatch.StartNew();
        // The probe works on a copy: the shared instance is only written below, on this (UI) thread, in one step.
        var working = game.ShallowClone();
        var userApiOverride = game.UserGraphicsApiOverride;
        try
        {
            GameAnalysis analysis;
            IReadOnlyList<string> files;
            AnalysisOutcomeKind kind;
            if (!working.HasConfirmedPatchTarget)
            {
                // No patch target: nothing is indexed (a refused manual folder may be $HOME or /).
                files = Array.Empty<string>();
                analysis = await Task.Run(() => Derive(working, files, skipDlss: true, cancellationToken), cancellationToken);
                kind = AnalysisOutcomeKind.NoPatchTarget;
            }
            else
            {
                files = await Task.Run(() => GameFileIndexer.Build(working.InstallPath, 8, cancellationToken), cancellationToken);
                await _probe.ProbeAsync(working, files, cancellationToken);
                await _managed.PrepareGameAsync(working, files, cancellationToken);
                analysis = await Task.Run(() => Derive(working, files, skipDlss: working.DlssScanSkippedByManifest, cancellationToken), cancellationToken);
                kind = AnalysisOutcomeKind.Ready;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation || !ReferenceEquals(Game, game)) return;

            analysis.Runtime.ApplyTo(game);
            if (game.UserGraphicsApiOverride != userApiOverride)
            {
                // The API override changed while the analysis ran: the rows must follow the new one.
                var components = ComponentProbe.Probe(game, files);
                _managed.Decorate(game, components);
                analysis = analysis with { Components = components };
            }
            _cache.Set(Key(game), analysis);
            Publish(game, analysis);
            timer.Stop();
            Completed?.Invoke(new AnalysisOutcome(game, kind, files.Count, timer.ElapsedMilliseconds,
                kind == AnalysisOutcomeKind.Ready && GameFileIndexer.IsTruncated(files)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Switching games is expected to cancel the previous analysis. Do not surface it as an error.
        }
        catch (Exception ex)
        {
            if (generation == _generation && ReferenceEquals(Game, game))
                Completed?.Invoke(new AnalysisOutcome(game, AnalysisOutcomeKind.Failed, Error: ex));
        }
        finally
        {
            if (generation == _generation && ReferenceEquals(Game, game))
            {
                IsProbeBusy = false;
                Changed?.Invoke();
            }
        }
    }

    private GameAnalysis Derive(DetectedGame working, IReadOnlyList<string> files, bool skipDlss, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var components = ComponentProbe.Probe(working, files, cancellationToken);
        _managed.Decorate(working, components);
        var dlss = skipDlss ? new DlssDetectionResult() : _dlssDefaults.DetectTargets(working, files, cancellationToken);
        var facts = SelectionFacts.Compute(working, dlss, _managed, _hdr);
        return new GameAnalysis(files, components, dlss, facts, GameRuntimeFacts.Capture(working));
    }

    private void Publish(DetectedGame game, GameAnalysis analysis)
    {
        analysis.Runtime.ApplyTo(game);
        Analysis = analysis;
        Changed?.Invoke();
    }

    // Per card and folder (w2/stores #147): a cached analysis never crosses a move; saved state uses GameKey.
    private static string Key(DetectedGame game) => LinuxGameLibraryStore.SessionKey(game);
}

/// <summary>
/// A small least-recently-used map: reading or writing an entry makes it the newest; the oldest goes first. Bounded by
/// entry count and, with <c>weigh</c>, by total weight; the newest entry is always kept, even when it alone is heavier.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Func<TValue, long> _weigh;
    private readonly long _maxWeight;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null, Func<TValue, long>? weigh = null, long maxWeight = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWeight, 0);
        _capacity = capacity;
        _weigh = weigh ?? (_ => 0);
        _maxWeight = maxWeight;
        _map = new(comparer);
    }

    public int Count => _map.Count;

    /// <summary>The entries' total weight (0 without a <c>weigh</c> function).</summary>
    public long Weight { get; private set; }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            Weight -= _weigh(existing.Value.Value);
        }
        var node = new LinkedListNode<(TKey, TValue)>((key, value));
        _order.AddFirst(node);
        _map[key] = node;
        Weight += _weigh(value);
        while ((_map.Count > _capacity || Weight > _maxWeight) && _order.Count > 1 && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _map.Remove(oldest.Value.Key);
            Weight -= _weigh(oldest.Value.Value);
        }
    }

    public bool Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return false;
        _order.Remove(node);
        Weight -= _weigh(node.Value.Value);
        return true;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
        Weight = 0;
    }
}
