using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/ui-settings (#297, upstream 2.8.9/2.9.0 <c>CogDialog</c>): the measures every component ⚙ shares — a fixed label
/// column with the option beside it and the explanation under it, cards, the compact two-per-row grid. Colours come from
/// the App.axaml brushes through style classes (<c>cogCard</c>, <c>cogLabel</c>, <c>cogHelp</c>, …), never from here.
/// </summary>
public static class CogLayout
{
    /// <summary>Upstream <c>LabelColumnWidth</c>: the setting names' column.</summary>
    public const double LabelColumnWidth = 170;
    /// <summary>Upstream <c>ColumnGap</c>.</summary>
    public const double ColumnGap = 12;
    /// <summary>Upstream <c>CompactControlWidth</c>: the dropdown column of the two-per-row grid.</summary>
    public const double CompactControlWidth = 160;
    /// <summary>Upstream <c>DisabledOpacity</c>: rows that can't be used yet (component not installed).</summary>
    public const double DisabledOpacity = 0.45;
    /// <summary>The 2 s tooltip delay the app uses everywhere (owner rule).</summary>
    public const int TooltipDelay = 2000;

    /// <summary>Sets <paramref name="tip"/> as a 2 s tooltip (cleared when empty); shown on disabled controls too.</summary>
    public static void Tip(Control control, string? tip)
    {
        ToolTip.SetTip(control, string.IsNullOrWhiteSpace(tip) ? null : tip);
        ToolTip.SetShowDelay(control, TooltipDelay);
        ToolTip.SetShowOnDisabled(control, true);
    }

    /// <summary>A ⚙ text line: <c>cogIntro</c> (grey), <c>cogWarning</c> (amber), <c>cogNote</c> (small grey) or <c>cogEmpty</c>.</summary>
    public static TextBlock Text(string text, string kind) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { kind } };
}

/// <summary>
/// One ⚙ setting (upstream <c>CogDialog.AddRow</c>): the name in the fixed left column, the control stretched beside it, and
/// the explanation in small grey text underneath. The explanation is also the name's (and the control's) 2 s tooltip.
/// Usable from AXAML (<c>&lt;views:CogRow Label="…" Help="…"&gt;&lt;ComboBox/&gt;&lt;/views:CogRow&gt;</c>) and from code.
/// </summary>
public class CogRow : Control
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<CogRow, string?>(nameof(Label));
    public static readonly StyledProperty<string?> HelpProperty = AvaloniaProperty.Register<CogRow, string?>(nameof(Help));
    public static readonly StyledProperty<Control?> SettingProperty = AvaloniaProperty.Register<CogRow, Control?>(nameof(Setting));

    private readonly TextBlock _label = new() { Classes = { "cogLabel" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _help = new()
    {
        Classes = { "cogHelp" }, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        Margin = new Thickness(CogLayout.LabelColumnWidth + CogLayout.ColumnGap, 0, 0, 2)
    };
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions($"{CogLayout.LabelColumnWidth},*"), ColumnSpacing = CogLayout.ColumnGap };
    // The control's tooltip is the explanation unless the caller gave it its own.
    private bool _settingTipIsHelp;

    public CogRow()
    {
        _grid.Children.Add(_label);
        var root = new StackPanel { Spacing = 3 };
        root.Children.Add(_grid);
        root.Children.Add(_help);
        LogicalChildren.Add(root);
        VisualChildren.Add(root);
    }

    public CogRow(string label, Control setting, string? help = null) : this()
    {
        Label = label;
        Help = help;
        Setting = setting;
    }

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Help { get => GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    [Content]
    public Control? Setting { get => GetValue(SettingProperty); set => SetValue(SettingProperty, value); }

    /// <summary>The name text (tests, and callers that dim a row).</summary>
    public TextBlock LabelBlock => _label;
    /// <summary>The explanation under the row.</summary>
    public TextBlock HelpBlock => _help;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) _label.Text = Label;
        else if (change.Property == HelpProperty)
        {
            _help.Text = Help;
            _help.IsVisible = !string.IsNullOrWhiteSpace(Help);
            CogLayout.Tip(_label, Help);
            if (Setting is { } setting && (_settingTipIsHelp || ToolTip.GetTip(setting) is null))
            {
                CogLayout.Tip(setting, Help);
                _settingTipIsHelp = true;
            }
        }
        else if (change.Property == SettingProperty)
        {
            if (change.OldValue is Control old) _grid.Children.Remove(old);
            if (change.NewValue is Control control)
            {
                if (control.HorizontalAlignment != HorizontalAlignment.Left) control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                _grid.Children.Add(control);
                _settingTipIsHelp = ToolTip.GetTip(control) is null;
                if (_settingTipIsHelp) CogLayout.Tip(control, Help);
            }
        }
    }
}

/// <summary>
/// One cell of the compact two-per-row grid (upstream <c>AddCompactGrid</c>, kept for OptiScaler's and RenoDX's compatibility
/// settings and the other dialogs with many small dropdowns): name | dropdown, with the explanation as a 2 s tooltip on
/// both (a disabled control shows no tooltip of its own). Put cells in a <see cref="CogCompactGrid"/>.
/// </summary>
public class CogCell : Control
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<CogCell, string?>(nameof(Label));
    public static readonly StyledProperty<string?> HelpProperty = AvaloniaProperty.Register<CogCell, string?>(nameof(Help));
    public static readonly StyledProperty<Control?> SettingProperty = AvaloniaProperty.Register<CogCell, Control?>(nameof(Setting));

    private readonly TextBlock _label = new() { Classes = { "cogLabel" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions($"*,{CogLayout.CompactControlWidth}"), ColumnSpacing = CogLayout.ColumnGap };

    public CogCell()
    {
        _grid.Children.Add(_label);
        LogicalChildren.Add(_grid);
        VisualChildren.Add(_grid);
    }

    public CogCell(string label, Control setting, string? help = null) : this()
    {
        Label = label;
        Help = help;
        Setting = setting;
    }

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Help { get => GetValue(HelpProperty); set => SetValue(HelpProperty, value); }

    [Content]
    public Control? Setting { get => GetValue(SettingProperty); set => SetValue(SettingProperty, value); }

    public TextBlock LabelBlock => _label;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) _label.Text = Label;
        else if (change.Property == HelpProperty)
        {
            CogLayout.Tip(_label, Help);
            if (Setting is { } setting) CogLayout.Tip(setting, Help);
        }
        else if (change.Property == SettingProperty)
        {
            if (change.OldValue is Control old) _grid.Children.Remove(old);
            if (change.NewValue is Control control)
            {
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                _grid.Children.Add(control);
                CogLayout.Tip(control, Help);
            }
        }
    }
}

/// <summary>The compact grid's panel: cells two per row (upstream: label | control | gap | label | control).</summary>
public class CogCompactGrid : UniformGrid
{
    public CogCompactGrid()
    {
        Columns = 2;
        ColumnSpacing = 24;
        RowSpacing = 8;
    }
}

/// <summary>
/// A ⚙ card (upstream <c>AddSection</c> on the Tabs look): a rounded panel with an optional small uppercase heading;
/// everything inside is one section. Usable from AXAML (<c>&lt;views:CogCard Header="…"&gt;…&lt;/views:CogCard&gt;</c>).
/// </summary>
public class CogCard : Control
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<CogCard, string?>(nameof(Header));

    private readonly TextBlock _heading = new() { Classes = { "cogHeading" }, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
    private readonly StackPanel _items = new() { Spacing = 10 };

    public CogCard()
    {
        var inner = new StackPanel { Spacing = 10 };
        inner.Children.Add(_heading);
        inner.Children.Add(_items);
        var border = new Border { Classes = { "cogCard" }, Child = inner };
        LogicalChildren.Add(border);
        VisualChildren.Add(border);
    }

    public CogCard(string? header) : this() => Header = header;

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The card's content, top to bottom.</summary>
    [Content]
    public Avalonia.Controls.Controls Children => _items.Children;

    public TextBlock HeadingBlock => _heading;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HeaderProperty) return;
        // Upstream's TabsTheme.SectionHeading: small uppercase.
        _heading.Text = Header?.ToUpperInvariant();
        _heading.IsVisible = !string.IsNullOrWhiteSpace(Header);
    }
}

/// <summary>Button look inside a ⚙ (upstream <c>CogButtonKind</c>).</summary>
public enum CogButtonKind
{
    /// <summary>Main action, e.g. "Deploy X.ini" (accent).</summary>
    Primary,
    /// <summary>Secondary action, e.g. "Open X.ini", "Open log" (grey).</summary>
    Secondary,
    /// <summary>Destructive action, e.g. "Delete folder" (red).</summary>
    Danger,
}

/// <summary>The ⚙ status line's tone (upstream <c>SetStatus</c> / <c>SetSuccess</c> / <c>SetError</c>).</summary>
public enum CogStatusKind { Neutral, Success, Error }
