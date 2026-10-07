using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Per-game ShortFuse preferences: ASI auto-configuration and the "ZZZ load order" add-on name.
/// The upstream contract for both is opt-in: absence means OFF.
/// The older Linux format was a bare array of disabled game keys; because that format
/// cannot distinguish "explicitly re-enabled" from "never configured", it migrates safely
/// to OFF rather than silently renaming ReShade/installing UAL on the next ShortFuse install.
/// </summary>
public sealed class ShortFuseSettingsStore : IGameStateStore
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
    private readonly JsonFileStore<SettingsDocument> _file;
    private readonly object _gate = new();

    public ShortFuseSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "shortfuse-settings.json");
        // An unreadable file is copied aside and never overwritten; until then every opt-in reads OFF.
        _file = new JsonFileStore<SettingsDocument>(_path, parse: Parse);
    }

    public bool IsAutoConfigEnabled(DetectedGame game)
    {
        lock (_gate) return LoadUnsafe().EnabledGames.Any(key => GameRecordIdentity.Matches(key, game));
    }

    public void SetAutoConfigEnabled(DetectedGame game, bool enabled)
    {
        lock (_gate)
        {
            var document = LoadUnsafe();
            document.EnabledGames = Toggle(document.EnabledGames, game, enabled);
            SaveUnsafe(document);
        }
    }

    /// <summary>True when the ShortFuse add-on should be deployed as <c>zzz_renodx-dlss.addon64</c>.</summary>
    public bool IsZzzLoadOrderEnabled(DetectedGame game)
    {
        lock (_gate) return (LoadUnsafe().ZzzLoadOrderGames ?? []).Any(key => GameRecordIdentity.Matches(key, game));
    }

    public void SetZzzLoadOrderEnabled(DetectedGame game, bool enabled)
    {
        lock (_gate)
        {
            var document = LoadUnsafe();
            document.ZzzLoadOrderGames = Toggle(document.ZzzLoadOrderGames ?? [], game, enabled);
            SaveUnsafe(document);
        }
    }

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable settings file is on disk.</summary>
    public void EnsureWritable()
    {
        lock (_gate) _file.EnsureWritable();
    }

    // Legacy keys for the game are dropped on every write, so an older entry migrates to the current key.
    private static string[] Toggle(IEnumerable<string> keys, DetectedGame game, bool enabled)
    {
        var set = new HashSet<string>(keys.Where(key => !GameRecordIdentity.Matches(key, game)), StringComparer.Ordinal);
        if (enabled) set.Add(LinuxDeploymentRecordStore.GameKey(game));
        return set.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    /// <summary>"Repair after move" (Refs #38): opt-ins stored under the game's old folder key follow it.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(document =>
            {
                if (document is null) return null;
                var enabled = move.MoveRecordKeyList(document.EnabledGames);
                var zzz = move.MoveRecordKeyList(document.ZzzLoadOrderGames);
                if (enabled is null && zzz is null) return null;
                if (enabled is not null) document.EnabledGames = enabled;
                if (zzz is not null) document.ZzzLoadOrderGames = zzz;
                return document;
            });
    }

    private void SaveUnsafe(SettingsDocument document) => _file.Save(document);

    private SettingsDocument LoadUnsafe() => _file.Load().Value ?? new SettingsDocument();

    private static SettingsDocument? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);

        // Legacy settings format: ["disabled-game-key", ...].
        // The new upstream default is OFF, so no old array entry can grant opt-in authority.
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            return new SettingsDocument();

        var value = JsonSerializer.Deserialize<SettingsDocument>(text);
        if (value is null) return null;
        // A newer format is someone's settings this build cannot read: unreadable, never overwritten.
        if (value.Version > 2) return null;
        // Older object formats carried no opt-in authority either: every opt-in stays OFF.
        if (value.Version < 2) return new SettingsDocument();
        value.EnabledGames ??= [];
        return value;
    }
}
