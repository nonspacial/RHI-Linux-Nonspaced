using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Stores optional GitHub release-tag pins for Neural Rendering add-ons.
/// A missing value means Latest and preserves the upstream auto-update behaviour.
/// </summary>
public sealed class NeuralAddonVersionPreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NeuralAddonVersionPreferenceStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "neural-addon-version-pins.json");

    public string? Get(DetectedGame game, ManagedComponentKind kind)
    {
        EnsureSupported(kind);
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return entries.TryGetValue(Key(game, kind), out var tag) && IsSafeTag(tag) ? tag : null;
        }
    }

    public void Set(DetectedGame game, ManagedComponentKind kind, string? releaseTag)
    {
        EnsureSupported(kind);
        var normalized = string.IsNullOrWhiteSpace(releaseTag) ? null : releaseTag.Trim();
        if (normalized is not null && !IsSafeTag(normalized))
            throw new ArgumentException("GitHub release tag contains unsafe characters.", nameof(releaseTag));

        lock (_gate)
        {
            var entries = LoadUnsafe();
            var key = Key(game, kind);
            if (normalized is null) entries.Remove(key);
            else entries[key] = normalized;
            SaveUnsafe(entries);
        }
    }

    private static string Key(DetectedGame game, ManagedComponentKind kind)
        => $"{LinuxDeploymentRecordStore.GameKey(game)}|{kind}";

    private static void EnsureSupported(ManagedComponentKind kind)
    {
        if (kind is not (ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Version pins are supported only for Neural Rendering add-ons.");
    }

    internal static bool IsSafeTag(string value)
        => value.Length is > 0 and <= 128
           && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant);

    private Dictionary<string, string> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), JsonOptions)
                         ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
