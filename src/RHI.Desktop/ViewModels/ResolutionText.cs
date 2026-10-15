using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #191: the resolution auto-toggle's wording (plans carry reason codes, as <see cref="HdrLaunchText"/>): the line under the
/// controls and the launch status additions.
/// </summary>
public static class ResolutionText
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>The plain line under the resolution controls; empty while the toggle is off.</summary>
    public static string Hint(ResolutionPlan plan, DetectedGame? game)
    {
        if (game is null || !plan.Enabled) return "";
        var size = plan.Target?.ToString() ?? "";
        return plan.Reason switch
        {
            ResolutionPlanReason.GamescopeSize => TF("Shell.ResGamescope", size),
            ResolutionPlanReason.KScreenDoctor or ResolutionPlanReason.WlrRandr => TF("Shell.ResSwitches", size),
            ResolutionPlanReason.Handoff => T("Shell.ResHandoff"),
            ResolutionPlanReason.DesktopUnsupported => T("Shell.ResDesktopUnsupported"),
            ResolutionPlanReason.ToolMissing => TF("Shell.ResToolMissing",
                plan.Tool == DisplayTool.WlrRandr ? DisplayModeSwitcher.WlrRandr : DisplayModeSwitcher.KScreenDoctor),
            ResolutionPlanReason.HostNotProbed => T("Shell.ResHostNotProbed"),
            _ => ""
        };
    }

    /// <summary>The status addition after a switch attempt before Play; null when there is nothing to say.</summary>
    public static string? Switch(DisplaySwitchResult result, ResolutionPlan plan)
    {
        var line = result.Outcome switch
        {
            DisplaySwitchOutcome.Switched => TF("Shell.ResSwitched", plan.Target),
            DisplaySwitchOutcome.EarlierSwitchPending => T("Shell.ResEarlierPending"),
            DisplaySwitchOutcome.RecordUnreadable => TF("Shell.ResRecordStuck", result.Detail),
            // The tool may have applied the mode and going back failed too: the record is kept for the next start (w5 nit 3).
            DisplaySwitchOutcome.Failed when result.State is not null => TF("Shell.ResSwitchFailedKept", UiLocalizationService.Present(result.Detail)),
            DisplaySwitchOutcome.Failed => TF("Shell.ResSwitchFailed", UiLocalizationService.Present(result.Detail)),
            _ => null
        };
        if (result.SetAsideCopy is not { } copy) return line;
        var note = TF("Shell.ResRecordSetAside", copy);
        return line is null ? note : note + " " + line;
    }

    /// <summary>The status line after putting the display back; null when nothing was to be done.</summary>
    public static string? Restore(DisplayRestoreResult result) => result.Outcome switch
    {
        DisplayRestoreOutcome.Restored => T("Shell.ResRestored"),
        DisplayRestoreOutcome.RecordUnreadable => result.SetAsideCopy is { } copy
            ? TF("Shell.ResRecordSetAside", copy)
            : TF("Shell.ResRecordStuck", result.Detail),
        DisplayRestoreOutcome.Failed => TF("Shell.ResRestoreFailed", UiLocalizationService.Present(result.Detail)),
        _ => null
    };
}
