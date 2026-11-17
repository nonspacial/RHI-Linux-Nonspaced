using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>Persists the preferred OptiScaler release channel per game before installation.</summary>
public sealed class OptiScalerVariantPreferenceStore : IGameStateStore
{
    private readonly string _path;
    // w14 final review m4: entries are read one by one, so one value that is not a version drops only that entry (logged),
    // never the whole file (a converter throw used to quarantine it and refuse every later save).
    private readonly JsonFileStore<Dictionary<string, JsonElement>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // #271: versions are written as their ids; the old enum names and numbers read through OptiScalerVariantJsonConverter.
        WriteIndented = true
    };

    public OptiScalerVariantPreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "optiscaler-variants.json");
        _file = new JsonFileStore<Dictionary<string, JsonElement>>(_path, JsonOptions);
    }

    public OptiScalerVariant Get(DetectedGame game) => Get(game, OptiScalerVariant.Stable);

    /// <summary>The game's choice, or <paramref name="fallback"/> (#270: the Default version) when it has none.</summary>
    public OptiScalerVariant Get(DetectedGame game, OptiScalerVariant fallback)
        => TryGet(game, out var variant) ? variant : fallback;

    /// <summary>#270: whether the game has a choice of its own (Stable picked in the ⚙ counts, so the default never overrides it).</summary>
    public bool TryGet(DetectedGame game, out OptiScalerVariant variant)
    {
        lock (_gate)
            return GameRecordIdentity.TryGet(LoadUnsafe(), game, out variant);
    }

    public void Set(DetectedGame game, OptiScalerVariant variant)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            // #270: Stable is stored too; before the Default version setting, Stable removed the entry.
            GameRecordIdentity.Set(entries, game, variant);
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
    {
        var entries = new Dictionary<string, OptiScalerVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in JsonStoreMaps.CaseInsensitive(_file.Load().Value))
        {
            // An id (string), an old enum name, or an old enum number (#271, OptiScalerVariant.TryParse).
            var raw = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt32(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                _ => null
            };
            if (OptiScalerVariant.TryParse(raw, out var variant)) entries[key] = variant;
            else AppLog.Warn("optiscaler", $"An OptiScaler version choice in {Path.GetFileName(_path)} is not a version and was dropped: {key} = {value.GetRawText()}");
        }
        return entries;
    }

    private void SaveUnsafe(Dictionary<string, OptiScalerVariant> entries)
        => _file.Save(entries.ToDictionary(entry => entry.Key, entry => JsonSerializer.SerializeToElement(entry.Value.Id), StringComparer.OrdinalIgnoreCase));
}
