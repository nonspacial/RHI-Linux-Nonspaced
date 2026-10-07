using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Stores optional GitHub release-tag pins for Neural Rendering add-ons.
/// A missing value means Latest and preserves the upstream auto-update behaviour.
/// </summary>
public sealed class NeuralAddonVersionPreferenceStore
{
    private readonly string _path;
    private readonly JsonFileStore<Dictionary<string, string>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NeuralAddonVersionPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "neural-addon-version-pins.json");
        _file = new JsonFileStore<Dictionary<string, string>>(_path, JsonOptions);
    }

    public string? Get(DetectedGame game, ManagedComponentKind kind)
    {
        EnsureSupported(kind);
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return GameRecordIdentity.TryGet(entries, game, out var tag, KindSuffix(kind)) && IsSafeTag(tag) ? tag : null;
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
            if (normalized is null) GameRecordIdentity.Remove(entries, game, KindSuffix(kind));
            else GameRecordIdentity.Set(entries, game, normalized, KindSuffix(kind));
            SaveUnsafe(entries);
        }
    }

    /// <summary>
    /// Per-game nvngx_dlssnr.dll manifest version for the orchestrated NR methods (ShortFuse,
    /// Feeder). Null means the newest manifest entry.
    /// </summary>
    public string? GetNeuralRenderingDllVersion(DetectedGame game)
    {
        lock (_gate)
            return GameRecordIdentity.TryGet(LoadUnsafe(), game, out var version, DllSuffix) && IsSafeDllVersion(version) ? version : null;
    }

    public void SetNeuralRenderingDllVersion(DetectedGame game, string? version)
    {
        var normalized = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (normalized is not null && !IsSafeDllVersion(normalized))
            throw new ArgumentException("DLSS NR version label is not valid.", nameof(version));
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (normalized is null) GameRecordIdentity.Remove(entries, game, DllSuffix);
            else GameRecordIdentity.Set(entries, game, normalized, DllSuffix);
            SaveUnsafe(entries);
        }
    }

    private const string DllSuffix = "|NeuralRenderingDll";

    // Manifest labels carry spaces and brackets, e.g. "310.8.2 (20/30/40/50)"; only control characters are refused.
    private static bool IsSafeDllVersion(string value)
        => value.Length is > 0 and <= 128 && !value.Any(char.IsControl);

    private static string KindSuffix(ManagedComponentKind kind) => $"|{kind}";

    private static void EnsureSupported(ManagedComponentKind kind)
    {
        if (kind is not (ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Version pins are supported only for Neural Rendering add-ons.");
    }

    internal static bool IsSafeTag(string value)
        => value.Length is > 0 and <= 128
           && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant);

    private Dictionary<string, string> LoadUnsafe()
        => JsonStoreMaps.CaseInsensitive(_file.Load().Value);

    private void SaveUnsafe(Dictionary<string, string> entries) => _file.Save(entries);
}
