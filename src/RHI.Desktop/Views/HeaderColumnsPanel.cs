using Avalonia;
using Avalonia.Controls;

namespace RHI.Desktop.Views;

/// <summary>
/// w14 final review m4 (upstream 2.9.4 <c>DetailLayoutHost.Header</c>): the Tabs and Spotlight header's two columns — the
/// name, tags, path and links on the left; Launch, then HDR, resolution and display modes on the right at their own width.
/// When the page is too narrow for the left column to keep <see cref="LeftMinWidth"/> beside the right one, the right
/// column goes under the left at the full width instead. The first child is the left column, the second the right.
/// </summary>
public sealed class HeaderColumnsPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<HeaderColumnsPanel, double>(nameof(Spacing), 20);
    public static readonly StyledProperty<double> LeftMinWidthProperty = AvaloniaProperty.Register<HeaderColumnsPanel, double>(nameof(LeftMinWidth), 340);

    static HeaderColumnsPanel() => AffectsMeasure<HeaderColumnsPanel>(SpacingProperty, LeftMinWidthProperty);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double LeftMinWidth { get => GetValue(LeftMinWidthProperty); set => SetValue(LeftMinWidthProperty, value); }

    /// <summary>True when the right column goes under the left (pure, for tests).</summary>
    public static bool Stacks(double available, double leftMinWidth, double rightWidth, double spacing)
        => double.IsFinite(available) && available < leftMinWidth + spacing + rightWidth;

    /// <summary>True after the last measure put the right column under the left.</summary>
    public bool IsStacked { get; private set; }

    private Control? Left => Children.Count > 0 ? Children[0] : null;
    private Control? Right => Children.Count > 1 && Children[1].IsVisible ? Children[1] : null;

    protected override Size MeasureOverride(Size available)
    {
        var right = Right;
        var spacing = Spacing;
        right?.Measure(new Size(double.PositiveInfinity, available.Height));
        var rightWidth = right?.DesiredSize.Width ?? 0;
        IsStacked = right is not null && Stacks(available.Width, LeftMinWidth, rightWidth, spacing);
        if (right is null)
        {
            Left?.Measure(available);
            return Left?.DesiredSize ?? default;
        }
        if (IsStacked)
        {
            Left?.Measure(new Size(available.Width, double.PositiveInfinity));
            right.Measure(new Size(available.Width, double.PositiveInfinity));
            var left = Left?.DesiredSize ?? default;
            return new Size(Math.Max(left.Width, right.DesiredSize.Width), left.Height + spacing + right.DesiredSize.Height);
        }
        var leftWidth = double.IsFinite(available.Width) ? available.Width - spacing - rightWidth : double.PositiveInfinity;
        Left?.Measure(new Size(leftWidth, available.Height));
        var l = Left?.DesiredSize ?? default;
        return new Size(l.Width + spacing + rightWidth, Math.Max(l.Height, right.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var right = Right;
        var spacing = Spacing;
        if (right is null)
        {
            Left?.Arrange(new Rect(final));
            return final;
        }
        if (IsStacked)
        {
            var leftHeight = Left?.DesiredSize.Height ?? 0;
            Left?.Arrange(new Rect(0, 0, final.Width, leftHeight));
            right.Arrange(new Rect(0, leftHeight + spacing, final.Width, right.DesiredSize.Height));
            return final;
        }
        var rightWidth = Math.Min(right.DesiredSize.Width, final.Width);
        Left?.Arrange(new Rect(0, 0, Math.Max(0, final.Width - spacing - rightWidth), final.Height));
        right.Arrange(new Rect(final.Width - rightWidth, 0, rightWidth, final.Height));
        return final;
    }
}
