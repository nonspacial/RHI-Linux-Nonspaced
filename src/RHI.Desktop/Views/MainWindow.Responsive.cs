using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Window-size responsiveness: the library column becomes a slide-out overlay below 900 px, the status-bar version
/// hides below 700 px, and the toolbar overflow menu.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Below this window width the library column is removed and opened as an overlay instead.</summary>
    public const double LibraryOverlayBelow = 900;
    private const double LibraryOverlayMaxWidth = 340;
    private const double VersionTextHiddenBelow = 700;

    // Live source of the '…' menu: refilled on Opening; assigned before the first open so the presenter binds to it.
    private readonly System.Collections.ObjectModel.ObservableCollection<MenuItem> _overflowMenuItems = [];
    private bool _libraryOverlayOpen;
    private string? _libraryLayoutKey;

    /// <summary>True when the library is shown as an overlay (window narrower than <see cref="LibraryOverlayBelow"/>).</summary>
    public bool IsLibraryNarrow { get; private set; }

    /// <summary>True while the library overlay is open.</summary>
    public bool IsLibraryOverlayOpen => _libraryOverlayOpen;

    private void InitializeResponsive()
    {
        if (this.FindControl<Button>("ToolbarOverflowButton")?.Flyout is MenuFlyout overflowMenu)
            overflowMenu.ItemsSource = _overflowMenuItems;
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
        if (version is not null) version.IsVisible = width >= VersionTextHiddenBelow;
        if (main is null || sidebar is null) return;

        var overlayWidth = Math.Min(LibraryOverlayMaxWidth, Math.Floor(width * 0.85));
        var key = narrow ? $"narrow:{_libraryOverlayOpen}:{overlayWidth}" : $"wide:{compact}";
        if (key == _libraryLayoutKey) return;
        _libraryLayoutKey = key;

        if (toggle is not null) toggle.IsVisible = narrow;
        if (backdrop is not null) backdrop.IsVisible = narrow && _libraryOverlayOpen;
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
        if (this.FindControl<ListBox>("GameLibraryList") is { } list)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_libraryOverlayOpen) return;
                if (list.SelectedItem is { } selected && list.ContainerFromItem(selected) is { } container) container.Focus();
                else list.Focus();
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

    private void GameLibrary_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_libraryOverlayOpen && e.Key == Key.Enter)
        {
            CloseLibraryOverlay(restoreFocus: true);
            e.Handled = true;
        }
    }

    // The '…' menu lists the buttons the toolbar moved out of the row, in order, with their labels, enabled
    // state, tooltips and actions.
    private void ToolbarOverflowMenu_Opening(object? sender, EventArgs e)
    {
        if (this.FindControl<ToolbarPanel>("ToolbarRow") is not { } toolbar) return;
        _overflowMenuItems.Clear();
        foreach (var button in toolbar.OverflowItems.OfType<Button>())
        {
            var item = new MenuItem();
            item.Bind(MenuItem.HeaderProperty, button.GetObservable(ContentControl.ContentProperty));
            item.Bind(IsEnabledProperty, button.GetObservable(IsEffectivelyEnabledProperty));
            if (ToolTip.GetTip(button) is { } tip) ToolTip.SetTip(item, tip);
            var source = button;
            item.Click += (_, _) => InvokeToolbarButton(source);
            _overflowMenuItems.Add(item);
        }
    }

    private static void InvokeToolbarButton(Button button)
    {
        if (!button.IsEffectivelyEnabled) return;
        if (button.Command is { } command && command.CanExecute(button.CommandParameter))
            command.Execute(button.CommandParameter);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
}
