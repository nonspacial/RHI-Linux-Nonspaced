using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Update All and the background update pass (epic #89 unit 7): the toolbar's Update All (cancellable from the busy
/// overlay, Refs #68), the per-game file indexes it needs, and the background loop that Settings starts and stops.
/// </summary>
public sealed partial class UpdatesViewModel : PanelViewModel
{
    private readonly LinuxUpdateAllService _updateAll;

    internal UpdatesViewModel(IPanelHost host, LinuxUpdateAllService updateAll) : base(host) => _updateAll = updateAll;

    /// <summary>The Update All service (tests reach its running probe).</summary>
    internal LinuxUpdateAllService UpdateAll => _updateAll;

    private CancellationTokenSource? _backgroundUpdateCts;
    // #242: the running games the loop's passes deferred, polled until they close (one per loop run).
    private DeferredGameRetry? _deferred;
    // #177: stops a Follow newest pass a Full Refresh started when the shell closes.
    private readonly CancellationTokenSource _followCts = new();
    private Task? _followRequest;

    // Library-wide facts that need disk access, worked out off the UI thread after scans and operations (Refs #71).
    private bool _libraryHasManagedTargets;

    /// <summary>The library-wide fact behind Update All (worked out off the UI thread by the shell after scans and operations).</summary>
    internal void SetLibraryHasManagedTargets(bool managed)
    {
        _libraryHasManagedTargets = managed;
        NotifyCanUpdateAll();
    }

    internal void NotifyCanUpdateAll()
    {
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops the background loop (the shell is closing).</summary>
    internal void StopBackgroundLoop()
    {
        try { _followCts.Cancel(); }
        catch (ObjectDisposedException) { /* already stopped */ }
        try { _backgroundUpdateCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already stopped */ }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        _deferred = null;
    }

    public bool CanUpdateAll => !IsBusy && _libraryHasManagedTargets;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll) return;
        var games = Host.AllGames.ToArray();
        using var op = BeginOperation("update-all");
        if (op is null) return;
        try
        {
            StatusText = T("Vm.UpdateAllPreparingRHIManagedGames");
            var indexes = await PrepareUpdateIndexesAsync(games, op.Token);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Manual,
                op.Token);
            // Always re-analyse the selected game: dropping its cached analysis without one wedged every component
            // action on "wait for analysis" (Refs #72).
            await InvalidateAllAndReprobeSelectedAsync();
            StatusText = UiLocalizationService.UpdateAllSummary(result) + Shell.F5Driver.UpdateAllNote(games, result); // w11/nr-gpu hook (#231)
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            await InvalidateAllAndReprobeSelectedAsync(); // games finished before the cancel were updated
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            await InvalidateAllAndReprobeSelectedAsync();
            StatusText = TF("Vm.UpdateAllFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareUpdateIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget || !_updateAll.HasManagedTargets([game])) continue;
            var key = Key(game);
            if (Host.Session.CachedFiles(game) is { } cached)
            {
                indexes[key] = cached;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    internal void RestartBackgroundUpdateLoop(bool runImmediate)
    {
        try { _backgroundUpdateCts?.Cancel(); } catch { }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        _deferred = null; // #242: a restarted loop's first pass defers the running games again
        if (!LoopWanted(Host.Settings)) return;
        _backgroundUpdateCts = new CancellationTokenSource();
        UiExceptionGuard.Forget(BackgroundUpdateLoopAsync(_backgroundUpdateCts.Token, runImmediate), "BackgroundUpdateLoopAsync");
    }

    // A failed pass is logged, reported and retried with back-off; the loop ends only when cancelled (#81).
    private Task BackgroundUpdateLoopAsync(CancellationToken cancellationToken, bool runImmediate)
        => BackgroundUpdateLoop.RunAsync(RunBackgroundUpdatePassAsync, TimeSpan.FromHours(Host.Settings.UpdateCheckIntervalHours), runImmediate,
            (ex, retry) => StatusText = BackgroundUpdateLoop.FailedStatus(ex, retry), cancellationToken);

    // One pass: the component updates (when that setting is on), then, with the gate released, Follow newest (#177).
    private async Task RunBackgroundUpdatePassAsync(CancellationToken cancellationToken)
    {
        await RunBackgroundComponentPassAsync(cancellationToken);
        await FollowNewestAsync(cancellationToken);
    }

    private async Task RunBackgroundComponentPassAsync(CancellationToken cancellationToken)
    {
        if (!Host.Settings.AutoUpdateComponents || IsBusy) return;
        var games = Host.AllGames.ToArray();
        if (!await Task.Run(() => _updateAll.HasManagedTargets(games), cancellationToken)) return;
        using var op = Host.Operations.TryBegin("background-update", linkedToken: cancellationToken);
        if (op is null) return;
        try
        {
            var indexes = await PrepareUpdateIndexesAsync(games, op.Token);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Background,
                op.Token);
            if (result.RunningGames.Count > 0) DeferUntilClosed(result.RunningGames, cancellationToken); // #242
            if (result.ChangedCount > 0)
            {
                await InvalidateAllAndReprobeSelectedAsync();
                StatusText = UiLocalizationService.UpdateAllSummary(result) + Shell.F5Driver.UpdateAllNote(games, result) + T("Vm.Background"); // w11/nr-gpu hook (#231)
            }
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            // The user cancelled this pass; the loop runs the next one on schedule.
            await InvalidateAllAndReprobeSelectedAsync();
            StatusText = OperationCancelledText() + T("Vm.Background");
        }
    }

    /// <summary>#242: time between two looks at the deferred running games (a test shortens it).</summary>
    internal TimeSpan DeferredRetryInterval { get; set; } = DeferredGameRetry.DefaultInterval;

    /// <summary>#242: the wait between those looks; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test drives it.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? DeferredRetryDelay { get; set; }

    /// <summary>#242: the running games the background loop waits for (null before its first deferral).</summary>
    internal DeferredGameRetry? DeferredRetry => _deferred;

    // #242: games a background pass deferred because they were running get a pass of their own once they close, while
    // automatic updates stay on. Manual Update All only reports them as deferred, as before.
    private void DeferUntilClosed(IReadOnlyList<DetectedGame> games, CancellationToken loopToken)
    {
        _deferred ??= new DeferredGameRetry(_updateAll.IsRunning, () => Host.Settings.AutoUpdateComponents, RunPassForClosedGameAsync,
            DeferredRetryInterval, DeferredRetryDelay);
        UiExceptionGuard.Forget(_deferred.Add(games, loopToken), "deferred-update-retry");
    }

    // One closed game's pass, with the same rules as the background pass (exclusions, holds, families). Waits for the gate;
    // true = keep waiting (the game runs again, or another operation took the gate first).
    private async Task<bool> RunPassForClosedGameAsync(DetectedGame deferred, CancellationToken cancellationToken)
    {
        await Host.Operations.WhenIdleAsync(cancellationToken);
        if (!Host.Settings.AutoUpdateComponents) return false;
        // The library may have been refreshed meanwhile: the game as it is now, or nothing when it is gone.
        var game = Host.AllGames.FirstOrDefault(candidate => Key(candidate) == Key(deferred));
        if (game is null) return false;
        using var op = Host.Operations.TryBegin("background-update", linkedToken: cancellationToken);
        if (op is null) return true;
        try
        {
            var indexes = await PrepareUpdateIndexesAsync([game], op.Token);
            var result = await _updateAll.RunAsync(
                [game],
                candidate => indexes.TryGetValue(Key(candidate), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Background,
                op.Token);
            if (result.ChangedCount > 0)
            {
                await InvalidateAllAndReprobeSelectedAsync();
                StatusText = ClosedGamePassStatus(game, result) + Shell.F5Driver.UpdateAllNote([game], result) + T("Vm.Background");
            }
            return result.AnotherPassRunning || result.RunningGames.Count > 0;
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            await InvalidateAllAndReprobeSelectedAsync();
            StatusText = OperationCancelledText() + T("Vm.Background");
            return false;
        }
    }

    /// <summary>#242: the status line of a closed game's pass: what ran and why, then the usual Update All counts.</summary>
    internal static string ClosedGamePassStatus(DetectedGame game, LinuxUpdateAllResult result)
        => UiLocalizationService.Format("Ua.DeferredUpdatesRan", game.Name) + " " + UiLocalizationService.UpdateAllSummary(result);

    /// <summary>#177: the background loop runs while component updates or either Follow newest toggle is on.</summary>
    internal static bool LoopWanted(LinuxAppSettings settings)
        => settings.AutoUpdateComponents || settings.FollowNewestDlss || settings.FollowNewestStreamline;

    /// <summary>Whether a Settings save must restart the background loop.</summary>
    internal static bool LoopSettingsChanged(LinuxAppSettings previous, LinuxAppSettings saved)
        => previous.AutoUpdateComponents != saved.AutoUpdateComponents
           || previous.UpdateCheckIntervalHours != saved.UpdateCheckIntervalHours
           || previous.FollowNewestDlss != saved.FollowNewestDlss
           || previous.FollowNewestStreamline != saved.FollowNewestStreamline;

    /// <summary>A restarted loop runs at once when component updates are on, or when a Follow newest toggle was just
    /// turned on (its first pass seeds the baseline from today's manifest).</summary>
    internal static bool RunsImmediatelyAfterSave(LinuxAppSettings previous, LinuxAppSettings saved)
        => saved.AutoUpdateComponents
           || (saved.FollowNewestDlss && !previous.FollowNewestDlss)
           || (saved.FollowNewestStreamline && !previous.FollowNewestStreamline);

    /// <summary>
    /// #177: a Full Refresh (start-up's included) asks for a Follow newest pass once the gate is free; a request made
    /// while one is pending joins it. Runs with the toggles off too: that pass only forgets the baselines.
    /// </summary>
    internal Task RequestFollowNewest()
    {
        if (_followRequest is { IsCompleted: false } pending) return pending;
        var request = FollowNewestWhenIdleAsync();
        _followRequest = request;
        UiExceptionGuard.Forget(request, "dlss-follow");
        return request;
    }

    /// <summary>The last Follow newest pass a Full Refresh asked for (tests wait on it).</summary>
    internal Task? LastFollowRequest => _followRequest;

    private async Task FollowNewestWhenIdleAsync()
    {
        await Task.Yield(); // the refresh that asked still holds the gate until it returns
        var token = _followCts.Token;
        try { await FollowNewestAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* the shell closed */ }
    }

    /// <summary>
    /// #177: one Follow newest pass. The manifest check runs without the busy gate (nothing changes on disk but the
    /// baseline file); only a pass that found a newer version takes the gate as a background operation (Cancel between
    /// games) and swaps the files. A gate still taken after waiting for it skips the pass: the next one repeats it.
    /// </summary>
    internal async Task FollowNewestAsync(CancellationToken cancellationToken = default)
    {
        if (Shell.UpdatePolicy is not { } policy) return;
        var choice = UpdatePolicyFeatures.ChoiceFrom(Host.Settings);
        var check = await Task.Run(() => policy.Follow.CheckAsync(choice, cancellationToken), cancellationToken);
        if (!check.NeedsApply) return;
        await Host.Operations.WhenIdleAsync(cancellationToken);
        using var op = Host.Operations.TryBegin("dlss-follow", linkedToken: cancellationToken);
        if (op is null) return;
        var games = Host.AllGames.ToArray();
        // File indexes the selection analysis already built; the service builds the rest itself, off the UI thread.
        var cached = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
            if (Host.Session.CachedFiles(game) is { } files) cached[Key(game)] = files;
        try
        {
            var result = await Task.Run(() => policy.Follow.ApplyAsync(check, games,
                game => cached.TryGetValue(Key(game), out var files) ? files : null, op.Token));
            if (result.UpdatedCount > 0) await InvalidateAllAndReprobeSelectedAsync();
            if (UpdatePolicyFeatures.FollowSummary(result) is { } line) StatusText = line;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            await InvalidateAllAndReprobeSelectedAsync(); // games finished before the cancel were swapped
            StatusText = OperationCancelledText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("dlss-follow", "Follow newest DLSS/Streamline pass failed", ex);
            StatusText = TF("Upd.FollowFailed", UiLocalizationService.Present(ex.Message));
        }
    }
}
