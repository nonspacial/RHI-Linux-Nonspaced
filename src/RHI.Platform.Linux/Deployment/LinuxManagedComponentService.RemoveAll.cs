using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>What "uninstall every component" did: the components removed, and those still installed with the reason.</summary>
public sealed record RemoveAllComponentsResult(IReadOnlyList<ManagedComponentKind> Removed, IReadOnlyList<(ManagedComponentKind Component, string Reason)> Kept)
{
    public bool Success => Kept.Count == 0;
}

public sealed partial class LinuxManagedComponentService
{
    // Kinds a record can carry that are not components of their own: DLSS/Streamline swaps are the game's own DLLs swapped
    // in place, and the Addon Manager's per-game selection has no files of its own.
    private static readonly HashSet<ManagedComponentKind> NotRemovableOnTheirOwn =
    [
        ManagedComponentKind.Unknown, ManagedComponentKind.Dlss, ManagedComponentKind.Streamline, ManagedComponentKind.ReShadeAddonManager
    ];

    // w14 final review M1: the removal order is a priority table, not an allow-list — every kind with a record is removed.
    // Layers and add-ons before what they sit on; the loaders and what the others depend on (ReShade, RE Framework) last.
    // A kind that is not listed (a newer one) goes after the listed add-ons and before the loaders. A refusal for a
    // dependency ("Remove X first") is retried once the rest is gone.
    private static readonly ManagedComponentKind[] RemovalPriority =
    [
        ManagedComponentKind.NrCostScaler, // a layer over the Neural Rendering owner
        ManagedComponentKind.Dlss5Tool, ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Dx11Bridge, ManagedComponentKind.Dlss5Feeder,
        ManagedComponentKind.GitcUplift, ManagedComponentKind.Hsf,
        ManagedComponentKind.RenoDXUpgrade, ManagedComponentKind.RenoDX, ManagedComponentKind.LumaFramework, ManagedComponentKind.DofFix,
        ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander, ManagedComponentKind.RtxEncore, ManagedComponentKind.MfgAdaUnlock,
        ManagedComponentKind.Dlssg2030Unlock, ManagedComponentKind.DlssEnablerStandalone, ManagedComponentKind.Rtx40MfgStandalone,
        ManagedComponentKind.OptiScaler, ManagedComponentKind.DxvkHdr
    ];

    private static readonly ManagedComponentKind[] RemovedLast =
    [
        ManagedComponentKind.UltimateAsiLoader, ManagedComponentKind.ReShade, ManagedComponentKind.REFramework
    ];

    /// <summary>Where <paramref name="kind"/> stands in the removal order (lower goes first).</summary>
    internal static int RemovalRank(ManagedComponentKind kind)
    {
        var first = Array.IndexOf(RemovalPriority, kind);
        if (first >= 0) return first;
        var last = Array.IndexOf(RemovedLast, kind);
        return last >= 0 ? RemovalPriority.Length + 1 + last : RemovalPriority.Length;
    }

    /// <summary>Whether a record of <paramref name="kind"/> is a component "uninstall every component" takes out.</summary>
    public static bool IsRemovableComponent(ManagedComponentKind kind) => !NotRemovableOnTheirOwn.Contains(kind);

    /// <summary>
    /// #293 (upstream 2.9.0 "Remove from RHI" › "Also uninstall all RHI-managed components"): the components RHI installed
    /// in <paramref name="game"/> that a removal takes out (DLSS/Streamline swaps and the Addon Manager's selection are not
    /// components of their own), in the order they are removed.
    /// </summary>
    public IReadOnlyList<ManagedComponentKind> RemovableComponents(DetectedGame game)
        => _records.FindAll(game).Select(record => record.Component).Where(IsRemovableComponent).Distinct()
            .OrderBy(RemovalRank).ThenBy(kind => (int)kind).ToArray();

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
                if (_records.Find(game, kind) is null)
                {
                    // Removed with another one (the DX11 Bridge takes its DLSS5 Tool along).
                    pending.Remove(kind);
                    removed.Add(kind);
                    reasons.Remove(kind);
                    progress = true;
                    continue;
                }
                var result = kind switch
                {
                    ManagedComponentKind.NrCostScaler => await RemoveNrCostScalerAsync(game, cancellationToken).ConfigureAwait(false),
                    ManagedComponentKind.UltimateAsiLoader => await RemoveUltimateAsiLoaderAsync(game, cancellationToken).ConfigureAwait(false),
                    ManagedComponentKind.Hsf => await RemoveHsfAsync(game, cancellationToken).ConfigureAwait(false),
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
