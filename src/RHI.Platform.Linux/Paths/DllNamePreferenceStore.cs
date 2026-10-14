using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Paths;

/// <summary>A game's chosen proxy DLL names (#120). Null = automatic (RHI manifest name, else by graphics API).</summary>
public sealed class DllNameChoices
{
    [JsonPropertyName("reshade")]
    public string? ReShade { get; set; }
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
        lock (_gate)
        {
            var entries = LoadUnsafe();
            if (canonical is null) GameRecordIdentity.Remove(entries, game);
            else GameRecordIdentity.Set(entries, game, new DllNameChoices { ReShade = canonical });
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
