using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// True when the game was moved and "Repair after move" has something to update: records, or entries in the
    /// per-game stores (this service's and <paramref name="libraryStores"/>) that still name the old folder (Refs #147).
    /// </summary>
    public bool NeedsRelocationRepair(DetectedGame game, IReadOnlyList<IGameStateStore>? libraryStores = null)
        => _relocation.NeedsRepair(game, RepairStores(libraryStores));

    /// <summary>The records "Repair after move" would update, for the confirmation shown before adopting.</summary>
    public IReadOnlyList<RelocatedRecord> RelocationPreview(DetectedGame game) => _relocation.Preview(game);

    /// <summary>
    /// Explicit repair after a move: re-points the game's records at its current folder once every
    /// owned file is verified there and carries its per-game state with them (Addon Manager files,
    /// preferences, opt-ins, update history, plus <paramref name="libraryStores"/> from the UI:
    /// favourites/hidden/edits/HDR and the preference stores it owns). All or nothing.
    /// </summary>
    public RelocationRepairResult RepairAfterMove(DetectedGame game, IReadOnlyList<IGameStateStore>? libraryStores = null)
        => ReShadeDeploymentService.AfterRepair(_reshade, game, _relocation.Repair(game, RepairStores(libraryStores), out _)); // w8/reshade-ini hook (#214)

    /// <summary>
    /// Every per-game store "Repair after move" rewrites: the ones this service owns plus <paramref name="libraryStores"/>.
    /// A per-game store missing here loses its entries when the game moves (a contract test walks the app's object graph).
    /// </summary>
    internal IReadOnlyList<IGameStateStore> RepairStores(IReadOnlyList<IGameStateStore>? libraryStores = null)
    {
        IGameStateStore?[] owned =
        [
            _addonManager, _neuralVersionPins, _updateLog,
            _dlssg2030?.GameState, _rtx40Mfg?.GameState, _dlssEnablerStandalone?.GameState,
            _shortFuse?.GameState, _lumaPolicy?.GameState,
            _reshade?.DllNames, // w3/dllnames (#120); RTX Encore's name and NR choice live in the same store (#158)
            // w5/update-policy rows
            _updateInclusion, // #178 per-game update inclusion
            _dlssFollow, // #177 Follow newest: games a pass skipped, held per game
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            _shaderSets, // #179 per-game Global / Select / Off and Select lists
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            // w10/dlss-presets rows
            // w10/archives rows
            // w10/journal-holds rows
            // w11/download-idle rows
            // w11/nr-gpu rows
            // w11/nightly-switch rows
            // w11/nr-check rows
        ];
        return owned.OfType<IGameStateStore>().Concat(libraryStores ?? []).ToArray();
    }
}
