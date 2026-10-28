using Avalonia;
using Avalonia.Controls;

namespace RHI.Desktop.Views;

/// <summary>
/// Label/value form laid out like a <c>Grid ColumnDefinitions="Auto,*"</c> with Auto rows (children use
/// <c>Grid.Row</c> and <c>Grid.Column</c> 0 = label, 1 = value). Below <see cref="StackBelow"/> px of its own width
/// each label moves above its value and both take the full width, so values never collapse to nothing.
/// </summary>
public sealed class LabelValuePanel : Panel
{
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<LabelValuePanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<LabelValuePanel, double>(nameof(RowSpacing));

    /// <summary>Gap between a label and its value when stacked.</summary>
    public static readonly StyledProperty<double> StackedSpacingProperty =
        AvaloniaProperty.Register<LabelValuePanel, double>(nameof(StackedSpacing), 2);

    public static readonly StyledProperty<double> StackBelowProperty =
        AvaloniaProperty.Register<LabelValuePanel, double>(nameof(StackBelow));

    private bool _stacked;
    private double _labelWidth;
    private double[] _rowHeights = [];
    private bool[] _rowShown = [];

    static LabelValuePanel() => AffectsMeasure<LabelValuePanel>(ColumnSpacingProperty, RowSpacingProperty, StackedSpacingProperty, StackBelowProperty);

    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double StackedSpacing { get => GetValue(StackedSpacingProperty); set => SetValue(StackedSpacingProperty, value); }
    public double StackBelow { get => GetValue(StackBelowProperty); set => SetValue(StackBelowProperty, value); }

    /// <summary>True while labels sit above their values.</summary>
    public bool IsStacked => _stacked;

    private int RowCount => Children.Count == 0 ? 0 : Children.Max(Grid.GetRow) + 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = RowCount;
        _rowHeights = new double[rows];
        // w12/hdr-wsi (#251): a row whose every cell is hidden takes no row spacing either (no gap where it was).
        _rowShown = new bool[rows];
        foreach (var child in Children)
            if (child.IsVisible) _rowShown[Grid.GetRow(child)] = true;
        var gaps = RowSpacing * Math.Max(0, _rowShown.Count(shown => shown) - 1);
        _stacked = !double.IsInfinity(availableSize.Width) && availableSize.Width < StackBelow;
        if (rows == 0) return default;

        if (_stacked)
        {
            var column = new Size(availableSize.Width, double.PositiveInfinity);
            double height = 0, width = 0;
            for (var r = 0; r < rows; r++)
            {
                double rowHeight = 0;
                var parts = 0;
                foreach (var child in Children.Where(c => Grid.GetRow(c) == r).OrderBy(Grid.GetColumn))
                {
                    child.Measure(column);
                    if (!child.IsVisible) continue;
                    rowHeight += child.DesiredSize.Height + (parts++ > 0 ? StackedSpacing : 0);
                    width = Math.Max(width, child.DesiredSize.Width);
                }
                _rowHeights[r] = rowHeight;
                height += rowHeight;
            }
            return new Size(width, height + gaps);
        }

        // Auto label column: labels measured unconstrained, as Grid does for Auto columns.
        _labelWidth = 0;
        foreach (var label in Children.Where(c => Grid.GetColumn(c) == 0))
        {
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _labelWidth = Math.Max(_labelWidth, label.DesiredSize.Width);
        }
        var valueWidth = Math.Max(0, availableSize.Width - _labelWidth - ColumnSpacing);
        double maxValue = 0;
        foreach (var value in Children.Where(c => Grid.GetColumn(c) != 0))
        {
            value.Measure(new Size(valueWidth, double.PositiveInfinity));
            maxValue = Math.Max(maxValue, value.DesiredSize.Width);
        }
        foreach (var child in Children)
            _rowHeights[Grid.GetRow(child)] = Math.Max(_rowHeights[Grid.GetRow(child)], child.DesiredSize.Height);
        return new Size(_labelWidth + ColumnSpacing + maxValue, _rowHeights.Sum() + gaps);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        for (var r = 0; r < _rowHeights.Length; r++)
        {
            var cells = Children.Where(c => Grid.GetRow(c) == r).OrderBy(Grid.GetColumn).ToList();
            if (_stacked)
            {
                var top = y;
                var parts = 0;
                foreach (var child in cells)
                {
                    if (!child.IsVisible) continue;
                    if (parts++ > 0) top += StackedSpacing;
                    child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
                    top += child.DesiredSize.Height;
                }
            }
            else
            {
                var valueX = _labelWidth + ColumnSpacing;
                foreach (var child in cells)
                {
                    var rect = Grid.GetColumn(child) == 0
                        ? new Rect(0, y, _labelWidth, _rowHeights[r])
                        : new Rect(valueX, y, Math.Max(0, finalSize.Width - valueX), _rowHeights[r]);
                    child.Arrange(rect);
                }
            }
            y += _rowHeights[r] + (r < _rowShown.Length && !_rowShown[r] ? 0 : RowSpacing);
        }
        return finalSize;
    }
}
