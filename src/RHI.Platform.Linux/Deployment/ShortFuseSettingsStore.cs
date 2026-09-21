using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Per-game ShortFuse ASI auto-configuration preference.
/// The upstream contract is opt-in: absence means OFF.
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
    }

    private readonly string _path;
    private readonly object _gate = new();

    public ShortFuseSettingsStore(string? path = null)
        => _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "shortfuse-settings.json");

    public bool IsAutoConfigEnabled(DetectedGame game)
    {
        lock (_gate) return LoadEnabledUnsafe().Contains(LinuxDeploymentRecordStore.GameKey(game));
    }

    public void SetAutoConfigEnabled(DetectedGame game, bool enabled)
    {
        lock (_gate)
        {
            var enabledGames = LoadEnabledUnsafe();
            var key = LinuxDeploymentRecordStore.GameKey(game);
            if (enabled) enabledGames.Add(key); else enabledGames.Remove(key);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            var document = new SettingsDocument { EnabledGames = enabledGames.OrderBy(x => x).ToArray() };
            File.WriteAllText(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private HashSet<string> LoadEnabledUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var text = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(text);

            // Legacy settings format: ["disabled-game-key", ...].
            // The new upstream default is OFF, so no old array entry can grant opt-in authority.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return new(StringComparer.Ordinal);

            var value = JsonSerializer.Deserialize<SettingsDocument>(text);
            if (value?.Version != 2) return new(StringComparer.Ordinal);
            return new(value.EnabledGames ?? [], StringComparer.Ordinal);
        }
        catch
        {
            // Corrupt/malformed preferences fail closed: auto-config remains OFF.
            return new(StringComparer.Ordinal);
        }
    }
}
