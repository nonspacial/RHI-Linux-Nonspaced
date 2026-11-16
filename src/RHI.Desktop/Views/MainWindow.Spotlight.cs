using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Theming;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Artwork;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.0–2.9.4 Spotlight, mockup D; MainWindow.SpotlightChrome / DetailLayoutHost.Spotlight):
/// the game list becomes a strip of covers above (or below) the page; the Tabs header sits in a banner over the game's
/// art; "What's installed" and "Tune this game" tiles; a side panel that slides in with a section (moved in while open, so
/// every row keeps its rules), the component cards, Screenshots, Achievements, Game settings or Artwork, and a component's
/// ⚙ inside it with a back button. Artwork is loaded only while Spotlight shows. Switching layout only changes what
/// shows; nothing is rebuilt.
/// </summary>
public sealed partial class MainWindow
{
    private const int CoverDecodeStep = 80;

    private readonly HashSet<ShelfTile> _shelfTiles = [];
    private readonly Dictionary<string, Bitmap> _coverBitmaps = new(StringComparer.Ordinal);
    private int _coverDecodeWidth;
    private bool _spotOn;
    private MainWindowViewModel? _spotVm;
    private DetectedGame? _heroGame;
    private CancellationTokenSource? _spotLoads;
    private string? _panelShown;
    private Control? _panelContent;
    private SpotlightCog? _panelCog;
    private bool _shelfDragging;
    private Point _shelfDragStart;
    private double _shelfDragOffset;
    private IPointer? _shelfDragPointer;

    private sealed record SpotlightCog(CogHostWindow Window, Control Content, string TitleBefore, TaskCompletionSource Done);

    /// <summary>The cover tile's width and the cover's height at the saved cover size.</summary>
    internal double ShelfTileWidth => _spotVm?.Spotlight.TileWidth ?? SpotlightPresenter.BaseTileWidth;
    internal double ShelfCoverHeight => _spotVm?.Spotlight.CoverHeight ?? SpotlightPresenter.BaseCoverHeight;

    private void InitializeSpotlight()
    {
        DataContextChanged += (_, _) => AttachSpotlight(DataContext as MainWindowViewModel);
        if (this.FindControl<SegmentedSwitch>("SpotlightMainFilter") is { } mainFilter)
            mainFilter.SelectionRequested += index => _spotVm?.ChooseLibraryMainFilter(index);
        if (this.FindControl<ListBox>("SpotlightShelf") is { } shelf)
        {
            shelf.AddHandler(PointerWheelChangedEvent, Shelf_Wheel, RoutingStrategies.Tunnel);
            shelf.AddHandler(PointerPressedEvent, Shelf_Pressed, RoutingStrategies.Tunnel);
            shelf.AddHandler(PointerMovedEvent, Shelf_Moved, RoutingStrategies.Tunnel);
            shelf.AddHandler(PointerReleasedEvent, Shelf_Released, RoutingStrategies.Tunnel);
            shelf.AddHandler(PointerCaptureLostEvent, (_, _) => _shelfDragPointer = null, RoutingStrategies.Bubble);
            shelf.SizeChanged += (_, _) => UpdateShelfCentering();
            shelf.SelectionChanged += (_, _) =>
            {
                if (_spotOn && shelf.SelectedItem is { } selected) Dispatcher.UIThread.Post(() => shelf.ScrollIntoView(selected));
            };
        }
        if (this.FindControl<Grid>("SpotlightHeroLayers") is { } layers)
            layers.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyHeroHeight(); };
        if (this.FindControl<Border>("SpotlightHeroGrip") is { } heroGrip) HookSpotlightGrip(heroGrip, hero: true);
        if (this.FindControl<Border>("SpotlightCoverGrip") is { } coverGrip) HookSpotlightGrip(coverGrip, hero: false);
        if (this.FindControl<Grid>("SpotlightPanelOverlay") is { } overlay)
            overlay.SizeChanged += (_, e) =>
            {
                if (this.FindControl<Border>("SpotlightPanelSheet") is { } sheet)
                    sheet.Width = Math.Min(e.NewSize.Width, Math.Clamp(e.NewSize.Width * 0.62, 560, 980));
            };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || _spotVm?.Layout.PanelKey is null) return;
            if (_panelCog is not null) CloseSpotlightCog();
            else _spotVm.Layout.ClosePanel();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        ThemeResources.Changed += ApplyHeroFades;
    }

    private void AttachSpotlight(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _spotVm)) return;
        if (_spotVm is { } old)
        {
            old.PropertyChanged -= OnSpotlightVmChanged;
            old.Spotlight.Changed -= OnSpotlightChoicesChanged;
            old.Components.CollectionChanged -= OnSpotlightComponentsChanged;
            old.Games.CollectionChanged -= OnSpotlightGamesChanged;
            old.TopBar.PropertyChanged -= OnSpotlightUpdatesChanged;
            old.ArtworkChanged -= OnArtworkChanged;
            old.PickCustomArt = null;
            old.BrowseArt = null;
        }
        _spotVm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnSpotlightVmChanged;
        vm.Spotlight.Changed += OnSpotlightChoicesChanged;
        vm.Components.CollectionChanged += OnSpotlightComponentsChanged;
        vm.Games.CollectionChanged += OnSpotlightGamesChanged;
        vm.TopBar.PropertyChanged += OnSpotlightUpdatesChanged;
        vm.ArtworkChanged += OnArtworkChanged;
        vm.PickCustomArt = PickCustomArtAsync;
        vm.BrowseArt = (game, kind) =>
        {
            _pickKind = kind;
            if (!ReferenceEquals(vm.SelectedGame, game)) vm.SelectedGame = game;
            vm.Layout.ClosePanel();
            vm.Layout.OpenPanel(SpotlightPresenter.ArtworkPickerKey);
        };
    }

    private void OnSpotlightVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_spotOn || _spotVm is not { } vm) return;
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.SelectedGame):
                RefreshHero();
                RefreshSpotlightTiles();
                if (_panelCog is not null) CloseSpotlightCog();
                if (vm.Layout.PanelKey is { } key && IsBuiltPanel(key)) ShowSpotlightPanel(key, rebuild: true);
                break;
            case nameof(MainWindowViewModel.LibraryMainFilterIndex):
            case nameof(MainWindowViewModel.GameCountText):
                RefreshSpotlightSwitch();
                break;
        }
    }

    private void OnSpotlightChoicesChanged()
    {
        if (!_spotOn) return;
        ApplyHeroHeight();
        ApplyShelfPlacement();
        ApplyCoverSize(redecode: true);
    }

    private void OnSpotlightComponentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_spotOn) Dispatcher.UIThread.Post(RefreshSpotlightTiles, DispatcherPriority.Background);
    }

    private void OnSpotlightGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_spotOn) Dispatcher.UIThread.Post(UpdateShelfCentering, DispatcherPriority.Background);
    }

    private void OnSpotlightUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_spotOn || e.PropertyName is not (null or "" or nameof(TopBarPresenter.UpdateCount))) return;
        foreach (var tile in _shelfTiles) ApplyTileUpdate(tile);
    }

    private void OnArtworkChanged(DetectedGame game, ArtworkKind kind)
    {
        if (kind == ArtworkKind.Hero)
        {
            if (ReferenceEquals(_spotVm?.SelectedGame, game)) ReloadHero();
            return;
        }
        foreach (var tile in _shelfTiles.Where(t => ReferenceEquals(t.Row?.Game, game)).ToList())
        {
            tile.SetCover(null);
            LoadShelfCover(tile);
        }
    }

    // ---- the window's arrangement -----------------------------------------------------------------------------------

    /// <summary>
    /// Spotlight's part of the window's arrangement (from <see cref="ApplyLibraryLayout"/>): the strip over the full width
    /// (above or below the page) instead of the game list. False outside Spotlight (the game list's own rules apply).
    /// </summary>
    private bool ApplySpotlightLibraryLayout()
    {
        var on = _spotVm?.Layout.IsSpotlight == true;
        var main = this.FindControl<Grid>("MainContentGrid");
        var sidebar = this.FindControl<Border>("LibrarySidebar");
        var strip = this.FindControl<Border>("SpotlightStrip");
        var scroller = this.FindControl<ScrollViewer>("DetailScroller");
        if (main is null || sidebar is null || strip is null || scroller is null) return false;
        if (_gameListGrip is { } listGrip) listGrip.IsVisible = !on;
        strip.IsVisible = on;
        if (!on)
        {
            if (Grid.GetRowSpan(scroller) != 2)
            {
                main.RowDefinitions = new RowDefinitions("Auto,*");
                Grid.SetRow(scroller, 0);
                Grid.SetRowSpan(scroller, 2);
            }
            return false;
        }
        _libraryOverlayOpen = false;
        IsLibraryNarrow = false;
        _libraryLayoutKey = "spotlight";
        main.ColumnDefinitions = new ColumnDefinitions("0,*");
        sidebar.IsVisible = false;
        if (this.FindControl<Button>("LibraryToggleButton") is { } toggle) toggle.IsVisible = false;
        if (this.FindControl<Border>("LibraryBackdrop") is { } backdrop) backdrop.IsVisible = false;
        if (this.FindControl<Border>("StatusBarBackdrop") is { } statusBackdrop) statusBackdrop.IsVisible = false;
        ApplyOverlayFocusScope(false);
        ApplyShelfPlacement();
        return true;
    }

    /// <summary>Spotlight's part of <see cref="ApplyGameLayout"/>: the banner, the tiles, the side panel.</summary>
    private void ApplySpotlightLayout(MainWindowViewModel vm)
    {
        var on = vm.Layout.IsSpotlight;
        var changed = on != _spotOn;
        _spotOn = on;
        var tabsChrome = vm.Layout.UsesTabsChrome;
        SetVisible("SpotlightHero", tabsChrome);
        SetVisible("SpotlightBody", on);
        SetVisible("SpotlightScreenshots", on);
        foreach (var name in new[] { "SpotlightHeroArt", "SpotlightHeroFadeBottom", "SpotlightHeroFadeSide", "SpotlightHeroGrip" }) SetVisible(name, on);
        if (this.FindControl<Border>("SpotlightHero") is { } hero) hero.Classes.Set("spot", on);
        if (changed)
        {
            _libraryLayoutKey = null;
            ApplyLibraryLayout();
            if (!on)
            {
                _spotLoads?.Cancel();
                _spotLoads = null;
                _heroGame = null;
                ReturnSpotlightPanelContent();
                SetVisible("SpotlightPanelOverlay", false);
                if (this.FindControl<ListBox>("GameLibraryList") is { SelectedItem: { } selected } list) list.ScrollIntoView(selected);
            }
            else
            {
                vm.PrefetchArtworkNames();
                ApplyHeroFades();
                ApplyCoverSize(redecode: false);
                RefreshSpotlightSwitch();
            }
        }
        if (!on) return;
        ApplyHeroHeight();
        RefreshHero();
        RefreshSpotlightTiles();
        foreach (var tile in _shelfTiles.ToList()) { tile.Refresh(); ApplyTileUpdate(tile); if (!tile.HasCover) LoadShelfCover(tile); }
        SyncSpotlightPanel(vm);
    }

    private void RefreshSpotlightSwitch()
    {
        if (_spotVm is not { } vm || this.FindControl<SegmentedSwitch>("SpotlightMainFilter") is not { } mainFilter) return;
        mainFilter.Items = MainWindowViewModel.LibraryMainFilters
            .Select(f => new SegmentItem(T(f.LabelKey), TooltipSweep.FilterTipKey(f.Mode) is { } tip ? T(tip) : null)).ToArray();
        mainFilter.SelectedIndex = vm.LibraryMainFilterIndex;
    }

    /// <summary>The strip above or below the page (rows mirrored at the bottom: covers next to the page), its background.</summary>
    private void ApplyShelfPlacement()
    {
        if (_spotVm is not { Layout.IsSpotlight: true } vm) return;
        var bottom = vm.Spotlight.ShelfAtBottom;
        var strip = this.FindControl<Border>("SpotlightStrip");
        var scroller = this.FindControl<ScrollViewer>("DetailScroller");
        var main = this.FindControl<Grid>("MainContentGrid");
        if (strip is null || scroller is null || main is null) return;
        main.RowDefinitions = bottom ? new RowDefinitions("*,Auto") : new RowDefinitions("Auto,*");
        Grid.SetRow(strip, bottom ? 1 : 0);
        Grid.SetRow(scroller, bottom ? 0 : 1);
        Grid.SetRowSpan(scroller, 1);
        Grid.SetColumn(scroller, 1);
        strip.Classes.Set("bottom", bottom);
        strip.Classes.Set("plain", vm.Spotlight.ShelfPlain);
        if (this.FindControl<Grid>("SpotlightSearchRow") is { } search) Grid.SetRow(search, bottom ? 1 : 0);
        if (this.FindControl<Border>("SpotlightFilterBar") is { } filters) Grid.SetRow(filters, bottom ? 2 : 1);
        foreach (var name in new[] { "SpotlightShelf", "SpotlightEmptyText", "SpotlightCoverGrip" })
            if (this.FindControl<Control>(name) is { } control) Grid.SetRow(control, bottom ? 0 : 2);
        if (this.FindControl<Border>("SpotlightCoverGrip") is { } grip)
        {
            // The grip sits on the strip's edge facing the page, half outside so it stays off the scroll bar.
            grip.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            grip.Margin = bottom ? new Thickness(0, -5, 0, 0) : new Thickness(0, 0, 0, -5);
        }
        if (this.FindControl<ListBox>("SpotlightShelf") is { } shelf)
            shelf.Padding = new Thickness(shelf.Padding.Left, bottom ? 12 : 4, shelf.Padding.Right, bottom ? 2 : 10);
    }

    // ---- the strip's ⚙ and Filters -----------------------------------------------------------------------------------

    private void SpotlightShelfOptions_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || _spotVm is not { } vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, FlyoutPresenterClasses = { "navy" } };
        menu.Items.Add(ToggleItem("SpotlightCoversBelow", T("Spot.CoversBelow"), T("Spot.CoversBelowTip"), vm.Spotlight.ShelfAtBottom,
            on => vm.Spotlight.SetShelfAtBottom(on)));
        menu.Items.Add(ToggleItem("SpotlightNoBackground", T("Spot.NoBackground"), T("Spot.NoBackgroundTip"), vm.Spotlight.ShelfPlain,
            on => vm.Spotlight.SetShelfPlain(on)));
        menu.ShowAt(anchor);

        MenuItem ToggleItem(string name, string header, string tip, bool on, Func<bool, string?> set)
        {
            var item = new MenuItem { Name = name, Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            ToolTip.SetTip(item, tip);
            item.Click += (_, _) =>
            {
                if (set(!on) is { } refused) vm.StatusText = refused;
                // Keep the ⚙ focused (it may have moved) so a keyboard or controller user stays on it.
                Dispatcher.UIThread.Post(() => anchor.Focus(), DispatcherPriority.Loaded);
            };
            return item;
        }
    }

    private void SpotlightFilters_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton toggle) SetVisible("SpotlightFilterBar", toggle.IsChecked == true);
    }

    // ---- covers ----------------------------------------------------------------------------------------------------

    internal void RegisterShelfTile(ShelfTile tile, bool attached)
    {
        if (attached) _shelfTiles.Add(tile);
        else _shelfTiles.Remove(tile);
    }

    private void ApplyTileUpdate(ShelfTile tile)
        => tile.SetUpdate(tile.Row is { } row && _spotVm?.GameHasUpdate(row.Game) == true);

    /// <summary>Shows a tile's cover: the game's own, else Steam's (from disk, or downloaded while Spotlight shows), else its name.</summary>
    internal void LoadShelfCover(ShelfTile tile)
    {
        ApplyTileUpdate(tile);
        if (!_spotOn || _spotVm is not { Artwork: { } artwork } vm || tile.Row is not { } row) { tile.SetCover(null); return; }
        var game = row.Game;
        if (artwork.TryGetCustom(MainWindowViewModel.ArtKey(game), ArtworkKind.Cover) is { } custom) { ShowCover(tile, game, custom); return; }
        if (vm.ArtworkAppId(game) is not int appId) { tile.SetCover(null); return; }
        if (artwork.TryGetCached(appId, ArtworkKind.Cover) is { } cached) { ShowCover(tile, game, cached); return; }
        tile.SetCover(null);
        var token = SpotlightLoads();
        _ = Task.Run(async () =>
        {
            var path = await artwork.GetAsync(appId, ArtworkKind.Cover, token).ConfigureAwait(false);
            if (path is null || token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(tile.Row?.Game, game)) ShowCover(tile, game, path); });
        }, CancellationToken.None);
    }

    private CancellationToken SpotlightLoads() => (_spotLoads ??= new CancellationTokenSource()).Token;

    private void ShowCover(ShelfTile tile, DetectedGame game, string path)
    {
        if (_coverBitmaps.TryGetValue(path, out var cached)) { tile.SetCover(cached); return; }
        var width = _coverDecodeWidth = CoverDecodeWidth;
        _ = Task.Run(() =>
        {
            var bitmap = DecodeImage(path, width);
            Dispatcher.UIThread.Post(() =>
            {
                if (bitmap is null) return;
                _coverBitmaps[path] = bitmap;
                if (ReferenceEquals(tile.Row?.Game, game)) tile.SetCover(bitmap);
            });
        });
    }

    /// <summary>Covers are decoded at twice the tile width, in 80 px steps (decoded again only when a drag ends).</summary>
    private int CoverDecodeWidth => (int)(Math.Ceiling(ShelfTileWidth * 2 / CoverDecodeStep) * CoverDecodeStep);

    private static Bitmap? DecodeImage(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            AppLog.Warn("artwork", $"{path} not shown", ex);
            return null;
        }
    }

    /// <summary>Resizes the tiles and the strip; with <paramref name="redecode"/> the covers are decoded again at the new size.</summary>
    private void ApplyCoverSize(bool redecode)
    {
        if (this.FindControl<ListBox>("SpotlightShelf") is { } shelf) shelf.Height = ShelfCoverHeight + 58;
        foreach (var tile in _shelfTiles) tile.ApplySize(ShelfTileWidth, ShelfCoverHeight);
        UpdateShelfCentering();
        if (!redecode || CoverDecodeWidth == _coverDecodeWidth) return;
        _coverBitmaps.Clear();
        foreach (var tile in _shelfTiles.ToList()) LoadShelfCover(tile);
    }

    /// <summary>The covers centre when they all fit (whatever the filter or collection); a longer row starts at the left.</summary>
    private void UpdateShelfCentering()
    {
        if (!_spotOn || _spotVm is not { } vm || this.FindControl<ListBox>("SpotlightShelf") is not { } shelf) return;
        var side = SpotlightPresenter.ShelfSidePadding(vm.Games.Count, ShelfTileWidth, shelf.Bounds.Width);
        if (Math.Abs(shelf.Padding.Left - side) > 0.5) shelf.Padding = new Thickness(side, shelf.Padding.Top, side, shelf.Padding.Bottom);
    }

    // The mouse wheel scrolls the covers sideways (they have no vertical scrolling).
    private void Shelf_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || sender is not ListBox shelf || ShelfScroller(shelf) is not { } scroller) return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;
        scroller.Offset = new Vector(Math.Clamp(scroller.Offset.X - delta * 90, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)), 0);
        e.Handled = true;
    }

    private static ScrollViewer? ShelfScroller(ListBox shelf) => shelf.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    // Drag the covers to scroll (upstream 2.9.2): past 6 px a press becomes a drag; a plain click still picks the game.
    private void Shelf_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox shelf || e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(shelf).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null) return;
        if (ShelfScroller(shelf) is not { } scroller) return;
        _shelfDragPointer = e.Pointer;
        _shelfDragging = false;
        _shelfDragStart = e.GetPosition(this);
        _shelfDragOffset = scroller.Offset.X;
        e.Pointer.Capture(shelf);
        e.Handled = true;
    }

    private void Shelf_Moved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_shelfDragPointer, e.Pointer) || sender is not ListBox shelf || ShelfScroller(shelf) is not { } scroller) return;
        var dx = (e.GetPosition(this).X - _shelfDragStart.X) / _uiScale;
        if (!_shelfDragging && Math.Abs(dx) < 6) return;
        _shelfDragging = true;
        scroller.Offset = new Vector(Math.Clamp(_shelfDragOffset - dx, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)), 0);
        e.Handled = true;
    }

    private void Shelf_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_shelfDragPointer, e.Pointer) || sender is not ListBox shelf) return;
        _shelfDragPointer = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (_shelfDragging) { _shelfDragging = false; return; }
        // A plain click: the cover under the pointer picks its game, as the game list does.
        if (shelf.InputHitTest(e.GetPosition(shelf)) is Visual hit && hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item)
        {
            shelf.SelectedItem = item.DataContext;
            item.Focus(NavigationMethod.Pointer);
        }
    }

    // ---- the banner ------------------------------------------------------------------------------------------------

    private void ApplyHeroHeight()
    {
        if (this.FindControl<Grid>("SpotlightHeroLayers") is not { } layers) return;
        layers.MinHeight = _spotOn && _spotVm is { } vm ? vm.Spotlight.HeroHeight(layers.Bounds.Width) : 0;
    }

    private void ApplyHeroFades()
    {
        if (this.FindControl<Border>("SpotlightHeroFadeBottom") is { } bottom)
            bottom.Background = ThemeResources.Fade("RhiSpotlightFadeBrush", vertical: true, (0.15, 0), (0.6, 0.45), (0.95, 1));
        if (this.FindControl<Border>("SpotlightHeroFadeSide") is { } side)
            side.Background = ThemeResources.Fade("RhiSpotlightFadeBrush", vertical: false, (0.7, 0), (0.25, 0.65), (0, 1));
    }

    private void RefreshHero()
    {
        if (!_spotOn || _spotVm is not { } vm) return;
        var game = vm.SelectedGame;
        if (ReferenceEquals(game, _heroGame)) return;
        _heroGame = game;
        SetHero(null);
        if (game is null || vm.Artwork is not { } artwork) return;
        if (artwork.TryGetCustom(MainWindowViewModel.ArtKey(game), ArtworkKind.Hero) is { } custom) { ShowHero(game, custom); return; }
        if (vm.ArtworkAppId(game) is not int appId) return;
        if (artwork.TryGetCached(appId, ArtworkKind.Hero) is { } cached) { ShowHero(game, cached); return; }
        var token = SpotlightLoads();
        _ = Task.Run(async () =>
        {
            var path = await artwork.GetAsync(appId, ArtworkKind.Hero, token).ConfigureAwait(false);
            if (path is null || token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_heroGame, game)) ShowHero(game, path); });
        }, CancellationToken.None);
    }

    /// <summary>Loads the selected game's banner again (its custom art changed).</summary>
    private void ReloadHero()
    {
        _heroGame = null;
        RefreshHero();
    }

    private void ShowHero(DetectedGame game, string path)
        => _ = Task.Run(() =>
        {
            var bitmap = DecodeImage(path, 1920);
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_heroGame, game)) SetHero(bitmap); });
        });

    private void SetHero(Bitmap? bitmap)
    {
        if (this.FindControl<Border>("SpotlightHeroArt") is not { } art) return;
        // A brush, not an Image: it fills and crops the banner while staying centred (upstream).
        art.Background = bitmap is null ? null : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top };
        art.Transitions ??= [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) }];
        art.Opacity = bitmap is null ? 0 : 1;
    }

    private void SpotlightHero_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Right-click on the banner: the game menu (Artwork › Set custom banner, upstream).
        if (!_spotOn || _spotVm is not { SelectedGameRow: { } row } vm || sender is not Control anchor) return;
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        e.Handled = true;
        ShowCardMenu(vm, row, anchor, atPointer: e.TryGetPosition(anchor, out _));
    }

    /// <summary>The banner's bottom edge (its height) and the strip's page-side edge (the cover size): drag, double-click resets.</summary>
    private void HookSpotlightGrip(Border grip, bool hero)
    {
        double startY = 0, startValue = 0, live = 0;
        var dragging = false;
        Control? Reference() => _uiScaleHost?.Child ?? this;
        grip.PointerPressed += (_, e) =>
        {
            if (_spotVm is not { } vm || Reference() is not { } reference || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            startY = e.GetPosition(reference).Y;
            startValue = live = hero ? (this.FindControl<Grid>("SpotlightHeroLayers")?.Bounds.Height ?? 0) : vm.Spotlight.CoverHeight;
            dragging = true;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging || _spotVm is not { } vm || Reference() is not { } reference) return;
            var dy = e.GetPosition(reference).Y - startY;
            if (hero)
            {
                live = SpotlightPresenter.DraggedHeroHeight(startValue, dy);
                if (this.FindControl<Grid>("SpotlightHeroLayers") is { } layers) layers.MinHeight = live;
            }
            else
            {
                live = vm.Spotlight.CoverScaleFromDrag(startValue, dy);
                if (this.FindControl<ListBox>("SpotlightShelf") is { } shelf) shelf.Height = Math.Round(SpotlightPresenter.BaseCoverHeight * live) + 58;
                foreach (var tile in _shelfTiles) tile.ApplySize(Math.Round(SpotlightPresenter.BaseTileWidth * live), Math.Round(SpotlightPresenter.BaseCoverHeight * live));
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (_spotVm is not { } vm) return;
            var refused = hero ? vm.Spotlight.SetHeroHeight(live) : vm.Spotlight.SetCoverScale(live);
            if (refused is not null) vm.StatusText = refused;
            OnSpotlightChoicesChanged();
        };
        grip.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            dragging = false;
            if (_spotVm is not { } vm) return;
            var refused = hero ? vm.Spotlight.SetHeroHeight(0) : vm.Spotlight.SetCoverScale(1);
            if (refused is not null) vm.StatusText = refused;
            OnSpotlightChoicesChanged();
        };
    }

    // ---- What's installed / Tune this game ----------------------------------------------------------------------------

    private void RefreshSpotlightTiles()
    {
        if (!_spotOn || _spotVm is not { } vm) return;
        if (this.FindControl<AutoFitPanel>("SpotlightStatusTiles") is { } status)
        {
            var tiles = SpotlightPresenter.StatusTiles(vm.Components.Where(r => r.Kind is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline)));
            while (status.Children.Count > tiles.Count) status.Children.RemoveAt(status.Children.Count - 1);
            while (status.Children.Count < tiles.Count) status.Children.Add(NewStatusTile(vm));
            for (var i = 0; i < tiles.Count; i++) FillStatusTile((Button)status.Children[i], tiles[i]);
        }
        if (this.FindControl<AutoFitPanel>("SpotlightTuneTiles") is { } tune)
        {
            var sections = SpotlightPresenter.TuneSections;
            if (tune.Children.Count != sections.Count)
            {
                tune.Children.Clear();
                foreach (var key in sections)
                {
                    var section = key;
                    var tile = new Button
                    {
                        Name = "Tune" + section, Classes = { "glanceTile" }, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                        Content = new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Classes = { "glanceTitle" } },
                                new TextBlock { Classes = { "glanceSummary" }, TextWrapping = TextWrapping.Wrap, MaxLines = 3 },
                            },
                        },
                    };
                    tile.Click += (_, _) => vm.Layout.ShowSection(section);
                    tune.Children.Add(tile);
                }
            }
            for (var i = 0; i < sections.Count; i++)
            {
                var tile = (Button)tune.Children[i];
                var parts = ((StackPanel)tile.Content!).Children;
                ((TextBlock)parts[0]).Text = GamePageSections.Title(sections[i]);
                ((TextBlock)parts[1]).Text = vm.GlanceText(sections[i]);
                ToolTip.SetTip(tile, TF("Spot.OpenSectionTip", GamePageSections.Title(sections[i])));
            }
        }
    }

    private static Button NewStatusTile(MainWindowViewModel vm)
    {
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Classes = { "statusDot" } };
        var name = new TextBlock { Classes = { "glanceTitle" }, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Classes = { "statusLine" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, status } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        var tile = new Button
        {
            Classes = { "glanceTile", "statusTile" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = grid,
        };
        ToolTip.SetTip(tile, T("Spot.ManageComponents"));
        tile.Click += (_, _) => vm.Layout.OpenPanel(GamePageSections.Overview);
        return tile;
    }

    private static void FillStatusTile(Button tile, SpotlightStatusTile state)
    {
        var grid = (Grid)tile.Content!;
        var dot = (Ellipse)grid.Children[0];
        var text = ((StackPanel)grid.Children[1]).Children;
        ((TextBlock)text[0]).Text = state.Name;
        ((TextBlock)text[1]).Text = state.Status;
        foreach (var tone in Enum.GetValues<StatusTone>()) dot.Classes.Set(tone.ToString().ToLowerInvariant(), tone == state.Tone);
        tile.Tag = state.Name;
    }

    // ---- the side panel ----------------------------------------------------------------------------------------------

    private static bool IsBuiltPanel(string key)
        => key is SpotlightPresenter.ScreenshotsKey or SpotlightPresenter.AchievementsKey or SpotlightPresenter.GameSettingsKey or SpotlightPresenter.ArtworkPickerKey;

    private void SyncSpotlightPanel(MainWindowViewModel vm)
    {
        var key = vm.Layout.PanelKey;
        if (key == _panelShown) return;
        ShowSpotlightPanel(key, rebuild: false);
    }

    /// <summary>Puts <paramref name="key"/>'s content in the side panel (null closes it); a built panel is built again.</summary>
    private void ShowSpotlightPanel(string? key, bool rebuild)
    {
        if (_spotVm is not { } vm || this.FindControl<StackPanel>("SpotlightPanelHost") is not { } host) return;
        if (_panelCog is not null) CloseSpotlightCog();
        var wasOpen = _panelShown is not null;
        ReturnSpotlightPanelContent();
        if (key is null)
        {
            SetVisible("SpotlightPanelOverlay", false);
            return;
        }
        Control? content = key switch
        {
            SpotlightPresenter.ScreenshotsKey => BuildScreenshotsPanel(vm),
            SpotlightPresenter.GameSettingsKey when vm.SelectedGame is not null => GameSettingsView.Build(vm, () => vm.Layout.ClosePanel()).Root,
            SpotlightPresenter.ArtworkPickerKey => BuildArtworkPickerPanel(vm),
            SpotlightPresenter.AchievementsKey => BuildAchievementsPanel(vm),
            GamePageSections.Overview => FindSectionContainer(GamePageSections.Components),
            _ => FindSectionContainer(key),
        };
        if (content is null)
        {
            vm.Layout.ClosePanel();
            return;
        }
        if (content.Parent is Panel parent) parent.Children.Remove(content);
        host.Children.Add(content);
        content.IsVisible = true;
        _panelShown = key;
        _panelContent = content;
        if (this.FindControl<TextBlock>("SpotlightPanelTitle") is { } title) title.Text = SpotlightPresenter.PanelTitle(key);
        if (this.FindControl<ScrollViewer>("SpotlightPanelScroll") is { } scroll) scroll.Offset = default;
        SetVisible("SpotlightPanelOverlay", true);
        if (!wasOpen && !rebuild) SlideInSheet();
    }

    private void SlideInSheet()
    {
        if (this.FindControl<Border>("SpotlightPanelSheet") is not { } sheet) return;
        sheet.Transitions = null;
        sheet.RenderTransform = TransformOperations.Parse("translateX(80px)");
        sheet.Opacity = 0.4;
        sheet.Transitions =
        [
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180) },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
        ];
        Dispatcher.UIThread.Post(() =>
        {
            sheet.RenderTransform = TransformOperations.Parse("translateX(0px)");
            sheet.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    /// <summary>Takes the panel's content out; a section goes back on the (hidden) page in its saved order.</summary>
    private void ReturnSpotlightPanelContent()
    {
        var key = _panelShown;
        var content = _panelContent;
        _panelShown = null;
        _panelContent = null;
        if (key is null || content is null) return;
        if (content.Parent is Panel parent) parent.Children.Remove(content);
        _shotsSeq++; // a screenshot listing still running is dropped
        _pickSeq++;
        if (IsBuiltPanel(key)) return;
        ApplyDetailSectionLayout(); // puts the section back in Classic+'s order; ApplyGameLayout hides it again
        RefreshSpotlightTiles(); // settings changed in the panel: the Tune tiles follow
    }

    private void SpotlightScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _spotVm?.Layout.ClosePanel();
    }

    private void SpotlightPanelClose_Click(object? sender, RoutedEventArgs e) => _spotVm?.Layout.ClosePanel();

    private void SpotlightPanelBack_Click(object? sender, RoutedEventArgs e) => CloseSpotlightCog();

    private void SpotlightScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        if (_spotVm is not { } vm) return;
        if (vm.Layout.PanelKey == SpotlightPresenter.ScreenshotsKey) ShowSpotlightPanel(SpotlightPresenter.ScreenshotsKey, rebuild: true);
        else vm.Layout.OpenPanel(SpotlightPresenter.ScreenshotsKey);
    }

    /// <summary>
    /// Spotlight (upstream DetailLayoutHost.CogHosts): a ⚙ clicked inside the side panel opens in it, over the section, with
    /// a back button to the section. False for a ⚙ outside the panel (its window opens, as before).
    /// </summary>
    private bool TryShowCogInSpotlightPanel(Window dialog, out Task shown)
    {
        shown = Task.CompletedTask;
        if (dialog is not CogHostWindow cog || !_spotOn || _panelShown is null || cog.Content is not Control frame) return false;
        if (this.FindControl<Border>("SpotlightPanelSheet") is not { } sheet || this.FindControl<Border>("SpotlightPanelCogHost") is not { } cogHost) return false;
        var source = _lastPressed;
        if (source is null || !sheet.IsVisualAncestorOf(source)) return false;
        var titleBefore = _panelCog?.TitleBefore ?? this.FindControl<TextBlock>("SpotlightPanelTitle")?.Text ?? "";
        if (_panelCog is not null) CloseSpotlightCog();
        cog.Content = null;
        cogHost.Child = frame;
        var done = new TaskCompletionSource();
        _panelCog = new SpotlightCog(cog, frame, titleBefore, done);
        cog.InlineClose = _ => CloseSpotlightCog();
        SetVisible("SpotlightPanelScroll", false);
        SetVisible("SpotlightPanelCogScroll", true);
        if (this.FindControl<Button>("SpotlightPanelBack") is { } back)
        {
            back.Content = "‹ " + titleBefore;
            back.IsVisible = true;
        }
        if (this.FindControl<TextBlock>("SpotlightPanelTitle") is { } title) title.Text = cog.Title;
        if (this.FindControl<ScrollViewer>("SpotlightPanelCogScroll") is { } scroll) scroll.Offset = default;
        cog.RaiseOpenedInline();
        shown = done.Task;
        return true;
    }

    private void CloseSpotlightCog()
    {
        if (_panelCog is not { } open) return;
        _panelCog = null;
        if (this.FindControl<Border>("SpotlightPanelCogHost") is { } cogHost) cogHost.Child = null;
        SetVisible("SpotlightPanelCogScroll", false);
        SetVisible("SpotlightPanelScroll", true);
        SetVisible("SpotlightPanelBack", false);
        if (this.FindControl<TextBlock>("SpotlightPanelTitle") is { } title) title.Text = open.TitleBefore;
        open.Window.InlineClose = null;
        try { open.Window.RaiseClosedInline(); }
        finally { open.Done.TrySetResult(); }
    }
}
