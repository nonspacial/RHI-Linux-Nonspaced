using RHI.Core.Deployment;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public enum ComponentUpdateAction
{
    Install,
    Update,
    Remove
}

/// <summary>One component lifecycle event for one game (port of upstream 2.7.9 UpdateLogEntry).</summary>
public sealed record ComponentUpdateLogEntry
{
    public DateTime TimestampUtc { get; init; }
    public string GameName { get; init; } = "";
    public string GameKey { get; init; } = "";
    [JsonConverter(typeof(SafeManagedComponentKindConverter))]
    public ManagedComponentKind Component { get; init; }
    public string ComponentName { get; init; } = "";
    public ComponentUpdateAction Action { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    public bool Success { get; init; } = true;
    public string? Message { get; init; }
}

public sealed record ComponentUpdateLogGroup(DateTime LocalDate, IReadOnlyList<ComponentUpdateLogEntry> Entries);

/// <summary>
/// Bounded, newest-first history of component installs/updates/removals. Writes go through
/// <c>AtomicFile.WriteStoreFile</c> (unique temp, fsync, verify, rename, directory fsync). An unreadable file is copied aside and the store then refuses
/// to record until the user clears it, so a parse error never silently drops the history.
/// A failed write (disk full, no permission) keeps the events in memory, logs the failure once and reports it through
/// <see cref="WriteError"/>; the next write that succeeds saves them with it and clears the error (Refs #111).
/// </summary>
public sealed class ComponentUpdateLogStore : IGameStateStore
{
    public const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();
    private string? _corruptQuarantinePath;
    // Newest first: events recorded while the file could not be written; saved with the next write that succeeds.
    private readonly List<ComponentUpdateLogEntry> _unsaved = [];
    private string? _writeError;

    public ComponentUpdateLogStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "update-log.json");
    }

    public string FilePath => _path;

    /// <summary>Non-null when the log on disk is unreadable; points at the quarantined copy.</summary>
    public string? CorruptLogPath
    {
        get { lock (_gate) { LoadUnsafe(); return _corruptQuarantinePath; } }
    }

    /// <summary>
    /// Why the last write of the history failed, or null once a write succeeded again. The events recorded meanwhile
    /// (<see cref="UnsavedCount"/>) are listed by <see cref="GetAll"/> and saved with the next successful write.
    /// </summary>
    public string? WriteError
    {
        get { lock (_gate) return _writeError; }
    }

    /// <summary>Events kept in memory because the history file could not be written.</summary>
    public int UnsavedCount
    {
        get { lock (_gate) return _unsaved.Count; }
    }

    /// <summary>Newest first, including events not yet written (see <see cref="WriteError"/>).</summary>
    public IReadOnlyList<ComponentUpdateLogEntry> GetAll()
    {
        lock (_gate)
            return WithUnsaved(LoadUnsafe());
    }

    /// <summary>
    /// Returns false when the entry was not written: a corrupt log, a repeat of the latest failure, or a write that
    /// failed (then the entry is kept in memory and saved with the next write that succeeds; see <see cref="WriteError"/>).
    /// </summary>
    public bool Record(ComponentUpdateLogEntry entry)
    {
        lock (_gate)
        {
            var onDisk = LoadUnsafe();
            if (_corruptQuarantinePath is not null) return false;
            var entries = WithUnsaved(onDisk);

            // A background pass retrying the same broken update every few hours must not
            // push the whole history out of the bounded log.
            if (!entry.Success && entries.FirstOrDefault(e => e.GameKey == entry.GameKey && e.Component == entry.Component) is
                { Success: false } last && last.Action == entry.Action && last.Message == entry.Message)
                return false;

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            try
            {
                SaveUnsafe(entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // History is informational: never fail the install, keep the event for the next write, say so once.
                _unsaved.Insert(0, entry);
                if (_unsaved.Count > MaxEntries) _unsaved.RemoveRange(MaxEntries, _unsaved.Count - MaxEntries);
                if (_writeError is null)
                    AppLog.Warn("update-history", $"The update history could not be written to {_path}; new events are kept in memory until it can be.", ex);
                _writeError = ex.Message;
                return false;
            }
            WriteSucceeded();
            return true;
        }
    }

    /// <summary>Empties the history. On a corrupt log this starts a new one; the quarantined copy is kept.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            LoadUnsafe();
            if (_corruptQuarantinePath == _path)
                throw new InvalidOperationException(
                    $"The update history at {_path} is unreadable and could not be copied aside; move it manually before clearing.");
            SaveUnsafe([]);
            _corruptQuarantinePath = null;
            WriteSucceeded();
        }
    }

    private void WriteSucceeded()
    {
        if (_writeError is not null)
            AppLog.Warn("update-history", $"The update history at {_path} can be written again; {_unsaved.Count} event(s) kept in memory were saved.");
        _unsaved.Clear();
        _writeError = null;
    }

    /// <summary>The events not yet written, then those on disk (each list is newest first; unsaved ones are newer).</summary>
    private List<ComponentUpdateLogEntry> WithUnsaved(List<ComponentUpdateLogEntry> onDisk)
    {
        if (_unsaved.Count == 0) return onDisk;
        return _unsaved.Concat(onDisk).Take(MaxEntries).ToList();
    }

    /// <summary>
    /// "Repair after move" (Refs #38): the history of a folder-keyed game follows it to its new key.
    /// Writes nothing. Throws <see cref="IOException"/> when the history is unreadable (clear it
    /// first) or changes while it is read.
    /// </summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (move.OldRecordKeys.Count == 0) return null;
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            var before = File.ReadAllBytes(_path);
            var entries = LoadUnsafe();
            if (_corruptQuarantinePath is not null)
                throw new IOException($"{UnreadablePrefix}{_path}{UnreadableSuffix}");
            if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(before))
                throw new IOException($"{_path} changed while it was being read. Nothing was changed; try again.");
            var changed = false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (move.MovedRecordKey(entries[i].GameKey) is not { } key) continue;
                entries[i] = entries[i] with { GameKey = key };
                changed = true;
            }
            return changed
                ? new StagedFileWrite(Path.GetFullPath(_path), before, JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions))
                : null;
        }
    }

    public const string UnreadablePrefix = "The update history cannot be read: ";
    public const string UnreadableSuffix = ". Clear the history (a copy of it is kept), then try again.";

    public static IReadOnlyList<ComponentUpdateLogGroup> GroupByLocalDate(IEnumerable<ComponentUpdateLogEntry> entries)
        => entries
            .GroupBy(e => e.TimestampUtc.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new ComponentUpdateLogGroup(g.Key, g.OrderByDescending(e => e.TimestampUtc).ToArray()))
            .ToArray();

    private List<ComponentUpdateLogEntry> LoadUnsafe()
    {
        if (!File.Exists(_path))
        {
            _corruptQuarantinePath = null;
            return [];
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<ComponentUpdateLogEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
            if (loaded.Any(e => e is null)) throw new JsonException("Null update-log entry.");
            _corruptQuarantinePath = null;
            return loaded
                .Select(e => e with { TimestampUtc = AsUtc(e.TimestampUtc) })
                .OrderByDescending(e => e.TimestampUtc)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (_corruptQuarantinePath is null)
            {
                _corruptQuarantinePath = Quarantine();
                AppLog.Warn("update-history", $"The update history at {_path} cannot be read; recording stops until it is cleared (copy: {_corruptQuarantinePath}).", ex);
            }
            return [];
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private string Quarantine()
    {
        try
        {
            var target = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            AtomicFile.Copy(_path, target, overwrite: false); // verified, durable (#31)
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("update-history", $"The unreadable update history at {_path} could not be copied aside.", ex);
            return _path;
        }
    }

    /// <summary>
    /// The store-file writer: a unique temp per write (two instances never share one), fsync, full verify, rename,
    /// directory fsync; a symlinked log stays a link and keeps its mode.
    /// </summary>
    private void SaveUnsafe(List<ComponentUpdateLogEntry> entries)
        => RHI.Core.Deployment.AtomicFile.WriteStoreFile(_path, JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions));
}
