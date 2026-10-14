using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed class LinuxDeploymentRecordStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new SafeManagedComponentKindConverter() }
    };

    // Same converters and naming as the ledger, without indentation: Clone runs on every lookup (#118).
    private static readonly JsonSerializerOptions CloneOptions = new(JsonOptions) { WriteIndented = false };

    // Read cache keyed on the file's (mtime, length). The UI evaluates dozens of record lookups
    // per selection change; without this every lookup re-read and re-parsed the file.
    // Cached instances are private: reads hand out deep copies and writes cache a copy, so a
    // caller that mutates a record and then fails to save cannot poison the next save.
    private List<LinuxDeploymentRecord>? _cached;
    private (DateTime Mtime, long Length)? _cachedStamp;

    // Set when the on-disk file exists but cannot be parsed. While set, the store refuses to
    // save: silently replacing a corrupt ledger with a near-empty one orphans every game's
    // original-file backups and registry overrides at once.
    private string? _corruptQuarantinePath;

    public LinuxDeploymentRecordStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "deployments.json");
    }

    /// <summary>Non-null when the ledger on disk is unreadable; points at the quarantined copy.</summary>
    public string? CorruptLedgerPath
    {
        get { lock (_gate) { ReadUnsafe(); return _corruptQuarantinePath; } }
    }

    /// <summary>Test seam: number of record lookups served (Find, FindAll, All).</summary>
    internal int LookupsForTesting { get; private set; }

    public LinuxDeploymentRecord? Find(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
        {
            LookupsForTesting++;
            var record = ForGame(ReadUnsafe(), game).FirstOrDefault(r => r.Component == component);
            return record is null ? null : Clone(record);
        }
    }

    public IReadOnlyList<LinuxDeploymentRecord> FindAll(DetectedGame game)
    {
        lock (_gate)
        {
            LookupsForTesting++;
            return ForGame(ReadUnsafe(), game).Select(Clone).ToArray();
        }
    }

    /// <summary>Copies of every record in the ledger (all games): crash recovery and relocation repair.</summary>
    public IReadOnlyList<LinuxDeploymentRecord> All()
    {
        lock (_gate)
        {
            LookupsForTesting++;
            return ReadUnsafe().Select(Clone).ToArray();
        }
    }

    /// <summary>
    /// Re-keys records written under a key an older build used for a game (Heroic games were keyed by
    /// install path) to the game's current, relocation-tolerant key, so they keep matching after the
    /// game is moved. Run after each library scan. A component that already has a record under the
    /// current key keeps both untouched. Returns the number of records migrated (Refs #38).
    /// </summary>
    public int MigrateGameKeys(IEnumerable<DetectedGame> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        lock (_gate)
        {
            // Copy: ReadUnsafe hands out the private cache, which must never be mutated in place.
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            if (_corruptQuarantinePath is not null) return 0;
            var migrated = 0;
            foreach (var game in games)
            {
                var legacy = GameRecordIdentity.LegacyKeys(game);
                if (legacy.Count == 0) continue;
                var key = GameKey(game);
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (!legacy.Contains(record.GameKey, StringComparer.Ordinal)) continue;
                    if (records.Any(r => r.GameKey == key && r.Component == record.Component)) continue;
                    var copy = Clone(record);
                    copy.GameKey = key;
                    records[i] = copy;
                    migrated++;
                }
            }
            if (migrated > 0) SaveUnsafe(records);
            return migrated;
        }
    }

    /// <summary>
    /// Prepares the relocation repair's ledger change for <see cref="StagedFileCommit"/> (written
    /// together with the game's other stores): <paramref name="remove"/> is matched by key,
    /// component and binary directory. Writes nothing; call <see cref="InvalidateCache"/> after the
    /// commit. Throws when the ledger is unreadable or changes while it is read.
    /// </summary>
    internal StagedFileWrite StageReplace(IReadOnlyList<LinuxDeploymentRecord> remove, IReadOnlyList<LinuxDeploymentRecord> add)
    {
        lock (_gate)
        {
            var before = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            EnsureWritable();
            var after = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            if (!(before ?? []).AsSpan().SequenceEqual(after ?? []) || (before is null) != (after is null))
                throw new IOException($"{_path} changed while it was being read. Nothing was changed; try again.");
            records.RemoveAll(r => remove.Any(x => x.GameKey == r.GameKey && x.Component == r.Component
                                                   && string.Equals(x.BinaryDirectory, r.BinaryDirectory, StringComparison.Ordinal)));
            records.AddRange(add.Select(Clone));
            return new StagedFileWrite(Path.GetFullPath(_path), before, JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions));
        }
    }

    /// <summary>Drops the read cache (the file was rewritten outside <see cref="SaveUnsafe"/>).</summary>
    internal void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedStamp = null;
        }
    }

    // Records under the current key first, so a legacy duplicate (left by a failed migration) never wins.
    private static IEnumerable<LinuxDeploymentRecord> ForGame(IReadOnlyList<LinuxDeploymentRecord> records, DetectedGame game)
    {
        var key = GameKey(game);
        var current = records.Where(r => r.GameKey == key).ToList();
        var legacy = GameRecordIdentity.LegacyKeys(game);
        if (legacy.Count == 0) return current;
        return current.Concat(records.Where(r => legacy.Contains(r.GameKey, StringComparer.Ordinal)
                                                 && current.All(c => c.Component != r.Component)));
    }

    public void Upsert(LinuxDeploymentRecord record)
    {
        lock (_gate)
        {
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            EnsureWritable();
            var index = records.FindIndex(r => r.GameKey == record.GameKey && r.Component == record.Component);
            var copy = Clone(record);
            copy.SavedAtUtc = DateTime.UtcNow;
            if (index >= 0) records[index] = copy; else records.Add(copy);
            SaveUnsafe(records);
        }
    }

    /// <summary>
    /// Puts <paramref name="operationId"/> under <see cref="LinuxDeploymentRecovery.RemovalOperationIdMetadataKey"/> on
    /// every record <see cref="Remove"/> would remove for this game and component (the removal's commit probe: the
    /// removal is committed once no record carries it). Leaves <see cref="LinuxDeploymentRecord.SavedAtUtc"/> as it is:
    /// nothing was installed, so the Wine recovery's "later record" check must not see one. Returns how many records
    /// were stamped (0 = nothing to remove; the ledger is not written). Throws while the ledger is unreadable.
    /// </summary>
    public int StampRemoval(DetectedGame game, ManagedComponentKind component, string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        lock (_gate)
        {
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            EnsureWritable();
            var stamped = 0;
            for (var i = 0; i < records.Count; i++)
            {
                if (!GameRecordIdentity.Matches(records[i].GameKey, game) || records[i].Component != component) continue;
                var copy = Clone(records[i]);
                copy.Metadata[LinuxDeploymentRecovery.RemovalOperationIdMetadataKey] = operationId;
                records[i] = copy;
                stamped++;
            }
            if (stamped > 0) SaveUnsafe(records);
            return stamped;
        }
    }

    /// <summary>
    /// Saves several record changes in one ledger write (all or nothing): each of <paramref name="upserts"/> replaces the
    /// record with its key and component (stamped like <see cref="Upsert"/>), each of <paramref name="removals"/> goes as
    /// <see cref="Remove"/> would take it. For operations that change two components' records at their commit point
    /// (ShortFuse renames ReShade's payload), so a crash can never leave one saved and the other not.
    /// </summary>
    public void Commit(IEnumerable<LinuxDeploymentRecord> upserts, IEnumerable<(DetectedGame Game, ManagedComponentKind Component)>? removals = null)
    {
        lock (_gate)
        {
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            EnsureWritable();
            foreach (var (game, component) in removals ?? [])
                records.RemoveAll(r => GameRecordIdentity.Matches(r.GameKey, game) && r.Component == component);
            var now = DateTime.UtcNow;
            foreach (var record in upserts)
            {
                var copy = Clone(record);
                copy.SavedAtUtc = now;
                var index = records.FindIndex(r => r.GameKey == record.GameKey && r.Component == record.Component);
                if (index >= 0) records[index] = copy; else records.Add(copy);
            }
            SaveUnsafe(records);
        }
    }

    public void Remove(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
        {
            var records = new List<LinuxDeploymentRecord>(ReadUnsafe());
            EnsureWritable();
            records.RemoveAll(r => GameRecordIdentity.Matches(r.GameKey, game) && r.Component == component);
            SaveUnsafe(records);
        }
    }

    /// <summary>Deep copy, including files, directories, Wine overrides and metadata.</summary>
    public static LinuxDeploymentRecord Clone(LinuxDeploymentRecord record)
    {
        var copy = JsonSerializer.Deserialize<LinuxDeploymentRecord>(
            JsonSerializer.SerializeToUtf8Bytes(record, CloneOptions), CloneOptions)!;
        copy.Metadata = record.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>The game's relocation-tolerant key (<see cref="GameRecordIdentity.Key"/>).</summary>
    public static string GameKey(DetectedGame game) => GameRecordIdentity.Key(game);

    private void EnsureWritable()
    {
        if (_corruptQuarantinePath is not null)
            throw new InvalidOperationException(
                $"The RHI deployment ledger at {_path} is unreadable and was copied to {_corruptQuarantinePath}. " +
                "Repair or remove it before installing or removing components, otherwise existing backups would be orphaned.");
    }

    /// <summary>
    /// The parsed ledger, re-read only when the file's (mtime, length) changed. The returned list is the
    /// private cache itself: callers under the lock read it (and clone what they hand out) or copy it
    /// before changing it; it is never mutated in place (#32).
    /// </summary>
    private IReadOnlyList<LinuxDeploymentRecord> ReadUnsafe()
    {
        if (!File.Exists(_path))
        {
            _cached = null;
            _cachedStamp = null;
            _corruptQuarantinePath = null;
            return [];
        }

        var info = new FileInfo(_path);
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (_cached is not null && _cachedStamp == stamp)
            return _cached;

        try
        {
            var records = JsonSerializer.Deserialize<List<LinuxDeploymentRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
            records.RemoveAll(record => record.Component == ManagedComponentKind.Unknown);
            foreach (var record in records)
            {
                record.Metadata = record.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : JsonStoreMaps.CaseInsensitive(record.Metadata);
            }
            _cached = records;
            _cachedStamp = stamp;
            _corruptQuarantinePath = null;
            return records;
        }
        catch
        {
            _cached = null;
            _cachedStamp = null;
            _corruptQuarantinePath ??= Quarantine();
            return [];
        }
    }

    private string? Quarantine()
    {
        try
        {
            var target = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            if (!File.Exists(target)) AtomicFile.Copy(_path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy failed; the ledger itself is still untouched (writes are refused), so point at it.
            return _path;
        }
    }

    /// <summary>Test seam: when set, every save fails with this exception before anything is written.</summary>
    internal Func<Exception>? SaveFaultForTesting { get; set; }

    private void SaveUnsafe(List<LinuxDeploymentRecord> records)
    {
        if (SaveFaultForTesting is { } fault) throw fault();
        // The store-file writer (#31, #57): unique temp, fsync, full verify, rename, directory fsync; a symlinked
        // deployments.json stays a link (its target is replaced) and keeps its mode, as every other store does.
        AtomicFile.WriteStoreFile(_path, JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions));
        _cached = records;
        var info = new FileInfo(_path);
        _cachedStamp = (info.LastWriteTimeUtc, info.Length);
    }
}

internal sealed class SafeManagedComponentKindConverter : JsonConverter<ManagedComponentKind>
{
    public override ManagedComponentKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            return Enum.TryParse<ManagedComponentKind>(text, ignoreCase: true, out var value)
                ? value
                : ManagedComponentKind.Unknown;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(ManagedComponentKind), numeric))
            return (ManagedComponentKind)numeric;
        return ManagedComponentKind.Unknown;
    }

    public override void Write(Utf8JsonWriter writer, ManagedComponentKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
