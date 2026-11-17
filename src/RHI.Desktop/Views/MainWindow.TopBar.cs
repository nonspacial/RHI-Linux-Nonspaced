using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-a (#303, #306): upstream 2.9.0's top bar (every layout), the Classic+ / Tabs game list (All | Installed |
/// Favourites | Hidden switch, Filters button), the interface size (the whole window, Ctrl + mouse wheel) and the top bar's
/// and game list's own sizes (Settings, or drag their edges; double-click resets).
/// </summary>
public sealed partial class MainWindow
{
    private const double TopBarPadX = 16, TopBarPadY = 10;

    private LayoutTransformControl? _uiScaleHost;
    private double _uiScale = 1;
    private MainWindowViewModel? _layoutsVm;
    private DispatcherTimer? _checkedTimer;
    private Border? _topBarGrip;
    private Border? _gameListGrip;

    private void InitializeTopBar()
    {
        // #306: the window's content is laid out at window size ÷ interface size and scaled to fit (upstream ApplyUiScale).
        if (Content is Control root)
        {
            Content = null;
            _uiScaleHost = new LayoutTransformControl { Child = root, Name = "UiScaleHost" };
            Content = _uiScaleHost;
            AddGrips(root);
        }
        AddHandler(PointerWheelChangedEvent, UiScaleWheel_Changed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        if (this.FindControl<SegmentedSwitch>("LayoutSwitch") is { } layoutSwitch)
            layoutSwitch.SelectionRequested += index =>
            {
                if (_layoutsVm is not { } vm || index < 0 || index >= vm.Layout.Choices.Count) return;
                if (vm.Layout.SetLayout(vm.Layout.Choices[index].Layout) is { } refused) vm.StatusText = refused;
            };
        if (this.FindControl<SegmentedSwitch>("LibraryMainFilter") is { } mainFilter)
            mainFilter.SelectionRequested += index => _layoutsVm?.ChooseLibraryMainFilter(index);

        DataContextChanged += (_, _) => AttachLayouts(DataContext as MainWindowViewModel);
        _checkedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _checkedTimer.Tick += (_, _) => _layoutsVm?.TopBar.Tick();
        Opened += (_, _) => _checkedTimer.Start();
        Closed += (_, _) => _checkedTimer.Stop();
    }

    private void AttachLayouts(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _layoutsVm)) return;
        if (_layoutsVm is { } old)
        {
            old.PropertyChanged -= OnLayoutsVmChanged;
            old.Layout.Changed -= OnGameLayoutChanged;
            old.SizesChanged -= ApplySizes;
            old.NewMods.PropertyChanged -= OnNewModsChanged;
        }
        _layoutsVm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnLayoutsVmChanged;
        vm.Layout.Changed += OnGameLayoutChanged;
        vm.SizesChanged += ApplySizes;
        vm.NewMods.PropertyChanged += OnNewModsChanged;
        RefreshSwitches();
        ApplySizes();
        ApplyGameLayout();
    }

    private void OnNewModsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (this.FindControl<Button>("NavAvailableMods") is { } nav && _layoutsVm is { } vm) ToolTip.SetTip(nav, vm.NavAvailableModsTip);
    }

    private void OnGameLayoutChanged()
    {
        RefreshSwitches();
        ApplyGameLayout();
        _libraryLayoutKey = null;
        ApplyLibraryLayout();
    }

    private void OnLayoutsVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(MainWindowViewModel.LibraryMainFilterIndex) or nameof(MainWindowViewModel.GameCountText))
            RefreshSwitches();
    }

    /// <summary>The layout switch and the game list's main filter show the saved choices (and the UI language).</summary>
    private void RefreshSwitches()
    {
        if (_layoutsVm is not { } vm) return;
        if (this.FindControl<SegmentedSwitch>("LayoutSwitch") is { } layoutSwitch)
        {
            var choices = vm.Layout.Choices;
            layoutSwitch.Items = choices.Select(c => new SegmentItem(c.Label, c.Tip, c.IsEnabled)).ToArray();
            layoutSwitch.SelectedIndex = choices.ToList().FindIndex(c => c.IsSelected);
            ToolTip.SetTip(layoutSwitch, vm.Layout.SwitchTip);
        }
        if (this.FindControl<SegmentedSwitch>("LibraryMainFilter") is { } mainFilter)
        {
            mainFilter.Items = MainWindowViewModel.LibraryMainFilters
                .Select(f => new SegmentItem(T(f.LabelKey), TooltipSweep.FilterTipKey(f.Mode) is { } tip ? T(tip) : null)).ToArray(); // #302 tips
            mainFilter.SelectedIndex = vm.LibraryMainFilterIndex;
        }
        if (this.FindControl<Button>("NavAvailableMods") is { } nav) ToolTip.SetTip(nav, vm.NavAvailableModsTip);
    }

    // ---- top bar actions ---------------------------------------------------------------------------------------------

    /// <summary>Available Mods: the new mods first while the New tag shows (upstream), else the whole list.</summary>
    private void NavAvailableMods_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { NewMods.IsHidden: false }) NewMods_Click(sender, e);
        else AvailableMods_Click(sender, e);
    }

    private void GlobalShaders_Click(object? sender, RoutedEventArgs e) => ShowGlobalShadersAddons(0);

    private void GlobalAddons_Click(object? sender, RoutedEventArgs e) => ShowGlobalShadersAddons(1);

    private void ShowGlobalShadersAddons(int tab) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await ShaderSetDialogs.ShowGlobalAsync(this, vm, line => vm.StatusText = line, tab);
    });

    /// <summary>The Help menu, upstream's entries and groups (TopBarPresenter.HelpMenu).</summary>
    private void TopBarHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, FlyoutPresenterClasses = { "navy" } };
        foreach (var entry in TopBarPresenter.HelpMenu)
        {
            if (entry.SeparatorBefore) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = entry.Text, Name = "Help" + entry.Action };
            if (entry.Url is { } url) ToolTip.SetTip(item, url);
            item.Click += (_, args) => RunHelp(entry, args);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void RunHelp(HelpMenuItem entry, RoutedEventArgs e)
    {
        switch (entry.Action)
        {
            case HelpAction.QuickStart: QuickStart_Click(null, e); break;
            case HelpAction.PatchNotes: RunUiSafe(ShowChangelogAsync); break;
            case HelpAction.Motd: Motd_Click(null, e); break;
            case HelpAction.UpdateLog: UpdateHistory_Click(null, e); break;
            case HelpAction.About: RunUiSafe(ShowHelpAsync); break;
            case HelpAction.Donate: Donate_Click(null, e); break;
            default:
                if (entry.Url is { } url) OpenProjectLink(url);
                break;
        }
    }

    /// <summary>The Filters button shows the engine and mod filters (upstream 2.9.0).</summary>
    private void LibraryFilters_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton toggle && this.FindControl<Border>("LibraryFilterBar") is { } bar) bar.IsVisible = toggle.IsChecked == true;
    }

    // ---- #306 sizes ------------------------------------------------------------------------------------------------

    /// <summary>Applies the interface size, the top bar size and the game list size.</summary>
    private void ApplySizes()
    {
        if (_layoutsVm is not { } vm) return;
        _uiScale = vm.UiScale / 100.0;
        if (_uiScaleHost is { } host)
            host.LayoutTransform = vm.UiScale == InterfaceSize.Normal ? null : new ScaleTransform(_uiScale, _uiScale);
        ApplyTopBarScale(vm.TopBarScale);
        ApplyGameListScale(vm.GameListScale);
    }

    private void ApplyTopBarScale(int percent)
    {
        var scale = InterfaceSize.Clamp(percent) / 100.0;
        if (this.FindControl<LayoutTransformControl>("TopBarScaleBox") is { } box)
            box.LayoutTransform = percent == InterfaceSize.Normal ? null : new ScaleTransform(scale, scale);
        if (this.FindControl<Border>("TopBar") is { } bar) bar.Padding = new Thickness(TopBarPadX * scale, TopBarPadY * scale);
    }

    private void ApplyGameListScale(int percent)
    {
        var scale = InterfaceSize.Clamp(percent) / 100.0;
        if (this.FindControl<LayoutTransformControl>("GameListScaleBox") is { } box)
            box.LayoutTransform = percent == InterfaceSize.Normal ? null : new ScaleTransform(scale, scale);
        _libraryLayoutKey = null;
        ApplyLibraryLayout();
    }

    /// <summary>Ctrl + mouse wheel steps the interface size by 5%, like browser zoom (upstream), anywhere in the window.</summary>
    private void UiScaleWheel_Changed(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0 || _layoutsVm is not { } vm) return;
        e.Handled = true;
        vm.StepUiScale(e.Delta.Y > 0);
    }

    /// <summary>The top bar's bottom edge and the game list's right edge: drag to resize, double-click to reset.</summary>
    private void AddGrips(Control root)
    {
        if (root is not Grid rootGrid) return;
        _topBarGrip = new Border
        {
            Name = "TopBarGrip", Height = 8, Margin = new Thickness(0, 0, 0, -4), VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth), ZIndex = 30,
        };
        ToolTip.SetTip(_topBarGrip, T("Size.TopBarGripTip"));
        ToolTip.SetShowDelay(_topBarGrip, MiddleEllipsisText.TooltipDelay);
        Grid.SetRow(_topBarGrip, 0);
        rootGrid.Children.Add(_topBarGrip);
        HookGrip(_topBarGrip, vertical: true);

        if (this.FindControl<Grid>("MainContentGrid") is not { } main) return;
        _gameListGrip = new Border
        {
            Name = "GameListGrip", Width = 8, Margin = new Thickness(0, 0, -4, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), ZIndex = 9,
        };
        ToolTip.SetTip(_gameListGrip, T("Size.GameListGripTip"));
        ToolTip.SetShowDelay(_gameListGrip, MiddleEllipsisText.TooltipDelay);
        Grid.SetColumn(_gameListGrip, 0);
        Grid.SetRowSpan(_gameListGrip, 2); // w14/layouts-b: the main grid has Spotlight's strip row
        main.Children.Add(_gameListGrip);
        HookGrip(_gameListGrip, vertical: false);
    }

    private void HookGrip(Border grip, bool vertical)
    {
        Point start = default;
        double startExtent = 0;
        int startPercent = InterfaceSize.Normal, live = InterfaceSize.Normal;
        var dragging = false;
        Control? Reference() => _uiScaleHost?.Child;
        grip.PointerPressed += (_, e) =>
        {
            if (_layoutsVm is not { } vm || Reference() is not { } reference || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            start = e.GetPosition(reference);
            startPercent = live = vertical ? vm.TopBarScale : vm.GameListScale;
            startExtent = vertical
                ? this.FindControl<Border>("TopBar")?.Bounds.Height ?? 0
                : this.FindControl<Border>("LibrarySidebar")?.Bounds.Width ?? 0;
            dragging = startExtent > 0;
            if (dragging) e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging || Reference() is not { } reference) return;
            var now = e.GetPosition(reference);
            var percent = InterfaceSize.FromDrag(startPercent, startExtent, vertical ? now.Y - start.Y : now.X - start.X);
            if (percent == live) return;
            live = percent;
            if (vertical) ApplyTopBarScale(percent);
            else PreviewGameListScale(percent);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (_layoutsVm is not { } vm) return;
            var refused = vertical ? vm.SetTopBarScale(live) : vm.SetGameListScale(live);
            if (refused is not null) { vm.StatusText = refused; ApplySizes(); }
        };
        grip.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            dragging = false;
            if (_layoutsVm is not { } vm) return;
            var refused = vertical ? vm.SetTopBarScale(InterfaceSize.Normal) : vm.SetGameListScale(InterfaceSize.Normal);
            if (refused is not null) vm.StatusText = refused;
            ApplySizes();
        };
    }

    /// <summary>While the game list's edge is dragged: its size and width follow, saved on release.</summary>
    private void PreviewGameListScale(int percent)
    {
        var scale = percent / 100.0;
        if (this.FindControl<LayoutTransformControl>("GameListScaleBox") is { } box) box.LayoutTransform = new ScaleTransform(scale, scale);
        if (IsLibraryNarrow || this.FindControl<Grid>("MainContentGrid") is not { } main) return;
        var width = Math.Floor(Math.Clamp(InterfaceSize.GameListWidth(percent), 150, Math.Max(150, LayoutWidth - GamePageMinWidth)));
        main.ColumnDefinitions = new ColumnDefinitions($"{width.ToString(System.Globalization.CultureInfo.InvariantCulture)},*");
        _libraryLayoutKey = null;
    }

    /// <summary>Settings lights the top bar's ⚙ while it is open (upstream).</summary>
    private void SetSettingsLit(bool lit)
    {
        if (this.FindControl<Button>("TopBarSettings") is { } settings) settings.Classes.Set("on", lit);
    }
}
