using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

public sealed class StandaloneDlssEnablerPreferences
{
    public string ProxyName { get; set; } = StandaloneDlssEnablerDeploymentService.DefaultProxyName;

    public StandaloneDlssEnablerPreferences Normalize()
    {
        ProxyName = StandaloneDlssEnablerDeploymentService.NormalizeProxyName(ProxyName);
        return this;
    }
}

/// <summary>
/// XDG-native per-game proxy preference for the standalone DLSS Enabler path.
/// The preference is intentionally separate from deployment ownership so uninstalling
/// the component does not forget the user's selected proxy for a later reinstall.
/// </summary>
public sealed class StandaloneDlssEnablerPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StandaloneDlssEnablerPreferenceStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "dlss-enabler-standalone-preferences.json");

    public StandaloneDlssEnablerPreferences Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return entries.TryGetValue(LinuxDeploymentRecordStore.GameKey(game), out var preferences)
                ? (preferences ?? new StandaloneDlssEnablerPreferences()).Normalize()
                : new StandaloneDlssEnablerPreferences();
        }
    }

    public void Set(DetectedGame game, StandaloneDlssEnablerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var entries = LoadUnsafe();
            entries[LinuxDeploymentRecordStore.GameKey(game)] = preferences.Normalize();
            SaveUnsafe(entries);
        }
    }

    private Dictionary<string, StandaloneDlssEnablerPreferences> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, StandaloneDlssEnablerPreferences>>(File.ReadAllText(_path), JsonOptions)
                         ?? new Dictionary<string, StandaloneDlssEnablerPreferences>();
            return new Dictionary<string, StandaloneDlssEnablerPreferences>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe(Dictionary<string, StandaloneDlssEnablerPreferences> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
