using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using RHI.Desktop.Layout;

namespace RHI.Desktop.Views;

/// <summary>
/// One-line TextBlock for path values: shows <see cref="FullText"/> compacted with <see cref="PathEllipsis"/> to the
/// width it is given, with the full value in a tooltip after <see cref="TooltipDelay"/> ms. The compacted form is
/// computed off-tree (FormattedText) only when the text, the available width or the font changes, and is fed to the
/// text layout directly — no property is written during measure. Values that are not paths (status messages) wrap
/// like ordinary text when <see cref="WrapNonPath"/> is set, otherwise they are trimmed at the end (full text in the
/// tooltip). Styled as a plain TextBlock; <see cref="TextBlock.Text"/> holds the full value.
/// </summary>
// PINNED TO AVALONIA 12.1.2 INTERNALS (TextBlock source read at tag 12.1.2). On any Avalonia upgrade re-read
// TextBlock.cs and re-run the harness path-row check (rhi-ui-shots Checks.PathRows: the text drawn by every visible
// MiddleEllipsisText == DisplayText, a prefix…suffix compaction of FullText, never wider than its slot) and
// MiddleEllipsisTextPinTests. What this class relies on:
// - CreateTextLayout(string?) is protected virtual and is the only place TextBlock builds its TextLayout; ours copies
//   the base's construction (typeface, FontSize, Foreground, alignment rule, wrapping, trimming, decorations,
//   LineHeight, LetterSpacing, MaxLines, FontFeatures) minus LineSpacing, which is irrelevant on one line.
// - TextBlock's own TextRunCache (_textRunCache, private) is keyed by text position, not content, and is reset only
//   by InvalidateTextLayout (which also calls InvalidateMeasure), so the compacted string needs its own run cache.
// - The protected field _constraint holds the last deflated measure constraint; TextBlock.MeasureOverride disposes
//   and rebuilds the layout when the constraint differs, so setting it to (NaN, NaN) forces a rebuild in the same
//   measure. ArrangeOverride and OnMeasureInvalidated dispose _textLayout (ours included) — we never dispose it.
// - Shaping-relevant property names in OnPropertyChanged (FontSize … Foreground) mirror the base's switch.
public sealed class MiddleEllipsisText : TextBlock
{
    public const int TooltipDelay = 2000;

    public static readonly StyledProperty<string?> FullTextProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, string?>(nameof(FullText));

    /// <summary>When true (default), a value that is not a path wraps over several lines like ordinary text.</summary>
    public static readonly StyledProperty<bool> WrapNonPathProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, bool>(nameof(WrapNonPath), true);

    /// <summary>When true and <see cref="WrapNonPath"/> is false, a value that is not a path is shortened in the
    /// middle (start…end) instead of at the end.</summary>
    public static readonly StyledProperty<bool> MiddleNonPathProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, bool>(nameof(MiddleNonPath));

    private string? _display;
    private TextRunCache? _displayRuns;
    private string? _lastText;
    private double _lastWidth = double.NaN;
    private double _lastFontSize;
    private Typeface _lastTypeface;

    static MiddleEllipsisText() => AffectsMeasure<MiddleEllipsisText>(FullTextProperty, WrapNonPathProperty, MiddleNonPathProperty);

    public MiddleEllipsisText() => ToolTip.SetShowDelay(this, TooltipDelay);

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? FullText
    {
        get => GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    public bool WrapNonPath
    {
        get => GetValue(WrapNonPathProperty);
        set => SetValue(WrapNonPathProperty, value);
    }

    public bool MiddleNonPath
    {
        get => GetValue(MiddleNonPathProperty);
        set => SetValue(MiddleNonPathProperty, value);
    }

    /// <summary>The text currently shown (compacted, or the full value when it fits or wraps).</summary>
    public string DisplayText => Compacts && _display is not null ? _display : FullText ?? "";

    private bool Compacts => !WrapNonPath || PathEllipsis.LooksLikePath(FullText ?? "");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Same shaping-relevant set as TextBlock uses for its own run cache.
        switch (change.Property.Name)
        {
            case nameof(FontSize) or nameof(FontWeight) or nameof(FontStyle) or nameof(FontFamily) or nameof(FontStretch)
                or nameof(FlowDirection) or nameof(LetterSpacing) or nameof(Text) or nameof(TextDecorations)
                or nameof(FontFeatures) or nameof(Foreground):
                _displayRuns?.Invalidate();
                break;
        }
        if (change.Property != FullTextProperty && change.Property != WrapNonPathProperty && change.Property != MiddleNonPathProperty) return;
        var full = FullText ?? "";
        _display = null;
        _lastText = null;
        TextWrapping = Compacts ? TextWrapping.NoWrap : TextWrapping.Wrap;
        Text = full;
        ToolTip.SetTip(this, Compacts && full.Length > 0 ? full : null);
    }

    // The compacted text gets its own layout and shaped-run cache: TextBlock's run cache is keyed by text position,
    // not content, and is only reset by InvalidateTextLayout (which also re-queues a measure), so handing it a new
    // display string would reuse the previous string's glyphs. Ours is invalidated whenever the display string or a
    // shaping property changes, and survives a pure width change (measure → arrange, resize steps).
    protected override TextLayout CreateTextLayout(string? text)
    {
        if (!Compacts || _display is null) return base.CreateTextLayout(text);
        var max = GetMaxSizeFromConstraint();
        return new TextLayout(_display, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            IsMeasureValid ? TextAlignment : TextAlignment.Left, TextWrapping, TextTrimming, TextDecorations, FlowDirection,
            max.Width, max.Height, LineHeight, LetterSpacing, MaxLines, FontFeatures, null, _displayRuns ??= new TextRunCache());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Compacts)
        {
            var width = availableSize.Width - Padding.Left - Padding.Right;
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            if (!string.Equals(_lastText, FullText, StringComparison.Ordinal) || !_lastWidth.Equals(width)
                || !_lastFontSize.Equals(FontSize) || _lastTypeface != typeface)
            {
                _lastText = FullText;
                _lastWidth = width;
                _lastFontSize = FontSize;
                _lastTypeface = typeface;
                var fontSize = FontSize;
                // Widths are memoised per (text, font, size) in WidthEstimator, so a resize mostly re-reads cached
                // candidates. A new display string must reach the layout in this same measure: resetting the
                // protected constraint makes TextBlock.MeasureOverride drop any layout it holds and rebuild it via
                // CreateTextLayout. No InvalidateTextLayout, which would re-queue this control for a second measure.
                var display = PathEllipsis.Compact(FullText, width, s => WidthEstimator.TextWidth(s, typeface, fontSize), MiddleNonPath);
                if (!string.Equals(display, _display, StringComparison.Ordinal))
                {
                    _display = display;
                    _displayRuns?.Invalidate();
                    _constraint = new Size(double.NaN, double.NaN);
                }
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
