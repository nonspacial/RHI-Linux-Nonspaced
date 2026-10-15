using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
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
/// written once, only when it changes. The estimates depend only on the content, so they are cached and recomputed
/// only when a watched property of a descendant (content, text, font, padding, margin, size, visibility of a leading
/// child, or the Slot/IsSeparator/Collapsible markers), the panel's own Spacing/LeadingSpacing, or the children
/// change; a resize only re-runs the cheap partition. A control the panel hid and no longer manages (its marker
/// changed, or it was removed) is shown again. Hidden items keep their commands/handlers;
/// <see cref="OverflowItems"/> lists them for the overflow menu and <see cref="OverflowItemsChanged"/> reports
/// changes. Never scrolls or clips.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    public static readonly AttachedProperty<ToolbarSlot> SlotProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, ToolbarSlot>("Slot", ToolbarSlot.Item);

    public static readonly AttachedProperty<bool> IsSeparatorProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("IsSeparator");

    public static readonly AttachedProperty<bool> CollapsibleProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("Collapsible");

    /// <summary>
    /// #185: an item with nothing to offer right now (the "New mods" button while nothing is new): never shown, takes no
    /// room and never goes into the overflow menu. Bind it instead of <c>IsVisible</c>, which the panel itself writes.
    /// </summary>
    public static readonly AttachedProperty<bool> IsAbsentProperty =
        AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("IsAbsent");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(Spacing), 8);

    public static readonly StyledProperty<double> LeadingSpacingProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(LeadingSpacing), 10);

    /// <summary>Minimum gap between the leading group and the right group.</summary>
    public static readonly StyledProperty<double> MinGapProperty =
        AvaloniaProperty.Register<ToolbarPanel, double>(nameof(MinGap), 16);

    private const double FitTolerance = 1;

    // A change to any WidthEstimator.Inputs property or slot/separator/collapsible marker on a descendant (outside
    // the panel's own visibility writes), or a child added/removed anywhere below the panel, drops the cached
    // estimates (EstimateInputWatcher); the panel's own spacings drop them in OnPropertyChanged.
    private readonly EstimateInputWatcher _watcher;
    private bool _applying;
    private Estimates? _estimates;
    // Controls this panel has hidden (overflowed items, '…', collapsed leading text).
    private readonly HashSet<Control> _hidden = [];

    static ToolbarPanel() => AffectsMeasure<ToolbarPanel>(SpacingProperty, LeadingSpacingProperty, MinGapProperty);

    // Collapsible is looked up on the leading children's visual descendants; the watcher follows logical descendants,
    // which covers the XAML-authored markers (template parts are never marked).
    public ToolbarPanel() => _watcher = new EstimateInputWatcher(this, IsEstimateInput, () =>
    {
        _estimates = null;
        InvalidateMeasure();
    }, [SlotProperty, IsSeparatorProperty, CollapsibleProperty, IsAbsentProperty]);

    public static ToolbarSlot GetSlot(Control c) => c.GetValue(SlotProperty);
    public static void SetSlot(Control c, ToolbarSlot v) => c.SetValue(SlotProperty, v);
    public static bool GetIsSeparator(Control c) => c.GetValue(IsSeparatorProperty);
    public static void SetIsSeparator(Control c, bool v) => c.SetValue(IsSeparatorProperty, v);
    public static bool GetCollapsible(Control c) => c.GetValue(CollapsibleProperty);
    public static void SetCollapsible(Control c, bool v) => c.SetValue(CollapsibleProperty, v);
    public static bool GetIsAbsent(Control c) => c.GetValue(IsAbsentProperty);
    public static void SetIsAbsent(Control c, bool v) => c.SetValue(IsAbsentProperty, v);

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double LeadingSpacing { get => GetValue(LeadingSpacingProperty); set => SetValue(LeadingSpacingProperty, value); }
    public double MinGap { get => GetValue(MinGapProperty); set => SetValue(MinGapProperty, value); }

    /// <summary>Items currently moved into the overflow menu, in authored order (separators excluded).</summary>
    public IReadOnlyList<Control> OverflowItems { get; private set; } = [];

    /// <summary>Raised (during measure) when <see cref="OverflowItems"/> changes.</summary>
    public event EventHandler? OverflowItemsChanged;

    /// <summary>True while the collapsible leading descendants (brand subtitle) are hidden.</summary>
    public bool IsLeadingCompact { get; private set; }

    /// <summary>Content-only inputs of the partition: estimated widths and the controls they belong to.</summary>
    private sealed class Estimates
    {
        public required Control[] Items;
        public required bool[] Separators;
        public required double[] ItemWidths;
        public required Control? Overflow;
        public required double OverflowWidth;
        public required double TrailingWidth;
        public required double LeadingFull;
        public required double LeadingCompact;
        public required Control[] Collapsible;
        public required Control[] Absent;
    }

    private Estimates Estimate()
    {
        var items = new List<Control>();
        var leading = new List<Control>();
        var trailing = new List<Control>();
        var absent = new List<Control>();
        Control? overflow = null;
        foreach (var c in Children)
        {
            switch (GetSlot(c))
            {
                case ToolbarSlot.Item when GetIsAbsent(c): absent.Add(c); break;
                case ToolbarSlot.Item: items.Add(c); break;
                case ToolbarSlot.Leading: leading.Add(c); break;
                case ToolbarSlot.Trailing: trailing.Add(c); break;
                case ToolbarSlot.Overflow: overflow ??= c; break;
            }
        }
        var collapsible = leading.SelectMany(c => c.GetVisualDescendants().OfType<Control>().Prepend(c)).Where(GetCollapsible).ToArray();
        var owned = new HashSet<Control>(items.Concat(absent));
        if (overflow is not null) owned.Add(overflow);
        Release(owned, collapsible);
        var full = new WidthEstimator(new WidthEstimator.Values { IsIncluded = c => owned.Contains(c) || GetCollapsible(c) || c.IsVisible });
        var compactLeading = new WidthEstimator(new WidthEstimator.Values { IsIncluded = c => !GetCollapsible(c) && c.IsVisible });

        double Group(List<Control> group, WidthEstimator estimator, double spacing)
        {
            double sum = 0;
            var count = 0;
            foreach (var c in group)
            {
                var w = estimator.Estimate(c);
                if (w <= 0) continue;
                sum += w;
                count++;
            }
            return sum + spacing * Math.Max(0, count - 1);
        }

        return new Estimates
        {
            Items = items.ToArray(),
            Separators = items.Select(GetIsSeparator).ToArray(),
            ItemWidths = items.Select(full.Estimate).ToArray(),
            Overflow = overflow,
            OverflowWidth = overflow is null ? 0 : full.Estimate(overflow),
            TrailingWidth = Group(trailing, full, Spacing),
            LeadingFull = Group(leading, full, LeadingSpacing),
            LeadingCompact = Group(leading, compactLeading, LeadingSpacing),
            Collapsible = collapsible,
            Absent = absent.ToArray()
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var e = _estimates ??= Estimate();

        if (double.IsInfinity(availableSize.Width))
        {
            Apply(e, e.Items.Length, compact: false);
        }
        else
        {
            // Decide from off-tree estimates (WidthEstimator), never from trial measures of live controls: the
            // result depends only on the width and the content, whichever way the window is resized.
            double RightBudget(double leadingWidth)
                => availableSize.Width - leadingWidth - MinGap - e.TrailingWidth - (e.TrailingWidth > 0 ? Spacing : 0) - FitTolerance;

            double allItems = 0;
            foreach (var w in e.ItemWidths) allItems += w;
            allItems += Spacing * Math.Max(0, e.ItemWidths.Length - 1);
            if (allItems <= RightBudget(e.LeadingFull))
            {
                Apply(e, e.Items.Length, compact: false);
            }
            else
            {
                var kept = ToolbarOverflow.VisibleCount(e.ItemWidths, e.Separators, Spacing, RightBudget(e.LeadingCompact), e.OverflowWidth);
                Apply(e, kept, compact: true);
            }
        }

        // One measure of each child, after the visibility decision.
        foreach (var child in Children) child.Measure(unbounded);
        return Desired();
    }

    // Shows again what this panel hid but no longer manages because a marker changed, before the estimate reads
    // visibility. A control that has left the panel is only forgotten: its new owner decides its visibility.
    private void Release(HashSet<Control> owned, Control[] collapsible)
    {
        var released = _hidden.Where(c => !owned.Contains(c) && Array.IndexOf(collapsible, c) < 0).ToList();
        if (released.Count == 0) return;
        var wasApplying = _applying;
        _applying = true;
        try
        {
            foreach (var c in released)
            {
                _hidden.Remove(c);
                if (this.IsLogicalAncestorOf(c) && !c.IsVisible) c.IsVisible = true;
            }
        }
        finally
        {
            _applying = wasApplying;
        }
    }

    private void SetShown(Control c, bool shown)
    {
        if (c.IsVisible != shown) c.IsVisible = shown;
        if (shown) _hidden.Remove(c);
        else _hidden.Add(c);
    }

    private Size Desired()
    {
        double leadingWidth = 0, rightWidth = 0, height = 0;
        int leadingCount = 0, rightCount = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            height = Math.Max(height, c.DesiredSize.Height);
            if (GetSlot(c) == ToolbarSlot.Leading) { leadingWidth += c.DesiredSize.Width; leadingCount++; }
            else { rightWidth += c.DesiredSize.Width; rightCount++; }
        }
        var width = leadingWidth + LeadingSpacing * Math.Max(0, leadingCount - 1)
                    + rightWidth + Spacing * Math.Max(0, rightCount - 1)
                    + (leadingCount > 0 && rightCount > 0 ? MinGap : 0);
        return new Size(width, height);
    }

    private void Apply(Estimates e, int kept, bool compact)
    {
        _applying = true;
        try
        {
            foreach (var c in e.Collapsible) SetShown(c, !compact);
            foreach (var c in e.Absent) SetShown(c, false);
            IsLeadingCompact = compact;
            for (var i = 0; i < e.Items.Length; i++) SetShown(e.Items[i], i < kept);
            var hidden = ToolbarOverflow.Hidden(e.Separators, kept);
            if (e.Overflow is not null) SetShown(e.Overflow, hidden.Count > 0);
            if (!SameItems(hidden, e.Items))
            {
                OverflowItems = hidden.Select(i => e.Items[i]).ToArray();
                OverflowItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private bool SameItems(IReadOnlyList<int> hidden, Control[] items)
    {
        if (hidden.Count != OverflowItems.Count) return false;
        for (var i = 0; i < hidden.Count; i++)
            if (!ReferenceEquals(items[hidden[i]], OverflowItems[i])) return false;
        return true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var c in Children)
        {
            if (!c.IsVisible || GetSlot(c) != ToolbarSlot.Leading) continue;
            c.Arrange(Centred(x, c, finalSize.Height));
            x += c.DesiredSize.Width + LeadingSpacing;
        }

        // Right group in authored slot order: items, overflow, trailing.
        double width = 0;
        var count = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible || GetSlot(c) == ToolbarSlot.Leading) continue;
            width += c.DesiredSize.Width;
            count++;
        }
        x = finalSize.Width - width - Spacing * Math.Max(0, count - 1);
        foreach (var slot in (ReadOnlySpan<ToolbarSlot>)[ToolbarSlot.Item, ToolbarSlot.Overflow, ToolbarSlot.Trailing])
            foreach (var c in Children)
            {
                if (!c.IsVisible || GetSlot(c) != slot) continue;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Cached into TrailingWidth/LeadingFull/LeadingCompact; AffectsMeasure re-measures.
        if (change.Property == SpacingProperty || change.Property == LeadingSpacingProperty) _estimates = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watcher.Watch();
        _estimates = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _watcher.Unwatch();
    }

    // Also covers changes while detached (the watcher only runs while attached).
    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        _estimates = null;
    }

    private bool IsEstimateInput(Control sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_applying) return false;
        // Item/overflow/collapsible visibility is the panel's own output, not an input of the estimate.
        return !(e.Property == IsVisibleProperty
                 && (GetCollapsible(sender) || (Children.Contains(sender) && GetSlot(sender) is ToolbarSlot.Item or ToolbarSlot.Overflow)));
    }
}
