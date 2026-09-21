using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

public sealed class Rtx40MfgPreferences
{
    public string GpuGeneration { get; set; } = Rtx40MfgStandaloneDeploymentService.GpuGenRtx40;
    public string ProxyName { get; set; } = Rtx40MfgStandaloneDeploymentService.DefaultProxyName;

    public Rtx40MfgPreferences Normalize()
    {
        GpuGeneration = Rtx40MfgStandaloneDeploymentService.NormalizeGpuGeneration(GpuGeneration);
        ProxyName = Rtx40MfgStandaloneDeploymentService.NormalizeProxyName(ProxyName);
        return this;
    }
}

/// <summary>
/// XDG-native per-game GPU-generation and proxy preferences for Universal MFG.
/// The preference is intentionally separate from deployment ownership so uninstalling
/// the component does not forget the user's selected proxy for a later reinstall.
/// </summary>
public sealed class Rtx40MfgPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Rtx40MfgPreferenceStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "rtx40-mfg-standalone-preferences.json");

    public Rtx40MfgPreferences Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return entries.TryGetValue(LinuxDeploymentRecordStore.GameKey(game), out var preferences)
                ? (preferences ?? new Rtx40MfgPreferences()).Normalize()
                : new Rtx40MfgPreferences();
        }
    }

    public void Set(DetectedGame game, Rtx40MfgPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var entries = LoadUnsafe();
            entries[LinuxDeploymentRecordStore.GameKey(game)] = preferences.Normalize();
            SaveUnsafe(entries);
        }
    }

    private Dictionary<string, Rtx40MfgPreferences> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Rtx40MfgPreferences>>(File.ReadAllText(_path), JsonOptions)
                         ?? new Dictionary<string, Rtx40MfgPreferences>();
            return new Dictionary<string, Rtx40MfgPreferences>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe(Dictionary<string, Rtx40MfgPreferences> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
