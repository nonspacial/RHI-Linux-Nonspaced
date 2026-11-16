using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Artwork;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#305): Spotlight's built side panels — Screenshots (upstream 2.9.3 DetailLayoutHost.Screenshots) and
/// Artwork (SteamGridDB, upstream 2.9.5 beta DetailLayoutHost.ArtworkPicker) — and Artwork › Set custom cover / banner's
/// file picker.
/// </summary>
public sealed partial class MainWindow
{
    private const int ShotPage = 30;
    private const double ShotThumbWidth = 240;

    private int _shotsSeq;
    private int _pickSeq;
    private ArtworkKind _pickKind = ArtworkKind.Cover;

    private static TextBlock PanelText(string text, string cls = "spotMuted") => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Classes = { cls },
    };

    private static Border PanelCard(Control child) => new() { Classes = { "spotCard" }, Child = child };

    // ---- Screenshots -----------------------------------------------------------------------------------------------

    /// <summary>The game's ReShade screenshots, newest first (filled once the folder has been read).</summary>
    private Control BuildScreenshotsPanel(MainWindowViewModel vm)
    {
        var root = new StackPanel { Name = "ScreenshotsPanel", Spacing = 14 };
        if (vm.SelectedGame is not { } game) return root;
        root.Children.Add(PanelText(T("Shots.Looking")));
        var seq = ++_shotsSeq;
        _ = Task.Run(() =>
        {
            ScreenshotListing listing;
            try { listing = vm.FindScreenshots(game); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { listing = new ScreenshotListing(null, []); }
            Dispatcher.UIThread.Post(() => { if (seq == _shotsSeq) RenderShots(root, vm, game, listing); });
        });
        return root;
    }

    private void RenderShots(StackPanel root, MainWindowViewModel vm, DetectedGame game, ScreenshotListing listing)
    {
        root.Children.Clear();
        var shots = listing.Shots;
        var head = new StackPanel { Spacing = 8 };
        head.Children.Add(PanelText(shots.Count == 0 ? T("Shots.None") : shots.Count == 1 ? T("Shots.One") : TF("Shots.Many", shots.Count), "spotStrong"));
        if (listing.Folder is { } folder)
        {
            head.Children.Add(PanelText(folder));
            var open = new Button { Classes = { "rhi" }, Content = T("Ui.OpenFolder"), HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                if (!LinuxDesktopOpener.TryOpenDirectory(folder, out var error)) vm.StatusText = UiLocalizationService.Present(error ?? "");
            };
            head.Children.Add(open);
        }
        head.Children.Add(PanelText(shots.Count > 0 ? T("Shots.Hint") : listing.Folder is null ? T("Shots.HowNoFolder") : T("Shots.How")));
        root.Children.Add(PanelCard(head));
        if (shots.Count == 0) return;
        var grid = new AutoFitPanel { Name = "ScreenshotsGrid", MinColumnWidth = ShotThumbWidth, Spacing = 10 };
        root.Children.Add(grid);
        AddShotThumbs(root, grid, vm, game, shots, 0);
    }

    private void AddShotThumbs(StackPanel root, AutoFitPanel grid, MainWindowViewModel vm, DetectedGame game, IReadOnlyList<FileInfo> shots, int start)
    {
        var aspect = ScreenAspect();
        var end = Math.Min(shots.Count, start + ShotPage);
        for (var i = start; i < end; i++) grid.Children.Add(MakeShotThumb(grid, vm, game, shots[i], aspect));
        if (root.Children.LastOrDefault() is Button { Name: "ShotsMore" } old) root.Children.Remove(old);
        if (end >= shots.Count) return;
        var more = new Button { Name = "ShotsMore", Classes = { "rhi" }, Content = TF("Shots.More", shots.Count - end), HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) => AddShotThumbs(root, grid, vm, game, shots, end);
        root.Children.Add(more);
    }

    /// <summary>Height / width of the screen (screenshots have its shape); 16:9 when unknown.</summary>
    private double ScreenAspect()
        => Screens.ScreenFromWindow(this) is { Bounds: { Width: > 0, Height: > 0 } b } ? (double)b.Height / b.Width : 9.0 / 16;

    private Button MakeShotThumb(AutoFitPanel grid, MainWindowViewModel vm, DetectedGame game, FileInfo file, double aspect)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var fallback = new TextBlock
        {
            Text = TF("Shots.Fallback", file.Extension.TrimStart('.').ToUpperInvariant()), Classes = { "spotMuted" },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        var cell = new Grid { Classes = { "shotCell" }, Height = Math.Round(ShotThumbWidth * aspect), Children = { image, fallback } };
        cell.SizeChanged += (_, e) =>
        {
            var h = Math.Round(e.NewSize.Width * aspect);
            if (e.NewSize.Width > 0 && Math.Abs(cell.Height - h) > 0.5) cell.Height = h;
        };
        _ = Task.Run(() =>
        {
            // HDR .jxr screenshots have no decoder here: the file type shows instead (upstream: some .jxr files).
            var bitmap = file.Extension.Equals(".jxr", StringComparison.OrdinalIgnoreCase) ? null : DecodeImage(file.FullName, 440);
            Dispatcher.UIThread.Post(() => { image.Source = bitmap; fallback.IsVisible = bitmap is null; });
        });
        var thumb = new Button
        {
            Classes = { "shotThumb" }, Content = cell, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(thumb, TF("Shots.ThumbTip", file.Name, file.LastWriteTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
        AutomationProperties.SetName(thumb, TF("Shots.ThumbName", file.LastWriteTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
        thumb.Click += (_, _) => OpenShot(vm, file.FullName);
        thumb.ContextFlyout = ShotMenu(grid, thumb, vm, game, file);
        return thumb;
    }

    private static void OpenShot(MainWindowViewModel vm, string path)
    {
        if (!LinuxDesktopOpener.TryOpenFile(path, out var error)) vm.StatusText = UiLocalizationService.Present(error ?? "");
    }

    private MenuFlyout ShotMenu(AutoFitPanel grid, Button thumb, MainWindowViewModel vm, DetectedGame game, FileInfo file)
    {
        var menu = new MenuFlyout { FlyoutPresenterClasses = { "navy" } };
        void Add(string name, string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Name = name, Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("ShotOpen", T("Shots.Open"), () => OpenShot(vm, file.FullName));
        // HDR .jxr screenshots can't be shown as the banner (upstream).
        var bannerOk = !file.Extension.Equals(".jxr", StringComparison.OrdinalIgnoreCase);
        Add("ShotBanner", bannerOk ? T("Shots.SetBanner") : T("Shots.SetBannerJxr"), () => vm.SetBannerFromScreenshot(game, file.FullName), bannerOk);
        Add("ShotCopy", T("Shots.Copy"), () => RunUiSafe(() => CopyShotAsync(vm, file.FullName)));
        Add("ShotShow", T("Shots.ShowInFolder"), () =>
        {
            if (!LinuxDesktopOpener.TryShowItem(file.FullName, out var error)) vm.StatusText = UiLocalizationService.Present(error ?? "");
        });
        menu.Items.Add(new Separator());
        Add("ShotDelete", T("Shots.Delete"), () =>
        {
            if (vm.TrashScreenshot(file.FullName)) grid.Children.Remove(thumb);
        });
        return menu;
    }

    /// <summary>Copy to clipboard: the picture and the file (so both image editors and file managers can paste it).</summary>
    private async Task CopyShotAsync(MainWindowViewModel vm, string path)
    {
        if (Clipboard is not { } clipboard) { vm.StatusText = T("Win.ClipboardUnavailableSession"); return; }
        var item = new DataTransferItem();
        if (await Task.Run(() => DecodeImage(path, 3840)) is { } bitmap) item.SetBitmap(bitmap);
        if (await StorageProvider.TryGetFileFromPathAsync(path) is { } storageFile) item.SetFile(storageFile);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        vm.StatusText = TF("Shots.Copied", Path.GetFileName(path));
    }

    // ---- Artwork › Set custom cover / banner ------------------------------------------------------------------------

    private static readonly FilePickerFileType ImageFileType = new("Images")
    {
        Patterns = SteamArtworkService.ImageExtensions.Select(e => "*" + e).ToArray(),
        MimeTypes = ["image/jpeg", "image/png", "image/webp", "image/bmp"],
    };

    private async Task PickCustomArtAsync(DetectedGame game, ArtworkKind kind)
    {
        if (_spotVm is not { } vm || !StorageProvider.CanOpen) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = TF(kind == ArtworkKind.Cover ? "Art.PickCoverTitle" : "Art.PickBannerTitle", game.DisplayName()),
            AllowMultiple = false,
            FileTypeFilter = [ImageFileType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (vm.SetCustomArt(game, kind, path))
            vm.StatusText = TF(kind == ArtworkKind.Cover ? "Art.CoverSet" : "Art.BannerSet", game.DisplayName());
    }

    // ---- Artwork (SteamGridDB) ---------------------------------------------------------------------------------------

    /// <summary>
    /// Spotlight › Artwork (game menu › Artwork › Browse covers / Browse banners): Cover | Banner, the game's Steam art as
    /// the first tile (picking it goes back to Steam's), then SteamGridDB's community images page by page. Without an API
    /// key it says where to add one.
    /// </summary>
    private Control BuildArtworkPickerPanel(MainWindowViewModel vm)
    {
        var root = new StackPanel { Name = "ArtworkPickerPanel", Spacing = 14 };
        if (vm.SelectedGame is not { } game) return root;
        var kindSwitch = new SegmentedSwitch
        {
            Name = "ArtworkKindSwitch", HorizontalAlignment = HorizontalAlignment.Left,
            Items = [new SegmentItem(T("Art.Cover"), null), new SegmentItem(T("Art.Banner"), null)],
            SelectedIndex = _pickKind == ArtworkKind.Cover ? 0 : 1,
        };
        var status = PanelText("");
        status.Name = "ArtworkPickerStatus";
        var grid = new WrapPanel { Name = "ArtworkPickerGrid", ItemSpacing = 10, LineSpacing = 10 };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        root.Children.Add(kindSwitch);
        root.Children.Add(status);
        root.Children.Add(footer);
        root.Children.Add(grid);
        kindSwitch.SelectionRequested += index =>
        {
            _pickKind = index == 0 ? ArtworkKind.Cover : ArtworkKind.Hero;
            kindSwitch.SelectedIndex = index;
            LoadPicker(vm, game, grid, status, footer);
        };
        LoadPicker(vm, game, grid, status, footer);
        return root;
    }

    private void LoadPicker(MainWindowViewModel vm, DetectedGame game, WrapPanel grid, TextBlock status, StackPanel footer)
    {
        var seq = ++_pickSeq;
        var kind = _pickKind;
        grid.Children.Clear();
        footer.Children.Clear();
        grid.Children.Add(SteamPickTile(vm, game, kind, status));
        if (vm.SteamGridDb is not { HasKey: true } sgdb)
        {
            status.Text = T("Sgdb.NoKeyHint");
            footer.Children.Add(SettingsButton(T("Sgdb.AddKey")));
            return;
        }
        status.Text = T("Sgdb.Loading");
        LoadPickerPage(vm, sgdb, game, kind, grid, status, footer, seq, 0);
    }

    private void LoadPickerPage(MainWindowViewModel vm, SteamGridDbService sgdb, DetectedGame game, ArtworkKind kind, WrapPanel grid,
        TextBlock status, StackPanel footer, int seq, int page)
    {
        var appId = vm.ArtworkAppId(game);
        var token = SpotlightLoads();
        _ = Task.Run(async () =>
        {
            var result = await sgdb.GetArtAsync(appId, game.DisplayName(), kind, page, token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (seq != _pickSeq) return;
                footer.Children.Clear();
                switch (result.Status)
                {
                    case SteamGridDbStatus.BadKey:
                        status.Text = T("Sgdb.BadKey");
                        footer.Children.Add(SettingsButton(T("Sgdb.OpenSettings")));
                        return;
                    case SteamGridDbStatus.NoKey:
                        status.Text = T("Sgdb.NoKeyHint");
                        footer.Children.Add(SettingsButton(T("Sgdb.AddKey")));
                        return;
                    case SteamGridDbStatus.Offline or SteamGridDbStatus.Error:
                        status.Text = T(result.Status == SteamGridDbStatus.Offline ? "Sgdb.Offline" : "Sgdb.BadReply");
                        var retry = new Button { Classes = { "rhi" }, Content = T("Sgdb.TryAgain") };
                        retry.Click += (_, _) =>
                        {
                            status.Text = T("Sgdb.Loading");
                            footer.Children.Clear();
                            LoadPickerPage(vm, sgdb, game, kind, grid, status, footer, seq, page);
                        };
                        footer.Children.Add(retry);
                        return;
                }
                var chosen = sgdb.ChosenId(MainWindowViewModel.ArtKey(game), kind, vm.Artwork?.TryGetCustom(MainWindowViewModel.ArtKey(game), kind));
                foreach (var art in result.Items) grid.Children.Add(SgdbPickTile(vm, sgdb, game, kind, art, art.Id == chosen, status));
                var what = T(kind == ArtworkKind.Cover ? "Sgdb.Covers" : "Sgdb.Banners");
                status.Text = grid.Children.Count <= 1 ? TF("Sgdb.NoneYet", what) : TF("Sgdb.Intro", what);
                if (!result.MayHaveMore) return;
                var more = new Button { Name = "PickMore", Classes = { "rhi" }, Content = TF("Sgdb.ShowMore", what) };
                more.Click += (_, _) =>
                {
                    more.Content = T("Sgdb.LoadingShort");
                    more.IsEnabled = false;
                    LoadPickerPage(vm, sgdb, game, kind, grid, status, footer, seq, page + 1);
                };
                footer.Children.Add(more);
            });
        }, CancellationToken.None);
    }

    private Button SettingsButton(string text)
    {
        var button = new Button { Classes = { "rhi" }, Content = text };
        button.Click += (_, _) => OpenSettingsAt(SettingsSections.Accounts);
        return button;
    }

    private (Button Tile, Image Image, TextBlock Fallback) PickTile(ArtworkKind kind, string label, bool selected)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var fallback = new TextBlock { Text = label, Classes = { "spotMuted" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        // Fixed sizes: covers 150 × 225 (2:3), banners 340 wide at Steam hero art's shape.
        var (width, height) = kind == ArtworkKind.Cover ? (150.0, 225.0) : (340.0, Math.Round(340 * 1240.0 / 3840.0));
        var cell = new Grid { Classes = { "shotCell" }, Width = width, Height = height, Children = { fallback, image } };
        var tile = new Button
        {
            Classes = { "shotThumb", "pickTile" }, Content = cell, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        tile.Classes.Set("chosen", selected);
        return (tile, image, fallback);
    }

    /// <summary>The "Steam" tile: the game's own Steam art; picking it removes the custom art.</summary>
    private Control SteamPickTile(MainWindowViewModel vm, DetectedGame game, ArtworkKind kind, TextBlock status)
    {
        var custom = vm.Artwork?.TryGetCustom(MainWindowViewModel.ArtKey(game), kind);
        var (tile, image, fallback) = PickTile(kind, T("Sgdb.SteamNoArt"), custom is null);
        tile.Name = "PickSteam";
        ToolTip.SetTip(tile, T(kind == ArtworkKind.Cover ? "Sgdb.SteamCoverTip" : "Sgdb.SteamBannerTip"));
        var label = new Border { Classes = { "pickLabel" }, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6), Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = "Steam", FontSize = 12 } };
        ((Grid)tile.Content!).Children.Add(label);
        if (vm.Artwork is { } artwork && vm.ArtworkAppId(game) is int appId)
        {
            var token = SpotlightLoads();
            _ = Task.Run(async () =>
            {
                var path = await artwork.GetAsync(appId, kind, token).ConfigureAwait(false);
                var bitmap = path is null ? null : DecodeImage(path, kind == ArtworkKind.Cover ? 280 : 640);
                Dispatcher.UIThread.Post(() => { image.Source = bitmap; fallback.IsVisible = bitmap is null; });
            }, CancellationToken.None);
        }
        tile.Click += (_, _) =>
        {
            vm.ResetCustomArt(game, kind);
            status.Text = T(kind == ArtworkKind.Cover ? "Sgdb.UsingSteamCover" : "Sgdb.UsingSteamBanner");
            MarkChosen(tile);
        };
        return tile;
    }

    private Control SgdbPickTile(MainWindowViewModel vm, SteamGridDbService sgdb, DetectedGame game, ArtworkKind kind, SteamGridDbArt art, bool chosen, TextBlock status)
    {
        var (tile, image, fallback) = PickTile(kind, $"{art.Width} × {art.Height}", chosen);
        ToolTip.SetTip(tile, string.IsNullOrEmpty(art.Author) ? $"{art.Width} × {art.Height}" : TF("Sgdb.ByTip", art.Author, art.Width, art.Height));
        var token = SpotlightLoads();
        _ = Task.Run(async () =>
        {
            var path = await sgdb.GetThumbAsync(art, token).ConfigureAwait(false);
            var bitmap = path is null ? null : DecodeImage(path, kind == ArtworkKind.Cover ? 280 : 640);
            Dispatcher.UIThread.Post(() => { image.Source = bitmap; fallback.IsVisible = bitmap is null; });
        }, CancellationToken.None);
        tile.Click += (_, _) => RunUiSafe(async () =>
        {
            var seq = _pickSeq;
            status.Text = T("Sgdb.Downloading");
            var (path, error) = await sgdb.DownloadFullAsync(art, token);
            if (seq != _pickSeq) return;
            if (path is null) { status.Text = UiLocalizationService.Present(error ?? SteamGridDbService.DownloadFailedMessage); return; }
            if (!vm.SetCustomArt(game, kind, path)) { status.Text = vm.StatusText; return; }
            if (vm.Artwork?.TryGetCustom(MainWindowViewModel.ArtKey(game), kind) is { } custom) sgdb.RememberChoice(MainWindowViewModel.ArtKey(game), kind, art.Id, custom);
            status.Text = string.IsNullOrEmpty(art.Author)
                ? T(kind == ArtworkKind.Cover ? "Sgdb.CoverSet" : "Sgdb.BannerSet")
                : TF(kind == ArtworkKind.Cover ? "Sgdb.CoverSetBy" : "Sgdb.BannerSetBy", art.Author);
            MarkChosen(tile);
        });
        return tile;
    }

    private static void MarkChosen(Button tile)
    {
        if (tile.Parent is not Panel grid) return;
        foreach (var other in grid.Children.OfType<Button>()) other.Classes.Set("chosen", ReferenceEquals(other, tile));
    }

    // ---- Achievements (#307) ---------------------------------------------------------------------------------------

    private Control? BuildAchievementsPanel(MainWindowViewModel vm) => BuildAchievementsPanelCore(vm);
}
