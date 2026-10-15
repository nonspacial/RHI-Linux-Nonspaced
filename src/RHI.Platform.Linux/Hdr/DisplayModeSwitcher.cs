using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// The tool arguments that put the previous mode back, by resolution and refresh rate (never a KScreen mode number,
    /// which can name another mode after a reboot or a monitor change). Checked against the only shapes RHI writes
    /// before they are ever run (<see cref="DisplayModeSwitcher.IsValidState"/>).
    /// </summary>
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
    /// <summary>
    /// The record of an earlier switch cannot be read and could not be set aside either (<see cref="DisplaySwitchResult.Detail"/>
    /// = the record's path to fix or delete): nothing was switched.
    /// </summary>
    RecordUnreadable,
    Failed
}

/// <param name="Detail">For <see cref="DisplaySwitchOutcome.Failed"/>: the tool's message or the reason (platform text).</param>
/// <param name="SetAsideCopy">
/// The record of an earlier switch could not be read: it was set aside here (the original bytes) and this switch went
/// ahead without it.
/// </param>
public sealed record DisplaySwitchResult(DisplaySwitchOutcome Outcome, DisplayModeState? State = null, string? Detail = null,
    string? SetAsideCopy = null);

public enum DisplayRestoreOutcome
{
    NothingToRestore,
    Restored,
    /// <summary>The recorded game is still running: restored when it ends.</summary>
    WaitingForGame,
    /// <summary>
    /// The record cannot be read (damaged, or not in a shape RHI writes): it was not run. <see cref="DisplayRestoreResult.SetAsideCopy"/>
    /// holds the copy it was set aside to; null when even that failed (<see cref="DisplayRestoreResult.Detail"/> = the record's path).
    /// </summary>
    RecordUnreadable,
    Failed
}

public sealed record DisplayRestoreResult(DisplayRestoreOutcome Outcome, string? Detail = null, string? SetAsideCopy = null);

/// <summary>
/// Resolution auto-toggle (Refs #191, upstream <c>ResolutionToggleService</c> / <c>ResToggle_Click</c>). A direct launch
/// switches the primary output to the game's resolution with <c>kscreen-doctor</c> (KDE) or <c>wlr-randr</c> (wlroots)
/// and switches it back when the game's command ends; a gamescope HDR launch carries the size as <c>-W/-H</c> instead; a
/// store hand-off is never switched around (RHI does not see the game end). The mode to go back to is recorded before the
/// switch; the launch shell itself runs the restore once the game's command ends (<see cref="RestoreCommand"/>), so the
/// display goes back even when RHI was closed meanwhile; a record left behind is restored at the next start once the
/// recorded game has ended. Tools run through <see cref="ExternalProcess"/> (time limit, tree kill), found on PATH; the
/// runner is a seam so tests use fake tools and never touch a real display. Output names and modes from the tool's answer
/// and every record read back are checked against the shapes RHI writes before anything runs.
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
    /// <summary>The tool's answer named an output or mode outside the shapes RHI accepts (platform message, exact).</summary>
    public const string UnsafeAnswer = "The display tool named an output or mode RHI does not accept, so the display was not changed.";

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
        // A record in a shape RHI never writes counts as unreadable: it is set aside, never run.
        _state = new JsonFileStore<DisplayModeState>(StatePath, Options,
            parse: text => JsonSerializer.Deserialize<DisplayModeState>(text, Options) is { } state && IsValidState(state) ? state : null);
        _environment = environment ?? LinuxPaths.Environment;
        _run = run ?? RunToolAsync;
        _isRunning = isRunning ?? ((pid, ticks) => IsProcessRunning(pid, ticks));
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

    /// <summary>
    /// False where no plan can ever apply on this card and desktop, so the toggle is hidden: a store hand-off without
    /// gamescope (whose launch options would carry the size), or a desktop without a usable display tool and without
    /// gamescope. True while the host is not probed yet (nothing is known).
    /// </summary>
    public static bool CanEverApply(DetectedGame game, LinuxHdrHostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Probed || host.GamescopeAvailable) return true;
        return !LaunchHandoffClassifier.IsHandoff(game) && AvailableTool(host) != DisplayTool.None;
    }

    /// <summary>The desktop's tool when it is also installed, else <see cref="DisplayTool.None"/>.</summary>
    public static DisplayTool AvailableTool(LinuxHdrHostSnapshot host)
        => ToolFor(host) switch
        {
            DisplayTool.KScreenDoctor when host.KScreenDoctorAvailable => DisplayTool.KScreenDoctor,
            DisplayTool.WlrRandr when host.WlrRandrAvailable => DisplayTool.WlrRandr,
            _ => DisplayTool.None
        };

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
    /// second switch on one not yet put back; a record that cannot be read is set aside (copy kept) rather than blocking
    /// every later switch. A switch that fails or times out after the record was written tries to put the previous mode
    /// back (harmless when nothing changed) and removes the record only once that worked; otherwise the record stays for
    /// the next start's recovery.
    /// </summary>
    public async Task<DisplaySwitchResult> SwitchAsync(ResolutionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SwitchesAroundLaunch) throw new InvalidOperationException("This plan does not switch the display.");
        string? setAside = null;
        var load = _state.Load();
        if (load.IsUnreadable)
        {
            if (!TrySetAside(load, out setAside)) return new(DisplaySwitchOutcome.RecordUnreadable, Detail: Path.GetFullPath(StatePath));
        }
        else if (load.Value is not null)
            return new(DisplaySwitchOutcome.EarlierSwitchPending, load.Value);
        var result = await SwitchCoreAsync(plan, cancellationToken).ConfigureAwait(false);
        return setAside is null ? result : result with { SetAsideCopy = setAside };
    }

    private async Task<DisplaySwitchResult> SwitchCoreAsync(ResolutionPlan plan, CancellationToken cancellationToken)
    {
        var toolPath = FindTool(plan.Tool);
        if (toolPath is null) return new(DisplaySwitchOutcome.Failed, Detail: ToolMissingPrefix + ToolName(plan.Tool));

        Selection? selection;
        try { selection = await SelectAsync(plan.Tool, toolPath, plan.Target!, cancellationToken).ConfigureAwait(false); }
        catch (UnsafeAnswerException)
        {
            return new(DisplaySwitchOutcome.Failed, Detail: UnsafeAnswer);
        }
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
            // The tool may have applied the mode before it hung or reported an error: go back first, and keep the record
            // (next start's recovery) unless that worked. Never cancelled: this is the rollback.
            var back = await RunSafeAsync(toolPath, selection.RestoreArguments, CancellationToken.None).ConfigureAwait(false);
            if (back is { ExitCode: 0 }) Forget();
            else AppLog.Warn("display-mode", $"switch of {selection.Output} failed and going back to {selection.PreviousMode} failed too: {LastLine(back)}; kept for the next start");
            return new(DisplaySwitchOutcome.Failed, back is { ExitCode: 0 } ? null : state, ToolFailedPrefix + LastLine(result));
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
        var load = _state.Load();
        if (load.IsUnreadable)
        {
            // Damaged, or not a shape RHI writes: never run; set aside so later switches work again.
            AppLog.Warn("display-mode", $"{StatePath} cannot be read; not used");
            return TrySetAside(load, out var copy)
                ? new(DisplayRestoreOutcome.RecordUnreadable, SetAsideCopy: copy)
                : new(DisplayRestoreOutcome.RecordUnreadable, Path.GetFullPath(StatePath));
        }
        var state = load.Value;
        if (state is null) return new(DisplayRestoreOutcome.NothingToRestore);
        var toolPath = FindTool(state.Tool);
        if (toolPath is null) return new(DisplayRestoreOutcome.Failed, ToolMissingPrefix + ToolName(state.Tool));
        // The launch shell runs the same restore when the game's command ends: when the display is back already, only the
        // record goes (no second mode change).
        if (await CurrentModeAsync(state.Tool, toolPath, state.Output, cancellationToken).ConfigureAwait(false) == state.PreviousMode)
        {
            Forget();
            AppLog.Current?.Write("display-mode", $"{state.Output} is back at {state.PreviousMode}");
            return new(DisplayRestoreOutcome.Restored);
        }
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
            var load = _state.Load();
            if (load.IsUnreadable) return await RestoreAsync(cancellationToken).ConfigureAwait(false);
            var state = load.Value;
            if (state is null) return new(DisplayRestoreOutcome.NothingToRestore);
            if (state.GamePid is not int pid || !_isRunning(pid, state.GameStartTicks))
                return await RestoreAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The argument vector that puts <paramref name="state"/>'s previous mode back (the tool's absolute path, then the
    /// recorded arguments), for the launch shell to run when the game's command ends; null when the record is not in a
    /// shape RHI writes or the tool is not on PATH.
    /// </summary>
    public IReadOnlyList<string>? RestoreCommand(DisplayModeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidState(state) || FindTool(state.Tool) is not { } toolPath) return null;
        return [toolPath, .. state.RestoreArguments];
    }

    /// <summary>
    /// The display's own sizes (the primary output's modes, largest first, each once) where the desktop's tool lists
    /// them; null when it cannot (no tool, an error, an answer RHI does not accept).
    /// </summary>
    public async Task<IReadOnlyList<DisplayResolution>?> ListResolutionsAsync(DisplayTool tool, CancellationToken cancellationToken = default)
    {
        if (FindTool(tool) is not { } toolPath) return null;
        try
        {
            var answer = await QueryAsync(tool, toolPath, cancellationToken).ConfigureAwait(false);
            var output = tool == DisplayTool.KScreenDoctor ? KScreenPrimary(answer) : WlrPrimary(answer);
            if (output is null) return null;
            var sizes = output.Modes
                .Select(mode => DisplayResolution.Parse($"{mode.Width}x{mode.Height}"))
                .OfType<DisplayResolution>()
                .Distinct()
                .OrderByDescending(size => (long)size.Width * size.Height).ThenByDescending(size => size.Width)
                .ToArray();
            return sizes.Length == 0 ? null : sizes;
        }
        catch (Exception ex) when (ex is UnsafeAnswerException or IOException or InvalidOperationException or TimeoutException or JsonException
                                       or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("display-mode", $"could not list the display's modes with {ToolName(tool)}", ex);
            return null;
        }
    }

    /// <summary>The recorded output's current mode (<see cref="Spec"/>), or null when it cannot be told.</summary>
    private async Task<string?> CurrentModeAsync(DisplayTool tool, string toolPath, string outputName, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await QueryAsync(tool, toolPath, cancellationToken).ConfigureAwait(false);
            var outputs = tool == DisplayTool.KScreenDoctor ? KScreenOutputs(answer) : WlrOutputs(answer);
            return outputs.FirstOrDefault(o => o.Name == outputName) is { Name.Length: > 0, Current: { } current } ? Spec(current) : null;
        }
        catch (Exception ex) when (ex is UnsafeAnswerException or IOException or InvalidOperationException or TimeoutException or JsonException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Sets an unreadable record aside (its bytes kept) and removes it, so the feature keeps working.</summary>
    private bool TrySetAside(JsonStoreLoad<DisplayModeState> load, out string? copy)
    {
        copy = load.QuarantinePath;
        try
        {
            if (copy is null) copy = JsonStoreQuarantine.MoveAside(StatePath);
            else
            {
                File.Delete(StatePath);
                JsonStoreQuarantine.Forget(StatePath);
            }
            AppLog.Warn("display-mode", $"unreadable display-mode record set aside to {copy}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("display-mode", $"unreadable display-mode record {StatePath} could not be set aside", ex);
            copy = null;
            return false;
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

    /// <summary>The tool's answer named an output or mode outside the shapes RHI accepts.</summary>
    private sealed class UnsafeAnswerException() : Exception(UnsafeAnswer);

    private async Task<string> QueryAsync(DisplayTool tool, string toolPath, CancellationToken cancellationToken)
    {
        var query = tool == DisplayTool.KScreenDoctor ? new[] { "-j" } : new[] { "--json" };
        var result = await _run(toolPath, query, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(LastLine(result));
        return result.StandardOutput;
    }

    private async Task<Selection?> SelectAsync(DisplayTool tool, string toolPath, DisplayResolution target, CancellationToken cancellationToken)
    {
        var answer = await QueryAsync(tool, toolPath, cancellationToken).ConfigureAwait(false);
        return tool == DisplayTool.KScreenDoctor ? SelectKScreen(answer, target) : SelectWlr(answer, target);
    }

    internal sealed record Mode(string Id, int Width, int Height, double Refresh);

    internal sealed record OutputModes(string Name, IReadOnlyList<Mode> Modes, Mode? Current);

    // Shapes RHI accepts from a tool's answer and in a record read back (argv entries, never parsed by a shell, but a
    // damaged or hand-edited record must not run e.g. output.DP-1.disable). kscreen-doctor splits its argument on '.',
    // so a KScreen output name never holds one.
    private static readonly Regex KScreenOutputName = new(@"^[A-Za-z0-9_:-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex WlrOutputName = new(@"^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex KScreenModeId = new(@"^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex KScreenRestore = new(@"^output\.(?<output>[A-Za-z0-9_:-]{1,64})\.mode\.\d{3,5}x\d{3,5}@\d{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex WlrModeSpec = new(@"^\d{3,5}x\d{3,5}@\d{1,4}\.\d{3}Hz$", RegexOptions.CultureInvariant);

    /// <summary>True for a record in a shape RHI writes: a known tool, a plain output name and a by-resolution mode.</summary>
    public static bool IsValidState(DisplayModeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var args = state.RestoreArguments ?? [];
        return state.Tool switch
        {
            DisplayTool.KScreenDoctor => KScreenOutputName.IsMatch(state.Output) && args.Count == 1
                && KScreenRestore.Match(args[0]) is { Success: true } match && match.Groups["output"].Value == state.Output,
            DisplayTool.WlrRandr => WlrOutputName.IsMatch(state.Output) && args.Count == 4 && args[0] == "--output"
                && args[1] == state.Output && args[2] == "--mode" && WlrModeSpec.IsMatch(args[3]),
            _ => false
        };
    }

    // kscreen-doctor -j: {"outputs":[{"name","enabled","connected","priority" (1 = primary; older: "primary": true),
    // "currentModeId","modes":[{"id","name","size":{"width","height"},"refreshRate"}]}]}. Switch: output.<name>.mode.<id>
    // (the id just read); restore: output.<name>.mode.<W>x<H>@<refresh rounded>, kscreen-doctor's own mode name, which
    // still names the same mode after a reboot or a monitor change.
    internal static IReadOnlyList<OutputModes> KScreenOutputs(string json)
        => (JsonNode.Parse(json)?["outputs"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => o["enabled"]?.GetValue<bool>() != false && o["connected"]?.GetValue<bool>() != false)
            .OrderBy(o => o["primary"]?.GetValue<bool>() == true ? 0 : 1)
            .ThenBy(o => o["priority"] is JsonValue p && p.TryGetValue<int>(out var priority) && priority > 0 ? priority : int.MaxValue)
            .Select(o =>
            {
                var name = o["name"]?.GetValue<string>() ?? "";
                if (!KScreenOutputName.IsMatch(name)) throw new UnsafeAnswerException();
                var modes = (o["modes"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new Mode(
                    Text(m["id"]), m["size"]?["width"]?.GetValue<int>() ?? 0, m["size"]?["height"]?.GetValue<int>() ?? 0,
                    m["refreshRate"]?.GetValue<double>() ?? 0)).ToArray();
                var currentId = Text(o["currentModeId"]);
                return new OutputModes(name, modes, modes.FirstOrDefault(m => m.Id == currentId));
            })
            .ToArray();

    private static OutputModes? KScreenPrimary(string json) => KScreenOutputs(json).FirstOrDefault();

    internal static Selection? SelectKScreen(string json, DisplayResolution target)
    {
        if (KScreenPrimary(json) is not { Current: { } current } output) return null;
        var name = output.Name;
        if (current.Width == target.Width && current.Height == target.Height)
            return new Selection(name, Spec(current), Spec(current), [], [], AlreadyAtTarget: true);
        var pick = Pick(output.Modes, target, current.Refresh);
        if (pick is null) return null;
        if (!KScreenModeId.IsMatch(pick.Id)) throw new UnsafeAnswerException();
        return new Selection(name, Spec(current), Spec(pick),
            [$"output.{name}.mode.{pick.Id}"], [$"output.{name}.mode.{KScreenModeName(current)}"], AlreadyAtTarget: false);
    }

    // wlr-randr --json: [{"name","enabled","modes":[{"width","height","refresh","current"}]}];
    // switch: --output <name> --mode <w>x<h>@<refresh>Hz.
    internal static IReadOnlyList<OutputModes> WlrOutputs(string json)
        => (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => o["enabled"]?.GetValue<bool>() != false)
            .Select(o =>
            {
                var name = o["name"]?.GetValue<string>() ?? "";
                if (!WlrOutputName.IsMatch(name)) throw new UnsafeAnswerException();
                var modes = (o["modes"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (Mode: new Mode(
                    "", m["width"]?.GetValue<int>() ?? 0, m["height"]?.GetValue<int>() ?? 0, m["refresh"]?.GetValue<double>() ?? 0),
                    Current: m["current"]?.GetValue<bool>() == true)).ToArray();
                return new OutputModes(name, modes.Select(m => m.Mode).ToArray(), modes.FirstOrDefault(m => m.Current).Mode);
            })
            .ToArray();

    private static OutputModes? WlrPrimary(string json) => WlrOutputs(json).FirstOrDefault();

    internal static Selection? SelectWlr(string json, DisplayResolution target)
    {
        if (WlrPrimary(json) is not { Current: { } current } output) return null;
        var name = output.Name;
        if (current.Width == target.Width && current.Height == target.Height)
            return new Selection(name, Spec(current), Spec(current), [], [], AlreadyAtTarget: true);
        var pick = Pick(output.Modes, target, current.Refresh);
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

    /// <summary>kscreen-doctor's name for a mode: <c>W</c>x<c>H</c>@<c>refresh rounded</c> (its own qRound).</summary>
    private static string KScreenModeName(Mode mode)
        => string.Create(CultureInfo.InvariantCulture, $"{mode.Width}x{mode.Height}@{(int)Math.Round(mode.Refresh, MidpointRounding.AwayFromZero)}");

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

    /// <summary>
    /// Whether the recorded game still runs: a process at <paramref name="pid"/> whose start time is the recorded one. A
    /// record without a start time (it could not be read when the game started) never matches, so a reused pid is never
    /// waited on.
    /// </summary>
    internal static bool IsProcessRunning(int pid, long? startTicks, string procRoot = "/proc")
        => startTicks is long recorded && ProcessStartTicks(pid, procRoot) == recorded;
}
