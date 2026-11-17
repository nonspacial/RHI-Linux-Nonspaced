using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
/// w14 controller fixes: the right stick click is upstream 2.9.4's full Home (the next collection, else back to the
/// selected cover); the controller reaches whichever RHI window is active (Settings, see MainWindow.Controller.Windows.cs,
/// and the other dialog windows), and nothing while another app is; the right stick also scrolls the covers sideways; the
/// mouse hides the hints and the next controller input takes control back (upstream's Controller.Pointer).
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
    private WeakReference<Window>? _padWindow;

    private const int PadFastMs = 16, PadSlowMs = 250; // upstream: polled fast while RHI is active, slowly otherwise

    private void InitializeController()
    {
        // The poll keeps running while RHI is not active and checks each time which RHI window is (PadTick). Avalonia raises
        // Activated before IsActive turns true, so deciding there stopped the controller after any switch away and back (a
        // dialog, Settings, a popup the mouse opened) until the layout changed (w14 controller fixes, the owner's report).
        Activated += (_, _) => UpdateControllerRunning();
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

    private bool ControllerWanted => _spotOn && _spotVm?.SpotlightSettings.ControllerSupport == true;

    /// <summary>Starts or stops reading the controller (Spotlight shown, Settings › Controller support on); it acts only while an RHI window is active.</summary>
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
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(PadFastMs) };
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
        if (_pad is null || _padTimer is null || !ControllerWanted) { UpdateControllerRunning(); return; }
        // Only the active RHI window gets the controller; nothing while another app is active (a game in front).
        var windows = RhiWindows();
        var index = PadRouter.Target(windows.Select(w => w.IsActive).ToList());
        if (index < 0)
        {
            _padActive = false; // a button held in a game does nothing until released (upstream)
            foreach (var w in windows) PadPointerShield.Hide(w); // never leave the shield over another app's turn
            _padTimer.Interval = TimeSpan.FromMilliseconds(PadSlowMs);
            return;
        }
        _padTimer.Interval = TimeSpan.FromMilliseconds(PadFastMs);
        var window = windows[index];
        if (_padWindow is null || !_padWindow.TryGetTarget(out var before) || !ReferenceEquals(before, window))
        {
            _padWindow = new WeakReference<Window>(window);
            _padActive = false; // another window: what is held now does nothing until released
            HookPadPointer(window);
        }
        var now = _padClock.ElapsedMilliseconds;
        if (!_pad.TryRead(now, out var state))
        {
            _padActive = false;
            ShowPadHints(false);
            return;
        }
        var memory = PadMemory(window);
        if (!_padActive)
        {
            _padActive = true;
            _padStyle = ControllerPresenter.StyleOf(_pad.Name);
            _poller.Reset(state);
            if (memory.PointerInUse) return; // the mouse has the window: the next controller input takes it back
            ShowPadHints(true);
            PadPointerShield.Show(window);
            if (ReferenceEquals(window, this))
            {
                if (FocusManager?.GetFocusedElement() is not Control { IsEffectivelyVisible: true }) FocusSelectedCover();
            }
            else EnterPadWindow(window);
            return;
        }
        var actions = _poller.Update(state, now, out var scrollY, out var scrollX);
        if (actions.Count == 0 && scrollY == 0 && scrollX == 0) return;
        if (memory.PointerInUse) TakeBackFromPointer(window, memory);
        if (ReferenceEquals(window, this))
        {
            foreach (var action in actions) DoPad(action);
            if (scrollY != 0 || scrollX != 0) ScrollFocused(scrollX, scrollY);
            if (actions.Count > 0) RefreshPadHints();
        }
        else
        {
            foreach (var action in actions) DoPadInWindow(window, action);
            if (scrollY != 0) ScrollInWindow(window, scrollY);
        }
    }

    /// <summary>RHI's open windows, the main window first.</summary>
    private List<Window> RhiWindows()
    {
        var list = new List<Window> { this };
        void Owned(Window owner)
        {
            foreach (var w in owner.OwnedWindows)
                if (w.IsVisible && !list.Contains(w)) { list.Add(w); Owned(w); }
        }
        Owned(this); // Settings and the dialogs shown over the main window (a dialog's own dialogs after it)
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            list.AddRange(desktop.Windows.Where(w => w.IsVisible && !list.Contains(w)));
        return list;
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
            case PadAction.Left or PadAction.Right when focused is ListBoxItem { Parent: not null } cover
                                                          && cover.FindAncestorOfType<ListBox>() is { Name: "SpotlightShelf" }:
                StepGame(vm, action == PadAction.Right ? 1 : -1); // upstream: on the covers left / right change the game
                break;
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
                // Upstream 2.9.4 CycleMainFilter: All → Installed → Favourites → All; from a collection, All first.
                vm.ChooseLibraryMainFilter(PadRouter.NextMainFilter(vm.LibraryMainFilterIndex, vm.Collections.ActiveName is not null));
                FocusSelectedCover();
                break;
            case PadAction.Home:
                PadHome(vm);
                break;
        }
    }

    private static void SendKey(Control target, Key key) => PadControls.SendKey(target, key);

    private void PressFocused(Control? focused)
    {
        if (focused is null) FocusSelectedCover();
        else PadControls.Press(focused);
    }

    /// <summary>
    /// Upstream 2.9.4 <c>PadAction.Home</c> (right stick click): on the covers with collections and no panel open, the next
    /// collection (after the last one, All games); otherwise back to the selected game's cover — the ⚙ and the side panel
    /// close and the covers get the focus. (From Settings, MainWindow.Controller.Windows.cs closes Settings first.)
    /// </summary>
    private void PadHome(MainWindowViewModel vm)
    {
        var panelOpen = vm.Layout.PanelKey is not null || _panelCog is not null;
        switch (PadRouter.Home(panelOpen, menuOpen: false, vm.Collections.Chips.Count))
        {
            case PadHomeRoute.NextCollection:
                var next = ControllerPoller.NextCollection(vm.Collections.Chips.Select(c => c.Name).ToList(), vm.Collections.ActiveName);
                if (next is null) vm.ChooseLibraryMainFilter(0); // after the last collection: All games (upstream Filter_Click("Detected"))
                else vm.Collections.ToggleCommand.Execute(next);
                Dispatcher.UIThread.Post(FocusSelectedCover, DispatcherPriority.Loaded);
                break;
            case PadHomeRoute.BackToCover:
                BackToCover(vm);
                break;
        }
    }

    /// <summary>Closes the ⚙ and the side panel and focuses the selected game's cover.</summary>
    private void BackToCover(MainWindowViewModel vm)
    {
        if (_panelCog is not null) CloseSpotlightCog();
        if (vm.Layout.PanelKey is not null) vm.Layout.ClosePanel();
        FocusSelectedCover();
        Dispatcher.UIThread.Post(FocusSelectedCover, DispatcherPriority.Loaded); // once the panel has gone
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
        var reference = (Visual?)_uiScaleHost?.Child ?? this;
        var scope = PadScope();
        var focused = FocusManager?.GetFocusedElement() as Control;
        // Nothing focused, or the focus somewhere the controller does not work: start at home (upstream PadMove → FocusHome).
        if (focused is null || !PadControls.Usable(focused, this, ReferenceEquals(scope, this) ? null : scope))
        {
            if (PadHomeIn(this, PadWindowKind.Main) is { } home) home.Focus(NavigationMethod.Directional);
            else FocusSelectedCover();
            return;
        }
        var target = PadControls.Move(scope, reference, focused, direction);
        // On the covers the focus is the selection (upstream: the focused cover is the selected game).
        if (target is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { Name: "SpotlightShelf" } shelf) shelf.SelectedItem = item.DataContext;
    }

    /// <summary>Where the controller works on the main window: the side panel while it is open, else the whole window.</summary>
    private Visual PadScope() => _panelShown is not null && this.FindControl<Border>("SpotlightPanelSheet") is { } sheet ? sheet : this;

    /// <summary>
    /// The right stick: up / down scroll what holds the focus (the page, the side panel); on the covers, left / right also
    /// scroll the shelf sideways (a Linux addition; upstream scrolls up / down only).
    /// </summary>
    private void ScrollFocused(double x, double y)
    {
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var shelf = this.FindControl<ListBox>("SpotlightShelf");
        var onShelf = focused is not null && shelf is not null && shelf.IsVisualAncestorOf(focused);
        var (shelfX, pageY) = PadRouter.Scroll(onShelf, x, y);
        if (shelfX != 0 && shelf is not null && ShelfScroller(shelf) is { } shelfScroller) PadControls.ScrollBy(shelfScroller, shelfX, 0);
        if (pageY == 0) return;
        var scroller = PadControls.VerticalAround(focused)
                       ?? (_panelShown is not null ? this.FindControl<ScrollViewer>("SpotlightPanelScroll") : this.FindControl<ScrollViewer>("DetailScroller"));
        if (scroller is not null) PadControls.ScrollBy(scroller, 0, pageY);
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
