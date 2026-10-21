using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// A game's chosen proxy DLL names (#120; OptiScaler's since #172, RTX Encore's since #158). Null = automatic/default.
/// A record, so a change to one choice copies every other one generically (<see cref="Copy"/>, <see cref="IsEmpty"/>):
/// a choice added later is kept by every setter without touching <c>DllNamePreferenceStore.Update</c>.
/// </summary>
public sealed record DllNameChoices
{
    /// <summary>Every choice automatic (the store then drops the game's entry).</summary>
    public static DllNameChoices Automatic => new();

    [JsonPropertyName("reshade")]
    public string? ReShade { get; set; }

    /// <summary>OptiScaler's DLL name (#172); null = automatic: <c>dxgi.dll</c>, or the first of OptiScaler's names no
    /// other RHI component holds while dxgi.dll is held (<c>LinuxManagedComponentService.OptiScalerAutomaticProxyName</c>).</summary>
    [JsonPropertyName("optiscaler")]
    public string? OptiScaler { get; set; }

    /// <summary>RTX Encore's name (#158); null = its default (<c>version.dll</c>).</summary>
    [JsonPropertyName("rtxEncore")]
    public string? RtxEncore { get; set; }

    /// <summary>Whether RTX Encore is installed with NVIDIA's Neural Rendering file (#158); null = no.</summary>
    [JsonPropertyName("rtxEncoreNeuralRendering")]
    public bool? RtxEncoreNeuralRendering { get; set; }

    /// <summary>Fields a newer RHI wrote that this build does not know: kept as they are on every save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Every field, copied (record <c>with</c>); the unknown fields are never changed, so sharing them is safe.</summary>
    internal DllNameChoices Copy() => this with { };

    /// <summary>No choice at all (every field at its default): the game's entry can go.</summary>
    internal bool IsEmpty() => this with { Unknown = Unknown is { Count: > 0 } ? Unknown : null } == new DllNameChoices();
}

/// <summary>
/// Per-game proxy DLL names chosen before ReShade (#120), OptiScaler (#172) or RTX Encore (#158) is installed
/// (upstream's per-game DLL-name dropdowns). Only names each component offers are stored or handed out, so the file can
/// never point a deployment at another folder. An installed component keeps its recorded name on update; a new name for it
/// is an in-place rename (#196, <c>ProxyRename</c>), which saves the choice with the component's record.
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

    /// <summary>The OptiScaler name chosen for <paramref name="game"/> (#172), or null for automatic. A stored name that is
    /// not one of <see cref="Deployment.OptiScalerDeploymentService.ProxyNames"/> reads as automatic and is logged.</summary>
    public string? GetOptiScaler(DetectedGame game)
    {
        lock (_gate)
        {
            if (!GameRecordIdentity.TryGet(LoadUnsafe(), game, out var choices) || string.IsNullOrWhiteSpace(choices?.OptiScaler))
                return null;
            if (OptiScalerCanonical(choices.OptiScaler) is { } name) return name;
            AppLog.Warn("dll-names", $"Ignored an OptiScaler DLL name that RHI does not offer for {game.Name}: {choices.OptiScaler}");
            return null;
        }
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

    /// <summary>Saves OptiScaler's name for <paramref name="game"/> (#172); null (or blank) goes back to automatic.</summary>
    /// <exception cref="ArgumentException">The name is not one of OptiScaler's names.</exception>
    public void SetOptiScaler(DetectedGame game, string? name)
    {
        var canonical = string.IsNullOrWhiteSpace(name)
            ? null
            : OptiScalerCanonical(name) ?? throw new ArgumentException($"Not a DLL name OptiScaler can be installed under: {name}", nameof(name));
        Update(game, choices => choices.OptiScaler = canonical);
    }

    private static string? OptiScalerCanonical(string name)
        => Deployment.OptiScalerDeploymentService.ProxyNames.FirstOrDefault(known => known.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

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
            Apply(entries, game, change);
            _file.Save(entries);
        }
    }

    /// <summary>
    /// #196: the file write that applies <paramref name="change"/> to <paramref name="game"/>'s choices, staged for a
    /// journaled operation (a rename saves the new choice with the component's record); null when nothing would change.
    /// Nothing is written here. Throws <see cref="JsonStoreUnavailableException"/> when the file holds unreadable data.
    /// </summary>
    public StagedFileWrite? StageChoices(DetectedGame game, Action<DllNameChoices> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var staged = _file.Stage(current =>
            {
                var entries = JsonStoreMaps.CaseInsensitive(current);
                Apply(entries, game, change);
                return current is null && entries.Count == 0 ? null : entries; // no file and nothing to keep: no write
            });
            return staged is null || (staged.Before is { } before && before.AsSpan().SequenceEqual(staged.After)) ? null : staged;
        }
    }

    /// <summary>
    /// #196 Reset DLL names: whether <paramref name="game"/> has a ReShade or OptiScaler name saved (another component's
    /// choice, such as RTX Encore's, is not a DLL-name reset matter and stays).
    /// </summary>
    public bool HasReShadeOrOptiScalerChoice(DetectedGame game) => GetReShade(game) is not null || GetOptiScaler(game) is not null;

    private static void Apply(Dictionary<string, DllNameChoices> entries, DetectedGame game, Action<DllNameChoices> change)
    {
        var choices = GameRecordIdentity.TryGet(entries, game, out var existing) && existing is not null
            ? existing.Copy()
            : new DllNameChoices();
        change(choices);
        if (choices.IsEmpty()) GameRecordIdentity.Remove(entries, game);
        else GameRecordIdentity.Set(entries, game, choices);
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
