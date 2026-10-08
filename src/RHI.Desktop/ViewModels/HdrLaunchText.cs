using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// HDR launch profile wording, in the UI language (Refs #146: the service returns reason codes, never prose).
/// Store hand-offs get the steps to enter the profile in that launcher instead of a launch that would wrap the
/// store client and report HDR as applied (Refs #46).
/// </summary>
public static class HdrLaunchText
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>The line under the HDR controls.</summary>
    public static string Hint(LinuxHdrLaunchPlan plan, DetectedGame? game)
    {
        var text = Describe(plan);
        return game is not null && plan.Enabled && plan.CanApply && plan.Handoff != LaunchHandoff.None
            ? text + " " + ApplyInLauncher(plan, game)
            : text;
    }

    /// <summary>What the plan does (or why it is blocked), then the host it was built for.</summary>
    public static string Describe(LinuxHdrLaunchPlan plan)
    {
        var text = plan.Reason switch
        {
            LinuxHdrPlanReason.NoGame => T("Vm.SelectGameConfigureHDRLaunchOrchestration"),
            LinuxHdrPlanReason.Off => T("Hdr.Off"),
            LinuxHdrPlanReason.GeProtonNativeWayland => T("Hdr.GeProtonNativeWayland"),
            LinuxHdrPlanReason.CachyNativeWayland => T("Hdr.CachyNativeWayland"),
            LinuxHdrPlanReason.GamescopeNativeGame => T("Hdr.GamescopeNativeGame"),
            LinuxHdrPlanReason.GamescopeRunnerGame => T("Hdr.GamescopeRunnerGame"),
            LinuxHdrPlanReason.WaylandSessionRequired => T("Hdr.WaylandSessionRequired"),
            LinuxHdrPlanReason.NativeLinuxToolkitSpecific => T("Hdr.NativeLinuxToolkitSpecific"),
            LinuxHdrPlanReason.DirectWineLaunch => T("Hdr.DirectWineLaunch"),
            LinuxHdrPlanReason.NoRunnerContract => T("Hdr.NoRunnerContract"),
            LinuxHdrPlanReason.GamescopeMissing => T("Hdr.GamescopeMissing"),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Reason, null)
        };
        if (plan.Host is { } host) text += " " + TF("Hdr.Host", Host(host));
        if (plan.Enabled && plan.CanApply)
        {
            text += " " + plan.LegacyWsi switch
            {
                LinuxHdrLegacyWsiNote.Enabled => T("Hdr.LegacyWsiEnabled"),
                LinuxHdrLegacyWsiNote.OlderNvidiaDriver => T("Hdr.LegacyWsiOlderDriver"),
                _ => T("Hdr.LegacyWsiOff")
            };
        }
        return text;
    }

    public static string Host(LinuxHdrHostSnapshot host)
    {
        var parts = new List<string>
        {
            host.WaylandSession ? T("Hdr.HostWayland") : T("Hdr.HostNotWayland"),
            host.Desktop ?? T("Hdr.HostDesktopUnknown"),
            host.GamescopeAvailable ? T("Hdr.HostGamescopeAvailable") : T("Hdr.HostGamescopeNotFound")
        };
        if (!string.IsNullOrWhiteSpace(host.NvidiaDriverVersion)) parts.Add(TF("Hdr.HostNvidia", host.NvidiaDriverVersion));
        if (host.LegacyWsiLikelyRequired) parts.Add(T("Hdr.HostLegacyWsiMayBeNeeded"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Why Launch does not go ahead, or null when it may: a blocked profile, or a launcher that cannot take
    /// launch options. A store hand-off is not refused: it starts unwrapped (see <see cref="HandoffNotice"/>).
    /// </summary>
    public static string? LaunchRefusal(LinuxHdrLaunchPlan plan, bool launcherAcceptsOptions)
    {
        if (!plan.Enabled) return null;
        if (!plan.CanApply) return TF("Vm.HDRLaunchBlocked", Describe(plan));
        if (plan.Handoff != LaunchHandoff.None) return null;
        return launcherAcceptsOptions ? null : T("Vm.HDRLaunchProfileEnabledButLauncher");
    }

    /// <summary>
    /// For an enabled profile on a store hand-off: RHI starts the game through its launcher without the
    /// profile (wrapping the store client would not reach the game, Refs #46) and says where to enter it.
    /// Null otherwise.
    /// </summary>
    public static string? HandoffNotice(LinuxHdrLaunchPlan plan, DetectedGame game)
        => plan.Enabled && plan.CanApply && plan.Handoff != LaunchHandoff.None ? ApplyInLauncher(plan, game) : null;

    /// <summary>Status after Copy launch profile: where to paste it.</summary>
    public static string Copied(LinuxHdrLaunchPlan plan, DetectedGame? game)
        => game is not null && plan.Handoff != LaunchHandoff.None
            ? ApplyInLauncher(plan, game)
            : T("Win.HDRLaunchProfileCopiedSteamCan");

    /// <summary>The launcher that starts the game, and where in it the profile goes.</summary>
    public static string ApplyInLauncher(LinuxHdrLaunchPlan plan, DetectedGame game)
    {
        var entry = string.IsNullOrWhiteSpace(game.ParentLauncherName) ? game.Name : game.ParentLauncherName;
        return plan.Handoff switch
        {
            LaunchHandoff.Steam => TF("Hdr.ApplyInSteam", plan.LaunchOptions),
            LaunchHandoff.Heroic => TF("Hdr.ApplyInHeroic", entry, Settings(plan, "Hdr.WrapperCommand")),
            LaunchHandoff.Lutris => TF("Hdr.ApplyInLutris", entry, Settings(plan, "Hdr.CommandPrefix")),
            _ => TF("Hdr.ApplyInOtherLauncher", Settings(plan, "Hdr.WrapperCommand"))
        };
    }

    // Heroic and Lutris take variables and a wrapper in separate fields; Steam's %command% form is not their syntax.
    private static string Settings(LinuxHdrLaunchPlan plan, string wrapperKey)
    {
        var parts = new List<string>();
        if (plan.Environment.Count > 0)
            parts.Add(TF("Hdr.EnvironmentVariables",
                string.Join(", ", plan.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))));
        if (!string.IsNullOrWhiteSpace(plan.CommandPrefix)) parts.Add(TF(wrapperKey, plan.CommandPrefix.Trim()));
        return string.Join("; ", parts);
    }
}
