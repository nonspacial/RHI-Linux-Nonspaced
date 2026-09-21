using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

/// <summary>Persists the preferred OptiScaler release channel per game before installation.</summary>
public sealed class OptiScalerVariantPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public OptiScalerVariantPreferenceStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "optiscaler-variants.json");

    public OptiScalerVariant Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return entries.TryGetValue(LinuxDeploymentRecordStore.GameKey(game), out var variant)
                ? variant
                : OptiScalerVariant.Stable;
        }
    }

    public void Set(DetectedGame game, OptiScalerVariant variant)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            var key = LinuxDeploymentRecordStore.GameKey(game);
            if (variant == OptiScalerVariant.Stable) entries.Remove(key);
            else entries[key] = variant;
            SaveUnsafe(entries);
        }
    }

    private Dictionary<string, OptiScalerVariant> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, OptiScalerVariant>>(File.ReadAllText(_path), JsonOptions)
                         ?? new Dictionary<string, OptiScalerVariant>();
            return new Dictionary<string, OptiScalerVariant>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe(Dictionary<string, OptiScalerVariant> entries)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
