using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Launch;

namespace RHI.Platform.Linux.Hdr;

public static class LinuxHdrModes
{
    public const string Off = "Off";
    public const string NativeWayland = "Native Wayland";
    public const string Gamescope = "Gamescope";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        NativeWayland => NativeWayland,
        Gamescope => Gamescope,
        _ => Off
    };
}

public sealed class LinuxHdrGameProfile
{
    public string Mode { get; set; } = LinuxHdrModes.Off;
    public bool EnableLegacyWsi { get; set; }

    /// <summary>
    /// #191: switch the display to <see cref="Resolution"/> while the game runs (upstream's per-game RES toggle; off by
    /// default). Not written while off, so a profile without it keeps the shape older builds wrote.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ResolutionToggle { get; set; }

    /// <summary>#191: the target resolution, <c>WIDTHxHEIGHT</c>; null = none chosen (the toggle then does nothing).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    /// <summary>
    /// #211: the game's NVIDIA DLSS settings (presets, render scale, frame generation), set through Proton's NVAPI layer
    /// at launch. Null (not written) while nothing is chosen, so the profile keeps the shape older builds wrote.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NvidiaGameSettings? Nvidia { get; set; }

    public LinuxHdrGameProfile Normalize()
    {
        Mode = LinuxHdrModes.Normalize(Mode);
        Resolution = DisplayResolution.Parse(Resolution)?.ToString();
        Nvidia = Nvidia?.Normalize() is { } nvidia && !nvidia.IsDefault() ? nvidia : null;
        return this;
    }

    /// <summary>True when nothing differs from a game without a profile (the entry can be dropped).</summary>
    public bool IsDefault() => Mode == LinuxHdrModes.Off && !EnableLegacyWsi && !ResolutionToggle && Resolution is null
        && (Nvidia is null || Nvidia.IsDefault());
}

/// <param name="Desktop">XDG_CURRENT_DESKTOP (else DESKTOP_SESSION); null when neither is set.</param>
public sealed record LinuxHdrHostSnapshot(
    bool WaylandSession,
    string? Desktop,
    bool GamescopeAvailable,
    string? NvidiaDriverVersion,
    bool LegacyWsiLikelyRequired)
{
    /// <summary>#191: <c>kscreen-doctor</c> (KDE Plasma) is on PATH.</summary>
    public bool KScreenDoctorAvailable { get; init; }

    /// <summary>#191: <c>wlr-randr</c> (wlroots compositors) is on PATH.</summary>
    public bool WlrRandrAvailable { get; init; }

    /// <summary>
    /// Stand-in used before the host has been probed (a getter never probes it on the UI thread); the plan is rebuilt
    /// once the real probe lands. No desktop name: the UI words an unknown desktop in its own language.
    /// </summary>
    public static LinuxHdrHostSnapshot NotProbed { get; } = new(false, null, false, null, false) { Probed = false };

    /// <summary>False only for <see cref="NotProbed"/>.</summary>
    public bool Probed { get; init; } = true;

    /// <summary>#211: the host's NVIDIA GPU (none until probed).</summary>
    public NvidiaGpuFacts Gpu { get; init; } = NvidiaGpuFacts.None;
}

/// <summary>
/// Why a plan looks the way it does. The service returns codes, never prose: the desktop words them in the
/// UI language (Refs #146; the host line used to read "gamescope available" in English in the PT-BR UI).
/// </summary>
public enum LinuxHdrPlanReason
{
    /// <summary>No game selected (the view model's placeholder plan).</summary>
    NoGame,
    Off,
    GeProtonNativeWayland,
    CachyNativeWayland,
    GamescopeNativeGame,
    GamescopeRunnerGame,
    // Blocked:
    WaylandSessionRequired,
    NativeLinuxToolkitSpecific,
    DirectWineLaunch,
    NoRunnerContract,
    GamescopeMissing,
    /// <summary>
    /// The host has not been probed yet (<see cref="LinuxHdrHostSnapshot.NotProbed"/>): nothing is known about the
    /// session or gamescope, so the profile is not judged (never "gamescope not found"); rebuilt when the probe lands.
    /// </summary>
    HostNotProbed
}

/// <summary>What the plan says about the legacy HDR WSI layer (ENABLE_HDR_WSI).</summary>
public enum LinuxHdrLegacyWsiNote
{
    Off,
    Enabled,
    /// <summary>Not enabled, but the NVIDIA driver is older than 595 and may need it.</summary>
    OlderNvidiaDriver
}

/// <param name="Handoff">
/// The store client that starts the game (<see cref="LaunchHandoffClassifier"/>). Anything but
/// <see cref="LaunchHandoff.None"/> means RHI cannot apply the profile when it launches the game: the profile
/// has to be entered in that launcher (Refs #46).
/// </param>
public sealed record LinuxHdrLaunchPlan(
    bool Enabled,
    bool CanApply,
    IReadOnlyDictionary<string, string> Environment,
    string? CommandPrefix,
    string LaunchOptions,
    LinuxHdrPlanReason Reason,
    LinuxHdrHostSnapshot? Host = null,
    LinuxHdrLegacyWsiNote LegacyWsi = LinuxHdrLegacyWsiNote.Off,
    LaunchHandoff Handoff = LaunchHandoff.None)
{
    /// <summary>Placeholder while no game is selected.</summary>
    public static LinuxHdrLaunchPlan NoGame { get; } = new(false, false, new Dictionary<string, string>(StringComparer.Ordinal), null, "%command%", LinuxHdrPlanReason.NoGame);

    /// <summary>
    /// #211: the game's NVIDIA settings; their variables are already part of <see cref="Environment"/> and
    /// <see cref="LaunchOptions"/> (for every HDR mode, Off included).
    /// </summary>
    public NvidiaLaunchPlan Nvidia { get; init; } = NvidiaLaunchPlan.Hidden(NvidiaPlanReason.NoGame);

    /// <summary>
    /// RHI applies this profile itself when it launches the game (not a store hand-off): an enabled, valid HDR profile,
    /// or (#211) NVIDIA settings with HDR Off.
    /// </summary>
    public bool AppliesAtLaunch => Handoff == LaunchHandoff.None && (Enabled ? CanApply : Nvidia.HasVariables);
}

/// <summary>
/// Linux HDR launch orchestration only. The translation stack belongs to the selected Wine/Proton runner.
/// This service never installs/replaces translation-layer DLLs, Gamescope, Vulkan layers, GPU drivers,
/// or compositor configuration. It only prepares the
/// environment/wrapper for an already configured Wine/Proton/native Linux launch path.
/// </summary>
public sealed partial class LinuxHdrLaunchProfileService
{
    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _commandExists;
    private readonly Func<string?> _nvidiaVersion;
    private readonly Func<NvidiaGpuFacts> _nvidiaGpu;

    /// <param name="nvidiaGpu">#211: the GPU probe (default: <see cref="NvidiaGpuProbe.Read()"/>, read-only /proc and /sys).</param>
    public LinuxHdrLaunchProfileService(
        Func<string, string?>? environment = null,
        Func<string, bool>? commandExists = null,
        Func<string?>? nvidiaVersion = null,
        Func<NvidiaGpuFacts>? nvidiaGpu = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _commandExists = commandExists ?? CommandExists;
        _nvidiaVersion = nvidiaVersion ?? NvidiaGpuProbe.ReadDriverVersion; // #231: one reader (moved to the probe)
        _nvidiaGpu = nvidiaGpu ?? NvidiaGpuProbe.Read;
    }

    public LinuxHdrHostSnapshot ProbeHost()
    {
        var session = _environment("XDG_SESSION_TYPE") ?? "";
        var waylandDisplay = _environment("WAYLAND_DISPLAY") ?? "";
        var wayland = session.Equals("wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(waylandDisplay);
        var desktop = _environment("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = _environment("DESKTOP_SESSION");
        desktop = string.IsNullOrWhiteSpace(desktop) ? null : desktop.Trim();
        var nvidia = _nvidiaVersion();
        var legacy = TryParseMajor(nvidia, out var major) && major < 595;
        return new LinuxHdrHostSnapshot(wayland, desktop, _commandExists("gamescope"), nvidia, legacy)
        {
            KScreenDoctorAvailable = _commandExists(DisplayModeSwitcher.KScreenDoctor),
            WlrRandrAvailable = _commandExists(DisplayModeSwitcher.WlrRandr),
            Gpu = _nvidiaGpu()
        };
    }

    public LinuxHdrLaunchPlan BuildPlan(DetectedGame game, LinuxHdrGameProfile? profile)
        => BuildPlan(game, profile, ProbeHost());

    /// <summary>
    /// The plan for a host probed earlier (<see cref="ProbeHost"/> reads /proc and searches PATH, so the UI probes it
    /// off its thread and builds plans from the snapshot).
    /// </summary>
    public LinuxHdrLaunchPlan BuildPlan(DetectedGame game, LinuxHdrGameProfile? profile, LinuxHdrHostSnapshot host)
        => BuildPlan(game, profile, host, dlss: null);

    /// <summary>
    /// The plan with the game's NVIDIA settings merged in (#211): <paramref name="dlss"/> = the DLSS features the game
    /// ships (null while its files are not known: a saved choice then applies as it is).
    /// </summary>
    public LinuxHdrLaunchPlan BuildPlan(DetectedGame game, LinuxHdrGameProfile? profile, LinuxHdrHostSnapshot host, NvidiaDlssFamilies? dlss)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(host);
        profile = (profile ?? new LinuxHdrGameProfile()).Normalize();
        var handoff = LaunchHandoffClassifier.Classify(game);
        var nvidia = NvidiaLaunchSettings.Plan(game, profile.Nvidia, host, dlss);
        LinuxHdrLaunchPlan plan;
        if (profile.Mode == LinuxHdrModes.Off)
            plan = new LinuxHdrLaunchPlan(false, true, EmptyEnvironment(), null, "%command%", LinuxHdrPlanReason.Off, host);
        // A stand-in host says nothing about the session or gamescope: never word it as a missing tool or a session.
        else if (!host.Probed)
            plan = Blocked(profile, host, LinuxHdrPlanReason.HostNotProbed);
        else
            plan = profile.Mode == LinuxHdrModes.NativeWayland
                ? BuildNativeWayland(game, profile, host)
                : BuildGamescope(game, profile, host);
        plan = WithNvidia(plan, nvidia) with { Handoff = handoff };
        return handoff == LaunchHandoff.None ? KeepingInheritedDrsSettings(plan) : plan;
    }

    /// <summary>
    /// #211: a direct launch replaces the game's <see cref="NvidiaLaunchSettings.DrsSettings"/> with the plan's, so the
    /// user's own value (in RHI's environment) is kept in front of RHI's entries. A store hand-off line is pasted over
    /// the user's launch options anyway and stays RHI's alone.
    /// </summary>
    private LinuxHdrLaunchPlan KeepingInheritedDrsSettings(LinuxHdrLaunchPlan plan)
    {
        if (!plan.Environment.TryGetValue(NvidiaLaunchSettings.DrsSettings, out var ours)) return plan;
        var inherited = _environment(NvidiaLaunchSettings.DrsSettings);
        if (string.IsNullOrWhiteSpace(inherited)) return plan;
        var env = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal)
        {
            [NvidiaLaunchSettings.DrsSettings] = NvidiaLaunchSettings.MergeDrsSettings(inherited, ours)
        };
        return plan with { Environment = env };
    }

    /// <summary>
    /// #211: adds the NVIDIA variables to the plan's environment and its launch options. A blocked HDR mode adds nothing
    /// of its own to the launch options (they stay copyable for the NVIDIA part only); HDR's variables win a clash.
    /// </summary>
    private static LinuxHdrLaunchPlan WithNvidia(LinuxHdrLaunchPlan plan, NvidiaLaunchPlan nvidia)
    {
        if (!nvidia.HasVariables) return plan with { Nvidia = nvidia };
        var env = new Dictionary<string, string>(nvidia.Environment, StringComparer.Ordinal);
        foreach (var pair in plan.Environment) env[pair.Key] = pair.Value;
        var applies = plan.Enabled && plan.CanApply;
        var options = BuildLaunchOptions(applies ? env : nvidia.Environment, applies ? plan.CommandPrefix : null);
        return plan with { Environment = env, LaunchOptions = options, Nvidia = nvidia };
    }

    private LinuxHdrLaunchPlan BuildNativeWayland(DetectedGame game, LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        // Native Wayland HDR requires a Wayland desktop session; RHI will not change the desktop session for the game.
        if (!host.WaylandSession)
            return Blocked(profile, host, LinuxHdrPlanReason.WaylandSessionRequired);

        // Native Linux HDR is application/toolkit-specific: no generic SDL/Vulkan colour-management variables.
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return Blocked(profile, host, LinuxHdrPlanReason.NativeLinuxToolkitSpecific);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        var family = RunnerFamily(game);
        // Started with the Proton build's wine, not the proton script: PROTON_* variables would be ignored.
        if (family != "Wine" && IsDirectWineLaunch(game))
            return Blocked(profile, host, LinuxHdrPlanReason.DirectWineLaunch);
        LinuxHdrPlanReason reason;
        if (family == "GE")
        {
            // GE-Proton exposes its own HDR alias; do not redundantly inject the lower-level runner toggle.
            env["PROTON_ENABLE_HDR"] = "1";
            env["PROTON_ENABLE_WAYLAND"] = "1";
            reason = LinuxHdrPlanReason.GeProtonNativeWayland;
        }
        else if (family == "Cachy")
        {
            // Proton-Cachy removed PROTON_ENABLE_HDR; its documented runner toggle is DXVK_HDR.
            env["DXVK_HDR"] = "1";
            env["PROTON_ENABLE_WAYLAND"] = "1";
            reason = LinuxHdrPlanReason.CachyNativeWayland;
        }
        else
        {
            // Only runner families with an explicit current contract; RHI will not guess HDR variables.
            return Blocked(profile, host, LinuxHdrPlanReason.NoRunnerContract);
        }

        ApplyLegacyWsi(profile, env);
        var options = BuildLaunchOptions(env, null);
        return new LinuxHdrLaunchPlan(true, true, env, null, options, reason, host, LegacyNote(profile, host));
    }

    private LinuxHdrLaunchPlan BuildGamescope(DetectedGame game, LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        // RHI never installs gamescope.
        if (!host.GamescopeAvailable)
            return Blocked(profile, host, LinuxHdrPlanReason.GamescopeMissing);

        // Gamescope is the HDR authority in this mode. Do not layer runner-specific HDR toggles
        // on top of it; that would duplicate policy already owned by the compositor/runner path.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyLegacyWsi(profile, env);
        // #191: with the resolution toggle on, gamescope's own output size is the game's resolution (no display switch).
        var prefix = DisplayModeSwitcher.GamescopeSize(profile) is { } size
            ? $"gamescope -W {size.Width} -H {size.Height} --hdr-enabled --"
            : "gamescope --hdr-enabled --";
        var options = BuildLaunchOptions(env, prefix);
        var reason = string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
            ? LinuxHdrPlanReason.GamescopeNativeGame
            : LinuxHdrPlanReason.GamescopeRunnerGame;
        return new LinuxHdrLaunchPlan(true, true, env, prefix, options, reason, host, LegacyNote(profile, host));
    }

    private static LinuxHdrLaunchPlan Blocked(LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host, LinuxHdrPlanReason reason)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyLegacyWsi(profile, env);
        return new LinuxHdrLaunchPlan(true, false, env, null, "%command%", reason, host, LegacyNote(profile, host));
    }

    private static void ApplyLegacyWsi(LinuxHdrGameProfile profile, IDictionary<string, string> environment)
    {
        if (profile.EnableLegacyWsi) environment["ENABLE_HDR_WSI"] = "1";
    }

    private static LinuxHdrLegacyWsiNote LegacyNote(LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        if (profile.EnableLegacyWsi) return LinuxHdrLegacyWsiNote.Enabled;
        return host.LegacyWsiLikelyRequired ? LinuxHdrLegacyWsiNote.OlderNvidiaDriver : LinuxHdrLegacyWsiNote.Off;
    }

    private static IReadOnlyDictionary<string, string> EmptyEnvironment()
        => new Dictionary<string, string>(StringComparer.Ordinal);


    /// <summary>
    /// Store launchers spawn the actual game later. Wrapping the launcher process itself with
    /// Gamescope or HDR variables is not a reliable per-game application path, so RHI only
    /// generates a profile for the launcher UI in those cases. Same classification as the launcher's
    /// working-directory choice (<see cref="LaunchHandoffClassifier"/>, Refs #106).
    /// </summary>
    public static bool IsLauncherMediated(DetectedGame game) => LaunchHandoffClassifier.IsHandoff(game);

    /// <summary>
    /// Standalone Wine-prefix entries (including Proton-built prefixes) are started with the runner's
    /// <c>wine</c> binary, never the <c>proton</c> script that reads PROTON_* variables. Manual games
    /// keep their own launch command (which may be umu-run/proton) and are not judged here.
    /// </summary>
    internal static bool IsDirectWineLaunch(DetectedGame game)
        => string.Equals(game.Source, "Wine", StringComparison.OrdinalIgnoreCase);

    internal static string BuildLaunchOptions(IReadOnlyDictionary<string, string> environment, string? prefix)
    {
        var parts = new List<string>();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            parts.Add($"{pair.Key}={ShellQuote(pair.Value)}");
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix.Trim());
        parts.Add("%command%");
        return string.Join(" ", parts);
    }

    internal static string RunnerFamily(DetectedGame game)
    {
        var text = string.Join(' ', new[] { game.CompatibilityTool, game.Runner, game.CompatibilityLayerType }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (text.Contains("cachy", StringComparison.OrdinalIgnoreCase)) return "Cachy";
        if (text.Contains("proton-ge", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ge-proton", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\bGE[-_ ]?Proton", RegexOptions.IgnoreCase)) return "GE";
        if (text.Contains("proton", StringComparison.OrdinalIgnoreCase)) return "Proton";
        return "Wine";
    }


    private static string ShellQuote(string value)
        => value.Length > 0 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool CommandExists(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar)) return File.Exists(command);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, command)));
    }

    private static bool TryParseMajor(string? value, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var first = value.Split('.', 2)[0];
        return int.TryParse(first, out major);
    }
}
