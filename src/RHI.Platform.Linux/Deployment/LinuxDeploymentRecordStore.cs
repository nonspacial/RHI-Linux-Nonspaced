using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

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
        get { lock (_gate) { LoadUnsafe(); return _corruptQuarantinePath; } }
    }

    public LinuxDeploymentRecord? Find(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
        {
            var record = LoadUnsafe().FirstOrDefault(r => r.GameKey == GameKey(game) && r.Component == component);
            return record is null ? null : Clone(record);
        }
    }

    public IReadOnlyList<LinuxDeploymentRecord> FindAll(DetectedGame game)
    {
        lock (_gate)
            return LoadUnsafe().Where(r => r.GameKey == GameKey(game)).Select(Clone).ToArray();
    }

    public void Upsert(LinuxDeploymentRecord record)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            EnsureWritable();
            var index = records.FindIndex(r => r.GameKey == record.GameKey && r.Component == record.Component);
            var copy = Clone(record);
            if (index >= 0) records[index] = copy; else records.Add(copy);
            SaveUnsafe(records);
        }
    }

    public void Remove(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            EnsureWritable();
            records.RemoveAll(r => r.GameKey == GameKey(game) && r.Component == component);
            SaveUnsafe(records);
        }
    }

    /// <summary>Deep copy, including files, directories, Wine overrides and metadata.</summary>
    public static LinuxDeploymentRecord Clone(LinuxDeploymentRecord record)
    {
        var copy = JsonSerializer.Deserialize<LinuxDeploymentRecord>(
            JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions), JsonOptions)!;
        copy.Metadata = record.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    public static string GameKey(DetectedGame game)
        => game.SteamAppId is int id ? $"steam:{id}" : Path.GetFullPath(game.InstallPath);

    private void EnsureWritable()
    {
        if (_corruptQuarantinePath is not null)
            throw new InvalidOperationException(
                $"The RHI deployment ledger at {_path} is unreadable and was copied to {_corruptQuarantinePath}. " +
                "Repair or remove it before installing or removing components, otherwise existing backups would be orphaned.");
    }

    private List<LinuxDeploymentRecord> LoadUnsafe()
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
            return new List<LinuxDeploymentRecord>(_cached);

        try
        {
            var records = JsonSerializer.Deserialize<List<LinuxDeploymentRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
            records.RemoveAll(record => record.Component == ManagedComponentKind.Unknown);
            foreach (var record in records)
            {
                record.Metadata = record.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
            }
            _cached = records;
            _cachedStamp = stamp;
            _corruptQuarantinePath = null;
            return new List<LinuxDeploymentRecord>(records);
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
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Unique temp, fsync, full verify, rename, directory fsync (#31).
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions));
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
