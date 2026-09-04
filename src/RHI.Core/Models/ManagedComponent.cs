namespace RHI.Core.Models;

public enum ManagedComponentKind
{
    ReShade,
    RenoDX,
    RenoDXUpgrade,
    ReLimiter,
    DisplayCommander,
    OptiScaler,
    REFramework,
    LumaFramework,
    DXVK,
    DofFix,
    Dlss,
    Streamline
}

public enum ComponentAvailability
{
    Available,
    Installed,
    UpdateAvailable,
    Unsupported,
    Unknown
}

public sealed class ManagedComponent
{
    public required ManagedComponentKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public ComponentAvailability Availability { get; set; }
    public string? InstalledVersion { get; set; }
    public string? LatestVersion { get; set; }
    public string? Note { get; set; }
}
