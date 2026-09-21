using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

public enum LumaModePreference
{
    Auto,
    Enabled,
    Disabled
}

/// <summary>
/// Persists an explicit per-game Luma mode choice independently from manifest defaults.
/// Auto means "follow the upstream manifest/default policy". Enabled/Disabled are
/// user authority and survive refresh/restart.
/// </summary>
public sealed class LumaModePreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LumaModePreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "luma-mode.json");
    }

    public LumaModePreference Get(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var map = LoadUnsafe();
            return map.TryGetValue(LinuxDeploymentRecordStore.GameKey(game), out var value)
                ? value
                : LumaModePreference.Auto;
        }
    }

    public void Set(DetectedGame game, LumaModePreference preference)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var map = LoadUnsafe();
            var key = LinuxDeploymentRecordStore.GameKey(game);
            if (preference == LumaModePreference.Auto) map.Remove(key);
            else map[key] = preference;
            SaveUnsafe(map);
        }
    }

    private Dictionary<string, LumaModePreference> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, LumaModePreference>>(
                File.ReadAllText(_path), JsonOptions) ?? new();
            return new Dictionary<string, LumaModePreference>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void SaveUnsafe(Dictionary<string, LumaModePreference> map)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
