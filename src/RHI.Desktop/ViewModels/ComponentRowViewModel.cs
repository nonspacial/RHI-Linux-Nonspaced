using RHI.Core.Models;

namespace RHI.Desktop.ViewModels;

public sealed class ComponentRowViewModel(ManagedComponent component)
{
    public string Name => component.DisplayName;
    public string Status => component.Availability switch
    {
        ComponentAvailability.Installed => "Installed",
        ComponentAvailability.UpdateAvailable => "Update available",
        ComponentAvailability.Unsupported => "Unsupported",
        ComponentAvailability.Available => "Available",
        _ => "Unknown"
    };
    public string Action => component.Availability == ComponentAvailability.Installed ? "Remove" : "Install";
    public string Note => component.Note ?? "";
    public bool IsActionEnabled => false; // intentionally locked until the installer backend is parity-tested
}
