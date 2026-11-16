using RHI.Platform.Linux.DisplayModes;

namespace RHI.Platform.Linux.Deployment;

// w14/display-modes (#311): display modes write their game-file values through this (journaled, RHI's components only).
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The journaled game-file side of display modes (AppComposition); null in older wiring (an unjournaled one is used).</summary>
    public DisplayModeGameFiles? DisplayModeFiles { get; set; }
}
