using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>w12/luma (#245): what the selected game's Luma row asks about a Luma mod the wiki lists only on Nexus Mods.</summary>
public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// The game's Nexus-only Luma mod page with Nexus's answer when RHI installed it (null: the mod is not Nexus-only).
    /// </summary>
    public Task<LumaNexusPage?> AskNexusAboutLumaAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _lumaFramework.NexusPageAsync(game, cancellationToken);
}
