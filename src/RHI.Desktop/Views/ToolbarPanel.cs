using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using RHI.Desktop.Layout;

namespace RHI.Desktop.Views;

/// <summary>Where a <see cref="ToolbarPanel"/> child sits.</summary>
public enum ToolbarSlot
{
    /// <summary>Left group, never hidden by the panel (library toggle, brand block).</summary>
    Leading,
    /// <summary>Right group, in order; overflows into the menu from the right end.</summary>
    Item,
    /// <summary>The '…' button; shown only while something overflows.</summary>
    Overflow,
    /// <summary>Right end, always visible (Settings).</summary>
    Trailing
}

/// <summary>
/// One-row toolbar: leading children on the left, items + overflow + trailing children right-aligned. When the
/// row is too narrow it first hides descendants marked <see cref="CollapsibleProperty"/> (the brand subtitle), then
/// hides items from the right end (<see cref="ToolbarOverflow"/>) and shows the overflow button. Widths come from
/// <see cref="WidthEstimator"/> (hidden items included), so the decision is made once per measure and visibility is
/// written once, only when it changes. Hidden items keep
/// their commands/handlers; <see cref="OverflowItems"/> lists them for the overflow menu. Never scrolls or clips.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    public static readonly AttachedProperty<ToolbarSlot> SlotProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, ToolbarSlot>("Slot", ToolbarSlot.Item);

    public static readonly AttachedProperty<bool> IsSeparatorProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("IsSeparator");

    public static readonly AttachedProperty<bool> CollapsibleProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("Collapsible");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(Spacing), 8);

    public static readonly StyledProperty<double> LeadingSpacingProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(LeadingSpacing), 10);

    /// <summary>Minimum gap between the leading group and the right group.</summary>
    public static readonly StyledProperty<double> MinGapProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(MinGap), 16);

    private const double FitTolerance = 1;

    static ToolbarPanel() => AffectsMeasure<ToolbarPanel>(SpacingProperty, LeadingSpacingProperty, MinGapProperty);

    public static ToolbarSlot GetSlot(Control c) => c.GetValue(SlotProperty);
    public static void SetSlot(Control c, ToolbarSlot v) => c.SetValue(SlotProperty, v);
    public static bool GetIsSeparator(Control c) => c.GetValue(IsSeparatorProperty);
    public static void SetIsSeparator(Control c, bool v) => c.SetValue(IsSeparatorProperty, v);
    public static bool GetCollapsible(Control c) => c.GetValue(CollapsibleProperty);
    public static void SetCollapsible(Control c, bool v) => c.SetValue(CollapsibleProperty, v);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double LeadingSpacing { get => GetValue(LeadingSpacingProperty); set => SetValue(LeadingSpacingProperty, value); }
    public double MinGap { get => GetValue(MinGapProperty); set => SetValue(MinGapProperty, value); }

    /// <summary>Items currently moved into the overflow menu, in authored order (separators excluded).</summary>
    public IReadOnlyList<Control> OverflowItems { get; private set; } = [];

    /// <summary>True while the collapsible leading descendants (brand subtitle) are hidden.</summary>
    public bool IsLeadingCompact { get; private set; }

    private IEnumerable<Control> In(ToolbarSlot slot) => Children.Where(c => GetSlot(c) == slot);

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var items = In(ToolbarSlot.Item).ToList();
        var overflow = In(ToolbarSlot.Overflow).FirstOrDefault();
        var leading = In(ToolbarSlot.Leading).ToList();
        var trailing = In(ToolbarSlot.Trailing).ToList();
        var collapsible = leading.SelectMany(c => c.GetVisualDescendants().OfType<Control>().Prepend(c)).Where(GetCollapsible).ToList();

        if (double.IsInfinity(availableSize.Width))
        {
            Apply(items, overflow, collapsible, items.Count, compact: false);
        }
        else
        {
            // Decide from off-tree estimates (WidthEstimator), never from trial measures of live controls: the
            // result depends only on the width and the content, whichever way the window is resized.
            var owned = new HashSet<Control>(items);
            if (overflow is not null) owned.Add(overflow);
            var full = new WidthEstimator(new WidthEstimator.Values { IsIncluded = c => owned.Contains(c) || GetCollapsible(c) || c.IsVisible });
            var compactLeading = new WidthEstimator(new WidthEstimator.Values { IsIncluded = c => !GetCollapsible(c) && c.IsVisible });

            double Group(IEnumerable<Control> group, WidthEstimator estimator, double spacing)
            {
                var widths = group.Select(estimator.Estimate).Where(w => w > 0).ToList();
                return widths.Sum() + spacing * Math.Max(0, widths.Count - 1);
            }

            var trailingWidth = Group(trailing, full, Spacing);
            double RightBudget(double leadingWidth)
                => availableSize.Width - leadingWidth - MinGap - trailingWidth - (trailingWidth > 0 ? Spacing : 0) - FitTolerance;

            var widths = items.Select(full.Estimate).ToList();
            var separators = items.Select(GetIsSeparator).ToList();
            var allItems = widths.Sum() + Spacing * Math.Max(0, widths.Count - 1);
            if (allItems <= RightBudget(Group(leading, full, LeadingSpacing)))
            {
                Apply(items, overflow, collapsible, items.Count, compact: false);
            }
            else
            {
                var budget = RightBudget(Group(leading, compactLeading, LeadingSpacing));
                var overflowWidth = overflow is null ? 0 : full.Estimate(overflow);
                var kept = ToolbarOverflow.VisibleCount(widths, separators, Spacing, budget, overflowWidth);
                Apply(items, overflow, collapsible, kept, compact: true);
            }
        }

        // One measure of each child, after the visibility decision.
        foreach (var child in Children) child.Measure(unbounded);
        return Desired(unbounded);
    }

    private Size Desired(Size unbounded)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var leading = visible.Where(c => GetSlot(c) == ToolbarSlot.Leading).ToList();
        var right = visible.Where(c => GetSlot(c) != ToolbarSlot.Leading).ToList();
        var width = leading.Sum(c => c.DesiredSize.Width) + LeadingSpacing * Math.Max(0, leading.Count - 1)
                    + right.Sum(c => c.DesiredSize.Width) + Spacing * Math.Max(0, right.Count - 1)
                    + (leading.Count > 0 && right.Count > 0 ? MinGap : 0);
        return new Size(width, visible.Count == 0 ? 0 : visible.Max(c => c.DesiredSize.Height));
    }

    private void Apply(List<Control> items, Control? overflow, List<Control> collapsible, int kept, bool compact)
    {
        SetVisible(collapsible, !compact);
        IsLeadingCompact = compact;
        for (var i = 0; i < items.Count; i++)
            if (items[i].IsVisible != i < kept) items[i].IsVisible = i < kept;
        var hidden = ToolbarOverflow.Hidden(items.Select(GetIsSeparator).ToList(), kept).Select(i => items[i]).ToList();
        if (overflow is not null && overflow.IsVisible != hidden.Count > 0) overflow.IsVisible = hidden.Count > 0;
        OverflowItems = hidden;
    }

    private static void SetVisible(List<Control> controls, bool visible)
    {
        foreach (var c in controls)
            if (c.IsVisible != visible) c.IsVisible = visible;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var c in Children.Where(c => c.IsVisible && GetSlot(c) == ToolbarSlot.Leading))
        {
            c.Arrange(Centred(x, c, finalSize.Height));
            x += c.DesiredSize.Width + LeadingSpacing;
        }

        // Right group in authored slot order: items, overflow, trailing.
        var right = Children.Where(c => c.IsVisible && GetSlot(c) == ToolbarSlot.Item)
            .Concat(Children.Where(c => c.IsVisible && GetSlot(c) == ToolbarSlot.Overflow))
            .Concat(Children.Where(c => c.IsVisible && GetSlot(c) == ToolbarSlot.Trailing))
            .ToList();
        var width = right.Sum(c => c.DesiredSize.Width) + Spacing * Math.Max(0, right.Count - 1);
        x = finalSize.Width - width;
        foreach (var c in right)
        {
            c.Arrange(Centred(x, c, finalSize.Height));
            x += c.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }

    // Each child keeps its desired height, vertically centred (as in the former centred StackPanels).
    private static Rect Centred(double x, Control c, double height)
    {
        var h = Math.Min(c.DesiredSize.Height, height);
        return new Rect(x, (height - h) / 2, c.DesiredSize.Width, h);
    }

}
