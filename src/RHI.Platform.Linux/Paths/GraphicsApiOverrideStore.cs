using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Persists explicit per-game graphics API authority separately from runtime detection.
/// This mirrors upstream RHI's per-game API override concept without rewriting detector evidence.
/// </summary>
public sealed class GraphicsApiOverrideStore : IGameStateStore
{
    private readonly string _path;
    private readonly JsonFileStore<Dictionary<string, GraphicsApi>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GraphicsApiOverrideStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "game-api-overrides.json");
        _file = new JsonFileStore<Dictionary<string, GraphicsApi>>(_path, JsonOptions);
    }

    public GraphicsApi? Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return GameRecordIdentity.TryGet(entries, game, out var api) ? api : null;
        }
    }

    /// <summary>Stores an explicit API. Null/Unknown clears the override and returns to automatic detection.</summary>
    public void Set(DetectedGame game, GraphicsApi? api)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (!api.HasValue || api.Value == GraphicsApi.Unknown)
                GameRecordIdentity.Remove(entries, game);
            else
                GameRecordIdentity.Set(entries, game, api.Value);
            SaveUnsafe(entries);
        }
    }

    /// <summary>"Repair after move" (Refs #38): entries under the game's old folder key follow it.</summary>
    public StagedFileWrite? StageMove(GameStateMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        lock (_gate)
            return _file.Stage(current =>
            {
                var map = JsonStoreMaps.CaseInsensitive(current);
                return move.MoveRecordEntries(map) ? map : null;
            });
    }

    private Dictionary<string, GraphicsApi> LoadUnsafe()
        => JsonStoreMaps.CaseInsensitive(_file.Load().Value);

    private void SaveUnsafe(Dictionary<string, GraphicsApi> entries) => _file.Save(entries);
}
