namespace RHI.Platform.Linux.Deployment;

// w14/extras-addons (#282): Display Commander's main settings in its ⚙.
public sealed partial class LinuxManagedComponentService
{
    /// <summary>The journaled DisplayCommander.ini settings (AppComposition); null in older wiring (an unjournaled one is used).</summary>
    public DisplayCommanderSettingsService? DisplayCommanderSettings { get; set; }
}
