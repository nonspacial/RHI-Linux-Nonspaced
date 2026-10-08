using System.Text.RegularExpressions;
using RHI.Core.Models;
using RHI.Core.Services;

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

    public LinuxHdrGameProfile Normalize()
    {
        Mode = LinuxHdrModes.Normalize(Mode);
        return this;
    }
}

public sealed record LinuxHdrHostSnapshot(
    bool WaylandSession,
    string Desktop,
    bool GamescopeAvailable,
    string? NvidiaDriverVersion,
    bool LegacyWsiLikelyRequired)
{
    public string Summary
    {
        get
        {
            var session = WaylandSession ? "Wayland" : "non-Wayland";
            var gamescope = GamescopeAvailable ? "gamescope available" : "gamescope not found";
            var nvidia = string.IsNullOrWhiteSpace(NvidiaDriverVersion) ? "" : $" · NVIDIA {NvidiaDriverVersion}";
            var legacy = LegacyWsiLikelyRequired ? " · legacy HDR WSI may be required" : "";
            return $"{session} · {Desktop} · {gamescope}{nvidia}{legacy}";
        }
    }
}

public sealed record LinuxHdrLaunchPlan(
    bool Enabled,
    bool CanApply,
    IReadOnlyDictionary<string, string> Environment,
    string? CommandPrefix,
    string LaunchOptions,
    string Summary);

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

    public LinuxHdrLaunchProfileService(
        Func<string, string?>? environment = null,
        Func<string, bool>? commandExists = null,
        Func<string?>? nvidiaVersion = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _commandExists = commandExists ?? CommandExists;
        _nvidiaVersion = nvidiaVersion ?? ReadNvidiaVersion;
    }

    public LinuxHdrHostSnapshot ProbeHost()
    {
        var session = _environment("XDG_SESSION_TYPE") ?? "";
        var waylandDisplay = _environment("WAYLAND_DISPLAY") ?? "";
        var wayland = session.Equals("wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(waylandDisplay);
        var desktop = _environment("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = _environment("DESKTOP_SESSION");
        desktop = string.IsNullOrWhiteSpace(desktop) ? "desktop unknown" : desktop.Trim();
        var nvidia = _nvidiaVersion();
        var legacy = TryParseMajor(nvidia, out var major) && major < 595;
        return new LinuxHdrHostSnapshot(wayland, desktop, _commandExists("gamescope"), nvidia, legacy);
    }

    public LinuxHdrLaunchPlan BuildPlan(DetectedGame game, LinuxHdrGameProfile? profile)
        => BuildPlan(game, profile, ProbeHost());

    /// <summary>
    /// The plan for a host probed earlier (<see cref="ProbeHost"/> reads /proc and searches PATH, so the UI probes it
    /// off its thread and builds plans from the snapshot).
    /// </summary>
    public LinuxHdrLaunchPlan BuildPlan(DetectedGame game, LinuxHdrGameProfile? profile, LinuxHdrHostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(host);
        profile = (profile ?? new LinuxHdrGameProfile()).Normalize();
        if (profile.Mode == LinuxHdrModes.Off)
            return new LinuxHdrLaunchPlan(false, true, EmptyEnvironment(), null, "%command%",
                $"HDR launch profile is Off. Runner graphics stack remains untouched. Host: {host.Summary}.");

        if (profile.Mode == LinuxHdrModes.NativeWayland)
            return BuildNativeWayland(game, profile, host);

        return BuildGamescope(game, profile, host);
    }

    private LinuxHdrLaunchPlan BuildNativeWayland(DetectedGame game, LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        if (!host.WaylandSession)
            return Blocked(profile, host, "Native Wayland HDR requires a Wayland desktop session. RHI will not change the desktop session for the game.");

        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return Blocked(profile, host,
                "Native Linux HDR is application/toolkit-specific. RHI does not force generic SDL/Vulkan color-management variables onto native games; use Gamescope mode only when that game is known to work through Gamescope.");

        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        var family = RunnerFamily(game);
        if (family != "Wine" && IsDirectWineLaunch(game))
            return Blocked(profile, host,
                "This game is started directly with its Proton build's wine, not through the proton script, so Proton's HDR/Wayland variables do not apply. Launch it through Steam or a Proton/umu launcher for native Wayland HDR, or use Gamescope mode.");
        string note;
        if (family == "GE")
        {
            // GE-Proton exposes its own HDR alias; do not redundantly inject the lower-level runner toggle.
            env["PROTON_ENABLE_HDR"] = "1";
            env["PROTON_ENABLE_WAYLAND"] = "1";
            note = "GE-Proton native Wayland HDR: PROTON_ENABLE_HDR + PROTON_ENABLE_WAYLAND.";
        }
        else if (family == "Cachy")
        {
            // Proton-Cachy removed PROTON_ENABLE_HDR; its documented runner toggle is DXVK_HDR.
            env["DXVK_HDR"] = "1";
            env["PROTON_ENABLE_WAYLAND"] = "1";
            note = "Proton-Cachy native Wayland HDR: runner HDR toggle + PROTON_ENABLE_WAYLAND. RHI does not install or replace translation-layer binaries.";
        }
        else
        {
            return Blocked(profile, host,
                "Native Wayland HDR automation is enabled only for runner families with an explicit current contract (GE-Proton or Proton-Cachy). Generic Proton/Wine remains runner-configured; RHI will not guess HDR variables.");
        }

        ApplyLegacyWsi(profile, env);
        var options = BuildLaunchOptions(env, null);
        return new LinuxHdrLaunchPlan(true, true, env, null, options,
            $"{note} Desktop HDR must already be enabled. Host: {host.Summary}.{LegacyNote(profile, host)}");
    }

    private LinuxHdrLaunchPlan BuildGamescope(DetectedGame game, LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        if (!host.GamescopeAvailable)
            return Blocked(profile, host, "Gamescope mode was selected, but the gamescope executable was not found in PATH. RHI will not install it automatically.");

        // Gamescope is the HDR authority in this mode. Do not layer runner-specific HDR toggles
        // on top of it; that would duplicate policy already owned by the compositor/runner path.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyLegacyWsi(profile, env);
        const string prefix = "gamescope --hdr-enabled --";
        var options = BuildLaunchOptions(env, prefix);
        var runnerNote = string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
            ? "Native game is only wrapped; no Wine/Proton variables are injected."
            : "The existing Wine/Proton graphics stack remains runner-managed; RHI adds no translation-layer HDR variables in Gamescope mode.";
        return new LinuxHdrLaunchPlan(true, true, env, prefix, options,
            $"Gamescope HDR wrapper enabled. {runnerNote} Desktop/output HDR support is still required. Host: {host.Summary}.{LegacyNote(profile, host)}");
    }

    private static LinuxHdrLaunchPlan Blocked(LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host, string reason)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        ApplyLegacyWsi(profile, env);
        return new LinuxHdrLaunchPlan(true, false, env, null, "%command%", $"{reason} Host: {host.Summary}.");
    }

    private static void ApplyLegacyWsi(LinuxHdrGameProfile profile, IDictionary<string, string> environment)
    {
        if (profile.EnableLegacyWsi) environment["ENABLE_HDR_WSI"] = "1";
    }

    private static string LegacyNote(LinuxHdrGameProfile profile, LinuxHdrHostSnapshot host)
    {
        if (profile.EnableLegacyWsi)
            return " Legacy HDR WSI compatibility is explicitly enabled (ENABLE_HDR_WSI=1).";
        if (host.LegacyWsiLikelyRequired)
            return " Detected NVIDIA driver is older than 595; Legacy WSI may be needed if the driver lacks native Vulkan HDR WSI.";
        return " Legacy WSI is off.";
    }

    private static IReadOnlyDictionary<string, string> EmptyEnvironment()
        => new Dictionary<string, string>(StringComparer.Ordinal);


    /// <summary>
    /// Store launchers spawn the actual game later. Wrapping the launcher process itself with
    /// Gamescope or HDR variables is not a reliable per-game application path, so RHI only
    /// generates a profile for the launcher UI in those cases.
    /// </summary>
    public static bool IsLauncherMediated(DetectedGame game)
    {
        var source = game.Source ?? "";
        if (source.Equals("Steam", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("Heroic", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("Lutris", StringComparison.OrdinalIgnoreCase)) return true;

        var command = game.LaunchCommand?.TrimStart() ?? "";
        return command.StartsWith("steam ", StringComparison.OrdinalIgnoreCase) ||
               command.StartsWith("heroic ", StringComparison.OrdinalIgnoreCase) ||
               command.StartsWith("lutris ", StringComparison.OrdinalIgnoreCase);
    }

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

    private static string? ReadNvidiaVersion()
    {
        try
        {
            const string path = "/proc/driver/nvidia/version";
            if (!File.Exists(path)) return null;
            var match = NvidiaVersionRegex().Match(File.ReadAllText(path));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static bool TryParseMajor(string? value, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var first = value.Split('.', 2)[0];
        return int.TryParse(first, out major);
    }

    [GeneratedRegex(@"(?:Kernel Module\s+|NVIDIA[^0-9]*)([0-9]{3,}(?:\.[0-9]+)+)", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaVersionRegex();
}
