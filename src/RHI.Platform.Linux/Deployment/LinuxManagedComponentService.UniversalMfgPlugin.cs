using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14/extras-fg (#278): Universal MFG Unlocker follows OptiScaler. After RHI installs OptiScaler Nightly, F5 DLSSNR or
/// DLSS NR, a proxy install of the mod moves into OptiScaler's plugins folder; before RHI removes OptiScaler, a plugin
/// install moves back beside the game. Either move's outcome is added to OptiScaler's own message; a move that cannot be
/// made never turns OptiScaler's result into a failure.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    private async Task<ComponentOperationResult> MovingUniversalMfgIntoOptiScalerAsync(
        DetectedGame game, Task<ComponentOperationResult> install, CancellationToken cancellationToken)
    {
        var result = await install.ConfigureAwait(false);
        if (!result.Success) return result;
        var moved = await _rtx40Mfg.MoveIntoOptiScalerAsync(game, cancellationToken).ConfigureAwait(false);
        return moved is null ? result : result with { Message = result.Message + moved };
    }

    private async Task<ComponentOperationResult> RemoveOptiScalerMovingUniversalMfgAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var moved = _optiscaler.GetRecord(game) is null ? null : await _rtx40Mfg.MoveOutOfOptiScalerAsync(game, cancellationToken).ConfigureAwait(false);
        var result = await _optiscaler.RemoveAsync(game, cancellationToken).ConfigureAwait(false);
        return moved is null ? result : result with { Message = result.Message + moved };
    }

    /// <summary>Universal MFG Unlocker's ⚙ facts for the game (#278).</summary>
    public UniversalMfgStatus UniversalMfgStatus(DetectedGame game) => _rtx40Mfg.Status(game);

    /// <summary>Whether installing Universal MFG Unlocker now would put it in OptiScaler's plugins folder (#278).</summary>
    public bool UniversalMfgWouldBePlugin(DetectedGame game) => _rtx40Mfg.WouldInstallAsPlugin(game);

    /// <summary>
    /// Whether installing Universal MFG Unlocker now warrants upstream's ReShade warning (#278): it would go beside the game
    /// (not into OptiScaler), ReShade is there (RHI's or the user's) and the game is not Vulkan (ReShade's Vulkan layer).
    /// </summary>
    public bool UniversalMfgNeedsReShadeWarning(DetectedGame game)
    {
        if (_rtx40Mfg.WouldInstallAsPlugin(game) || game.EffectiveGraphicsApi == GraphicsApi.Vulkan) return false;
        if (_records.Find(game, ManagedComponentKind.ReShade) is not null) return true;
        var binaryDir = Rtx40MfgStandaloneDeploymentService.ResolveBinaryDirectory(game);
        return binaryDir is not null && GitHubReShadeAddonDeploymentService.HasReShade(binaryDir);
    }
}
