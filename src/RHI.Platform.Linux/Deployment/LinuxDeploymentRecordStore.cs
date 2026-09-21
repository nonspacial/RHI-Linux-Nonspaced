using System.Text.Json;
using System.Text.Json.Serialization;
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

    public LinuxDeploymentRecordStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.DataHome, "rhi-linux", "deployments.json");
    }

    public LinuxDeploymentRecord? Find(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
            return LoadUnsafe().FirstOrDefault(r => r.GameKey == GameKey(game) && r.Component == component);
    }

    public IReadOnlyList<LinuxDeploymentRecord> FindAll(DetectedGame game)
    {
        lock (_gate)
            return LoadUnsafe().Where(r => r.GameKey == GameKey(game)).ToArray();
    }

    public void Upsert(LinuxDeploymentRecord record)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            var index = records.FindIndex(r => r.GameKey == record.GameKey && r.Component == record.Component);
            if (index >= 0) records[index] = record; else records.Add(record);
            SaveUnsafe(records);
        }
    }

    public void Remove(DetectedGame game, ManagedComponentKind component)
    {
        lock (_gate)
        {
            var records = LoadUnsafe();
            records.RemoveAll(r => r.GameKey == GameKey(game) && r.Component == component);
            SaveUnsafe(records);
        }
    }

    public static string GameKey(DetectedGame game)
        => game.SteamAppId is int id ? $"steam:{id}" : Path.GetFullPath(game.InstallPath);

    private List<LinuxDeploymentRecord> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var records = JsonSerializer.Deserialize<List<LinuxDeploymentRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
            records.RemoveAll(record => record.Component == ManagedComponentKind.Unknown);
            foreach (var record in records)
            {
                record.Metadata = record.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase);
            }
            return records;
        }
        catch { return []; }
    }

    private void SaveUnsafe(List<LinuxDeploymentRecord> records)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temp, _path, overwrite: true);
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
