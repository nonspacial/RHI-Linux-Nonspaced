using System.Windows.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

public sealed class ComponentRowViewModel(ManagedComponent component, ICommand command)
{
    public ManagedComponentKind Kind => component.Kind;
    public ComponentAvailability Availability => component.Availability;
    public string? InstalledVersion => component.InstalledVersion;
    public string Name => component.Kind == ManagedComponentKind.Streamline
        ? UiLocalizationService.Text("Ui.StreamlineUpdate")
        : component.DisplayName;
    public string Status
    {
        get
        {
            if (component.Kind == ManagedComponentKind.Streamline)
                return UiLocalizationService.Text(component.Availability == ComponentAvailability.Installed
                    ? "Ui.StatusDetected"
                    : "Ui.StatusNotDetected");

            var baseStatus = component.Availability switch
            {
                ComponentAvailability.Installed when component.ManagedByRhi => $"{UiLocalizationService.Text("Ui.StatusInstalled")} · RHI",
                ComponentAvailability.Installed => $"{UiLocalizationService.Text("Ui.StatusInstalled")} · {UiLocalizationService.Text("Ui.StatusExternal")}",
                ComponentAvailability.UpdateAvailable => UiLocalizationService.Text("Ui.StatusUpdateAvailable"),
                ComponentAvailability.Unsupported => UiLocalizationService.Text("Ui.StatusUnsupported"),
                ComponentAvailability.Available => UiLocalizationService.Text("Ui.StatusAvailable"),
                _ => UiLocalizationService.Text("Ui.StatusUnknown")
            };

            // API-driven add-ons expose their installed version in the picker.
            if (component.Kind is ManagedComponentKind.RenoDXUpgrade or ManagedComponentKind.MfgAdaUnlock or ManagedComponentKind.Dlssg2030Unlock or ManagedComponentKind.DlssEnablerStandalone or ManagedComponentKind.Rtx40MfgStandalone or ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse or ManagedComponentKind.DofFix or ManagedComponentKind.REFramework or ManagedComponentKind.LumaFramework)
            {
                // Show the version actually installed on disk. If no installed version is
                // available yet, fall back to the newest resolved version as informational data.
                var version = component.InstalledVersion ?? component.LatestVersion;
                if (!string.IsNullOrWhiteSpace(version))
                    return $"{baseStatus} · {version}";
            }

            return baseStatus;
        }
    }

    public bool HasAction => component.Kind is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline);
    public string Action => !HasAction
        ? string.Empty
        : component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable
            ? UiLocalizationService.Text("Ui.ActionRemove")
            : UiLocalizationService.Text("Ui.ActionInstall");
    public bool IsInstallAction => component.Availability is not (ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable);
    public bool IsRemoveAction => !IsInstallAction;
    public string Note => component.Kind == ManagedComponentKind.Streamline
        ? UiLocalizationService.Text(component.Availability == ComponentAvailability.Installed
            ? "Ui.StreamlineDetectedNote"
            : "Ui.StreamlineMissingNote")
        : UiLocalizationService.Present(component.Note);
    public bool IsActionEnabled => component.ActionAllowed;
    public bool HasRenoDxSettings => component.Kind == ManagedComponentKind.RenoDX
        && component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
    public bool HasOptiScalerSettings => component.Kind == ManagedComponentKind.OptiScaler
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasDlssg2030Settings => component.Kind == ManagedComponentKind.Dlssg2030Unlock
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasStandaloneDlssEnablerSettings => component.Kind == ManagedComponentKind.DlssEnablerStandalone
        && component.Availability is not ComponentAvailability.Unsupported;
    public bool HasRtx40MfgSettings => component.Kind == ManagedComponentKind.Rtx40MfgStandalone
        && component.Availability is not ComponentAvailability.Unsupported;
    public ICommand Command => command;
    public ManagedComponentKind CommandParameter => component.Kind;
}
