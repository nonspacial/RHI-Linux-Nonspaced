using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

public enum LumaModePreference
{
    Auto,
    Enabled,
    Disabled
}

/// <summary>
/// Persists an explicit per-game Luma mode choice independently from manifest defaults.
/// Auto means "follow the upstream manifest/default policy". Enabled/Disabled are
/// user authority and survive refresh/restart.
/// </summary>
public sealed class LumaModePreferenceStore : IGameStateStore
{
    private readonly string _path;
    private readonly JsonFileStore<Dictionary<string, LumaModePreference>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LumaModePreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "luma-mode.json");
        _file = new JsonFileStore<Dictionary<string, LumaModePreference>>(_path, JsonOptions);
    }

    public LumaModePreference Get(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var map = LoadUnsafe();
            return GameRecordIdentity.TryGet(map, game, out var value)
                ? value
                : LumaModePreference.Auto;
        }
    }

    public void Set(DetectedGame game, LumaModePreference preference)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            var map = LoadUnsafe();
            if (preference == LumaModePreference.Auto) GameRecordIdentity.Remove(map, game);
            else GameRecordIdentity.Set(map, game, preference);
            SaveUnsafe(map);
        }
    }

    /// <summary>"Repair after move" (Refs #38): entries under the game's old folder key follow it.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(current =>
            {
                var map = new Dictionary<string, LumaModePreference>(current ?? new(), StringComparer.Ordinal);
                return move.MoveRecordEntries(map) ? map : null;
            });
    }

    private Dictionary<string, LumaModePreference> LoadUnsafe()
        => new(_file.Load().Value ?? new Dictionary<string, LumaModePreference>(), StringComparer.Ordinal);

    private void SaveUnsafe(Dictionary<string, LumaModePreference> map) => _file.Save(map);
}
