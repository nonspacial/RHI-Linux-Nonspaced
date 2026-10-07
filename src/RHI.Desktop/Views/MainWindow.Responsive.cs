using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Window-size responsiveness: the library column becomes a slide-out overlay below 900 px, the status-bar version
/// hides below 700 px, the toolbar overflow menu, and dialogs capped to the main window.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Below this window width the library column is removed and opened as an overlay instead.</summary>
    public const double LibraryOverlayBelow = 900;
    private const double LibraryOverlayMaxWidth = 340;
    private const double VersionTextHiddenBelow = 700;
    private const double DialogSideMargin = 32;
    private const string OverflowMenuClass = "toolbarOverflow";

    // Live source of the '…' menu: assigned before the first open so the presenter binds to it. It holds the
    // prebuilt items (one per toolbar button, bindings created once) of the buttons currently overflowing.
    private readonly System.Collections.ObjectModel.ObservableCollection<MenuItem> _overflowMenuItems = [];
    private readonly Dictionary<Button, MenuItem> _overflowMenuItemFor = [];
    private bool _libraryOverlayOpen;
    private bool _overflowSyncQueued;
    private string? _libraryLayoutKey;

    /// <summary>True when the library is shown as an overlay (window narrower than <see cref="LibraryOverlayBelow"/>).</summary>
    public bool IsLibraryNarrow { get; private set; }

    /// <summary>True while the library overlay is open.</summary>
    public bool IsLibraryOverlayOpen => _libraryOverlayOpen;

    private void InitializeResponsive()
    {
        if (this.FindControl<ToolbarPanel>("ToolbarRow") is { } toolbar)
            toolbar.OverflowItemsChanged += (_, _) => QueueOverflowMenuSync();
        if (this.FindControl<Button>("ToolbarOverflowButton")?.Flyout is MenuFlyout overflowMenu)
            overflowMenu.ItemsSource = _overflowMenuItems;
        // Enter on a game commits the pick. ListBoxItem marks Enter handled before it bubbles, so listen in the
        // tunnelling phase (and to handled events) rather than with a bubbled KeyDown.
        if (this.FindControl<ListBox>("GameLibraryList") is { } list)
            list.AddHandler(KeyDownEvent, GameLibrary_PreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        SizeChanged += (_, _) => ApplyLibraryLayout();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _libraryOverlayOpen)
            {
                CloseLibraryOverlay(restoreFocus: true);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private double LayoutWidth => ClientSize.Width > 0 ? ClientSize.Width : Width;

    /// <summary>Applies the library column/overlay arrangement for the current width and view mode.</summary>
    private void ApplyLibraryLayout()
    {
        var width = LayoutWidth;
        if (double.IsNaN(width) || width <= 0) return;
        var compact = DataContext is MainWindowViewModel { IsCompactView: true };
        var narrow = width < LibraryOverlayBelow;
        if (!narrow) _libraryOverlayOpen = false;
        IsLibraryNarrow = narrow;

        var main = this.FindControl<Grid>("MainContentGrid");
        var sidebar = this.FindControl<Border>("LibrarySidebar");
        var backdrop = this.FindControl<Border>("LibraryBackdrop");
        var toggle = this.FindControl<Button>("LibraryToggleButton");
        var version = this.FindControl<TextBlock>("ProductVersionText");
        if (version is not null && version.IsVisible != width >= VersionTextHiddenBelow) version.IsVisible = width >= VersionTextHiddenBelow;
        if (main is null || sidebar is null) return;

        var overlayWidth = Math.Min(LibraryOverlayMaxWidth, Math.Floor(width * 0.85));
        var key = narrow ? $"narrow:{_libraryOverlayOpen}:{overlayWidth}" : $"wide:{compact}";
        if (key == _libraryLayoutKey) return;
        _libraryLayoutKey = key;

        // Focus that is about to disappear (a game in the column that becomes a closed overlay, or the toggle that
        // hides when the column returns) moves to the control that now stands for the library.
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var focusInSidebar = focused is not null && sidebar.IsVisualAncestorOf(focused);
        var focusOnToggle = focused is not null && toggle is not null && (ReferenceEquals(focused, toggle) || toggle.IsVisualAncestorOf(focused));

        if (toggle is not null) toggle.IsVisible = narrow;
        if (backdrop is not null) backdrop.IsVisible = narrow && _libraryOverlayOpen;
        if (this.FindControl<Border>("StatusBarBackdrop") is { } statusBackdrop) statusBackdrop.IsVisible = narrow && _libraryOverlayOpen;
        if (narrow)
        {
            main.ColumnDefinitions = new ColumnDefinitions("0,*");
            Grid.SetColumnSpan(sidebar, 2);
            sidebar.HorizontalAlignment = HorizontalAlignment.Left;
            sidebar.Width = overlayWidth;
            sidebar.ZIndex = 11;
            sidebar.IsVisible = _libraryOverlayOpen;
        }
        else
        {
            main.ColumnDefinitions = new ColumnDefinitions(compact ? "235,*" : "300,*");
            Grid.SetColumnSpan(sidebar, 1);
            sidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
            sidebar.Width = double.NaN;
            sidebar.ZIndex = 0;
            sidebar.IsVisible = true;
        }
        ApplyOverlayFocusScope(narrow && _libraryOverlayOpen);

        if (narrow && !_libraryOverlayOpen && focusInSidebar) toggle?.Focus();
        else if (!narrow && focusOnToggle) FocusLibraryList();
    }

    // While the overlay is open, Tab/Shift+Tab cycle inside it (search box, chips, list) and the controls behind
    // it (detail pane, status bar) are no tab stops and take no pointer input; the toolbar above stays usable. The
    // dimmed backdrop covers both (LibraryBackdrop, StatusBarBackdrop), so a click on either closes the overlay.
    private void ApplyOverlayFocusScope(bool open)
    {
        if (this.FindControl<Border>("LibrarySidebar") is { } sidebar)
        {
            if (open) KeyboardNavigation.SetTabNavigation(sidebar, KeyboardNavigationMode.Cycle);
            else sidebar.ClearValue(KeyboardNavigation.TabNavigationProperty);
        }
        foreach (var behind in new Control?[] { this.FindControl<ScrollViewer>("DetailScroller"), this.FindControl<Border>("StatusBar") })
        {
            if (behind is null) continue;
            if (open)
            {
                KeyboardNavigation.SetTabNavigation(behind, KeyboardNavigationMode.None);
                behind.IsHitTestVisible = false;
            }
            else
            {
                behind.ClearValue(KeyboardNavigation.TabNavigationProperty);
                behind.ClearValue(IsHitTestVisibleProperty);
            }
        }
        if (open && FocusManager?.GetFocusedElement() is Visual focused
            && this.FindControl<ScrollViewer>("DetailScroller") is { } detail && detail.IsVisualAncestorOf(focused))
            FocusLibraryList();
    }

    private void FocusLibraryList()
    {
        if (this.FindControl<ListBox>("GameLibraryList") is not { } list) return;
        if (list.SelectedItem is { } selected && list.ContainerFromItem(selected) is { } container) container.Focus();
        else list.Focus();
    }

    private void LibraryToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_libraryOverlayOpen) CloseLibraryOverlay(restoreFocus: true);
        else OpenLibraryOverlay();
    }

    /// <summary>Opens the library overlay (narrow windows only).</summary>
    public void OpenLibraryOverlay()
    {
        if (!IsLibraryNarrow || _libraryOverlayOpen) return;
        _libraryOverlayOpen = true;
        ApplyLibraryLayout();
        Dispatcher.UIThread.Post(() =>
        {
            if (_libraryOverlayOpen) FocusLibraryList();
        });
    }

    private void CloseLibraryOverlay(bool restoreFocus)
    {
        if (!_libraryOverlayOpen) return;
        _libraryOverlayOpen = false;
        ApplyLibraryLayout();
        if (restoreFocus) this.FindControl<Button>("LibraryToggleButton")?.Focus();
    }

    private void LibraryBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseLibraryOverlay(restoreFocus: true);
        e.Handled = true;
    }

    // Picking a game (pointer on an item, or Enter) closes the overlay; arrow keys only move the selection.
    private void GameLibrary_Tapped(object? sender, TappedEventArgs e)
    {
        if (_libraryOverlayOpen && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            CloseLibraryOverlay(restoreFocus: true);
    }

    private void GameLibrary_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_libraryOverlayOpen || e.Key != Key.Enter || sender is not ListBox list) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            && list.ItemFromContainer(item) is { } picked)
            list.SelectedItem = picked;
        CloseLibraryOverlay(restoreFocus: true);
        e.Handled = true;
    }

    // The '…' menu lists the buttons the toolbar moved out of the row, in order, with their labels, enabled state,
    // tooltips and actions. One MenuItem per toolbar button is built the first time that button overflows (its
    // bindings live as long as the window; a button added to the toolbar later gets one too); afterwards only which
    // of them sit in the menu's collection changes.
    private MenuItem OverflowMenuItemFor(Button button)
    {
        if (_overflowMenuItemFor.TryGetValue(button, out var existing)) return existing;
        var item = new MenuItem();
        item.Classes.Add(OverflowMenuClass);
        item.Bind(MenuItem.HeaderProperty, button.GetObservable(ContentControl.ContentProperty));
        item.Bind(IsEnabledProperty, button.GetObservable(IsEffectivelyEnabledProperty));
        item.Bind(ToolTip.TipProperty, button.GetObservable(ToolTip.TipProperty));
        item.Click += (_, _) => InvokeToolbarButton(button);
        _overflowMenuItemFor[button] = item;
        return item;
    }

    private void ToolbarOverflowMenu_Opening(object? sender, EventArgs e) => SyncOverflowMenuItems();

    // Called from the toolbar's measure: sync after layout. A menu left open when the partition changes (resize)
    // closes rather than listing stale items under a button that may have been hidden.
    private void QueueOverflowMenuSync()
    {
        if (_overflowSyncQueued) return;
        _overflowSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _overflowSyncQueued = false;
            if (this.FindControl<Button>("ToolbarOverflowButton")?.Flyout is MenuFlyout { IsOpen: true } menu) menu.Hide();
            SyncOverflowMenuItems();
        });
    }

    private void SyncOverflowMenuItems()
    {
        if (this.FindControl<ToolbarPanel>("ToolbarRow") is not { } toolbar) return;
        // Buttons removed from the toolbar drop their item (and with it the item's bindings to them).
        foreach (var gone in _overflowMenuItemFor.Keys.Where(b => !toolbar.Children.Contains(b)).ToList())
            _overflowMenuItemFor.Remove(gone);
        var wanted = toolbar.OverflowItems.OfType<Button>().Select(OverflowMenuItemFor).ToList();
        if (wanted.SequenceEqual(_overflowMenuItems)) return;
        _overflowMenuItems.Clear();
        foreach (var item in wanted) _overflowMenuItems.Add(item);
    }

    private static void InvokeToolbarButton(Button button)
    {
        if (!button.IsEffectivelyEnabled) return;
        if (button.Command is { } command && command.CanExecute(button.CommandParameter))
            command.Execute(button.CommandParameter);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    /// <summary>Caps a dialog to the main window (width − 32 px, height) and lets its content scroll vertically.</summary>
    private void FitDialogToOwner(Window dialog)
    {
        var maxWidth = Math.Max(200, LayoutWidth - DialogSideMargin);
        var maxHeight = Math.Max(160, ClientSize.Height > 0 ? ClientSize.Height : Height);
        dialog.MaxWidth = Math.Min(dialog.MaxWidth, maxWidth);
        dialog.MinWidth = Math.Min(dialog.MinWidth, maxWidth);
        if (!double.IsNaN(dialog.Width)) dialog.Width = Math.Min(dialog.Width, maxWidth);
        dialog.MaxHeight = Math.Min(dialog.MaxHeight, maxHeight);
        dialog.MinHeight = Math.Min(dialog.MinHeight, maxHeight);
        if (!double.IsNaN(dialog.Height)) dialog.Height = Math.Min(dialog.Height, maxHeight);
        // Dialogs that already own a scroll region keep it (their footers stay pinned); the rest scroll as a whole.
        if (dialog.Content is Control content and not ScrollViewer
            && !content.GetLogicalDescendants().Any(d => d is ScrollViewer or TabControl))
        {
            dialog.Content = null;
            dialog.Content = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }
    }

    private Task ShowFittedDialogAsync(Window dialog)
    {
        FitDialogToOwner(dialog);
        return dialog.ShowDialog(this);
    }

    private Task<T> ShowFittedDialogAsync<T>(Window dialog)
    {
        FitDialogToOwner(dialog);
        return dialog.ShowDialog<T>(this);
    }
}
