using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#271: custom OptiScaler builds (upstream 2.9.2 Settings › Components › Custom OptiScaler builds).</summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The games among <paramref name="games"/> whose RHI-installed OptiScaler is <paramref name="version"/>.</summary>
    public IReadOnlyList<DetectedGame> GamesWithOptiScalerVersion(IEnumerable<DetectedGame> games, OptiScalerVariant version)
        => games.Where(game => GetInstalledOptiScalerVariant(game) == version).ToArray();

    /// <summary>
    /// After a custom build was replaced (upstream "Update the games using it?"): its new files go into the game through the
    /// update transaction, guarded like an update and recorded in the update history. OptiScaler.ini is kept.
    /// </summary>
    public Task<ComponentOperationResult> RedeployOptiScalerCustomBuildAsync(DetectedGame game, OptiScalerVariant build, CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game) ?? LayerGuard(game, ManagedComponentKind.OptiScaler);
        return guard is not null
            ? Task.FromResult(guard)
            : Track(game, ManagedComponentKind.OptiScaler, ComponentUpdateAction.Update,
                () => _optiscaler.RedeployCustomBuildAsync(game, build, cancellationToken));
    }
}
