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
    private readonly Func<LinuxDeploymentRecord, string?> _renoDxVersion;

    public ComponentUpdateRecorder(LinuxDeploymentRecordStore records, ComponentUpdateLogStore log, Func<DateTime>? utcNow = null)
        : this(records, log, utcNow, RenoDxDeploymentService.InstalledAddonVersion)
    {
    }

    // Test seam: the RenoDX add-on version reader (file I/O for records from older builds).
    internal ComponentUpdateRecorder(
        LinuxDeploymentRecordStore records,
        ComponentUpdateLogStore log,
        Func<DateTime>? utcNow,
        Func<LinuxDeploymentRecord, string?> renoDxVersion)
    {
        _records = records;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _renoDxVersion = renoDxVersion;
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

    /// <summary>
    /// Wraps an in-place swap of one file owned by <paramref name="owner"/> whose version lives in the
    /// owner's record metadata rather than in <see cref="LinuxDeploymentRecord.Version"/> (e.g. the
    /// nvngx_dlssnr.dll inside a ShortFuse or DLSS5 Feeder Full stack). Logs one Update entry when the
    /// version read by <paramref name="versionOf"/> changes, and one failed entry when the swap fails.
    /// </summary>
    public async Task<ComponentOperationResult> TrackOwnedFileAsync(
        DetectedGame game,
        ManagedComponentKind owner,
        string fileLabel,
        Func<LinuxDeploymentRecord, string?> versionOf,
        Func<Task<ComponentOperationResult>> operation)
    {
        var (beforeKnown, before) = OwnedVersion(game, owner, versionOf);
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
            try
            {
                var failed = result is { Success: false } || (error is not null && error is not OperationCanceledException);
                var (afterKnown, after) = OwnedVersion(game, owner, versionOf);
                ComponentUpdateLogEntry? entry = null;
                if (failed)
                    entry = OwnedEntry(game, owner, fileLabel, before, null) with { Success = false, Message = result?.Message ?? error?.Message };
                else if (result is { Success: true } && beforeKnown && afterKnown && !string.Equals(before, after, StringComparison.Ordinal))
                    entry = OwnedEntry(game, owner, fileLabel, before, after);
                if (entry is not null) _log.Record(entry);
            }
            catch
            {
                // History is informational; it must never fail or alter the swap.
            }
        }
    }

    private (bool Known, string? Version) OwnedVersion(DetectedGame game, ManagedComponentKind owner, Func<LinuxDeploymentRecord, string?> versionOf)
    {
        try
        {
            var record = _records.Find(game, owner);
            return (true, record is null ? null : versionOf(record));
        }
        catch
        {
            return (false, null);
        }
    }

    private ComponentUpdateLogEntry OwnedEntry(DetectedGame game, ManagedComponentKind owner, string fileLabel, string? from, string? to) => new()
    {
        TimestampUtc = _utcNow(),
        GameName = game.Name,
        GameKey = LinuxDeploymentRecordStore.GameKey(game),
        Component = owner,
        ComponentName = $"{LinuxManagedComponentService.DisplayDependencyName(owner)} ({fileLabel})",
        Action = ComponentUpdateAction.Update,
        FromVersion = from,
        ToVersion = to
    };

    private Dictionary<ManagedComponentKind, (string? Version, DateTime InstalledAtUtc)> Snapshot(DetectedGame game)
    {
        try
        {
            // Copy values: some services mutate the cached record in place before upserting.
            return _records.FindAll(game)
                .GroupBy(r => r.Component)
                .ToDictionary(g => g.Key, g => (HistoryVersion(g.First()), g.First().InstalledAtUtc));
        }
        catch
        {
            return [];
        }
    }

    // RenoDX records keep the catalogue mod name in Version; a rebuilt snapshot keeps that name,
    // so history diffs the add-on file's own version instead (upstream 2.8.0). A failed read falls
    // back to the record's Version for that record only: an empty snapshot would log every other
    // component as installed or removed.
    private string? HistoryVersion(LinuxDeploymentRecord record)
    {
        if (record.Component != ManagedComponentKind.RenoDX) return record.Version;
        try
        {
            return _renoDxVersion(record) ?? record.Version;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return record.Version;
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
            if (failed)
            {
                // A failed operation may still have changed the requested component's ledger entry (a service
                // upserts, then a later step fails and its files are rolled back): that change is the failure,
                // never a success. Dependencies it really installed or removed on the way stay as they are.
                var message = result?.Message ?? error?.Message;
                var marked = false;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Component != requested) continue;
                    entries[i] = entries[i] with { Success = false, Message = message };
                    marked = true;
                }
                if (!marked)
                    entries.Add(Entry(requested, intent, before.TryGetValue(requested, out var prior) ? prior.Version : null, null) with
                    {
                        Success = false,
                        Message = message
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
