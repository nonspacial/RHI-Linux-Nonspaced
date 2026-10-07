using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

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
    private readonly JsonFileStore<Dictionary<string, Rtx40MfgPreferences>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Rtx40MfgPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "rtx40-mfg-standalone-preferences.json");
        _file = new JsonFileStore<Dictionary<string, Rtx40MfgPreferences>>(_path, JsonOptions);
    }

    public Rtx40MfgPreferences Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return GameRecordIdentity.TryGet(entries, game, out var preferences)
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
            GameRecordIdentity.Set(entries, game, preferences.Normalize());
            SaveUnsafe(entries);
        }
    }

    private Dictionary<string, Rtx40MfgPreferences> LoadUnsafe()
        => new(_file.Load().Value ?? new Dictionary<string, Rtx40MfgPreferences>(), StringComparer.OrdinalIgnoreCase);

    private void SaveUnsafe(Dictionary<string, Rtx40MfgPreferences> entries) => _file.Save(entries);
}
