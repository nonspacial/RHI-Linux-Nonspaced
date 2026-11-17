using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views.Dialogs;
using RHI.Platform.Linux.Controller;

namespace RHI.Desktop.Views;

/// <summary>
/// w14 controller fixes: the controller in RHI's other windows, and handing the window back and forth with the mouse.
/// <list type="bullet">
/// <item>Settings (upstream 2.9.4: a page in the main window, here a window of its own): Menu and B close it, back to the
/// main window's selected cover; LB / RB jump between its sections; opened with the controller it starts on the section
/// list; the D-pad moves row by row; A on a drop-down opens it on the current choice, then A picks and B closes it
/// unchanged; the right stick scrolls. The right stick click closes it too (upstream: from anywhere back to the cover).</item>
/// <item>The ⚙ dialogs, confirmations and RHI's other dialog windows (upstream's popups): the D-pad moves between their
/// controls, A presses, B closes (as Cancel), the right stick scrolls their largest scrolling area.</item>
/// <item>The mouse (upstream MainWindow.Controller.Pointer): moving or clicking it hides the controller's hints and gives
/// it the window; the next controller input takes control back straight away — a see-through layer stops a resting pointer
/// hovering, and the focus goes back to what the controller last focused when it is still there, else the window's home
/// (the selected cover, the side panel's first control, Settings' section list, a dialog's first control).</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private readonly ConditionalWeakTable<Window, PadFocusMemory<Control>> _padMemories = [];
    private readonly ConditionalWeakTable<Window, PadPointerAnchor> _padPointerHooks = [];

    /// <summary>Where the pointer rested when the controller took the window (null: not seen yet).</summary>
    private sealed class PadPointerAnchor
    {
        public Point? Last;
        public Point? Anchor;
    }

    private PadFocusMemory<Control> PadMemory(Window window) => _padMemories.GetValue(window, _ => new PadFocusMemory<Control>());

    private static PadWindowKind PadKind(Window window, out SettingsDialogView? settings)
    {
        settings = window.GetVisualDescendants().OfType<SettingsDialogView>().FirstOrDefault();
        return settings is not null ? PadWindowKind.Settings : PadWindowKind.Dialog;
    }

    /// <summary>Watches the window's mouse and the controller's focus there (once per window).</summary>
    private void HookPadPointer(Window window)
    {
        if (_padPointerHooks.TryGetValue(window, out _)) return;
        var anchor = new PadPointerAnchor();
        _padPointerHooks.AddOrUpdate(window, anchor);
        window.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var at = e.GetPosition(window);
            anchor.Last = at;
            if (PadMemory(window).PointerInUse || !_padActive) return;
            anchor.Anchor ??= at;
            var from = anchor.Anchor.Value;
            if (PadFocusMemory<Control>.Moved(from.X, from.Y, at.X, at.Y)) PointerTookOver(window);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(PointerPressedEvent, (_, _) => PointerTookOver(window), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(PointerWheelChangedEvent, (_, _) => PointerTookOver(window), RoutingStrategies.Tunnel, handledEventsToo: true);
        // What the controller focuses is where it comes back to (a mouse click's focus comes after PointerPressed: not kept).
        window.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (_padActive && !PadMemory(window).PointerInUse && e.Source is Control focused) PadMemory(window).Remember(focused);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The mouse was used: the hints go and the shield lifts; the controller's next input takes the window back.</summary>
    private void PointerTookOver(Window window)
    {
        PadPointerShield.Hide(window);
        if (_padPointerHooks.TryGetValue(window, out var anchor)) anchor.Anchor = null;
        if (PadMemory(window).PointerUsed() && ReferenceEquals(window, this)) ShowPadHints(false);
    }

    /// <summary>The first controller input after the mouse: the shield goes up and the focus back to a valid target.</summary>
    private void TakeBackFromPointer(Window window, PadFocusMemory<Control> memory)
    {
        var kind = ReferenceEquals(window, this) ? PadWindowKind.Main : PadKind(window, out _);
        Visual? scope = kind == PadWindowKind.Main ? PadScope() : null;
        if (scope is not null && ReferenceEquals(scope, this)) scope = null;
        var target = memory.TakeBack(c => PadControls.Usable(c, window, scope), PadHomeIn(window, kind));
        if (_padPointerHooks.TryGetValue(window, out var anchor)) anchor.Anchor = anchor.Last;
        PadPointerShield.Show(window);
        if (ReferenceEquals(window, this)) ShowPadHints(true);
        if (target is not null) target.Focus(NavigationMethod.Directional);
        else if (kind == PadWindowKind.Main) FocusSelectedCover();
    }

    /// <summary>Where the controller starts in a window: the selected cover (the panel's first control while it is open), Settings' section list, a dialog's first control.</summary>
    private Control? PadHomeIn(Window window, PadWindowKind kind)
    {
        switch (kind)
        {
            case PadWindowKind.Main:
                if (_panelShown is not null && this.FindControl<Border>("SpotlightPanelSheet") is { } sheet)
                    return PadControls.First(sheet, (Visual?)_uiScaleHost?.Child ?? this);
                return SelectedCoverItem();
            case PadWindowKind.Settings:
                return PadKind(window, out var settings) == PadWindowKind.Settings ? settings!.PadStart() : null;
            default:
                return PadControls.First(window, window);
        }
    }

    /// <summary>The controller arrived in another window (it opened, or RHI came back to it): focus its start, unless the focus is already on a control there.</summary>
    private void EnterPadWindow(Window window)
    {
        var kind = PadKind(window, out _);
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        // Settings opened with the controller starts on the section list (upstream PadFocusSettingsStart); a dialog keeps a
        // control it focused itself, except a text box (typing needs a keyboard).
        if (kind == PadWindowKind.Dialog && focused is not null and not TextBox && PadControls.Usable(focused, window)) return;
        Dispatcher.UIThread.Post(() => PadHomeIn(window, kind)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    /// <summary>A controller action in the Settings window or another RHI dialog window.</summary>
    private void DoPadInWindow(Window window, PadAction action)
    {
        var kind = PadKind(window, out var settings);
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        var menu = PadControls.OpenMenuTarget(window, focused);
        switch (PadRouter.InWindow(kind, action, menu is not null))
        {
            case PadWindowCommand.DropDownKey:
                PadControls.MenuKey(menu!, action);
                break;
            case PadWindowCommand.Move:
                if (settings is not null) MoveInSettings(window, settings, focused, action);
                else PadControls.Move(window, window, focused, action);
                break;
            case PadWindowCommand.Select:
                if (focused is null || !PadControls.Usable(focused, window)) PadHomeIn(window, kind)?.Focus(NavigationMethod.Directional);
                else PadControls.Press(focused);
                break;
            case PadWindowCommand.PrevSection or PadWindowCommand.NextSection:
                if (settings?.PadJumpSection(action == PadAction.NextGame ? 1 : -1) is { } section)
                    Dispatcher.UIThread.Post(() => PadControls.First(section, window)?.Focus(NavigationMethod.Directional), DispatcherPriority.Background);
                break;
            case PadWindowCommand.Close:
                window.Close();
                if (kind == PadWindowKind.Settings && _spotVm is { } vm)
                {
                    // Back to the main window and the selected cover (the right stick click also closes the side panel).
                    var home = action == PadAction.Home;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (home) BackToCover(vm);
                        else FocusSelectedCover();
                    }, DispatcherPriority.Loaded);
                }
                break;
        }
    }

    /// <summary>
    /// Settings has two parts, as upstream's page: the section list (up / down through it, right to the shown section's first
    /// setting) and the settings (row by row; left at the start of a row back to the section list).
    /// </summary>
    private static void MoveInSettings(Window window, SettingsDialogView settings, Control? focused, PadAction action)
    {
        var nav = settings.PadNav;
        if (focused is not null && nav is not null && nav.IsVisualAncestorOf(focused))
        {
            if (action is PadAction.Up or PadAction.Down) PadControls.Move(nav, window, focused, action);
            else if (action == PadAction.Right) settings.PadSectionFirst()?.Focus(NavigationMethod.Directional);
            return;
        }
        bool InContent(Control c) => nav is null || !nav.IsVisualAncestorOf(c);
        if (focused is null || !PadControls.Usable(focused, window) || !InContent(focused))
        {
            settings.PadStart()?.Focus(NavigationMethod.Directional);
            return;
        }
        if (PadControls.Move(settings, window, focused, action, InContent) is null && action == PadAction.Left)
            settings.PadStart()?.Focus(NavigationMethod.Directional);
    }

    /// <summary>The right stick in Settings (its settings) or a dialog (its largest scrolling area, else the one around the focus).</summary>
    private static void ScrollInWindow(Window window, double y)
    {
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        var scroller = PadKind(window, out var settings) == PadWindowKind.Settings
            ? PadControls.VerticalAround(focused) ?? settings!.PadScroller
            : PadControls.LargestVertical(window) ?? PadControls.VerticalAround(focused);
        if (scroller is not null) PadControls.ScrollBy(scroller, 0, y);
    }

    /// <summary>For screenshots and checks: one controller action as the poll would route it to the active RHI window.</summary>
    internal void InjectPad(PadAction action, Window? window = null)
    {
        window ??= RhiWindows().LastOrDefault(w => w.IsActive) ?? this;
        _padActive = true;
        ShowPadHints(true);
        if (_padWindow is null || !_padWindow.TryGetTarget(out var before) || !ReferenceEquals(before, window))
        {
            // As the poll: the controller arriving in a window focuses its start, and that reading does nothing else.
            _padWindow = new WeakReference<Window>(window);
            HookPadPointer(window);
            if (!ReferenceEquals(window, this)) { EnterPadWindow(window); return; }
        }
        if (ReferenceEquals(window, this)) { DoPad(action); RefreshPadHints(); }
        else DoPadInWindow(window, action);
    }

    /// <summary>For screenshots and checks: the mouse used on <paramref name="window"/>, then a controller input there.</summary>
    internal void InjectPointerThenPad(Window window, PadAction action)
    {
        HookPadPointer(window);
        _padActive = true;
        PointerTookOver(window);
        var memory = PadMemory(window);
        if (memory.PointerInUse) TakeBackFromPointer(window, memory);
        if (ReferenceEquals(window, this)) DoPad(action);
        else DoPadInWindow(window, action);
    }
}
