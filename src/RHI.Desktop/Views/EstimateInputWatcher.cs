using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace RHI.Desktop.Views;

/// <summary>
/// Tells a panel that caches <see cref="WidthEstimator"/> results when they may be stale. It watches the panel's
/// logical descendants for a change to any <see cref="WidthEstimator.Inputs"/> property, and the Children collection
/// of every panel in the subtree (the watching panel's own included) for controls added or removed, e.g. a button
/// added to the actions panel at runtime. A structural change, or a content swap (Content/Child, e.g. a Button whose
/// Content changes from a string to a control), rebuilds the watch set, so late-added descendants are watched too.
/// Template-generated children (a ContentPresenter's TextBlock for string content, created on first measure) are
/// deliberately not tracked as structure: the estimate reads the Content, not the presenter's child.
/// A panel whose own cache also reads properties outside <see cref="WidthEstimator.Inputs"/> on its descendants
/// (its attached layout markers: toolbar slot/separator/collapsible, header role) passes them as extra inputs; its
/// own properties (e.g. toolbar spacings) are the panel's to watch. Shared by <see cref="ToolbarPanel"/> and
/// <see cref="DetailHeaderPanel"/>.
/// </summary>
public sealed class EstimateInputWatcher
{
    private readonly Control _root;
    private readonly Func<Control, AvaloniaPropertyChangedEventArgs, bool> _isInput;
    private readonly HashSet<AvaloniaProperty> _extraInputs;
    private readonly Action _changed;
    private readonly List<Control> _properties = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private bool _watching;

    /// <param name="root">The panel; its descendants' properties and its whole subtree's children are watched.</param>
    /// <param name="isInput">Extra filter (the panel's own writes are outputs, not inputs); called only for
    /// <see cref="WidthEstimator.Inputs"/> and <paramref name="extraInputs"/> properties.</param>
    /// <param name="changed">Drops the cache and invalidates the panel's measure.</param>
    /// <param name="extraInputs">Descendant properties the panel's own cache reads besides
    /// <see cref="WidthEstimator.Inputs"/> (attached layout markers).</param>
    public EstimateInputWatcher(Control root, Func<Control, AvaloniaPropertyChangedEventArgs, bool> isInput, Action changed,
        IEnumerable<AvaloniaProperty>? extraInputs = null)
    {
        _root = root;
        _isInput = isInput;
        _changed = changed;
        _extraInputs = extraInputs is null ? [] : [.. extraInputs];
    }

    /// <summary>Number of controls whose properties are watched (diagnostics, tests).</summary>
    public int WatchedCount => _properties.Count;

    public void Watch()
    {
        Unwatch();
        _watching = true;
        Subscribe(_root);
        foreach (var c in _root.GetLogicalDescendants().OfType<Control>())
        {
            c.PropertyChanged += OnPropertyChanged;
            _properties.Add(c);
            Subscribe(c);
        }
    }

    public void Unwatch()
    {
        _watching = false;
        foreach (var c in _properties) c.PropertyChanged -= OnPropertyChanged;
        foreach (var l in _collections) l.CollectionChanged -= OnChildrenChanged;
        _properties.Clear();
        _collections.Clear();
    }

    private void Subscribe(Control node)
    {
        if (node is not Panel panel) return;
        panel.Children.CollectionChanged += OnChildrenChanged;
        _collections.Add(panel.Children);
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!(WidthEstimator.Inputs.Contains(e.Property) || _extraInputs.Contains(e.Property))
            || sender is not Control c || !_isInput(c, e)) return;
        if (_watching && (e.Property == ContentControl.ContentProperty || e.Property == Border.ChildProperty)
            && (e.OldValue is Control || e.NewValue is Control))
            Watch();
        _changed();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_watching) return;
        Watch();
        _changed();
    }
}
