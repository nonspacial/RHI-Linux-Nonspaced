using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace RHI.Desktop.Views;

/// <summary>
/// Off-tree estimate of a control's one-line desired width: text measured with <see cref="FormattedText"/>,
/// everything else from paddings, borders, margins, spacings and explicit widths. Lets panels choose a layout
/// before measuring, instead of mutating live controls in trial measures. The <see cref="Values"/> hooks let a caller
/// substitute the font size, padding or explicit width a control would have (e.g. at another typography scale).
/// </summary>
public sealed class WidthEstimator
{
    public sealed class Values
    {
        public Func<Control, bool> IsIncluded { get; init; } = c => c.IsVisible;
        public Func<Control, double?> FontSize { get; init; } = _ => null;
        public Func<Control, Thickness?> Padding { get; init; } = _ => null;
        public Func<Control, double?> Width { get; init; } = _ => null;
    }

    private readonly Values _values;

    public WidthEstimator(Values? values = null) => _values = values ?? new Values();

    // Shaping text is the expensive part of every estimate, and a resize asks for the same (text, font, size)
    // again and again (header scale search, path ellipsis candidates), so widths are memoised. Bounded: cleared when
    // full or when the UI culture changes.
    private const int TextWidthCacheLimit = 16384;
    private static readonly Dictionary<(string Text, Typeface Typeface, double Size), double> TextWidths = new();
    private static readonly object TextWidthsLock = new();
    private static CultureInfo? _textWidthsCulture;

    /// <summary>One-line width of <paramref name="text"/> (rounded up), memoised per (text, typeface, size).</summary>
    public static double TextWidth(string? text, Typeface typeface, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var culture = CultureInfo.CurrentUICulture;
        var key = (text, typeface, fontSize);
        lock (TextWidthsLock)
        {
            if (!Equals(culture, _textWidthsCulture) || TextWidths.Count >= TextWidthCacheLimit)
            {
                TextWidths.Clear();
                _textWidthsCulture = culture;
            }
            if (TextWidths.TryGetValue(key, out var cached)) return cached;
        }
        var width = Math.Ceiling(new FormattedText(text, culture, FlowDirection.LeftToRight, typeface, fontSize, null)
            .WidthIncludingTrailingWhitespace);
        lock (TextWidthsLock) TextWidths[key] = width;
        return width;
    }

    private static Typeface TypefaceOf(Control c) => c switch
    {
        TextBlock t => new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch),
        TemplatedControl tc => new Typeface(tc.FontFamily, tc.FontStyle, tc.FontWeight, tc.FontStretch),
        _ => Typeface.Default
    };

    private double FontSizeOf(Control c) => _values.FontSize(c) ?? c switch
    {
        TextBlock t => t.FontSize,
        TemplatedControl tc => tc.FontSize,
        _ => 12
    };

    /// <summary>Desired width of <paramref name="c"/> laid out on one line, margins included; 0 when excluded.</summary>
    public double Estimate(Control c)
    {
        if (!_values.IsIncluded(c)) return 0;
        var margin = c.Margin.Left + c.Margin.Right;
        var width = _values.Width(c) ?? c.Width;
        double content;
        if (!double.IsNaN(width))
        {
            content = width;
        }
        else
        {
            content = c switch
            {
                TextBlock t => TextWidth(t.Text, TypefaceOf(t), FontSizeOf(t)) + t.Padding.Left + t.Padding.Right,
                Button { Content: string s } b => TextWidth(s, TypefaceOf(b), FontSizeOf(b))
                    + Horizontal(_values.Padding(b) ?? b.Padding) + Horizontal(b.BorderThickness),
                StackPanel { Orientation: Orientation.Horizontal } sp => Row(sp.Children, sp.Spacing),
                StackPanel sp => sp.Children.Where(_values.IsIncluded).Select(Estimate).DefaultIfEmpty(0).Max(),
                WrapPanel wp => Row(wp.Children, wp.ItemSpacing),
                DockPanel dp => dp.Children.Where(_values.IsIncluded).Sum(Estimate),
                Border bd => (bd.Child is null ? 0 : Estimate(bd.Child)) + Horizontal(bd.Padding) + Horizontal(bd.BorderThickness),
                _ => c.DesiredSize.Width - margin
            };
        }
        content = Math.Clamp(content, c.MinWidth, double.IsNaN(c.MaxWidth) ? double.PositiveInfinity : Math.Max(c.MinWidth, c.MaxWidth));
        return content + margin;
    }

    private double Row(Controls children, double spacing)
    {
        var included = children.Where(_values.IsIncluded).ToList();
        return included.Sum(Estimate) + spacing * Math.Max(0, included.Count - 1);
    }

    private static double Horizontal(Thickness t) => t.Left + t.Right;
}
