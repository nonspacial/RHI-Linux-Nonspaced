using Avalonia;
using Avalonia.Controls;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-a (#304, upstream Controls.AutoFitGrid / mockup A's "auto-fit, minmax(N px, 1fr)"): as many equal columns as
/// fit at <see cref="MinColumnWidth"/>, never more than there are visible children; each row as tall as its tallest child.
/// The Tabs layout's component cards, At a glance tiles and Management tiles, and Classic+'s Management row.
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

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Visible();
        var columns = Columns(availableSize.Width, visible.Count, MinColumnWidth, Spacing);
        if (columns == 0) return default;
        var columnWidth = double.IsInfinity(availableSize.Width)
            ? MinColumnWidth
            : Math.Max(0, (availableSize.Width - Spacing * (columns - 1)) / columns);
        double height = 0, rowHeight = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if ((i + 1) % columns != 0 && i != visible.Count - 1) continue;
            height += rowHeight + (height > 0 ? Spacing : 0);
            rowHeight = 0;
        }
        var width = double.IsInfinity(availableSize.Width) ? columns * columnWidth + Spacing * (columns - 1) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        var columns = Columns(finalSize.Width, visible.Count, MinColumnWidth, Spacing);
        if (columns == 0) return finalSize;
        var columnWidth = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (columnWidth + Spacing), y, columnWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
