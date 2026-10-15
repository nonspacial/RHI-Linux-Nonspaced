namespace RHI.Core.Models;

public enum ManagedComponentKind
{
    Unknown = -1,
    ReShade = 0,
    RenoDX = 1,
    RenoDXUpgrade = 2,
    ReLimiter = 3,
    DisplayCommander = 4,
    MfgAdaUnlock = 5,
    Dlss5Feeder = 6,
    Dlss5Dx11Bridge = 7,
    OptiScaler = 8,
    REFramework = 9,
    LumaFramework = 10,
    // 11 was a retired Linux translation-layer component. Keep the numeric slot unused
    // so a legacy numeric record cannot be reinterpreted as another component.
    DofFix = 12,
    Dlss = 13,
    Streamline = 14,
    UltimateAsiLoader = 15,
    ShortFuse = 16,
    Dlssg2030Unlock = 17,
    DlssEnablerStandalone = 18,
    Rtx40MfgStandalone = 19,
    // w3/parity (#96): a layer over the nvngx_dlssnr.dll owner, never an owner itself.
    NrCostScaler = 20,
    // w4/encore (#158): a proxy DLL under a chosen name; owns nvngx_dlssnr.dll only with its Neural Rendering file.
    RtxEncore = 21
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
    public required string DisplayName { get; set; }
    public ComponentAvailability Availability { get; set; }
    public string? InstalledVersion { get; set; }
    public string? LatestVersion { get; set; }
    public string? Note { get; set; }
    public bool ManagedByRhi { get; set; }
    public bool ActionAllowed { get; set; }
    /// <summary>Add-on file names the probe found on disk for this component (RenoDX HDR and Luma Framework only).</summary>
    public IReadOnlyList<string> DetectedAddonFiles { get; set; } = [];
    /// <summary>Add-on file names RHI installed for this component, read from its deployment record and limited to an
    /// installed row (and to what the probe found, when it found any); empty when RHI does not manage it.</summary>
    public IReadOnlyList<string> InstalledAddonFiles { get; set; } = [];
    /// <summary>Display-ready mod author(s) (e.g. "Musa, ShortFuse"), or null when unknown.</summary>
    public string? Author { get; set; }
    /// <summary>
    /// The game ships its own <c>nvngx_dlss.dll</c> (<see cref="Services.DlssManagerService.ShipsOwnDlss"/>: an
    /// <c>OptiScaler/</c> folder does not count). Set by the probe on the RTX Encore row, which needs the game's DLSS.
    /// </summary>
    public bool GameShipsDlss { get; set; }
}
