using Avalonia;
using Avalonia.Controls;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-a (#304, upstream Controls.AutoFitGrid / mockup A's "auto-fit, minmax(N px, 1fr)"): as many equal columns as
/// fit at <see cref="MinColumnWidth"/>, never more than there are visible children; each row as tall as its tallest child.
/// The Tabs layout's component cards, At a glance tiles and Management tiles, and Classic+'s Management row.
/// w14/layouts-c (upstream 2.9.0 "A component's ⚙ settings open right under it on the page"): one extra control, the
/// <see cref="Inset"/>, can be shown across the whole width right under the line of cards that holds a given card; the lines
/// after it move down. It is the panel's own child (never one of <see cref="Panel.Children"/>, which an ItemsControl owns).
/// </summary>
public sealed class AutoFitPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<AutoFitPanel, double>(nameof(MinColumnWidth), 220);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AutoFitPanel, double>(nameof(Spacing), 12);

    static AutoFitPanel() => AffectsMeasure<AutoFitPanel>(MinColumnWidthProperty, SpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many columns <paramref name="count"/> children get in <paramref name="width"/>.</summary>
    public static int Columns(double width, int count, double minColumnWidth, double spacing)
    {
        if (count <= 0) return 0;
        if (double.IsInfinity(width) || double.IsNaN(width)) return count;
        var fit = (int)Math.Floor((width + spacing) / (Math.Max(1, minColumnWidth) + spacing));
        return Math.Clamp(fit, 1, count);
    }

    private List<Control> Visible() => Children.Where(c => c.IsVisible).ToList();

    private Control? _inset;
    private Control? _insetAfter;

    /// <summary>The control shown across the width under a card's line (a ⚙ opened on the page), or null.</summary>
    public Control? Inset => _inset;

    /// <summary>
    /// Shows <paramref name="inset"/> across the whole width right under the line that holds <paramref name="after"/> (one of
    /// <see cref="Panel.Children"/>); an inset already shown is taken away first. A card that is no longer there (the list was
    /// rebuilt) leaves the inset under the last line.
    /// </summary>
    public void ShowInset(Control inset, Control after)
    {
        ArgumentNullException.ThrowIfNull(inset);
        HideInset();
        _inset = inset;
        _insetAfter = after;
        LogicalChildren.Add(inset);
        VisualChildren.Add(inset);
        InvalidateMeasure();
    }

    /// <summary>Takes the inset away (nothing when none shows).</summary>
    public void HideInset()
    {
        if (_inset is not { } inset) return;
        _inset = null;
        _insetAfter = null;
        VisualChildren.Remove(inset);
        LogicalChildren.Remove(inset);
        InvalidateMeasure();
    }

    /// <summary>
    /// Where the cards and the inset go: <paramref name="heights"/> are the visible cards' heights in order, laid out
    /// <paramref name="columns"/> to a line, each line as tall as its tallest card; with <paramref name="insetAfter"/> ≥ 0 the
    /// inset (<paramref name="insetHeight"/> tall, the whole <paramref name="width"/>) follows the line holding that card, and
    /// with an index past the end it follows the last line. Returns each card's rectangle, the inset's and the total height.
    /// </summary>
    public static (Rect[] Cards, Rect? Inset, double Height) Plan(IReadOnlyList<double> heights, int columns, double columnWidth, double spacing,
        double width, int insetAfter = -1, double insetHeight = 0)
    {
        var cards = new Rect[heights.Count];
        Rect? inset = null;
        if (columns <= 0) return (cards, inset, 0);
        var insetLine = insetAfter < 0 ? -1 : Math.Min(insetAfter, Math.Max(0, heights.Count - 1)) / columns;
        double y = 0;
        var lines = (heights.Count + columns - 1) / columns;
        for (var line = 0; line < lines; line++)
        {
            var start = line * columns;
            var count = Math.Min(columns, heights.Count - start);
            var lineHeight = 0.0;
            for (var i = 0; i < count; i++) lineHeight = Math.Max(lineHeight, heights[start + i]);
            if (y > 0) y += spacing;
            for (var i = 0; i < count; i++) cards[start + i] = new Rect(i * (columnWidth + spacing), y, columnWidth, lineHeight);
            y += lineHeight;
            if (line != insetLine) continue;
            y += spacing;
            inset = new Rect(0, y, width, insetHeight);
            y += insetHeight;
        }
        if (insetAfter >= 0 && inset is null)
        {
            if (y > 0) y += spacing;
            inset = new Rect(0, y, width, insetHeight);
            y += insetHeight;
        }
        return (cards, inset, y);
    }

    // The visible card the inset follows: its anchor, else (gone, or hidden) the last card.
    private int InsetAfter(List<Control> visible)
        => _inset is null ? -1 : _insetAfter is { } after && visible.IndexOf(after) is >= 0 and var index ? index : int.MaxValue;

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Visible();
        var columns = Columns(availableSize.Width, visible.Count, MinColumnWidth, Spacing);
        var columnWidth = columns == 0 ? 0 : double.IsInfinity(availableSize.Width)
            ? MinColumnWidth
            : Math.Max(0, (availableSize.Width - Spacing * (columns - 1)) / columns);
        foreach (var card in visible) card.Measure(new Size(columnWidth, double.PositiveInfinity));
        var width = double.IsInfinity(availableSize.Width) ? columns * columnWidth + Spacing * Math.Max(0, columns - 1) : availableSize.Width;
        _inset?.Measure(new Size(width, double.PositiveInfinity));
        if (columns == 0 && _inset is null) return default;
        var plan = Plan(visible.Select(c => c.DesiredSize.Height).ToList(), Math.Max(1, columns), columnWidth, Spacing, width,
            InsetAfter(visible), _inset?.DesiredSize.Height ?? 0);
        return new Size(width, plan.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        var columns = Columns(finalSize.Width, visible.Count, MinColumnWidth, Spacing);
        var columnWidth = columns == 0 ? 0 : Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        var plan = Plan(visible.Select(c => c.DesiredSize.Height).ToList(), Math.Max(1, columns), columnWidth, Spacing, finalSize.Width,
            InsetAfter(visible), _inset?.DesiredSize.Height ?? 0);
        for (var i = 0; i < visible.Count; i++) visible[i].Arrange(plan.Cards[i]);
        if (_inset is { } inset && plan.Inset is { } rect) inset.Arrange(rect);
        return finalSize;
    }
}
