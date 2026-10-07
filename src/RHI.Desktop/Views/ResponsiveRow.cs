using Avalonia;
using Avalonia.Controls;

namespace RHI.Desktop.Views;

/// <summary>
/// A single grid row (<see cref="Columns"/> like <c>Grid.ColumnDefinitions</c>; children use <c>Grid.Column</c>,
/// several children may share a column) that wraps instead of overflowing. One line, Grid-identical, while every
/// fixed/auto column fits and the star column keeps at least <see cref="MinStarWidth"/>; otherwise the first
/// <see cref="HeadColumns"/> columns stay on the first line (star takes the rest) and the remaining columns move
/// below — on one line if they fit (a star among them taking the rest), else flowing left to right, a star column
/// taking a whole line.
/// </summary>
public sealed class ResponsiveRow : Panel
{
    public static readonly StyledProperty<string> ColumnsProperty =
        AvaloniaProperty.Register<ResponsiveRow, string>(nameof(Columns), "*");

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsiveRow, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<ResponsiveRow, double>(nameof(LineSpacing), 6);

    public static readonly StyledProperty<int> HeadColumnsProperty =
        AvaloniaProperty.Register<ResponsiveRow, int>(nameof(HeadColumns), 1);

    public static readonly StyledProperty<double> MinStarWidthProperty =
        AvaloniaProperty.Register<ResponsiveRow, double>(nameof(MinStarWidth));

    private readonly List<Placement> _placements = [];
    private string? _parsedSource;
    private ColumnDefinitions _definitions = new("*");

    static ResponsiveRow() => AffectsMeasure<ResponsiveRow>(ColumnsProperty, ColumnSpacingProperty, LineSpacingProperty, HeadColumnsProperty, MinStarWidthProperty);

    public string Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double LineSpacing { get => GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }
    public int HeadColumns { get => GetValue(HeadColumnsProperty); set => SetValue(HeadColumnsProperty, value); }
    public double MinStarWidth { get => GetValue(MinStarWidthProperty); set => SetValue(MinStarWidthProperty, value); }

    /// <summary>True when the row currently spans more than one line.</summary>
    public bool IsWrapped { get; private set; }

    private readonly record struct Placement(int Column, int Line, double X, double Width);

    private ColumnDefinitions Definitions
    {
        get
        {
            if (!string.Equals(_parsedSource, Columns, StringComparison.Ordinal))
            {
                _definitions = new ColumnDefinitions(Columns);
                _parsedSource = Columns;
            }
            return _definitions;
        }
    }

    private IEnumerable<Control> InColumn(int column) => Children.Where(c => Grid.GetColumn(c) == column);

    protected override Size MeasureOverride(Size availableSize)
    {
        var defs = Definitions;
        var n = defs.Count;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);

        // Natural width of fixed and auto columns.
        var natural = new double[n];
        for (var i = 0; i < n; i++)
        {
            var w = defs[i].Width;
            if (w.IsAbsolute) natural[i] = w.Value;
            else if (w.IsAuto)
            {
                foreach (var c in InColumn(i)) c.Measure(unbounded);
                natural[i] = InColumn(i).Where(c => c.IsVisible).Select(c => c.DesiredSize.Width).DefaultIfEmpty(0).Max();
            }
        }
        bool HasContent(int i) => InColumn(i).Any(c => c.IsVisible);

        _placements.Clear();
        var width = availableSize.Width;
        var all = Enumerable.Range(0, n).ToList();
        if (double.IsInfinity(width))
        {
            PlaceLine(all, 0, width, natural, defs, starFallback: true);
            IsWrapped = false;
        }
        else if (LineFits(all, width, natural, defs))
        {
            PlaceLine(all, 0, width, natural, defs, starFallback: false);
            IsWrapped = false;
        }
        else
        {
            IsWrapped = true;
            var head = all.Take(Math.Clamp(HeadColumns, 1, n)).ToList();
            PlaceLine(head, 0, width, natural, defs, starFallback: false);
            var tail = all.Skip(head.Count).Where(HasContent).ToList();
            var line = 1;
            if (tail.Count > 0 && LineFits(tail, width, natural, defs))
            {
                PlaceLine(tail, line, width, natural, defs, starFallback: false);
            }
            else
            {
                var x = 0.0;
                var any = false;
                foreach (var i in tail)
                {
                    if (defs[i].Width.IsStar)
                    {
                        if (any) line++;
                        _placements.Add(new Placement(i, line, 0, width));
                        line++;
                        x = 0;
                        any = false;
                        continue;
                    }
                    if (any && x + natural[i] > width)
                    {
                        line++;
                        x = 0;
                    }
                    _placements.Add(new Placement(i, line, x, natural[i]));
                    x += natural[i] + ColumnSpacing;
                    any = true;
                }
            }
        }

        // Measure every child at its placed width; line height = tallest child on the line. Auto columns are placed
        // at exactly their natural width, so they keep the unbounded constraint they were just measured with (a
        // no-op re-measure instead of re-laying-out their text at a new constraint on every resize step).
        var lines = _placements.Count == 0 ? 0 : _placements.Max(p => p.Line) + 1;
        _lineHeights = new double[lines];
        double desiredWidth = 0;
        foreach (var p in _placements)
            foreach (var c in InColumn(p.Column))
            {
                c.Measure(defs[p.Column].Width.IsAuto || double.IsInfinity(p.Width)
                    ? unbounded
                    : new Size(p.Width, double.PositiveInfinity));
                if (!c.IsVisible) continue;
                _lineHeights[p.Line] = Math.Max(_lineHeights[p.Line], c.DesiredSize.Height);
                desiredWidth = Math.Max(desiredWidth, p.X + (double.IsInfinity(p.Width) ? c.DesiredSize.Width : p.Width));
            }
        // Skip empty lines (e.g. a wrapped tail whose columns are all hidden).
        var used = _lineHeights.Count(h => h > 0);
        return new Size(double.IsInfinity(width) ? desiredWidth : Math.Min(desiredWidth, width),
            _lineHeights.Sum() + LineSpacing * Math.Max(0, used - 1));
    }

    private double[] _lineHeights = [];

    private bool LineFits(List<int> cols, double width, double[] natural, ColumnDefinitions defs)
    {
        var fixedWidth = cols.Where(i => !defs[i].Width.IsStar).Sum(i => natural[i]) + ColumnSpacing * Math.Max(0, cols.Count - 1);
        var stars = cols.Count(i => defs[i].Width.IsStar);
        if (fixedWidth > width) return false;
        return stars == 0 || (width - fixedWidth) / stars >= MinStarWidth;
    }

    // Grid-identical placement of one line: spacing between every column (empty ones included), stars share the rest.
    private void PlaceLine(List<int> cols, int line, double width, double[] natural, ColumnDefinitions defs, bool starFallback)
    {
        var fixedWidth = cols.Where(i => !defs[i].Width.IsStar).Sum(i => natural[i]) + ColumnSpacing * Math.Max(0, cols.Count - 1);
        var starTotal = cols.Where(i => defs[i].Width.IsStar).Sum(i => defs[i].Width.Value);
        var remaining = starFallback ? double.PositiveInfinity : Math.Max(0, width - fixedWidth);
        var x = 0.0;
        foreach (var i in cols)
        {
            var w = defs[i].Width.IsStar ? (double.IsInfinity(remaining) ? remaining : remaining * defs[i].Width.Value / starTotal) : natural[i];
            _placements.Add(new Placement(i, line, x, w));
            if (!double.IsInfinity(w)) x += w + ColumnSpacing;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tops = new double[_lineHeights.Length];
        var y = 0.0;
        for (var l = 0; l < _lineHeights.Length; l++)
        {
            tops[l] = y;
            if (_lineHeights[l] > 0) y += _lineHeights[l] + LineSpacing;
        }
        foreach (var p in _placements)
        {
            var w = double.IsInfinity(p.Width) ? Math.Max(0, finalSize.Width - p.X) : p.Width;
            foreach (var c in InColumn(p.Column))
                c.Arrange(new Rect(p.X, tops[p.Line], w, _lineHeights[p.Line]));
        }
        return finalSize;
    }
}
