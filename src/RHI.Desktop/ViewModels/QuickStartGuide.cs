namespace RHI.Desktop.ViewModels;

/// <summary>One part of the Quick Start guide: a badge (step number or symbol), a title, what to do, and tips (optional).</summary>
public sealed record QuickStartStep(string Badge, string TitleKey, string BodyKey, string? TipKey);

/// <summary>
/// #309 (upstream 2.8.9 "The Quick Start guide has been rewritten to match the current app", MainWindow.FaqBuilder): the
/// Quick Start in Help, in upstream's order (welcome with the GitHub token first, pick a game, ReShade, RenoDX / Luma,
/// shaders, DOF Fix and frame limiters, DLSS / Streamline, Neural Rendering, OptiScaler, Extras, the HDR and resolution
/// launch controls, adding games, updating, troubleshooting). It names this app's own buttons and sections; upstream's
/// Windows-only parts (NVIDIA driver profile, Windows HDR, the global Vulkan layer, the system tray's jump list) are left
/// out or said the Linux way.
/// </summary>
public static class QuickStartGuide
{
    public static IReadOnlyList<QuickStartStep> Steps { get; } =
    [
        new("★", "Qs.WelcomeTitle", "Qs.WelcomeBody", "Qs.WelcomeTip"),
        new("1", "Qs.SelectTitle", "Qs.SelectBody", "Qs.SelectTip"),
        new("2", "Qs.ReShadeTitle", "Qs.ReShadeBody", "Qs.ReShadeTip"),
        new("3", "Qs.HdrModsTitle", "Qs.HdrModsBody", "Qs.HdrModsTip"),
        new("4", "Qs.ShadersTitle", "Qs.ShadersBody", "Qs.ShadersTip"),
        new("5", "Qs.LimitersTitle", "Qs.LimitersBody", "Qs.LimitersTip"),
        new("6", "Qs.DlssTitle", "Qs.DlssBody", "Qs.DlssTip"),
        new("N", "Qs.NeuralTitle", "Qs.NeuralBody", "Qs.NeuralTip"),
        new("⚙", "Qs.OptiScalerTitle", "Qs.OptiScalerBody", "Qs.OptiScalerTip"),
        new("X", "Qs.ExtrasTitle", "Qs.ExtrasBody", null),
        new("H", "Qs.LaunchTitle", "Qs.LaunchBody", "Qs.LaunchTip"),
        new("+", "Qs.AddTitle", "Qs.AddBody", "Qs.AddTip"),
        new("↑", "Qs.UpdateTitle", "Qs.UpdateBody", "Qs.UpdateTip"),
        new("↻", "Qs.TroubleTitle", "Qs.TroubleBody", "Qs.TroubleTip"),
    ];
}
