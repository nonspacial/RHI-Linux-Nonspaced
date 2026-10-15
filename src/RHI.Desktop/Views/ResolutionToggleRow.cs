using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #191: the resolution auto-toggle under the HDR controls (one AXAML element, DataContext the
/// <see cref="RuntimePanelViewModel"/>): a toggle, the resolution (shown only while the toggle is on) and the plain line
/// that says what will happen. Disabled with the HDR controls (no game, an operation running).
/// </summary>
public sealed class ResolutionToggleRow : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly CheckBox _toggle = new() { Content = T("Shell.ResToggle"), Name = "ResolutionToggle" };
    private readonly ComboBox _size = new() { MinWidth = 150, Name = "ResolutionSize" };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6B7A8E")) };
    private RuntimePanelViewModel? _vm;
    private bool _syncing;

    public ResolutionToggleRow()
    {
        ToolTip.SetTip(_toggle, T("Shell.ResToggleTip"));
        ToolTip.SetShowDelay(_toggle, MiddleEllipsisText.TooltipDelay);
        _size.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OptionItem>((item, _) => new TextBlock { Text = item?.Display });
        _toggle.IsCheckedChanged += (_, _) =>
        {
            if (_syncing || _vm is null) return;
            _vm.ResolutionToggle = _toggle.IsChecked == true;
        };
        _size.SelectionChanged += (_, _) =>
        {
            if (_syncing || _vm is null || _size.SelectedItem is not OptionItem<string> item) return;
            _vm.SelectedResolution = item;
        };
        var controls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        controls.Children.Add(_toggle);
        controls.Children.Add(_size);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(controls);
        panel.Children.Add(_hint);
        Content = panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as RuntimePanelViewModel;
        if (_vm is not null)
        {
            _size.ItemsSource = _vm.ResolutionOptions;
            _vm.PropertyChanged += OnVmChanged;
        }
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RuntimePanelViewModel.ResolutionToggle) or nameof(RuntimePanelViewModel.SelectedResolution)
            or nameof(RuntimePanelViewModel.ResolutionHint) or nameof(RuntimePanelViewModel.CanEditHdrProfile))
            Sync();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var on = _vm?.ResolutionToggle == true;
            _toggle.IsChecked = on;
            _toggle.IsEnabled = _size.IsEnabled = _vm?.CanEditHdrProfile == true;
            _size.IsVisible = on;
            if (_vm is not null) _size.SelectedItem = _vm.SelectedResolution;
            var hint = _vm?.ResolutionHint ?? "";
            _hint.Text = hint;
            _hint.IsVisible = on && hint.Length > 0;
        }
        finally { _syncing = false; }
    }
}
