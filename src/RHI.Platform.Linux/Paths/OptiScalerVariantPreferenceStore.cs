using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>Persists the preferred OptiScaler release channel per game before installation.</summary>
public sealed class OptiScalerVariantPreferenceStore : IGameStateStore
{
    private readonly string _path;
    private readonly JsonFileStore<Dictionary<string, OptiScalerVariant>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public OptiScalerVariantPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "optiscaler-variants.json");
        _file = new JsonFileStore<Dictionary<string, OptiScalerVariant>>(_path, JsonOptions);
    }

    public OptiScalerVariant Get(DetectedGame game)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            return GameRecordIdentity.TryGet(entries, game, out var variant)
                ? variant
                : OptiScalerVariant.Stable;
        }
    }

    public void Set(DetectedGame game, OptiScalerVariant variant)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (variant == OptiScalerVariant.Stable) GameRecordIdentity.Remove(entries, game);
            else GameRecordIdentity.Set(entries, game, variant);
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

    private Dictionary<string, OptiScalerVariant> LoadUnsafe()
        => JsonStoreMaps.CaseInsensitive(_file.Load().Value);

    private void SaveUnsafe(Dictionary<string, OptiScalerVariant> entries) => _file.Save(entries);
}
