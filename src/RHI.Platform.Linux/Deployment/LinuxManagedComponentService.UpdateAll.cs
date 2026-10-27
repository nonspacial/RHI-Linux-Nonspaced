using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

// w12/update-all (#243): what Update All asks the component service beyond the check and the update.
public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// #243 (upstream 2.8.8 <c>49d4473</c>): a ReShade on the Custom channel is checked before manual Update All refreshes
    /// it — the check is local (the chosen DLL is still there, and its hash), and a source that is gone reads as "could not
    /// check" with the reason instead of a refresh that fails. Stable, Nightly and the other channels refresh unchecked.
    /// </summary>
    public bool ChecksBeforeManualRefresh(DetectedGame game, ManagedComponentKind component)
        => component == ManagedComponentKind.ReShade && _reshade.GetInstalledOptions(game).Channel == ReShadeChannel.Custom;
}
