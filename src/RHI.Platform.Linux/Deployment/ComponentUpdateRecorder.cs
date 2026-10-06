using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Wraps one component operation and writes the resulting install/update/remove events to the
/// update history. The deployment ledger is diffed before and after, so managed dependencies
/// installed alongside (e.g. ReShade for RenoDX) and conflicting components removed on the way
/// are recorded too. Recording never alters or fails the wrapped operation.
/// </summary>
public sealed class ComponentUpdateRecorder
{
    private readonly LinuxDeploymentRecordStore _records;
    private readonly ComponentUpdateLogStore _log;
    private readonly Func<DateTime> _utcNow;

    public ComponentUpdateRecorder(LinuxDeploymentRecordStore records, ComponentUpdateLogStore log, Func<DateTime>? utcNow = null)
    {
        _records = records;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<ComponentOperationResult> TrackAsync(
        DetectedGame game,
        ManagedComponentKind requested,
        ComponentUpdateAction intent,
        Func<Task<ComponentOperationResult>> operation)
    {
        var before = Snapshot(game);
        ComponentOperationResult? result = null;
        Exception? error = null;
        try
        {
            result = await operation().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            TryRecord(game, requested, intent, before, result, error);
        }
    }

    private Dictionary<ManagedComponentKind, (string? Version, DateTime InstalledAtUtc)> Snapshot(DetectedGame game)
    {
        try
        {
            // Copy values: some services mutate the cached record in place before upserting.
            return _records.FindAll(game)
                .GroupBy(r => r.Component)
                .ToDictionary(g => g.Key, g => (g.First().Version, g.First().InstalledAtUtc));
        }
        catch
        {
            return [];
        }
    }

    private void TryRecord(
        DetectedGame game,
        ManagedComponentKind requested,
        ComponentUpdateAction intent,
        Dictionary<ManagedComponentKind, (string? Version, DateTime InstalledAtUtc)> before,
        ComponentOperationResult? result,
        Exception? error)
    {
        try
        {
            var after = Snapshot(game);
            var now = _utcNow();
            var gameKey = LinuxDeploymentRecordStore.GameKey(game);
            ComponentUpdateLogEntry Entry(ManagedComponentKind kind, ComponentUpdateAction action, string? from, string? to) => new()
            {
                TimestampUtc = now,
                GameName = game.Name,
                GameKey = gameKey,
                Component = kind,
                ComponentName = LinuxManagedComponentService.DisplayDependencyName(kind),
                Action = action,
                FromVersion = from,
                ToVersion = to
            };

            var entries = new List<ComponentUpdateLogEntry>();
            foreach (var (kind, current) in after)
            {
                if (!before.TryGetValue(kind, out var was))
                    entries.Add(Entry(kind, ComponentUpdateAction.Install, null, current.Version));
                else if (!string.Equals(was.Version, current.Version, StringComparison.Ordinal))
                    entries.Add(Entry(kind, ComponentUpdateAction.Update, was.Version, current.Version));
                else if (kind == requested && intent == ComponentUpdateAction.Update && current.Version is null &&
                         was.InstalledAtUtc != current.InstalledAtUtc)
                    entries.Add(Entry(kind, ComponentUpdateAction.Update, null, null)); // unversioned payload refreshed
            }
            foreach (var (kind, was) in before)
            {
                if (!after.ContainsKey(kind))
                    entries.Add(Entry(kind, ComponentUpdateAction.Remove, was.Version, null));
            }

            var failed = result is { Success: false } || (error is not null && error is not OperationCanceledException);
            if (failed && entries.All(e => e.Component != requested))
            {
                entries.Add(Entry(requested, intent, before.TryGetValue(requested, out var prior) ? prior.Version : null, null) with
                {
                    Success = false,
                    Message = result?.Message ?? error?.Message
                });
            }

            // The requested component is recorded last so it heads the newest-first list.
            foreach (var entry in entries.OrderBy(e => e.Component == requested))
                _log.Record(entry);
        }
        catch
        {
            // History is informational; a full disk or corrupt log must not fail an install.
        }
    }
}
