using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Controller;

namespace RHI.Desktop.Views;

/// <summary>
/// w14 controller fixes: the controller's work on controls, shared by the main window, the Settings window and RHI's other
/// dialog windows — row navigation (<see cref="ControllerPresenter.Next"/>), pressing the focused control, drop-downs
/// (open on the current choice; the arrows, Enter and Esc while open: Esc closes unchanged), the right stick's scroll and
/// whether a control can take the focus back after the mouse (upstream 2.9.4 MainWindow.Controller / .Popups / .Pointer).
/// </summary>
internal static class PadControls
{
    /// <summary>The controls the controller moves between inside <paramref name="scope"/>, with their bounds in <paramref name="reference"/>.</summary>
    public static List<(Control Control, Rect Rect)> Candidates(Visual scope, Visual reference, Func<Control, bool>? include = null)
        => scope.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled
                        && c is Button or ToggleButton or ComboBox or CheckBox or ListBoxItem or TextBox or Slider
                        && (include is null || include(c)))
            .Select(c => (Control: c, Rect: BoundsIn(c, reference) ?? default))
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToList();

    /// <summary>
    /// Moves the focus to the nearest control that way inside <paramref name="scope"/>, row by row; with nothing focused there,
    /// to its first control. The control focused, or null.
    /// </summary>
    public static Control? Move(Visual scope, Visual reference, Control? focused, PadAction direction, Func<Control, bool>? include = null)
    {
        var candidates = Candidates(scope, reference, include);
        if (candidates.Count == 0) return null;
        if (focused is null || !scope.IsVisualAncestorOf(focused) || BoundsIn(focused, reference) is not { } current)
        {
            candidates[0].Control.Focus(NavigationMethod.Directional);
            return candidates[0].Control;
        }
        var index = ControllerPresenter.Next(candidates.Select(c => c.Rect).ToList(), current, direction);
        if (index < 0) return null;
        var target = candidates[index].Control;
        target.Focus(NavigationMethod.Directional);
        target.BringIntoView();
        return target;
    }

    /// <summary>The first control the controller can focus inside <paramref name="scope"/> (top to bottom, left to right), or null.</summary>
    public static Control? First(Visual scope, Visual reference, Func<Control, bool>? include = null)
        => Candidates(scope, reference, include).OrderBy(c => Math.Round(c.Rect.Top)).ThenBy(c => c.Rect.Left).Select(c => c.Control).FirstOrDefault();

    public static Rect? BoundsIn(Control control, Visual reference)
        => control.TranslatePoint(default, reference) is { } origin ? new Rect(origin, control.Bounds.Size) : null;

    public static void SendKey(Control target, Key key)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });

    /// <summary>A as a click on the focused control; a drop-down opens with its current choice focused (upstream FocusOpenCombo).</summary>
    public static void Press(Control focused)
    {
        switch (focused)
        {
            case ComboBox combo: OpenDropDown(combo); break;
            case ListBoxItem item when item.FindAncestorOfType<ListBox>() is { } list: list.SelectedItem = item.DataContext; break;
            case ToggleButton or CheckBox: SendKey(focused, Key.Space); break;
            case Button button:
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
                break;
            case TextBox: break; // typing needs a keyboard (upstream)
            default: SendKey(focused, Key.Enter); break;
        }
    }

    /// <summary>Opens a drop-down and, once its items are built, focuses the current choice (else the first one).</summary>
    public static void OpenDropDown(ComboBox combo)
    {
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!combo.IsDropDownOpen) return;
            var index = Math.Max(0, combo.SelectedIndex);
            if (combo.ContainerFromIndex(index) is Control { IsEnabled: true } current) current.Focus(NavigationMethod.Directional);
            else if (combo.ContainerFromIndex(0) is Control first) first.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The control an open menu or drop-down's keys go to: the focused item inside it, else an open drop-down in
    /// <paramref name="window"/> (its choice gets the focus first). Null when none is open.
    /// </summary>
    public static Control? OpenMenuTarget(TopLevel window, Control? focused)
    {
        if (focused is not null && (focused is ComboBoxItem or MenuItem || focused.FindAncestorOfType<PopupRoot>(includeSelf: true) is not null
                                    || TopLevel.GetTopLevel(focused) is PopupRoot))
            return focused;
        return window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen);
    }

    /// <summary>The arrows, Enter and Esc to an open menu or drop-down (Esc: closed without changing the choice).</summary>
    public static void MenuKey(Control target, PadAction action)
    {
        if (target is ComboBox { IsDropDownOpen: true } combo && action is not PadAction.Select and not PadAction.Back)
        {
            // The drop-down is open but its choice is not focused yet: focus it, as A does.
            OpenDropDown(combo);
            return;
        }
        var key = action switch
        {
            PadAction.Up => Key.Up, PadAction.Down => Key.Down, PadAction.Left => Key.Left, PadAction.Right => Key.Right,
            PadAction.Select => Key.Enter, PadAction.Back => Key.Escape, _ => Key.None,
        };
        if (key == Key.None) return;
        if (target is ComboBox open && key == Key.Escape) { open.IsDropDownOpen = false; open.Focus(NavigationMethod.Directional); return; }
        SendKey(target, key);
    }

    /// <summary>Scrolls <paramref name="scroller"/> by the pixels given (positive: down / right), within its range.</summary>
    public static void ScrollBy(ScrollViewer scroller, double x, double y)
        => scroller.Offset = new Vector(
            Math.Clamp(scroller.Offset.X + x, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)),
            Math.Clamp(scroller.Offset.Y + y, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));

    /// <summary>The nearest scroll area around <paramref name="focused"/> that scrolls up and down, or null.</summary>
    public static ScrollViewer? VerticalAround(Visual? focused)
    {
        for (var sv = focused?.FindAncestorOfType<ScrollViewer>(); sv is not null; sv = sv.FindAncestorOfType<ScrollViewer>())
            if (sv.Extent.Height > sv.Viewport.Height + 1) return sv;
        return null;
    }

    /// <summary>The largest scroll area in <paramref name="scope"/> that scrolls up and down (upstream PopupScroller), or null.</summary>
    public static ScrollViewer? LargestVertical(Visual scope)
        => scope.GetVisualDescendants().OfType<ScrollViewer>().Where(sv => sv.IsEffectivelyVisible && sv.Extent.Height > sv.Viewport.Height + 1)
            .OrderByDescending(sv => sv.Extent.Height - sv.Viewport.Height).FirstOrDefault();

    /// <summary>Whether <paramref name="control"/> can take the controller's focus back in <paramref name="window"/> (inside <paramref name="scope"/>).</summary>
    public static bool Usable(Control control, TopLevel window, Visual? scope = null)
        => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Focusable
           && ReferenceEquals(TopLevel.GetTopLevel(control), window) && (scope is null || scope.IsVisualAncestorOf(control));
}

/// <summary>
/// Upstream 2.9.4 MainWindow.Controller.Pointer: while the controller is in use a see-through layer over the window catches
/// the mouse, so a pointer resting on the window does not hover controls or open tooltips as the controller moves things
/// under it. Moving the mouse or pressing a mouse button removes it (the controller hands over), as does RHI losing the
/// focus. The layer sits in the window's overlay layer; it takes the pointer, never the focus.
/// </summary>
internal static class PadPointerShield
{
    private static readonly ConditionalWeakTable<TopLevel, Border> Shields = [];

    public static void Show(TopLevel window)
    {
        if (OverlayLayer.GetOverlayLayer(window) is not { } layer) return;
        if (!Shields.TryGetValue(window, out var shield))
        {
            shield = new Border { Background = Brushes.Transparent, Focusable = false, IsHitTestVisible = true, ZIndex = int.MaxValue, Name = "PadPointerShield" };
            Shields.AddOrUpdate(window, shield);
        }
        if (shield.Parent is null) layer.Children.Add(shield);
        var size = layer.Bounds.Size is { Width: > 0, Height: > 0 } s ? s : window.ClientSize;
        shield.Width = size.Width;
        shield.Height = size.Height;
        shield.IsVisible = true;
    }

    public static void Hide(TopLevel window)
    {
        if (Shields.TryGetValue(window, out var shield)) shield.IsVisible = false;
    }

    public static bool IsShown(TopLevel window) => Shields.TryGetValue(window, out var shield) && shield.IsVisible && shield.Parent is not null;
}
