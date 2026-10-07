using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
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
/// Bounded, newest-first history of component installs/updates/removals. Writes are atomic
/// (temp file, fsync, rename). An unreadable file is copied aside and the store then refuses
/// to record until the user clears it, so a parse error never silently drops the history.
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

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ComponentUpdateLogEntry> GetAll()
    {
        lock (_gate)
            return LoadUnsafe();
    }

    /// <summary>Returns false when the entry was not written (corrupt log, or a repeat of the latest failure).</summary>
    public bool Record(ComponentUpdateLogEntry entry)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (_corruptQuarantinePath is not null) return false;

            // A background pass retrying the same broken update every few hours must not
            // push the whole history out of the bounded log.
            if (!entry.Success && entries.FirstOrDefault(e => e.GameKey == entry.GameKey && e.Component == entry.Component) is
                { Success: false } last && last.Action == entry.Action && last.Message == entry.Message)
                return false;

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            SaveUnsafe(entries);
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
        }
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
        catch
        {
            _corruptQuarantinePath ??= Quarantine();
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
            File.Copy(_path, target, overwrite: false);
            return target;
        }
        catch
        {
            return _path;
        }
    }

    private void SaveUnsafe(List<ComponentUpdateLogEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entries, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, overwrite: true);
    }
}
