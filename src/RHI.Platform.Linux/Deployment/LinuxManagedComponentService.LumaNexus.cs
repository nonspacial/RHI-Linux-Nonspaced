using RHI.Core.Models;
using RHI.Platform.Linux.Network;

namespace RHI.Platform.Linux.Deployment;

/// <summary>w12/luma (#245): what the selected game's Luma row asks about a Luma mod the wiki lists only on Nexus Mods.</summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>Nexus's answer for the game's installed Nexus-only Luma mod (null: no RHI Luma install, or not Nexus-only).</summary>
    public Task<NexusModUpdateAnswer?> AskNexusAboutLumaAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _lumaFramework.AskNexusAsync(game, cancellationToken);
}
