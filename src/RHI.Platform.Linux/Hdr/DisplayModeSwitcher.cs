using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Hdr;

/// <summary>A display resolution, <c>WIDTHxHEIGHT</c>.</summary>
public sealed record DisplayResolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";

    /// <summary><c>2560x1440</c> (any case, spaces allowed around the x); null for anything else or a size outside 320–16384.</summary>
    public static DisplayResolution? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().ToLowerInvariant().Split('x');
        if (parts.Length != 2) return null;
        return int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
               && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
               && width is >= 320 and <= 16384 && height is >= 200 and <= 16384
            ? new DisplayResolution(width, height)
            : null;
    }
}

/// <summary>The tool that changes the display mode around a direct launch (#191).</summary>
public enum DisplayTool
{
    None,
    /// <summary>KDE Plasma (X11 or Wayland).</summary>
    KScreenDoctor,
    /// <summary>wlroots compositors (sway, Hyprland, river, Wayfire, labwc…), Wayland only.</summary>
    WlrRandr
}

/// <summary>Why a resolution plan looks the way it does; the desktop words it (no prose here).</summary>
public enum ResolutionPlanReason
{
    Off,
    /// <summary>The HDR profile runs the game in gamescope: its <c>-W/-H</c> carry the resolution, the desktop is not touched.</summary>
    GamescopeSize,
    /// <summary>The display switches with kscreen-doctor before the game starts and back when it ends.</summary>
    KScreenDoctor,
    /// <summary>The display switches with wlr-randr before the game starts and back when it ends.</summary>
    WlrRandr,
    // Not applied:
    /// <summary>A store client starts the game: RHI does not see it end, so it never switches around it.</summary>
    Handoff,
    /// <summary>Neither KDE Plasma nor a wlroots compositor (GNOME, X11 without KDE…).</summary>
    DesktopUnsupported,
    /// <summary>The desktop's tool is not installed.</summary>
    ToolMissing,
    /// <summary>The host has not been probed yet; rebuilt when it lands.</summary>
    HostNotProbed
}

/// <summary>What happens to the display for a launch (#191).</summary>
public sealed record ResolutionPlan(bool Enabled, bool CanApply, ResolutionPlanReason Reason, DisplayResolution? Target, DisplayTool Tool)
{
    public static ResolutionPlan Off { get; } = new(false, true, ResolutionPlanReason.Off, null, DisplayTool.None);

    /// <summary>RHI switches the display before the launch and back when the game's command ends.</summary>
    public bool SwitchesAroundLaunch => Enabled && CanApply && Tool != DisplayTool.None && Target is not null;
}

/// <summary>
/// The mode RHI switched away from, written before the switch (<c>$XDG_STATE_HOME/rhi-linux/display-mode.json</c>) and removed
/// once it is back. A file left behind (RHI closed or crashed while the game ran) is restored at the next start, after the
/// recorded game process has ended.
/// </summary>
public sealed record DisplayModeState
{
    public DisplayTool Tool { get; init; }
    public string Output { get; init; } = "";
    /// <summary>The tool arguments that put the previous mode back.</summary>
    public IReadOnlyList<string> RestoreArguments { get; init; } = [];
    public string PreviousMode { get; init; } = "";
    public string TargetMode { get; init; } = "";
    public DateTime SwitchedAtUtc { get; init; }
    public int? GamePid { get; init; }
    /// <summary>The game process's start time (<c>/proc/&lt;pid&gt;/stat</c> field 22), so a reused pid is not taken for it.</summary>
    public long? GameStartTicks { get; init; }
}

public enum DisplaySwitchOutcome
{
    Switched,
    /// <summary>The display already shows the target size: nothing was changed or recorded.</summary>
    AlreadyAtTarget,
    /// <summary>An earlier switch has not been put back yet; RHI never stacks a second one on it.</summary>
    EarlierSwitchPending,
    Failed
}

/// <param name="Detail">For <see cref="DisplaySwitchOutcome.Failed"/>: the tool's message or the reason (platform text).</param>
public sealed record DisplaySwitchResult(DisplaySwitchOutcome Outcome, DisplayModeState? State = null, string? Detail = null);

public enum DisplayRestoreOutcome
{
    NothingToRestore,
    Restored,
    /// <summary>The recorded game is still running: restored when it ends.</summary>
    WaitingForGame,
    Failed
}

public sealed record DisplayRestoreResult(DisplayRestoreOutcome Outcome, string? Detail = null);

/// <summary>
/// Resolution auto-toggle (Refs #191, upstream <c>ResolutionToggleService</c> / <c>ResToggle_Click</c>). A direct launch
/// switches the primary output to the game's resolution with <c>kscreen-doctor</c> (KDE) or <c>wlr-randr</c> (wlroots)
/// and switches it back when the game's command ends; a gamescope HDR launch carries the size as <c>-W/-H</c> instead; a
/// store hand-off is never switched around (RHI does not see the game end). The mode to go back to is recorded before the
/// switch; a record left behind is restored at the next start once the recorded game has ended. Tools run through
/// <see cref="ExternalProcess"/> (time limit, tree kill), found on PATH; the runner is a seam so tests use fake tools and
/// never touch a real display.
/// </summary>
public sealed class DisplayModeSwitcher
{
    public const string KScreenDoctor = "kscreen-doctor";
    public const string WlrRandr = "wlr-randr";

    /// <summary>Fixed text before a tool's own message when it fails (platform message; PT-BR pair).</summary>
    public const string ToolFailedPrefix = "The display tool reported an error: ";
    /// <summary>Fixed text when the tool's answer lists no enabled output with the target size (platform message).</summary>
    public const string NoModePrefix = "The display does not offer this resolution: ";
    /// <summary>Fixed text when the tool is not on PATH (platform message).</summary>
    public const string ToolMissingPrefix = "The display tool is not installed: ";
    /// <summary>Fixed text when the record of the previous mode cannot be written (nothing was switched).</summary>
    public const string StateUnsavedPrefix = "RHI could not record the current display mode, so it did not change it: ";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly JsonFileStore<DisplayModeState> _state;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<ExternalProcessResult>> _run;
    private readonly Func<int, long?, bool> _isRunning;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _restoring = new(1, 1);

    /// <param name="statePath">Where the previous mode is recorded (default <c>$XDG_STATE_HOME/rhi-linux/display-mode.json</c>).</param>
    /// <param name="environment">PATH and desktop lookup (test seam).</param>
    /// <param name="run">Runs a tool (absolute path, arguments); test seam. Default: <see cref="ExternalProcess.RunAsync"/>.</param>
    /// <param name="isRunning">Whether the recorded game process (pid, start ticks) still runs; test seam over <c>/proc</c>.</param>
    public DisplayModeSwitcher(
        string? statePath = null,
        Func<string, string?>? environment = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ExternalProcessResult>>? run = null,
        Func<int, long?, bool>? isRunning = null)
    {
        StatePath = statePath ?? Path.Combine(LinuxPaths.StateHome, "rhi-linux", "display-mode.json");
        _state = new JsonFileStore<DisplayModeState>(StatePath, Options);
        _environment = environment ?? LinuxPaths.Environment;
        _run = run ?? RunToolAsync;
        _isRunning = isRunning ?? IsProcessRunning;
    }

    public string StatePath { get; }

    /// <summary>The recorded switch still to be put back, or null.</summary>
    public DisplayModeState? Pending => _state.Load().Value;

    // ---- plan ------------------------------------------------------------------------------------------------------

    /// <summary>The size gamescope runs the game at when the toggle is on with a resolution chosen, else null.</summary>
    public static DisplayResolution? GamescopeSize(LinuxHdrGameProfile profile)
        => profile.ResolutionToggle ? DisplayResolution.Parse(profile.Resolution) : null;

    /// <summary>
    /// The resolution plan for a launch of <paramref name="game"/> with <paramref name="profile"/>, next to its HDR plan
    /// (<paramref name="hdr"/>; a gamescope plan carries the size) on <paramref name="host"/>.
    /// </summary>
    public static ResolutionPlan Plan(DetectedGame game, LinuxHdrGameProfile profile, LinuxHdrLaunchPlan hdr, LinuxHdrHostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(hdr);
        ArgumentNullException.ThrowIfNull(host);
        var target = GamescopeSize(profile);
        if (target is null) return ResolutionPlan.Off;
        if (hdr.Enabled && hdr.CanApply && hdr.CommandPrefix?.StartsWith("gamescope ", StringComparison.Ordinal) == true)
            return new(true, true, ResolutionPlanReason.GamescopeSize, target, DisplayTool.None);
        if (LaunchHandoffClassifier.IsHandoff(game))
            return new(true, false, ResolutionPlanReason.Handoff, target, DisplayTool.None);
        if (!host.Probed) return new(true, false, ResolutionPlanReason.HostNotProbed, target, DisplayTool.None);
        var tool = ToolFor(host);
        if (tool == DisplayTool.None) return new(true, false, ResolutionPlanReason.DesktopUnsupported, target, DisplayTool.None);
        var available = tool == DisplayTool.KScreenDoctor ? host.KScreenDoctorAvailable : host.WlrRandrAvailable;
        if (!available) return new(true, false, ResolutionPlanReason.ToolMissing, target, tool);
        return new(true, true, tool == DisplayTool.KScreenDoctor ? ResolutionPlanReason.KScreenDoctor : ResolutionPlanReason.WlrRandr, target, tool);
    }

    private static readonly string[] WlrootsDesktops = ["sway", "hyprland", "river", "wayfire", "labwc", "niri", "dwl", "qtile"];

    /// <summary>The tool for the desktop: KDE → kscreen-doctor; a wlroots compositor in a Wayland session → wlr-randr.</summary>
    public static DisplayTool ToolFor(LinuxHdrHostSnapshot host)
    {
        var desktop = host.Desktop ?? "";
        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) || desktop.Contains("plasma", StringComparison.OrdinalIgnoreCase))
            return DisplayTool.KScreenDoctor;
        if (host.WaylandSession && WlrootsDesktops.Any(name => desktop.Contains(name, StringComparison.OrdinalIgnoreCase)))
            return DisplayTool.WlrRandr;
        return DisplayTool.None;
    }

    // ---- switch and restore ----------------------------------------------------------------------------------------

    /// <summary>
    /// Switches the primary output to the plan's size: reads the current mode, records it, then switches. Never stacks a
    /// second switch on one not yet put back. A failure after the record was written removes the record again.
    /// </summary>
    public async Task<DisplaySwitchResult> SwitchAsync(ResolutionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SwitchesAroundLaunch) throw new InvalidOperationException("This plan does not switch the display.");
        if (_state.Load() is { } load && (load.IsUnreadable || load.Value is not null))
            return new(DisplaySwitchOutcome.EarlierSwitchPending, load.Value);
        var toolPath = FindTool(plan.Tool);
        if (toolPath is null) return new(DisplaySwitchOutcome.Failed, Detail: ToolMissingPrefix + ToolName(plan.Tool));

        Selection? selection;
        try { selection = await SelectAsync(plan.Tool, toolPath, plan.Target!, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            return new(DisplaySwitchOutcome.Failed, Detail: ToolFailedPrefix + ex.Message);
        }
        if (selection is null) return new(DisplaySwitchOutcome.Failed, Detail: NoModePrefix + plan.Target);
        if (selection.AlreadyAtTarget) return new(DisplaySwitchOutcome.AlreadyAtTarget);

        var state = new DisplayModeState
        {
            Tool = plan.Tool, Output = selection.Output, RestoreArguments = selection.RestoreArguments,
            PreviousMode = selection.PreviousMode, TargetMode = selection.TargetMode, SwitchedAtUtc = DateTime.UtcNow
        };
        try { _state.Save(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(DisplaySwitchOutcome.Failed, Detail: StateUnsavedPrefix + ex.Message);
        }

        var result = await RunSafeAsync(toolPath, selection.SwitchArguments, cancellationToken).ConfigureAwait(false);
        if (result is null || result.ExitCode != 0)
        {
            Forget();
            return new(DisplaySwitchOutcome.Failed, Detail: ToolFailedPrefix + LastLine(result));
        }
        AppLog.Current?.Write("display-mode", $"switched {selection.Output} from {selection.PreviousMode} to {selection.TargetMode}");
        return new(DisplaySwitchOutcome.Switched, state);
    }

    /// <summary>Records the started game's process with the switch (crash recovery waits for it to end).</summary>
    public void AttachGame(int pid)
    {
        lock (_gate)
        {
            if (_state.Load().Value is not { } state) return;
            try { _state.Save(state with { GamePid = pid, GameStartTicks = ProcessStartTicks(pid) }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without the pid, recovery restores at the next start even while the game runs; the switch itself stays recorded.
                AppLog.Warn("display-mode", "could not record the game process with the display switch", ex);
            }
        }
    }

    /// <summary>Puts the recorded mode back (the game ended, or its launch failed). Keeps the record when that fails.</summary>
    public async Task<DisplayRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        // The game's end and a failed launch can both ask at once: one restore, the other then finds nothing to do.
        await _restoring.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { return await RestoreOnceAsync(cancellationToken).ConfigureAwait(false); }
        finally { _restoring.Release(); }
    }

    private async Task<DisplayRestoreResult> RestoreOnceAsync(CancellationToken cancellationToken)
    {
        var state = _state.Load().Value;
        if (state is null) return new(DisplayRestoreOutcome.NothingToRestore);
        var toolPath = FindTool(state.Tool);
        if (toolPath is null) return new(DisplayRestoreOutcome.Failed, ToolMissingPrefix + ToolName(state.Tool));
        var result = await RunSafeAsync(toolPath, state.RestoreArguments, cancellationToken).ConfigureAwait(false);
        if (result is null || result.ExitCode != 0)
        {
            var detail = ToolFailedPrefix + LastLine(result);
            AppLog.Warn("display-mode", $"could not put {state.Output} back to {state.PreviousMode}: {LastLine(result)}");
            return new(DisplayRestoreOutcome.Failed, detail);
        }
        Forget();
        AppLog.Current?.Write("display-mode", $"put {state.Output} back to {state.PreviousMode}");
        return new(DisplayRestoreOutcome.Restored);
    }

    /// <summary>
    /// At start-up: a record left behind is put back now when its game is no longer running, else once it ends (polled
    /// every <paramref name="pollInterval"/>, 5 s by default). The returned task ends when nothing is left to do.
    /// </summary>
    public async Task<DisplayRestoreResult> RecoverAsync(TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var state = _state.Load().Value;
            if (state is null) return new(DisplayRestoreOutcome.NothingToRestore);
            if (state.GamePid is not int pid || !_isRunning(pid, state.GameStartTicks))
                return await RestoreAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Forget()
    {
        try { File.Delete(StatePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("display-mode", "could not remove the display-mode record", ex);
        }
    }

    // ---- tool answers ----------------------------------------------------------------------------------------------

    internal sealed record Selection(string Output, string PreviousMode, string TargetMode, IReadOnlyList<string> SwitchArguments,
        IReadOnlyList<string> RestoreArguments, bool AlreadyAtTarget);

    private async Task<Selection?> SelectAsync(DisplayTool tool, string toolPath, DisplayResolution target, CancellationToken cancellationToken)
    {
        var query = tool == DisplayTool.KScreenDoctor ? new[] { "-j" } : new[] { "--json" };
        var result = await _run(toolPath, query, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(LastLine(result));
        return tool == DisplayTool.KScreenDoctor ? SelectKScreen(result.StandardOutput, target) : SelectWlr(result.StandardOutput, target);
    }

    internal sealed record Mode(string Id, int Width, int Height, double Refresh);

    // kscreen-doctor -j: {"outputs":[{"name","enabled","connected","priority" (1 = primary; older: "primary": true),
    // "currentModeId","modes":[{"id","name","size":{"width","height"},"refreshRate"}]}]}; switch: output.<name>.mode.<id>.
    internal static Selection? SelectKScreen(string json, DisplayResolution target)
    {
        var outputs = (JsonNode.Parse(json)?["outputs"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => o["enabled"]?.GetValue<bool>() != false && o["connected"]?.GetValue<bool>() != false)
            .OrderBy(o => o["primary"]?.GetValue<bool>() == true ? 0 : 1)
            .ThenBy(o => o["priority"] is JsonValue p && p.TryGetValue<int>(out var priority) && priority > 0 ? priority : int.MaxValue)
            .ToArray();
        var output = outputs.FirstOrDefault();
        if (output is null) return null;
        var name = output["name"]?.GetValue<string>() ?? "";
        var modes = (output["modes"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new Mode(
            Text(m["id"]), m["size"]?["width"]?.GetValue<int>() ?? 0, m["size"]?["height"]?.GetValue<int>() ?? 0,
            m["refreshRate"]?.GetValue<double>() ?? 0)).ToArray();
        var currentId = Text(output["currentModeId"]);
        var current = modes.FirstOrDefault(m => m.Id == currentId);
        if (current is null || name.Length == 0) return null;
        if (current.Width == target.Width && current.Height == target.Height)
            return new Selection(name, Spec(current), Spec(current), [], [], AlreadyAtTarget: true);
        var pick = Pick(modes, target, current.Refresh);
        if (pick is null) return null;
        return new Selection(name, Spec(current), Spec(pick),
            [$"output.{name}.mode.{pick.Id}"], [$"output.{name}.mode.{current.Id}"], AlreadyAtTarget: false);
    }

    // wlr-randr --json: [{"name","enabled","modes":[{"width","height","refresh","current"}]}];
    // switch: --output <name> --mode <w>x<h>@<refresh>Hz.
    internal static Selection? SelectWlr(string json, DisplayResolution target)
    {
        var output = (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(o => o["enabled"]?.GetValue<bool>() != false);
        if (output is null) return null;
        var name = output["name"]?.GetValue<string>() ?? "";
        var modes = (output["modes"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (Mode: new Mode(
            "", m["width"]?.GetValue<int>() ?? 0, m["height"]?.GetValue<int>() ?? 0, m["refresh"]?.GetValue<double>() ?? 0),
            Current: m["current"]?.GetValue<bool>() == true)).ToArray();
        var current = modes.FirstOrDefault(m => m.Current).Mode;
        if (current is null || name.Length == 0) return null;
        if (current.Width == target.Width && current.Height == target.Height)
            return new Selection(name, Spec(current), Spec(current), [], [], AlreadyAtTarget: true);
        var pick = Pick(modes.Select(m => m.Mode).ToArray(), target, current.Refresh);
        if (pick is null) return null;
        return new Selection(name, Spec(current), Spec(pick),
            ["--output", name, "--mode", WlrMode(pick)], ["--output", name, "--mode", WlrMode(current)], AlreadyAtTarget: false);
    }

    /// <summary>The target size at the current refresh rate when offered, else at its highest refresh rate.</summary>
    private static Mode? Pick(IReadOnlyList<Mode> modes, DisplayResolution target, double currentRefresh)
    {
        var sized = modes.Where(m => m.Width == target.Width && m.Height == target.Height).ToArray();
        return sized.FirstOrDefault(m => Math.Abs(m.Refresh - currentRefresh) < 0.5) ?? sized.MaxBy(m => m.Refresh);
    }

    private static string Spec(Mode mode) => string.Create(CultureInfo.InvariantCulture, $"{mode.Width}x{mode.Height}@{mode.Refresh:0.###}");

    private static string WlrMode(Mode mode) => string.Create(CultureInfo.InvariantCulture, $"{mode.Width}x{mode.Height}@{mode.Refresh:0.000}Hz");

    private static string Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var s) ? s : value.ToJsonString()
        : "";

    // ---- processes -------------------------------------------------------------------------------------------------

    private string? FindTool(DisplayTool tool)
    {
        var name = ToolName(tool);
        if (name.Length == 0) return null;
        foreach (var dir in (_environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir, name); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ToolName(DisplayTool tool) => tool switch
    {
        DisplayTool.KScreenDoctor => KScreenDoctor,
        DisplayTool.WlrRandr => WlrRandr,
        _ => ""
    };

    private async Task<ExternalProcessResult?> RunSafeAsync(string toolPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try { return await _run(toolPath, arguments, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("display-mode", $"{Path.GetFileName(toolPath)} failed", ex);
            return new ExternalProcessResult(-1, "", ex.Message);
        }
    }

    private static Task<ExternalProcessResult> RunToolAsync(string toolPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(toolPath);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return ExternalProcess.RunAsync(psi, ToolTimeout, cancellationToken);
    }

    private static string LastLine(ExternalProcessResult? result)
    {
        var text = (result?.StandardError ?? "").Trim();
        if (text.Length == 0) text = (result?.StandardOutput ?? "").Trim();
        if (text.Length == 0) return result is null ? "?" : $"exit code {result.ExitCode}";
        var lines = text.Split('\n');
        return lines[^1].Trim();
    }

    /// <summary>Start ticks (<c>/proc/&lt;pid&gt;/stat</c> field 22), or null when the process is gone or unreadable.</summary>
    internal static long? ProcessStartTicks(int pid, string procRoot = "/proc")
    {
        try
        {
            var stat = File.ReadAllText(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), "stat"));
            var afterName = stat.LastIndexOf(')');
            if (afterName < 0) return null;
            var fields = stat[(afterName + 2)..].Split(' ');
            return fields.Length > 19 && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsProcessRunning(int pid, long? startTicks)
    {
        var ticks = ProcessStartTicks(pid);
        return ticks is not null && (startTicks is null || ticks == startTicks);
    }
}
