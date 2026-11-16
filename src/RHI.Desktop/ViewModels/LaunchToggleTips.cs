using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #309 (upstream 2.8.9 <c>HdrToggleTooltip</c> / <c>ResToggleTooltip</c>): hovering the HDR and resolution launch
/// controls says whether they are on for the game and where that comes from. Linux keeps both per game only (no global
/// Settings default, unlike upstream's "following Settings › Display &amp; HDR"), so "on" is always "set for this game".
/// </summary>
public static class LaunchToggleTips
{
    /// <param name="modeDisplay">The HDR mode as the list shows it (Gamescope, Native Wayland…).</param>
    public static string Hdr(bool on, string modeDisplay)
        => on ? UiLocalizationService.Format("Launch.HdrOnTip", modeDisplay) : UiLocalizationService.Text("Launch.HdrOffTip");

    /// <summary>The state, what the switch does (the existing line, Gamescope included) and how to change it.</summary>
    public static string Resolution(bool on)
        => UiLocalizationService.Format(on ? "Launch.ResOnTip" : "Launch.ResOffTip", UiLocalizationService.Text("Shell.ResToggleTip"));
}
