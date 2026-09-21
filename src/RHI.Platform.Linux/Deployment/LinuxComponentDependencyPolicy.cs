using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Single dependency authority for managed component installation on Linux/Wine/Proton.
/// Dependencies describe components that RHI can safely provision itself. Runtime capabilities
/// supplied by the game (for example native DLSS/Streamline) remain eligibility prerequisites,
/// not installable dependencies.
/// </summary>
internal static class LinuxComponentDependencyPolicy
{
    private static readonly IReadOnlySet<ManagedComponentKind> ReShadeAddonComponents = new HashSet<ManagedComponentKind>
    {
        ManagedComponentKind.RenoDX,
        ManagedComponentKind.RenoDXUpgrade,
        ManagedComponentKind.ReLimiter,
        ManagedComponentKind.DisplayCommander,
        ManagedComponentKind.MfgAdaUnlock,
        ManagedComponentKind.Dlss5Feeder,
        ManagedComponentKind.Dlss5Dx11Bridge,
        ManagedComponentKind.ShortFuse,
        ManagedComponentKind.DofFix,
        ManagedComponentKind.LumaFramework
    };

    internal static IReadOnlyList<ManagedComponentKind> ManagedDependenciesFor(
        DetectedGame game,
        ManagedComponentKind component)
    {
        if (component == ManagedComponentKind.ReShade && REFrameworkDeploymentService.IsGameEligible(game))
            return [ManagedComponentKind.REFramework];

        return ReShadeAddonComponents.Contains(component)
            ? [ManagedComponentKind.ReShade]
            : Array.Empty<ManagedComponentKind>();
    }

    /// <summary>
    /// Luma and ShortFuse manipulate RHI-owned ReShade state transactionally and therefore cannot
    /// treat an externally installed ReShade as satisfying their dependency. Other add-ons may use
    /// a valid external ReShade without RHI taking ownership of it.
    /// </summary>
    internal static bool RequiresRhiOwnedDependency(
        ManagedComponentKind component,
        ManagedComponentKind dependency)
        => dependency == ManagedComponentKind.ReShade &&
           component is ManagedComponentKind.LumaFramework or ManagedComponentKind.ShortFuse;

    internal static bool IsReShadeAddon(ManagedComponentKind component)
        => ReShadeAddonComponents.Contains(component);
}
