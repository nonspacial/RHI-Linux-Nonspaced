using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Update All and the background update pass (epic #89 unit 7): the toolbar's Update All (cancellable from the busy
/// overlay, Refs #68), the per-game file indexes it needs, and the background loop that Settings starts and stops.
/// </summary>
public sealed partial class UpdatesViewModel : PanelViewModel
{
    private readonly LinuxUpdateAllService _updateAll;

    internal UpdatesViewModel(IPanelHost host, LinuxUpdateAllService updateAll) : base(host) => _updateAll = updateAll;

    private CancellationTokenSource? _backgroundUpdateCts;

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
        try { _backgroundUpdateCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already stopped */ }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
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
            StatusText = UiLocalizationService.UpdateAllSummary(result);
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
        if (!Host.Settings.AutoUpdateComponents) return;
        _backgroundUpdateCts = new CancellationTokenSource();
        UiExceptionGuard.Forget(BackgroundUpdateLoopAsync(_backgroundUpdateCts.Token, runImmediate), "BackgroundUpdateLoopAsync");
    }

    // A failed pass is logged, reported and retried with back-off; the loop ends only when cancelled (#81).
    private Task BackgroundUpdateLoopAsync(CancellationToken cancellationToken, bool runImmediate)
        => BackgroundUpdateLoop.RunAsync(RunBackgroundUpdatePassAsync, TimeSpan.FromHours(Host.Settings.UpdateCheckIntervalHours), runImmediate,
            (ex, retry) => StatusText = BackgroundUpdateLoop.FailedStatus(ex, retry), cancellationToken);

    private async Task RunBackgroundUpdatePassAsync(CancellationToken cancellationToken)
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
            if (result.ChangedCount > 0)
            {
                await InvalidateAllAndReprobeSelectedAsync();
                StatusText = UiLocalizationService.UpdateAllSummary(result) + T("Vm.Background");
            }
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            // The user cancelled this pass; the loop runs the next one on schedule.
            await InvalidateAllAndReprobeSelectedAsync();
            StatusText = OperationCancelledText() + T("Vm.Background");
        }
    }
}
