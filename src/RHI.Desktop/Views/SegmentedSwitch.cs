using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace RHI.Desktop.Views;

/// <summary>One choice of a <see cref="SegmentedSwitch"/>.</summary>
public sealed record SegmentItem(string Label, string? Tip = null, bool IsEnabled = true);

/// <summary>
/// w14/layouts-a (#303, upstream TabsTheme.MakeSegmented): a row of choices in one track, the chosen one lit (navy look,
/// App.axaml <c>segmentTrack</c> / <c>segment</c> classes). Used by the top bar's layout switch and the game list's
/// All / Installed / Favourites / Hidden. <see cref="SelectedIndex"/> -1 lights none (a collection is shown).
/// A click raises <see cref="SelectionRequested"/>; the owner decides and sets <see cref="SelectedIndex"/>.
/// </summary>
public sealed class SegmentedSwitch : ContentControl
{
    private readonly Border _track = new() { Classes = { "segmentTrack" } };
    private IReadOnlyList<SegmentItem> _items = [];
    private int _selected = -1;
    private bool _stretch;

    public SegmentedSwitch()
    {
        Content = _track;
        Focusable = false;
    }

    /// <summary>Raised with the index of the segment clicked (also the one already chosen).</summary>
    public event Action<int>? SelectionRequested;

    /// <summary>The segments share the width equally (the game list); otherwise each is as wide as its label.</summary>
    public bool Stretch
    {
        get => _stretch;
        set { _stretch = value; Build(); }
    }

    public IReadOnlyList<SegmentItem> Items
    {
        get => _items;
        set
        {
            if (_items.SequenceEqual(value)) return;
            _items = value;
            Build();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Light();
        }
    }

    /// <summary>The segment buttons, in order (tests and the window read them).</summary>
    public IReadOnlyList<Button> Segments { get; private set; } = [];

    private void Build()
    {
        Panel host = _stretch
            ? new UniformGrid { Rows = 1, Columns = Math.Max(1, _items.Count) }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var buttons = new List<Button>();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = _items[i];
            var button = new Button
            {
                Content = item.Label, Classes = { "segment" }, IsEnabled = item.IsEnabled, Name = Name is { Length: > 0 } name ? $"{name}{i}" : null,
                HorizontalAlignment = _stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            };
            if (item.Tip is { Length: > 0 } tip)
            {
                ToolTip.SetTip(button, tip);
                ToolTip.SetShowOnDisabled(button, true);
            }
            button.Click += (_, e) =>
            {
                e.Handled = true;
                SelectionRequested?.Invoke(index);
            };
            host.Children.Add(button);
            buttons.Add(button);
        }
        Segments = buttons;
        _track.Child = host;
        Light();
    }

    private void Light()
    {
        for (var i = 0; i < Segments.Count; i++) Segments[i].Classes.Set("on", i == _selected);
    }

    /// <summary>Clicks a segment as the user would (keyboard and tests).</summary>
    public void Click(int index)
    {
        if (index >= 0 && index < Segments.Count && Segments[index].IsEffectivelyEnabled)
            Segments[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
