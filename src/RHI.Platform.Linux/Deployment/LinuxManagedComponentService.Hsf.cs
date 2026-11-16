using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.Deployment;

// w14/extras-addons (#277): HSF frame generation, through the same guards and update history as every component.
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The HSF service (AppComposition); null in builds and tests constructed without it.</summary>
    public HsfDeploymentService? Hsf { get; set; }

    /// <summary>The host's NVIDIA card generation (#277: Universal MFG Unlocker is offered with HSF on RTX 20/30/40). Test seam.</summary>
    public Func<RtxGeneration> GpuGeneration { get; set; } = () => NvidiaGpuProbe.Read().Generation;

    public Task<ComponentOperationResult> InstallHsfAsync(DetectedGame game, bool launchpadOn, CancellationToken cancellationToken = default)
        => InstallGuard(game) is { } guard ? Task.FromResult(guard)
            : Hsf is null ? Task.FromResult(ComponentOperationResult.Fail(HsfDeploymentService.NotInstalledMessage))
            : Track(game, ManagedComponentKind.Hsf, ComponentUpdateAction.Install, () => Hsf.InstallAsync(game, launchpadOn, cancellationToken));

    public Task<ComponentOperationResult> RemoveHsfAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : Hsf is null ? Task.FromResult(ComponentOperationResult.Fail(HsfDeploymentService.NotInstalledMessage))
            : Track(game, ManagedComponentKind.Hsf, ComponentUpdateAction.Remove, () => Hsf.RemoveAsync(game, cancellationToken));

    public Task<ComponentOperationResult> UpdateHsfAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : Hsf is null ? Task.FromResult(ComponentOperationResult.Fail(HsfDeploymentService.NotInstalledMessage))
            : Track(game, ManagedComponentKind.Hsf, ComponentUpdateAction.Update, () => Hsf.UpdateAsync(game, cancellationToken));

    /// <summary>The ⚙'s Launchpad switch and DirectX 12 mode, behind the patch-target guard (no history entry).</summary>
    public Task<ComponentOperationResult> SetHsfLaunchpadAsync(DetectedGame game, bool on, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : Hsf is null ? Task.FromResult(ComponentOperationResult.Fail(HsfDeploymentService.NotInstalledMessage))
            : Hsf.SetLaunchpadAsync(game, on, cancellationToken);

    public Task<ComponentOperationResult> SetHsfRouteAsync(DetectedGame game, HsfRoute route, CancellationToken cancellationToken = default)
        => PatchTargetGuard(game) is { } guard ? Task.FromResult(guard)
            : Hsf is null ? Task.FromResult(ComponentOperationResult.Fail(HsfDeploymentService.NotInstalledMessage))
            : Hsf.SetRouteAsync(game, route, cancellationToken);

    /// <summary>
    /// #277 (upstream 2.9.3): installing HSF offers Universal MFG Unlocker on RTX 20, 30 and 40 cards, which HSF's frame
    /// generation needs there (RTX 40 for more than 2x); not on RTX 50 or an unknown card, nor when it is installed.
    /// </summary>
    public bool OffersUniversalMfgWithHsf(DetectedGame game)
        => GpuGeneration() is RtxGeneration.Rtx20 or RtxGeneration.Rtx30 or RtxGeneration.Rtx40
           && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is null
           && GameArchitecture.Of(game) == PeArchitecture.X64; // Linux: the 32-bit HSF-folder placement is #278's (not yet)

    /// <summary>Universal MFG Unlocker through its own install path (the extras-fg lane's component, unchanged).</summary>
    public Task<ComponentOperationResult> InstallUniversalMfgForHsfAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => InstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, [], cancellationToken);
}
