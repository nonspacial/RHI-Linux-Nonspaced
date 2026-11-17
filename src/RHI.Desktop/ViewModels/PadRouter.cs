using RHI.Platform.Linux.Controller;

namespace RHI.Desktop.ViewModels;

/// <summary>Which RHI window has the controller.</summary>
public enum PadWindowKind
{
    /// <summary>The main window (Spotlight).</summary>
    Main,
    /// <summary>The Settings window (upstream: the Settings page inside the main window).</summary>
    Settings,
    /// <summary>Any other RHI window: a ⚙ dialog, a confirmation, … (upstream: a popup).</summary>
    Dialog,
}

/// <summary>What the right stick click does on the main window (upstream 2.9.4 <c>PadAction.Home</c>).</summary>
public enum PadHomeRoute
{
    None,
    /// <summary>The next collection; after the last one, All games.</summary>
    NextCollection,
    /// <summary>Back to the selected game's cover: the side panel closes, the covers get the focus.</summary>
    BackToCover,
}

/// <summary>What a controller action does in the Settings window or another RHI dialog window.</summary>
public enum PadWindowCommand
{
    None,
    /// <summary>To the nearest control that way, row by row.</summary>
    Move,
    /// <summary>Presses the focused control; a drop-down opens on its current choice.</summary>
    Select,
    /// <summary>Closes the window (a dialog: as Cancel). From Settings, back to the main window's selected cover.</summary>
    Close,
    PrevSection,
    NextSection,
    /// <summary>An open drop-down gets the key (arrows move, Enter picks, Esc closes it unchanged).</summary>
    DropDownKey,
}

/// <summary>
/// w14 controller fixes: the controller's routing that does not need a window, so the rules are unit tested — which RHI window
/// gets the controller, upstream 2.9.4's right and left stick clicks, upstream's Settings navigation (here a window of its
/// own: Menu and B close it, LB / RB jump between sections, a drop-down gets the arrows, A and B), RHI's dialogs (upstream's
/// popups: B closes) and the right stick's two axes.
/// </summary>
public static class PadRouter
{
    /// <summary>
    /// The window that gets the controller: the active one among RHI's windows (the last when several claim it), or -1 while
    /// none is (another app has the focus: nothing reaches RHI).
    /// </summary>
    public static int Target(IReadOnlyList<bool> active)
    {
        for (var i = active.Count - 1; i >= 0; i--)
            if (active[i]) return i;
        return -1;
    }

    /// <summary>
    /// Upstream 2.9.4 <c>PadAction.Home</c> on the main window: nothing in a menu; on the covers with collections and no panel
    /// open the next collection; otherwise back to the selected game's cover (the panel closes).
    /// </summary>
    public static PadHomeRoute Home(bool panelOpen, bool menuOpen, int collections)
        => menuOpen ? PadHomeRoute.None
            : !panelOpen && collections > 0 ? PadHomeRoute.NextCollection
            : PadHomeRoute.BackToCover;

    /// <summary>
    /// Upstream 2.9.4 <c>CycleMainFilter</c> (left stick click): All → Installed → Favourites → All, as indexes into
    /// <see cref="MainWindowViewModel.LibraryMainFilters"/>. Hidden and the engine filters are not in the cycle (from them:
    /// All); from a collection the first press goes to All.
    /// </summary>
    public static int NextMainFilter(int currentIndex, bool inCollection)
    {
        const int cycle = 3; // All Games, Installed, Favourites
        var current = inCollection || currentIndex < 0 || currentIndex >= cycle ? -1 : currentIndex;
        return (current + 1) % cycle;
    }

    /// <summary>A controller action in the Settings window or another RHI dialog window (upstream's Settings page and popups).</summary>
    public static PadWindowCommand InWindow(PadWindowKind kind, PadAction action, bool dropDownOpen)
    {
        if (dropDownOpen)
            return action is PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right or PadAction.Select or PadAction.Back
                ? PadWindowCommand.DropDownKey
                : PadWindowCommand.None;
        var settings = kind == PadWindowKind.Settings;
        return action switch
        {
            PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right => PadWindowCommand.Move,
            PadAction.Select => PadWindowCommand.Select,
            PadAction.Back => PadWindowCommand.Close,
            PadAction.Settings when settings => PadWindowCommand.Close, // Menu opens and closes Settings
            PadAction.Home when settings => PadWindowCommand.Close,     // upstream: from anywhere back to the cover (leaves Settings)
            PadAction.PrevGame when settings => PadWindowCommand.PrevSection,
            PadAction.NextGame when settings => PadWindowCommand.NextSection,
            _ => PadWindowCommand.None,
        };
    }

    /// <summary>Upstream <c>PadJumpSection</c>: the section <paramref name="step"/> from the highlighted one, stopping at the ends.</summary>
    public static string? StepSection(IReadOnlyList<string> keys, string? current, int step)
    {
        if (keys.Count == 0) return null;
        var index = -1;
        for (var i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], current, StringComparison.Ordinal)) { index = i; break; }
        return keys[Math.Clamp(index < 0 ? 0 : index + step, 0, keys.Count - 1)];
    }

    /// <summary>
    /// The right stick's scroll: <paramref name="y"/> scrolls the page (or panel, Settings, dialog) as upstream; on the covers
    /// <paramref name="x"/> also scrolls the shelf sideways (a Linux addition). Elsewhere X does nothing.
    /// </summary>
    public static (double ShelfX, double PageY) Scroll(bool onShelf, double x, double y) => (onShelf ? x : 0, y);
}

/// <summary>
/// w14 controller fixes (upstream 2.9.4 MainWindow.Controller.Pointer): who has the window, the controller or the mouse.
/// Using the mouse hides the controller's hints; the next controller input takes control back straight away, focusing the
/// control the controller last focused when it is still usable (in the window, shown, and where the controller now works:
/// the side panel when it is open), else the window's home (the selected cover, the panel's first control, Settings'
/// section list). Held weakly: a removed control is not kept alive.
/// </summary>
public sealed class PadFocusMemory<T> where T : class
{
    /// <summary>Pointer moves smaller than this (px) are jitter, or the hover shield appearing under a resting pointer.</summary>
    public const double PointerSlack = 3;

    private WeakReference<T>? _last;

    /// <summary>Whether the mouse has been used since the controller last had control.</summary>
    public bool PointerInUse { get; private set; }

    /// <summary>The controller focused <paramref name="target"/>.</summary>
    public void Remember(T target) => _last = new WeakReference<T>(target);

    /// <summary>The mouse was used; true the first time (the hints go).</summary>
    public bool PointerUsed()
    {
        var first = !PointerInUse;
        PointerInUse = true;
        return first;
    }

    /// <summary>
    /// A controller input: when the mouse had the window, where the focus goes back to (the last controller focus when
    /// <paramref name="usable"/>, else <paramref name="home"/>); null while the controller already has control.
    /// </summary>
    public T? TakeBack(Func<T, bool> usable, T? home)
    {
        if (!PointerInUse) return null;
        PointerInUse = false;
        return _last is not null && _last.TryGetTarget(out var last) && usable(last) ? last : home;
    }

    /// <summary>Whether the pointer moved past <see cref="PointerSlack"/> from where it rested.</summary>
    public static bool Moved(double fromX, double fromY, double x, double y)
        => Math.Abs(x - fromX) > PointerSlack || Math.Abs(y - fromY) > PointerSlack;
}
