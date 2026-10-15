using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #191: the resolution auto-toggle around Play. Before a direct launch whose plan switches the display, the switch runs
/// (the previous mode recorded first) and the launch carries the restore command, which the launch shell runs itself
/// when the game's command ends (so the display goes back even when RHI was closed meanwhile), plus listeners: the
/// game's process id is recorded with the switch, and when the launched command ends RHI checks the display is back and
/// forgets the record. A launch that fails puts it back at once. At start-up a switch left behind (RHI crashed, or the
/// shell's restore failed) is put back once that game has ended. Once the host is probed the resolution list becomes the
/// display's own sizes where its tool lists them. Without a switcher (tests, the harness) nothing is ever switched or
/// queried.
/// </summary>
public sealed class ResolutionLaunchPresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, ResolutionLaunchPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private DisplayModeSwitcher? _switcher;
    private LinuxHdrHostSnapshot? _host;
    private Task? _listing;

    public static ResolutionLaunchPresenter For(MainWindowViewModel shell) => Presenters.GetValue(shell, s => new ResolutionLaunchPresenter(s));

    private ResolutionLaunchPresenter(MainWindowViewModel shell) => _shell = shell;

    /// <summary>Runs status updates on the UI thread (the game ends on a pool thread); direct when there is no app (tests).</summary>
    internal Action<Action> Post { get; set; } = action =>
    {
        if (Application.Current is null) action();
        else Dispatcher.UIThread.Post(action);
    };

    /// <summary>The display switcher (set by the app's composition). Setting it puts back a switch left behind.</summary>
    public DisplayModeSwitcher? Switcher
    {
        get => _switcher;
        set
        {
            _switcher = value;
            if (value is not null) Recovery = RecoverAsync(value);
            StartListing();
        }
    }

    /// <summary>The host probe landed (start-up): the display's own sizes are listed once its tool is known.</summary>
    internal void HostProbed(LinuxHdrHostSnapshot host)
    {
        _host = host;
        StartListing();
    }

    /// <summary>The listing of the display's sizes (tests await it); completed when none was started.</summary>
    internal Task DisplaySizesListed => _listing ?? Task.CompletedTask;

    private void StartListing()
    {
        if (_listing is not null || _switcher is not { } switcher || _host is not { Probed: true } host) return;
        var tool = DisplayModeSwitcher.AvailableTool(host);
        if (tool == DisplayTool.None) return;
        _listing = ListAsync(switcher, tool);
    }

    private async Task ListAsync(DisplayModeSwitcher switcher, DisplayTool tool)
    {
        var sizes = await switcher.ListResolutionsAsync(tool, CancellationToken.None).ConfigureAwait(false);
        if (sizes is { Count: > 0 }) Post(() => _shell.Runtime.UseDisplayResolutions(sizes));
    }

    /// <summary>The start-up recovery (tests await it).</summary>
    internal Task Recovery { get; private set; } = Task.CompletedTask;

    private async Task RecoverAsync(DisplayModeSwitcher switcher)
    {
        try
        {
            var result = await switcher.RecoverAsync().ConfigureAwait(false);
            if (ResolutionText.Restore(result) is { } line) Post(() => _shell.StatusText = line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("display-mode", "could not put a display switch left behind back", ex);
        }
    }

    /// <summary>
    /// Switches the display for <paramref name="plan"/> when it asks for it, and returns the launch options with the
    /// listeners that record the game and put the display back, plus the status addition (null when none).
    /// </summary>
    public async Task<(GameLaunchOptions Options, string? Notice)> PrepareAsync(ResolutionPlan plan, GameLaunchOptions options)
    {
        if (!plan.SwitchesAroundLaunch || _switcher is not { } switcher) return (options, null);
        var result = await switcher.SwitchAsync(plan, CancellationToken.None);
        var notice = ResolutionText.Switch(result, plan);
        if (result.Outcome != DisplaySwitchOutcome.Switched) return (options, notice);
        return (options with
        {
            Started = switcher.AttachGame,
            Exited = () => UiExceptionGuard.Forget(RestoreAsync(), "display-mode-restore"),
            // The launch shell puts the mode back itself when the game's command ends, RHI running or not.
            AfterExit = switcher.RestoreCommand(result.State!)
        }, notice);
    }

    /// <summary>Puts the display back (the game ended, or its launch failed) and says so.</summary>
    public async Task RestoreAsync()
    {
        if (_switcher is not { } switcher) return;
        var result = await switcher.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        if (ResolutionText.Restore(result) is { } line) Post(() => _shell.StatusText = line);
    }
}
