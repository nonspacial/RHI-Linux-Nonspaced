using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Per-game ShortFuse preferences: ASI auto-configuration and the "ZZZ load order" add-on name.
/// The upstream contract for both is opt-in: absence means OFF.
/// The older Linux format was a bare array of disabled game keys; because that format
/// cannot distinguish "explicitly re-enabled" from "never configured", it migrates safely
/// to OFF rather than silently renaming ReShade/installing UAL on the next ShortFuse install.
/// </summary>
public sealed class ShortFuseSettingsStore
{
    private sealed class SettingsDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 2;

        [JsonPropertyName("enabledGames")]
        public string[] EnabledGames { get; set; } = [];

        // Additive to version 2: older documents simply lack the list (OFF everywhere).
        [JsonPropertyName("zzzLoadOrderGames")]
        public string[]? ZzzLoadOrderGames { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();

    public ShortFuseSettingsStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "shortfuse-settings.json");

    public bool IsAutoConfigEnabled(DetectedGame game)
    {
        lock (_gate) return LoadUnsafe().EnabledGames.Contains(LinuxDeploymentRecordStore.GameKey(game));
    }

    public void SetAutoConfigEnabled(DetectedGame game, bool enabled)
    {
        lock (_gate)
        {
            var document = LoadUnsafe();
            document.EnabledGames = Toggle(document.EnabledGames, LinuxDeploymentRecordStore.GameKey(game), enabled);
            SaveUnsafe(document);
        }
    }

    /// <summary>True when the ShortFuse add-on should be deployed as <c>zzz_renodx-dlss.addon64</c>.</summary>
    public bool IsZzzLoadOrderEnabled(DetectedGame game)
    {
        lock (_gate) return (LoadUnsafe().ZzzLoadOrderGames ?? []).Contains(LinuxDeploymentRecordStore.GameKey(game));
    }

    public void SetZzzLoadOrderEnabled(DetectedGame game, bool enabled)
    {
        lock (_gate)
        {
            var document = LoadUnsafe();
            document.ZzzLoadOrderGames = Toggle(document.ZzzLoadOrderGames ?? [], LinuxDeploymentRecordStore.GameKey(game), enabled);
            SaveUnsafe(document);
        }
    }

    private static string[] Toggle(IEnumerable<string> keys, string key, bool enabled)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        if (enabled) set.Add(key); else set.Remove(key);
        return set.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private void SaveUnsafe(SettingsDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    private SettingsDocument LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var text = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(text);

            // Legacy settings format: ["disabled-game-key", ...].
            // The new upstream default is OFF, so no old array entry can grant opt-in authority.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return new();

            var value = JsonSerializer.Deserialize<SettingsDocument>(text);
            if (value?.Version != 2) return new();
            value.EnabledGames ??= [];
            return value;
        }
        catch
        {
            // Corrupt/malformed preferences fail closed: every opt-in remains OFF.
            return new();
        }
    }
}
