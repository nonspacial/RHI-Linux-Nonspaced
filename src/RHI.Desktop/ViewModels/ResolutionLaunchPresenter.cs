using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #191: the resolution auto-toggle around Play. Before a direct launch whose plan switches the display, the switch runs
/// (the previous mode recorded first) and the launch gets listeners: the game's process id is recorded with the switch,
/// and when the launched command ends the display goes back. A launch that fails puts it back at once. At start-up a
/// switch left behind (RHI closed or crashed while a game ran) is put back once that game has ended. Without a switcher
/// (tests, the harness) nothing is ever switched.
/// </summary>
public sealed class ResolutionLaunchPresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, ResolutionLaunchPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;
    private DisplayModeSwitcher? _switcher;

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
        }
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
            Exited = () => UiExceptionGuard.Forget(RestoreAsync(), "display-mode-restore")
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
