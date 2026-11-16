using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What "uninstall every component" did: the components removed, and those still installed with the reason.</summary>
public sealed record RemoveAllComponentsResult(IReadOnlyList<ManagedComponentKind> Removed, IReadOnlyList<(ManagedComponentKind Component, string Reason)> Kept)
{
    public bool Success => Kept.Count == 0;
}

public sealed partial class LinuxManagedComponentService
{
    // Layers and add-ons before what they sit on; ReShade, its loaders and RE Framework last. A refusal for a dependency
    // ("Remove X first") is retried once the rest is gone.
    private static readonly ManagedComponentKind[] RemovalOrder =
    [
        ManagedComponentKind.NrCostScaler, ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Dx11Bridge, ManagedComponentKind.Dlss5Feeder,
        ManagedComponentKind.RenoDXUpgrade, ManagedComponentKind.RenoDX, ManagedComponentKind.LumaFramework, ManagedComponentKind.DofFix,
        ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander, ManagedComponentKind.RtxEncore, ManagedComponentKind.MfgAdaUnlock,
        ManagedComponentKind.Dlssg2030Unlock, ManagedComponentKind.DlssEnablerStandalone, ManagedComponentKind.Rtx40MfgStandalone,
        ManagedComponentKind.OptiScaler, ManagedComponentKind.DxvkHdr, ManagedComponentKind.UltimateAsiLoader, ManagedComponentKind.ReShade,
        ManagedComponentKind.REFramework
    ];

    /// <summary>
    /// #293 (upstream 2.9.0 "Remove from RHI" › "Also uninstall all RHI-managed components"): the components RHI installed
    /// in <paramref name="game"/> that a removal takes out (DLSS/Streamline swaps and the Addon Manager's selection are not
    /// components of their own), in the order they are removed.
    /// </summary>
    public IReadOnlyList<ManagedComponentKind> RemovableComponents(DetectedGame game)
    {
        var installed = _records.FindAll(game).Select(record => record.Component).ToHashSet();
        return RemovalOrder.Where(installed.Contains).ToArray();
    }

    /// <summary>
    /// Removes every component of <see cref="RemovableComponents"/>, each through its own removal (journaled, with its own
    /// guards: a running game, a moved folder, a changed file refuse it). A component refused because another depends on it
    /// is tried again once the others are gone; whatever still cannot be removed is kept with its reason.
    /// </summary>
    public async Task<RemoveAllComponentsResult> RemoveAllComponentsAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var pending = RemovableComponents(game).ToList();
        var removed = new List<ManagedComponentKind>();
        var reasons = new Dictionary<ManagedComponentKind, string>();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var kind in pending.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = kind switch
                {
                    ManagedComponentKind.NrCostScaler => await RemoveNrCostScalerAsync(game, cancellationToken).ConfigureAwait(false),
                    ManagedComponentKind.UltimateAsiLoader => await RemoveUltimateAsiLoaderAsync(game, cancellationToken).ConfigureAwait(false),
                    _ => await RemoveAsync(game, kind, cancellationToken).ConfigureAwait(false)
                };
                if (result.Success || _records.Find(game, kind) is null)
                {
                    pending.Remove(kind);
                    removed.Add(kind);
                    reasons.Remove(kind);
                    progress = true;
                }
                else reasons[kind] = result.Message;
            }
        }
        return new(removed, pending.Select(kind => (kind, reasons.GetValueOrDefault(kind, ""))).ToArray());
    }
}
