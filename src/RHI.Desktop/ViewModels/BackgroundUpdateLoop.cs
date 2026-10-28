using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The automatic-update schedule (Refs #81). A pass that throws is logged and reported, then retried after a short
/// delay that doubles with each consecutive failure up to the configured interval; the loop only ends when it is
/// cancelled. It used to end for the rest of the session on its first exception, leaving auto-update silently off.
/// </summary>
public static class BackgroundUpdateLoop
{
    /// <summary>First retry delay after a failed pass (doubles per consecutive failure, capped at the interval).</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(15);

    /// <param name="pass">One update pass.</param>
    /// <param name="interval">Time between passes that succeed.</param>
    /// <param name="runImmediate">Run a pass before the first wait.</param>
    /// <param name="passFailed">Told about a failed pass and when the next try is (status line); runs on the caller's context.</param>
    /// <param name="delay">Waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test drives the clock.</param>
    public static async Task RunAsync(
        Func<CancellationToken, Task> pass,
        TimeSpan interval,
        bool runImmediate,
        Action<Exception, TimeSpan> passFailed,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(passFailed);
        delay ??= Task.Delay;
        var failures = 0;
        var next = runImmediate ? TimeSpan.Zero : interval;
        while (true)
        {
            try
            {
                if (next > TimeSpan.Zero) await delay(next, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await pass(cancellationToken);
                failures = 0;
                next = interval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Network, disk or a component service: none of them should stop automatic updates for the session.
                failures++;
                next = RetryDelay(failures, interval);
                AppLog.Warn("update", $"background update pass failed ({failures} in a row); next try in {next}", ex);
                passFailed(ex, next);
            }
        }
    }

    /// <summary>Status line for a failed pass: what went wrong and when the next try is, in whole minutes.</summary>
    public static string FailedStatus(Exception error, TimeSpan retry)
        => UiLocalizationService.Format("Vm.BackgroundUpdateFailedRetrying",
            UiLocalizationService.Present(error.Message), (int)Math.Ceiling(retry.TotalMinutes));

    /// <summary>15 min, 30 min, 1 h … after consecutive failures, never longer than <paramref name="interval"/>.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures, TimeSpan interval)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 16));
        var backoff = TimeSpan.FromTicks((long)Math.Min(FirstRetryDelay.Ticks * factor, TimeSpan.MaxValue.Ticks / 2.0));
        return backoff < interval ? backoff : interval;
    }
}

/// <summary>
/// #242 (upstream 2.8.8 <c>AutoUpdateService.EnqueueRetry</c>): the games a background pass left alone because they were
/// running. While any is left and automatic updates are on, every <see cref="DefaultInterval"/> the poll asks the running
/// probe about each one (no process is started) and gives a game that has closed a pass of its own. A game that runs again
/// before its pass is kept; one whose pass ran (whatever it found) is done. Never a tight loop: every poll waits the
/// interval first, and a probe that throws only means "ask again next time" (logged once).
/// </summary>
public sealed class DeferredGameRetry
{
    /// <summary>Time between two looks at the deferred games (upstream: 60 s).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, DetectedGame> _pending = new(StringComparer.Ordinal);
    private readonly Func<DetectedGame, bool> _isRunning;
    private readonly Func<bool> _wanted;
    private readonly Func<DetectedGame, CancellationToken, Task<bool>> _pass;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Task? _run;
    private bool _polling;
    private bool _probeFailureLogged;

    /// <param name="isRunning">Whether the game runs now (the pass's own probe).</param>
    /// <param name="wanted">Whether automatic updates are still on; when off the poll ends and forgets every game.</param>
    /// <param name="pass">A pass for one closed game; true when it must still wait (it runs again, or the gate was taken).</param>
    /// <param name="interval">Time between polls.</param>
    /// <param name="delay">Waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test drives the clock.</param>
    public DeferredGameRetry(Func<DetectedGame, bool> isRunning, Func<bool> wanted, Func<DetectedGame, CancellationToken, Task<bool>> pass,
        TimeSpan interval, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _isRunning = isRunning ?? throw new ArgumentNullException(nameof(isRunning));
        _wanted = wanted ?? throw new ArgumentNullException(nameof(wanted));
        _pass = pass ?? throw new ArgumentNullException(nameof(pass));
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The games still waiting to close.</summary>
    public IReadOnlyList<DetectedGame> Pending
    {
        get { lock (_gate) return _pending.Values.ToArray(); }
    }

    /// <summary>The poll that is running (or the last one); null before the first deferred game.</summary>
    public Task? Run
    {
        get { lock (_gate) return _run; }
    }

    /// <summary>Remembers the games (each once) and starts the poll unless one runs already.</summary>
    public Task Add(IEnumerable<DetectedGame> games, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var game in games) _pending[LinuxGameLibraryStore.SessionKey(game)] = game;
            if (_pending.Count == 0 || _polling) return _run ?? Task.CompletedTask;
            _polling = true;
            _run = PollAsync(cancellationToken);
            return _run;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (!StillWanted()) return;
                await _delay(_interval, cancellationToken);
                if (!StillWanted()) return;
                foreach (var (key, game) in Snapshot())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool running;
                    try
                    {
                        running = await Task.Run(() => _isRunning(game), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!_probeFailureLogged)
                        {
                            _probeFailureLogged = true;
                            AppLog.Warn("update", "running-game probe failed; deferred games are asked again at the next poll", ex);
                        }
                        continue;
                    }
                    if (running) continue;
                    bool keep;
                    try
                    {
                        keep = await _pass(game, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // The next scheduled pass covers it; retrying a failing pass every minute would only repeat the failure.
                        AppLog.Warn("update", $"update pass for a game that closed failed: {game.Name}", ex);
                        keep = false;
                    }
                    if (!keep)
                        lock (_gate) _pending.Remove(key);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The loop stopped (shell closing, settings changed): the restarted loop's first pass defers them again.
            lock (_gate) _polling = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("update", "the poll for deferred running games stopped; the next scheduled pass covers them", ex);
            lock (_gate) _polling = false;
        }
    }

    // Ends the poll (under the lock, so a game added meanwhile starts a new one) once nothing waits or updates are off.
    private bool StillWanted()
    {
        lock (_gate)
        {
            if (_pending.Count > 0 && _wanted()) return true;
            _pending.Clear();
            _polling = false;
            return false;
        }
    }

    private KeyValuePair<string, DetectedGame>[] Snapshot()
    {
        lock (_gate) return _pending.ToArray();
    }
}
