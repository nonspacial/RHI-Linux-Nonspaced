using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>A game's chosen proxy DLL names (#120; RTX Encore's since #158). Null = automatic/default.</summary>
public sealed class DllNameChoices
{
    [JsonPropertyName("reshade")]
    public string? ReShade { get; set; }

    /// <summary>RTX Encore's name (#158); null = its default (<c>version.dll</c>).</summary>
    [JsonPropertyName("rtxEncore")]
    public string? RtxEncore { get; set; }

    /// <summary>Whether RTX Encore is installed with NVIDIA's Neural Rendering file (#158); null = no.</summary>
    [JsonPropertyName("rtxEncoreNeuralRendering")]
    public bool? RtxEncoreNeuralRendering { get; set; }

    internal bool IsEmpty => ReShade is null && RtxEncore is null && RtxEncoreNeuralRendering is null;
}

/// <summary>
/// Per-game proxy DLL name chosen before ReShade is installed (#120, upstream's per-game DLL-name dropdown). Only names
/// in <see cref="ProxyDllNames.Offered"/> are stored or handed out, so the file can never point a deployment at another
/// folder. A name chosen while ReShade is installed is refused by the picker; the install keeps the recorded name.
/// </summary>
public sealed class DllNamePreferenceStore : IGameStateStore
{
    private readonly JsonFileStore<Dictionary<string, DllNameChoices>> _file;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DllNamePreferenceStore(string? path = null)
    {
        _file = new JsonFileStore<Dictionary<string, DllNameChoices>>(
            path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "dll-names.json"), JsonOptions);
    }

    /// <summary>The ReShade name chosen for <paramref name="game"/>, or null for automatic. A stored name that is not offered
    /// (edited by hand) reads as automatic and is logged.</summary>
    public string? GetReShade(DetectedGame game)
    {
        lock (_gate)
        {
            if (!GameRecordIdentity.TryGet(LoadUnsafe(), game, out var choices) || string.IsNullOrWhiteSpace(choices?.ReShade))
                return null;
            if (ProxyDllNames.Canonical(choices.ReShade) is { } name) return name;
            AppLog.Warn("dll-names", $"Ignored a ReShade DLL name that RHI does not offer for {game.Name}: {choices.ReShade}");
            return null;
        }
    }

    /// <summary>Saves the ReShade name for <paramref name="game"/>; null (or blank) goes back to automatic.</summary>
    /// <exception cref="ArgumentException">The name is not one of <see cref="ProxyDllNames.Offered"/>.</exception>
    public void SetReShade(DetectedGame game, string? name)
    {
        var canonical = string.IsNullOrWhiteSpace(name)
            ? null
            : ProxyDllNames.Canonical(name) ?? throw new ArgumentException($"Not a proxy DLL name RHI offers: {name}", nameof(name));
        Update(game, choices => choices.ReShade = canonical);
    }

    /// <summary>The RTX Encore name chosen for <paramref name="game"/> (#158), or null for its default. A stored name RTX
    /// Encore does not use (edited by hand) reads as the default and is logged.</summary>
    public string? GetRtxEncore(DetectedGame game)
    {
        lock (_gate)
        {
            if (!GameRecordIdentity.TryGet(LoadUnsafe(), game, out var choices) || string.IsNullOrWhiteSpace(choices?.RtxEncore))
                return null;
            if (RtxEncoreNames.Canonical(choices.RtxEncore) is { } name) return name;
            AppLog.Warn("dll-names", $"Ignored an RTX Encore name that RHI does not offer for {game.Name}: {choices.RtxEncore}");
            return null;
        }
    }

    /// <summary>Saves RTX Encore's name for <paramref name="game"/>; null goes back to its default.</summary>
    /// <exception cref="ArgumentException">The name is not one of <see cref="RtxEncoreNames.All"/>.</exception>
    public void SetRtxEncore(DetectedGame game, string? name)
    {
        var canonical = string.IsNullOrWhiteSpace(name)
            ? null
            : RtxEncoreNames.Canonical(name) ?? throw new ArgumentException($"Not a name RTX Encore uses: {name}", nameof(name));
        Update(game, choices => choices.RtxEncore = canonical);
    }

    /// <summary>Whether RTX Encore's next install brings NVIDIA's Neural Rendering file (#158).</summary>
    public bool GetRtxEncoreNeuralRendering(DetectedGame game)
    {
        lock (_gate)
            return GameRecordIdentity.TryGet(LoadUnsafe(), game, out var choices) && choices?.RtxEncoreNeuralRendering == true;
    }

    public void SetRtxEncoreNeuralRendering(DetectedGame game, bool enabled)
        => Update(game, choices => choices.RtxEncoreNeuralRendering = enabled ? true : null);

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> when the file cannot be saved (read before a deployment).</summary>
    public void EnsureWritable()
    {
        lock (_gate) _file.EnsureWritable();
    }

    // One entry per game holds every choice: a change to one keeps the others; an entry left empty is removed.
    private void Update(DetectedGame game, Action<DllNameChoices> change)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            var choices = GameRecordIdentity.TryGet(entries, game, out var existing) && existing is not null
                ? new DllNameChoices { ReShade = existing.ReShade, RtxEncore = existing.RtxEncore, RtxEncoreNeuralRendering = existing.RtxEncoreNeuralRendering }
                : new DllNameChoices();
            change(choices);
            if (choices.IsEmpty) GameRecordIdentity.Remove(entries, game);
            else GameRecordIdentity.Set(entries, game, choices);
            _file.Save(entries);
        }
    }

    /// <summary>"Repair after move": entries under the game's old folder key follow it (names hold no paths).</summary>
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

    /// <summary>The entries are file names, never folders: nothing here can name a folder that moved.</summary>
    public IReadOnlyList<string> MovedPaths(DetectedGame game) => [];

    private Dictionary<string, DllNameChoices> LoadUnsafe() => JsonStoreMaps.CaseInsensitive(_file.Load().Value);
}
