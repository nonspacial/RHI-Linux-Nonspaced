using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

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
    private readonly JsonFileStore<Dictionary<string, StandaloneDlssEnablerPreferences>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StandaloneDlssEnablerPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "dlss-enabler-standalone-preferences.json");
        _file = new JsonFileStore<Dictionary<string, StandaloneDlssEnablerPreferences>>(_path, JsonOptions);
    }

    public StandaloneDlssEnablerPreferences Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return GameRecordIdentity.TryGet(entries, game, out var preferences)
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
            GameRecordIdentity.Set(entries, game, preferences.Normalize());
            SaveUnsafe(entries);
        }
    }

    private Dictionary<string, StandaloneDlssEnablerPreferences> LoadUnsafe()
        => new(_file.Load().Value ?? new Dictionary<string, StandaloneDlssEnablerPreferences>(), StringComparer.OrdinalIgnoreCase);

    private void SaveUnsafe(Dictionary<string, StandaloneDlssEnablerPreferences> entries) => _file.Save(entries);
}
