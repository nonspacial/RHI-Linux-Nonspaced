using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.0 Spotlight shelf tile, mockup D): one game on Spotlight's cover row — the cover (the
/// game's own, else Steam's, else its name on a plain tile), a purple "Update" tag and outline while RHI knows of an
/// update for it, and the name under it (greyed while the game is hidden). The selected cover's outline is the accent
/// (styles in MainWindow.axaml). The window loads the cover (<see cref="MainWindow"/>'s Spotlight code) and sizes the tile.
/// </summary>
public sealed class ShelfTile : StackPanel
{
    private readonly Border _cover;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _fallback = new()
    {
        Classes = { "shelfFallback" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
    };
    private readonly Border _badge;
    private readonly TextBlock _name = new() { Classes = { "shelfName" }, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0) };
    private MainWindow? _window;

    public ShelfTile()
    {
        Spacing = 8;
        _badge = new Border
        {
            Classes = { "shelfBadge" }, Margin = new Thickness(6), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false,
            Child = new TextBlock { Text = UiLocalizationService.Text("Spot.UpdateTag"), FontSize = 11, FontWeight = FontWeight.SemiBold },
        };
        _cover = new Border
        {
            Classes = { "shelfCover" }, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), ClipToBounds = true,
            Child = new Grid { Children = { _fallback, _image, _badge } },
        };
        Children.Add(_cover);
        Children.Add(_name);
    }

    /// <summary>The game this tile shows.</summary>
    public GameRowViewModel? Row => DataContext as GameRowViewModel;

    /// <summary>Whether a cover image is shown (the window retries tiles without one).</summary>
    public bool HasCover => _image.Source is not null;

    /// <summary>The cover border (the harness and the tests read its classes).</summary>
    internal Border Cover => _cover;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Refresh();
        _window?.LoadShelfCover(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as MainWindow;
        _window?.RegisterShelfTile(this, true);
        Refresh();
        _window?.LoadShelfCover(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _window?.RegisterShelfTile(this, false);
        _window = null;
    }

    /// <summary>Name, hidden look, size and update state from the game and the window.</summary>
    public void Refresh()
    {
        var row = Row;
        _name.Text = row?.Name ?? "";
        _fallback.Text = row?.Name ?? "";
        _name.Classes.Set("hidden", row?.IsHidden == true);
        ToolTip.SetTip(this, row is null ? null : HasUpdate ? UiLocalizationService.Format("Spot.CoverUpdateTip", row.Name) : row.Name);
        if (_window is { } window) ApplySize(window.ShelfTileWidth, window.ShelfCoverHeight);
    }

    private bool HasUpdate => _cover.Classes.Contains("update");

    /// <summary>The tile's width and the cover's height (the cover size, upstream 120 × 180 at size 1).</summary>
    public void ApplySize(double width, double coverHeight)
    {
        Width = width;
        _cover.Height = coverHeight;
    }

    /// <summary>Shows the purple Update tag and outline (or not).</summary>
    public void SetUpdate(bool update)
    {
        _cover.Classes.Set("update", update);
        _badge.IsVisible = update;
        if (Row is { } row) ToolTip.SetTip(this, update ? UiLocalizationService.Format("Spot.CoverUpdateTip", row.Name) : row.Name);
    }

    /// <summary>Shows a decoded cover, or the name on the plain tile (null).</summary>
    public void SetCover(Bitmap? bitmap)
    {
        _image.Source = bitmap;
        _fallback.IsVisible = bitmap is null;
    }
}
