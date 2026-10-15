using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The notice lines of the OptiScaler ⚙ dialog (#166): the service's warning above the settings when there are settings
/// to show, and the line in their place when there are none. The same text is never shown twice: without settings the
/// warning (or "no RHI-managed settings") is the one line in their place.
/// </summary>
public static class OptiScalerSettingsNotice
{
    /// <summary>The line above the settings (null = none) and the line in their place (null = the settings are shown).</summary>
    public static (string? Warning, string? Message) For(OptiScalerAdvancedSnapshot snapshot)
    {
        var warning = string.IsNullOrWhiteSpace(snapshot.Warning) ? null : UiLocalizationService.Present(snapshot.Warning);
        if (snapshot.ManagedByRhi && snapshot.Settings.Count > 0) return (warning, null);
        return (null, warning ?? UiLocalizationService.Text("Win.NoRHIManagedOptiScalerSettingsAvailable"));
    }
}
