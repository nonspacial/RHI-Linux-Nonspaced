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
public sealed class MiddleEllipsisText : TextBlock
{
    public const int TooltipDelay = 2000;

    public static readonly StyledProperty<string?> FullTextProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, string?>(nameof(FullText));

    /// <summary>When true (default), a value that is not a path wraps over several lines like ordinary text.</summary>
    public static readonly StyledProperty<bool> WrapNonPathProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, bool>(nameof(WrapNonPath), true);

    private string? _display;
    private string? _lastText;
    private double _lastWidth = double.NaN;
    private double _lastFontSize;
    private Typeface _lastTypeface;

    static MiddleEllipsisText() => AffectsMeasure<MiddleEllipsisText>(FullTextProperty, WrapNonPathProperty);

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

    /// <summary>The text currently shown (compacted, or the full value when it fits or wraps).</summary>
    public string DisplayText => Compacts && _display is not null ? _display : FullText ?? "";

    private bool Compacts => !WrapNonPath || PathEllipsis.LooksLikePath(FullText ?? "");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FullTextProperty && change.Property != WrapNonPathProperty) return;
        var full = FullText ?? "";
        _display = null;
        _lastText = null;
        TextWrapping = Compacts ? TextWrapping.NoWrap : TextWrapping.Wrap;
        Text = full;
        ToolTip.SetTip(this, Compacts && full.Length > 0 ? full : null);
    }

    protected override TextLayout CreateTextLayout(string? text)
        => base.CreateTextLayout(Compacts && _display is not null ? _display : text);

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
                var display = PathEllipsis.Compact(FullText, width, s => WidthEstimator.TextWidth(s, typeface, fontSize));
                if (!string.Equals(display, _display, StringComparison.Ordinal))
                {
                    _display = display;
                    InvalidateTextLayout();
                }
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
