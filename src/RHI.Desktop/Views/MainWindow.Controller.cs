using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Controller;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#308, upstream 2.9.0–2.9.2 MainWindow.Controller*): a game controller drives Spotlight while RHI is the
/// active window and Settings › General › Controller support is on. Upstream's button map (<see cref="PadAction"/>):
/// directions move between controls row by row (a blue focus ring), A selects, B goes back (menu, ⚙, panel, covers), Y
/// launches, X opens the game menu, LB / RB step through the games (the sections in the side panel), LT / RT jump ten
/// games, View shows Filters, Menu opens Settings, the left stick click cycles All / Installed / Favourites / Hidden, the
/// right stick click the collections, the right stick scrolls. The footer shows the hints. Open menus and drop-downs get
/// the arrow, Enter and Esc keys. When a game RHI started closes, RHI asks to come back to the front (the compositor may
/// only flag it: Wayland lets the focused app decide).
/// </summary>
public sealed partial class MainWindow
{
    private readonly ControllerPoller _poller = new();
    private readonly Stopwatch _padClock = Stopwatch.StartNew();
    private SdlGamepad? _pad;
    private DispatcherTimer? _padTimer;
    private bool _padActive;
    private PadStyle _padStyle;
    private CancellationTokenSource? _returnWatch;

    private void InitializeController()
    {
        Activated += (_, _) => UpdateControllerRunning();
        Deactivated += (_, _) =>
        {
            _padActive = false; // a button held in a game does nothing until released (upstream)
            UpdateControllerRunning();
        };
        Closed += (_, _) =>
        {
            _padTimer?.Stop();
            _returnWatch?.Cancel();
            _pad?.Dispose();
            _pad = null;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.GameLaunched += OnGameLaunched;
            vm.ControllerSettingChanged += UpdateControllerRunning;
        };
    }

    private bool ControllerWanted => _spotOn && IsActive && _spotVm?.SpotlightSettings.ControllerSupport == true;

    /// <summary>Starts or stops reading the controller (Spotlight shown, RHI active, Settings › Controller support on).</summary>
    private void UpdateControllerRunning()
    {
        if (!ControllerWanted)
        {
            _padTimer?.Stop();
            ShowPadHints(false);
            return;
        }
        _pad ??= new SdlGamepad();
        if (!_pad.Available) return;
        _padTimer ??= NewPadTimer();
        _padTimer.Start();
    }

    private DispatcherTimer NewPadTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            try { PadTick(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                AppLog.Warn("controller", "controller step failed", ex);
            }
        };
        return timer;
    }

    private void PadTick()
    {
        if (_pad is null || !ControllerWanted) { UpdateControllerRunning(); return; }
        var now = _padClock.ElapsedMilliseconds;
        if (!_pad.TryRead(now, out var state))
        {
            _padActive = false;
            ShowPadHints(false);
            return;
        }
        if (!_padActive)
        {
            _padActive = true;
            _padStyle = ControllerPresenter.StyleOf(_pad.Name);
            _poller.Reset(state);
            ShowPadHints(true);
            if (FocusManager?.GetFocusedElement() is not Control { IsEffectivelyVisible: true }) FocusSelectedCover();
            return;
        }
        var actions = _poller.Update(state, now, out var scroll);
        foreach (var action in actions) DoPad(action);
        if (scroll != 0) ScrollFocused(scroll);
        if (actions.Count > 0) RefreshPadHints();
    }

    private void ShowPadHints(bool on)
    {
        Classes.Set("pad", on);
        SetVisible("ControllerHintsText", on);
        SetVisible("StatusBarText", !on);
        if (on) RefreshPadHints();
    }

    private void RefreshPadHints()
    {
        if (this.FindControl<TextBlock>("ControllerHintsText") is not { } text || _spotVm is not { } vm) return;
        var hasCollections = vm.Collections.Chips.Count > 0;
        text.Text = ControllerPresenter.Line(ControllerPresenter.Hints(PadContextNow(), hasCollections, _padStyle));
    }

    private PadContext PadContextNow()
    {
        var focused = FocusManager?.GetFocusedElement() as Visual;
        if (focused?.FindAncestorOfType<PopupRoot>(includeSelf: true) is not null || TopLevel.GetTopLevel(focused) is PopupRoot || OpenCardMenu is not null) return PadContext.Menu;
        if (_panelCog is not null) return PadContext.PanelCog;
        if (_panelShown is not null) return PadContext.Panel;
        return focused is not null && this.FindControl<ListBox>("SpotlightShelf") is { } shelf && shelf.IsVisualAncestorOf(focused) ? PadContext.Shelf : PadContext.Page;
    }

    // ---- actions ---------------------------------------------------------------------------------------------------

    private void DoPad(PadAction action)
    {
        if (_spotVm is not { } vm) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        // An open menu or drop-down gets keys (upstream's direct menu / dropdown control).
        if (PadContextNow() == PadContext.Menu && focused is not null)
        {
            var key = action switch
            {
                PadAction.Up => Key.Up, PadAction.Down => Key.Down, PadAction.Left => Key.Left, PadAction.Right => Key.Right,
                PadAction.Select => Key.Enter, PadAction.Back => Key.Escape, _ => Key.None,
            };
            if (key != Key.None) SendKey(focused, key);
            return;
        }
        switch (action)
        {
            case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                MoveFocus(action);
                break;
            case PadAction.Select:
                PressFocused(focused);
                break;
            case PadAction.Back:
                if (_panelCog is not null) CloseSpotlightCog();
                else if (vm.Layout.PanelKey is not null) vm.Layout.ClosePanel();
                else FocusSelectedCover();
                break;
            case PadAction.Launch:
                if (vm.LaunchCommand.CanExecute(null)) vm.LaunchCommand.Execute(null);
                break;
            case PadAction.GameMenu:
                if (vm.SelectedGameRow is { } row) ShowCardMenu(vm, row, SelectedCoverItem() ?? (Control)this, atPointer: false);
                break;
            case PadAction.PrevGame or PadAction.NextGame:
                var step = action == PadAction.NextGame ? 1 : -1;
                if (vm.Layout.PanelKey is { } panel && GamePageSections.TabOrder.Contains(panel)) StepSection(vm, panel, step);
                else StepGame(vm, step);
                break;
            case PadAction.PageBack or PadAction.PageForward:
                StepGame(vm, action == PadAction.PageForward ? 10 : -10);
                break;
            case PadAction.Settings:
                OpenSettingsAt(null);
                break;
            case PadAction.Filters:
                if (this.FindControl<ToggleButton>("SpotlightFiltersButton") is { } filters) filters.IsChecked = filters.IsChecked != true;
                break;
            case PadAction.CycleFilter:
                vm.ChooseLibraryMainFilter((vm.LibraryMainFilterIndex + 1) % MainWindowViewModel.LibraryMainFilters.Count);
                FocusSelectedCover();
                break;
            case PadAction.Home:
                var names = vm.Collections.Chips.Select(c => c.Name).ToList();
                if (names.Count == 0) { FocusSelectedCover(); break; }
                var next = ControllerPoller.NextCollection(names, vm.Collections.ActiveName);
                vm.Collections.ToggleCommand.Execute(next ?? vm.Collections.ActiveName);
                FocusSelectedCover();
                break;
        }
    }

    private static void SendKey(Control target, Key key)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, Source = target });

    private void PressFocused(Control? focused)
    {
        switch (focused)
        {
            case ComboBox combo: combo.IsDropDownOpen = true; break;
            case ListBoxItem item when item.FindAncestorOfType<ListBox>() is { } list: list.SelectedItem = item.DataContext; break;
            case ToggleButton or CheckBox: SendKey(focused, Key.Space); break;
            case Button button: button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter); break;
            case null: FocusSelectedCover(); break;
            default: SendKey(focused, Key.Enter); break;
        }
    }

    private void StepGame(MainWindowViewModel vm, int step)
    {
        var index = vm.SelectedGameRow is { } row ? vm.Games.IndexOf(row) : -1;
        var next = ControllerPoller.Step(index, step, vm.Games.Count);
        if (next < 0) return;
        vm.SelectedGameRow = vm.Games[next];
        Dispatcher.UIThread.Post(FocusSelectedCover, DispatcherPriority.Loaded);
    }

    private static void StepSection(MainWindowViewModel vm, string panel, int step)
    {
        var order = GamePageSections.TabOrder;
        var next = order[(order.ToList().IndexOf(panel) + step + order.Count) % order.Count];
        vm.Layout.ClosePanel();
        vm.Layout.OpenPanel(next);
    }

    private ListBoxItem? SelectedCoverItem()
        => this.FindControl<ListBox>("SpotlightShelf") is { SelectedItem: { } selected } shelf ? shelf.ContainerFromItem(selected) as ListBoxItem : null;

    private void FocusSelectedCover()
    {
        if (this.FindControl<ListBox>("SpotlightShelf") is not { } shelf || shelf.SelectedItem is not { } selected) return;
        shelf.ScrollIntoView(selected);
        (shelf.ContainerFromItem(selected) as ListBoxItem)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Moves the focus to the nearest control that way (in the side panel while it is open), row by row.</summary>
    private void MoveFocus(PadAction direction)
    {
        Visual scope = _panelShown is not null && this.FindControl<Border>("SpotlightPanelSheet") is { } sheet ? sheet : this;
        var reference = (Visual?)_uiScaleHost?.Child ?? this;
        var candidates = scope.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled
                        && c is Button or ToggleButton or ComboBox or CheckBox or ListBoxItem or TextBox or Slider)
            .Select(c => (Control: c, Rect: BoundsIn(c, reference) ?? default))
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToList();
        if (candidates.Count == 0) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is null || !scope.IsVisualAncestorOf(focused) || BoundsIn(focused, reference) is not { } current)
        {
            candidates[0].Control.Focus(NavigationMethod.Directional);
            return;
        }
        var index = ControllerPresenter.Next(candidates.Select(c => c.Rect).ToList(), current, direction);
        if (index < 0) return;
        var target = candidates[index].Control;
        target.Focus(NavigationMethod.Directional);
        target.BringIntoView();
        // On the covers the focus is the selection (upstream: the focused cover is the selected game).
        if (target is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { Name: "SpotlightShelf" } shelf) shelf.SelectedItem = item.DataContext;
    }

    private static Rect? BoundsIn(Control control, Visual reference)
        => control.TranslatePoint(default, reference) is { } origin ? new Rect(origin, control.Bounds.Size) : null;

    private void ScrollFocused(double pixels)
    {
        var scroller = (FocusManager?.GetFocusedElement() as Visual)?.FindAncestorOfType<ScrollViewer>()
                       ?? (_panelShown is not null ? this.FindControl<ScrollViewer>("SpotlightPanelScroll") : this.FindControl<ScrollViewer>("DetailScroller"));
        if (scroller is null) return;
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(scroller.Offset.Y + pixels, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
    }

    // ---- back to the front after a game ------------------------------------------------------------------------------

    private void OnGameLaunched(DetectedGame game)
    {
        if (!_spotOn || _spotVm is not { } vm || !vm.SpotlightSettings.ControllerSupport) return;
        _returnWatch?.Cancel();
        var cancel = _returnWatch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            if (!await GameExitWatcher.WaitForCloseAsync(() => vm.IsGameRunning(game), Task.Delay, cancel.Token)) return;
            await Task.Delay(800, cancel.Token).ConfigureAwait(false); // let the game's window finish closing (upstream)
            Dispatcher.UIThread.Post(() =>
            {
                if (cancel.IsCancellationRequested || !IsVisible || IsActive) return;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                AppLog.Warn("controller", $"'{game.Name}' closed: asked to bring RHI to the front");
            });
        }, CancellationToken.None);
    }
}
