using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

public sealed class Dlssg2030Preferences
{
    public string GpuGeneration { get; set; } = Dlssg2030UnlockDeploymentService.GpuGenRtx20;
    public string ProxyName { get; set; } = Dlssg2030UnlockDeploymentService.DefaultProxyName;

    public Dlssg2030Preferences Normalize()
    {
        GpuGeneration = Dlssg2030UnlockDeploymentService.NormalizeGpuGeneration(GpuGeneration);
        ProxyName = Dlssg2030UnlockDeploymentService.NormalizeProxyName(ProxyName);
        return this;
    }
}

/// <summary>
/// XDG-native per-game preferences for the legacy RTX 20 / SM75 experimental path.
/// Preferences are deliberately separate from deployment ownership so uninstalling the
/// component does not forget the user's preferred GPU route/proxy for a later field test.
/// </summary>
public sealed class Dlssg2030PreferenceStore
{
    private readonly string _path;
    private readonly JsonFileStore<Dictionary<string, Dlssg2030Preferences>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dlssg2030PreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "dlssg2030-preferences.json");
        _file = new JsonFileStore<Dictionary<string, Dlssg2030Preferences>>(_path, JsonOptions);
    }

    public Dlssg2030Preferences Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (!GameRecordIdentity.TryGet(entries, game, out var preferences))
                return new Dlssg2030Preferences();

            var normalized = (preferences ?? new Dlssg2030Preferences()).Normalize();
            // New RTX 30 installs use Universal MFG instead of the legacy SM86 backend.
            // Migrate saved (not deployed) RTX 30 preferences to the remaining RTX 20 / SM75 route.
            if (string.Equals(normalized.GpuGeneration, Dlssg2030UnlockDeploymentService.GpuGenRtx30, StringComparison.OrdinalIgnoreCase))
                normalized.GpuGeneration = Dlssg2030UnlockDeploymentService.GpuGenRtx20;
            return normalized;
        }
    }

    public void Set(DetectedGame game, Dlssg2030Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var entries = LoadUnsafe();
            GameRecordIdentity.Set(entries, game, preferences.Normalize());
            SaveUnsafe(entries);
        }
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> when the file is unreadable, so an install can refuse before touching the game.</summary>
    public void EnsureWritable()
    {
        lock (_gate) _file.EnsureWritable();
    }

    private Dictionary<string, Dlssg2030Preferences> LoadUnsafe()
        => JsonStoreMaps.CaseInsensitive(_file.Load().Value);

    private void SaveUnsafe(Dictionary<string, Dlssg2030Preferences> entries) => _file.Save(entries);
}
