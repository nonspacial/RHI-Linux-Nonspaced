using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// A game's chosen proxy DLL names (#120). Null = automatic (RHI manifest name, else by graphics API). A record so the
/// store copies every choice with <c>with</c> and tells "all automatic" by value equality: a choice added later is kept
/// and compared without touching <see cref="DllNamePreferenceStore"/>'s update code.
/// </summary>
public sealed record DllNameChoices
{
    /// <summary>Every choice automatic (the store then drops the game's entry).</summary>
    public static DllNameChoices Automatic => new();

    [JsonPropertyName("reshade")]
    public string? ReShade { get; set; }

    /// <summary>OptiScaler's DLL name (#172); null = automatic (<c>dxgi.dll</c>).</summary>
    [JsonPropertyName("optiscaler")]
    public string? OptiScaler { get; set; }
}

/// <summary>
/// Per-game proxy DLL names chosen before ReShade (#120) or OptiScaler (#172) is installed (upstream's per-game DLL-name
/// dropdowns). Only names
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

    // One component's choice changes; every other choice is copied as it is (the entry goes only when all are automatic).
    private void Update(DetectedGame game, Action<DllNameChoices> change)
    {
        lock (_gate)
        {
            var entries = LoadUnsafe();
            var choices = GameRecordIdentity.TryGet(entries, game, out var existing) && existing is not null
                ? existing with { }
                : DllNameChoices.Automatic;
            change(choices);
            if (choices == DllNameChoices.Automatic) GameRecordIdentity.Remove(entries, game);
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
