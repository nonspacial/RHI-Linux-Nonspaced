using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
/// The scale only ever decreases as the width decreases, so the header never jumps back up.
/// </summary>
public sealed class DetailHeaderPanel : Panel
{
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<DetailHeaderPanel, double>(nameof(RowSpacing), 8);

    public static readonly AttachedProperty<DetailHeaderRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<DetailHeaderPanel, Control, DetailHeaderRole>("Role");

    private const double TitleMinFontSize = 16;
    private const double BadgeMinSize = 36;
    private const int SearchSteps = 10;

    // Authored values captured before the panel first overrides them.
    private readonly ConditionalWeakTable<Control, Authored> _authored = new();
    private bool _stacked;

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

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2)
            throw new InvalidOperationException($"{nameof(DetailHeaderPanel)} expects exactly two children (title row, actions).");
        var title = Children[0];
        var actions = Children[1];
        var scaled = ScaledDescendants();
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);

        bool FitsOnOneLine(double scale)
        {
            Apply(scaled, scale);
            title.Measure(unbounded);
            actions.Measure(unbounded);
            return title.DesiredSize.Width + actions.DesiredSize.Width <= availableSize.Width;
        }

        double chosen;
        if (double.IsInfinity(availableSize.Width) || FitsOnOneLine(1))
        {
            chosen = 1;
            _stacked = false;
        }
        else if (!FitsOnOneLine(0))
        {
            chosen = 0;
            _stacked = true;
        }
        else
        {
            double lo = 0, hi = 1;
            for (var i = 0; i < SearchSteps; i++)
            {
                var mid = (lo + hi) / 2;
                if (FitsOnOneLine(mid)) lo = mid; else hi = mid;
            }
            chosen = lo;
            _stacked = false;
        }

        Scale = chosen;
        Apply(scaled, chosen);
        if (!_stacked)
        {
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

    private List<(Control Control, DetailHeaderRole Role, Authored Authored)> ScaledDescendants()
    {
        var list = new List<(Control, DetailHeaderRole, Authored)>();
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is not Control control) continue;
            var role = GetRole(control);
            if (role == DetailHeaderRole.None) continue;
            if (!_authored.TryGetValue(control, out var authored))
            {
                authored = Authored.Capture(control);
                _authored.Add(control, authored);
            }
            list.Add((control, role, authored));
        }
        return list;
    }

    private static double Lerp(double min, double max, double scale) => min + (max - min) * scale;

    private void Apply(List<(Control Control, DetailHeaderRole Role, Authored Authored)> scaled, double scale)
    {
        ApplyValues(scaled, scale);
        // A changed descendant only invalidates itself; its intermediate panels would otherwise answer the
        // trial measures below from their cached desired size.
        foreach (var (control, _, _) in scaled)
            for (var node = control.GetVisualParent(); node is Layoutable layoutable && !ReferenceEquals(node, this); node = node.GetVisualParent())
                layoutable.InvalidateMeasure();
    }

    private static void ApplyValues(List<(Control Control, DetailHeaderRole Role, Authored Authored)> scaled, double scale)
    {
        foreach (var (control, role, a) in scaled)
        {
            switch (role)
            {
                case DetailHeaderRole.Title when control is TextBlock text:
                    text.FontSize = scale >= 1 ? a.FontSize : Lerp(Math.Min(TitleMinFontSize, a.FontSize), a.FontSize, scale);
                    break;
                case DetailHeaderRole.Subtitle when control is TextBlock text:
                    text.FontSize = scale >= 1 ? a.FontSize : Lerp(a.FontSize - 1, a.FontSize, scale);
                    break;
                case DetailHeaderRole.Badge:
                    var size = scale >= 1 ? a.Width : Lerp(Math.Min(BadgeMinSize, a.Width), a.Width, scale);
                    control.Width = size;
                    control.Height = size * (a.Height / a.Width);
                    break;
                case DetailHeaderRole.Action when control is Button button:
                    button.FontSize = scale >= 1 ? a.FontSize : Lerp(a.FontSize - 1, a.FontSize, scale);
                    var p = a.Padding;
                    var f = scale >= 1 ? 1 : Lerp(0.75, 1, scale);
                    button.Padding = new Thickness(p.Left * f, p.Top * f, p.Right * f, p.Bottom * f);
                    break;
            }
        }
    }

    private sealed record Authored(double FontSize, double Width, double Height, Thickness Padding)
    {
        public static Authored Capture(Control control) => control switch
        {
            TextBlock t => new(t.FontSize, t.Width, t.Height, default),
            Button b => new(b.FontSize, b.Width, b.Height, b.Padding),
            _ => new(double.NaN, control.Width, control.Height, default)
        };
    }
}
