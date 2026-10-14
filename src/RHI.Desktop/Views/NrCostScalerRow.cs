using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The NR Cost Scaler row in the Neural Rendering versions box (#96): one AXAML element (<c>&lt;views:NrCostScalerRow/&gt;</c>)
/// over the shell's DataContext; all state comes from <see cref="NrCostScalerPresenter"/>. Hidden when it does not apply.
/// </summary>
public sealed class NrCostScalerRow : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly TextBlock _status = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _install = new() { Content = T("Parity.NrScalerInstall"), Padding = new Thickness(12, 6), Name = "NrCostScalerInstall" };
    private readonly Button _remove = new() { Content = T("Parity.NrScalerRemove"), Padding = new Thickness(12, 6), Name = "NrCostScalerRemove" };
    private MainWindowViewModel? _vm;

    public NrCostScalerRow()
    {
        IsVisible = false;
        var label = new TextBlock { Text = "NR Cost Scaler", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(label, T("Parity.NrScalerTip"));
        ToolTip.SetShowDelay(label, MiddleEllipsisText.TooltipDelay);
        ToolTip.SetTip(_install, T("Parity.NrScalerInstallTip"));
        ToolTip.SetShowDelay(_install, MiddleEllipsisText.TooltipDelay);
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        buttons.Children.Add(_install);
        buttons.Children.Add(_remove);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(label);
        grid.Children.Add(_status);
        Grid.SetColumn(_status, 1);
        grid.Children.Add(buttons);
        Grid.SetColumn(buttons, 2);
        Content = grid;
        _install.Click += (_, _) => { if (_vm?.Parity is { } p) UiExceptionGuard.Forget(p.NrCostScaler.InstallAsync(_vm), "NrCostScalerInstall"); };
        _remove.Click += (_, _) => { if (_vm?.Parity is { } p) UiExceptionGuard.Forget(p.NrCostScaler.RemoveAsync(_vm), "NrCostScalerRemove"); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.PropertyChanged += OnVmChanged; }
        Sync();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedGame) or nameof(MainWindowViewModel.IsBusy)
            or nameof(MainWindowViewModel.IsSelectionProbeBusy) or nameof(MainWindowViewModel.StatusText))
            Sync();
    }

    private void Sync()
    {
        var state = _vm?.Parity?.NrCostScaler.StateFor(_vm);
        IsVisible = state?.Visible == true;
        if (state is null || !state.Visible) return;
        _status.Text = state.Status;
        _install.IsVisible = !state.Installed;
        _remove.IsVisible = state.Installed;
        _install.IsEnabled = _remove.IsEnabled = state.CanAct;
    }
}
