using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace RHI.Desktop.Views;

/// <summary>Which header element a descendant is, so <see cref="DetailHeaderPanel"/> can step it down.</summary>
public enum DetailHeaderRole
{
    None,
    /// <summary>Title TextBlock: FontSize steps from its authored size down to 16.</summary>
    Title,
    /// <summary>Subtitle TextBlock: FontSize steps from its authored size down by one point.</summary>
    Subtitle,
    /// <summary>Square provider badge: Width/Height step from the authored size down to 36 px.</summary>
    Badge,
    /// <summary>Action button: FontSize down by one point, Padding to three quarters.</summary>
    Action
}

/// <summary>
/// Game-detail header. Child 0 is the title row, child 1 the action buttons (a wrapping panel).
/// Responsive order as the header narrows:
/// 1. everything fits at authored sizes: one line, title row left, actions right-aligned (the old
///    <c>*,Auto</c> grid arrangement, unchanged);
/// 2. otherwise typography steps down smoothly (scale 1 → 0, see <see cref="DetailHeaderRole"/>) to the
///    largest scale at which both still fit on one line;
/// 3. if they do not fit even at the minimum scale, the actions move to their own line(s) below the
///    title row, left-aligned, measured at the panel width so they wrap;
/// 4. the title row then trims its text with an ellipsis as the last resort.
/// The scale is chosen once per measure from an off-tree width estimate (<see cref="WidthEstimator"/>: text via
/// FormattedText at the candidate sizes, plus paddings/borders/spacings), so it depends only on the width and the
/// content: it never rises as the width falls and is the same whichever way the window is resized. The chosen
/// values are then written once (only when they change), the affected controls are invalidated, and the children
/// are measured a single time. At scale 1 style-provided values are cleared back to the style.
/// </summary>
public sealed class DetailHeaderPanel : Panel
{
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<DetailHeaderPanel, double>(nameof(RowSpacing), 8);

    public static readonly AttachedProperty<DetailHeaderRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<DetailHeaderPanel, Control, DetailHeaderRole>("Role");

    private const double TitleMinFontSize = 16;
    private const double BadgeMinSize = 36;
    private const int SearchSteps = 12;
    // Rounding headroom between the estimate and the real measure.
    private const double FitTolerance = 1;
    // A one-line header keeps at least this gap between the badges and the first action button.
    private const double MinActionsGap = 12;

    private List<RoleEntry>? _roles;
    // Authored values are captured once per control, before the panel first writes them, and survive re-attachment.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, RoleEntry> _entries = new();
    private readonly List<Control> _watched = [];
    private readonly Dictionary<double, double> _oneLineWidths = [];
    private bool _applying;
    private bool _stacked;

    // Properties the width estimate reads; a change outside ApplyOnce drops the cached widths and re-measures.
    private static readonly HashSet<AvaloniaProperty> EstimateInputs =
    [
        TextBlock.TextProperty, ContentControl.ContentProperty, IsVisibleProperty,
        TextBlock.FontSizeProperty, TextBlock.FontFamilyProperty, TextBlock.FontWeightProperty, TextBlock.FontStyleProperty,
        TextBlock.FontStretchProperty, TextBlock.PaddingProperty,
        TemplatedControl.FontSizeProperty, TemplatedControl.FontFamilyProperty, TemplatedControl.FontWeightProperty,
        TemplatedControl.FontStyleProperty, TemplatedControl.FontStretchProperty, TemplatedControl.PaddingProperty,
        TemplatedControl.BorderThicknessProperty, Border.PaddingProperty, Border.BorderThicknessProperty,
        MarginProperty, WidthProperty, MinWidthProperty, MaxWidthProperty,
        StackPanel.SpacingProperty, StackPanel.OrientationProperty, WrapPanel.ItemSpacingProperty, DockPanel.DockProperty
    ];

    static DetailHeaderPanel() => AffectsMeasure<DetailHeaderPanel>(RowSpacingProperty);

    public static DetailHeaderRole GetRole(Control control) => control.GetValue(RoleProperty);
    public static void SetRole(Control control, DetailHeaderRole value) => control.SetValue(RoleProperty, value);

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>True when the actions sit on their own line(s) below the title row.</summary>
    public bool IsStacked => _stacked;

    /// <summary>Current typography scale: 1 = authored sizes, 0 = minimum sizes.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Chooses the typography scale for an available width without touching the live controls.</summary>
    public (double Scale, bool Stacked) ChooseScale(double availableWidth)
    {
        if (Children.Count != 2 || double.IsInfinity(availableWidth)) return (1, false);
        bool Fits(double scale) => OneLineWidth(scale) + MinActionsGap + FitTolerance <= availableWidth;

        if (Fits(1)) return (1, false);
        if (!Fits(0)) return (0, true);
        double lo = 0, hi = 1;
        for (var i = 0; i < SearchSteps; i++)
        {
            var mid = (lo + hi) / 2;
            if (Fits(mid)) lo = mid; else hi = mid;
        }
        return (lo, false);
    }

    // Estimated one-line width (title row + actions) at a scale. It depends only on the content, not on the
    // available width, and the bisection always probes the same scales, so it is cached per scale until a watched
    // property of a descendant changes (content, text, visibility, font, padding, size).
    private double OneLineWidth(double scale)
    {
        if (_oneLineWidths.TryGetValue(scale, out var cached)) return cached;
        var roles = Roles();
        var estimator = new WidthEstimator(new WidthEstimator.Values
        {
            FontSize = c => Find(roles, c) is { } r ? r.FontSizeAt(scale) : null,
            Padding = c => Find(roles, c) is { } r ? r.PaddingAt(scale) : null,
            Width = c => Find(roles, c) is { } r ? r.WidthAt(scale) : null
        });
        var width = estimator.Estimate(Children[0]) + estimator.Estimate(Children[1]);
        _oneLineWidths[scale] = width;
        return width;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2)
            throw new InvalidOperationException($"{nameof(DetailHeaderPanel)} expects exactly two children (title row, actions).");
        var title = Children[0];
        var actions = Children[1];

        var (scale, stacked) = ChooseScale(availableSize.Width);
        Scale = scale;
        _stacked = stacked;
        ApplyOnce(scale);

        if (!_stacked)
        {
            var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
            actions.Measure(unbounded);
            var actionsWidth = actions.DesiredSize.Width;
            title.Measure(double.IsInfinity(availableSize.Width)
                ? unbounded
                : new Size(Math.Max(0, availableSize.Width - actionsWidth), availableSize.Height));
            return new Size(title.DesiredSize.Width + actionsWidth, Math.Max(title.DesiredSize.Height, actions.DesiredSize.Height));
        }

        var row = new Size(availableSize.Width, double.PositiveInfinity);
        title.Measure(row);
        actions.Measure(row);
        return new Size(
            Math.Max(title.DesiredSize.Width, actions.DesiredSize.Width),
            title.DesiredSize.Height + RowSpacing + actions.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var title = Children[0];
        var actions = Children[1];
        if (!_stacked)
        {
            var actionsWidth = Math.Min(actions.DesiredSize.Width, finalSize.Width);
            title.Arrange(new Rect(0, 0, finalSize.Width - actionsWidth, finalSize.Height));
            actions.Arrange(new Rect(finalSize.Width - actionsWidth, 0, actionsWidth, finalSize.Height));
        }
        else
        {
            var titleHeight = title.DesiredSize.Height;
            title.Arrange(new Rect(0, 0, finalSize.Width, titleHeight));
            actions.Arrange(new Rect(0, titleHeight + RowSpacing, finalSize.Width, actions.DesiredSize.Height));
        }
        return finalSize;
    }

    // Writes the chosen values once; only controls whose value really changes are touched, and each is invalidated
    // together with its template and its ancestors below this panel, so the single measure that follows is fresh.
    private void ApplyOnce(double scale)
    {
        _applying = true;
        try
        {
            foreach (var role in Roles())
            {
                if (!role.Control.IsVisible) continue;
                if (role.Apply(scale)) InvalidateForRemeasure(role.Control);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void InvalidateForRemeasure(Control control)
    {
        control.InvalidateMeasure();
        foreach (var d in control.GetVisualDescendants().OfType<Layoutable>()) d.InvalidateMeasure();
        for (var node = control.GetVisualParent(); node is Layoutable l && !ReferenceEquals(node, this); node = node.GetVisualParent())
            l.InvalidateMeasure();
    }

    private static RoleEntry? Find(List<RoleEntry> roles, Control c)
    {
        foreach (var r in roles) if (ReferenceEquals(r.Control, c)) return r;
        return null;
    }

    // Role controls are XAML children (logical tree), not template parts; cached until the panel is re-attached.
    private List<RoleEntry> Roles()
        => _roles ??= this.GetLogicalDescendants().OfType<Control>()
            .Where(c => GetRole(c) != DetailHeaderRole.None)
            .Select(c => _entries.GetValue(c, key => new RoleEntry(key, GetRole(key))))
            .ToList();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _roles = null;
        _oneLineWidths.Clear();
        // A content or visibility change that keeps a child's desired size would not re-measure this panel; the
        // chosen scale depends on it, so ask for a measure explicitly.
        foreach (var c in this.GetLogicalDescendants().OfType<Control>())
        {
            c.PropertyChanged += OnDescendantPropertyChanged;
            _watched.Add(c);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var c in _watched) c.PropertyChanged -= OnDescendantPropertyChanged;
        _watched.Clear();
        _roles = null;
        _oneLineWidths.Clear();
    }

    private void OnDescendantPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // The panel's own writes (ApplyOnce) are outputs, not inputs: the estimate substitutes the role values.
        if (_applying || !EstimateInputs.Contains(e.Property)) return;
        _oneLineWidths.Clear();
        InvalidateMeasure();
    }

    private static double Lerp(double min, double max, double scale) => min + (max - min) * scale;

    /// <summary>A role control with its authored values (captured before the panel first writes them).</summary>
    private sealed class RoleEntry
    {
        private readonly bool _fontSizeLocal, _paddingLocal;
        private readonly double _fontSize, _width, _height;
        private readonly Thickness _padding;

        public RoleEntry(Control control, DetailHeaderRole role)
        {
            Control = control;
            Role = role;
            switch (control)
            {
                case TextBlock t:
                    _fontSizeLocal = t.IsSet(TextBlock.FontSizeProperty);
                    _fontSize = t.FontSize;
                    break;
                case Button b:
                    _fontSizeLocal = b.IsSet(TemplatedControl.FontSizeProperty);
                    _fontSize = b.FontSize;
                    _paddingLocal = b.IsSet(TemplatedControl.PaddingProperty);
                    _padding = b.Padding;
                    break;
            }
            _width = control.Width;
            _height = control.Height;
        }

        public Control Control { get; }
        public DetailHeaderRole Role { get; }

        public double? FontSizeAt(double scale) => Role switch
        {
            DetailHeaderRole.Title => scale >= 1 ? _fontSize : Lerp(Math.Min(TitleMinFontSize, _fontSize), _fontSize, scale),
            DetailHeaderRole.Subtitle or DetailHeaderRole.Action => scale >= 1 ? _fontSize : Lerp(_fontSize - 1, _fontSize, scale),
            _ => null
        };

        public Thickness? PaddingAt(double scale)
        {
            if (Role != DetailHeaderRole.Action) return null;
            var f = scale >= 1 ? 1 : Lerp(0.75, 1, scale);
            return new Thickness(_padding.Left * f, _padding.Top * f, _padding.Right * f, _padding.Bottom * f);
        }

        public double? WidthAt(double scale)
            => Role == DetailHeaderRole.Badge && !double.IsNaN(_width)
                ? (scale >= 1 ? _width : Lerp(Math.Min(BadgeMinSize, _width), _width, scale))
                : null;

        /// <summary>Writes the values for <paramref name="scale"/>; true when anything changed.</summary>
        public bool Apply(double scale)
        {
            var changed = false;
            switch (Role)
            {
                case DetailHeaderRole.Title or DetailHeaderRole.Subtitle when Control is TextBlock text:
                    changed |= Set(text, TextBlock.FontSizeProperty, FontSizeAt(scale)!.Value, scale >= 1 && !_fontSizeLocal);
                    break;
                case DetailHeaderRole.Action when Control is Button button:
                    changed |= Set(button, TemplatedControl.FontSizeProperty, FontSizeAt(scale)!.Value, scale >= 1 && !_fontSizeLocal);
                    changed |= Set(button, TemplatedControl.PaddingProperty, PaddingAt(scale)!.Value, scale >= 1 && !_paddingLocal);
                    break;
                case DetailHeaderRole.Badge when WidthAt(scale) is { } size:
                    changed |= Set(Control, WidthProperty, size, false);
                    changed |= Set(Control, HeightProperty, size * (_height / _width), false);
                    break;
            }
            return changed;
        }

        // At scale 1 a value that came from a style is cleared back to the style rather than pinned locally.
        private static bool Set<T>(Control control, StyledProperty<T> property, T value, bool clearToStyle)
        {
            if (clearToStyle)
            {
                if (!control.IsSet(property)) return false;
                control.ClearValue(property);
                return true;
            }
            if (control.IsSet(property) && EqualityComparer<T>.Default.Equals(control.GetValue(property), value)) return false;
            control.SetValue(property, value);
            return true;
        }
    }
}
