using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The OptiScaler ⚙ dialog's \"Nightly build\" row (#156), laid out like the channel row above it (label column, combo,
/// note). All state lives in <see cref="OptiScalerNightlyBuildPresenter"/>; the list is fetched when the row is shown.
/// </summary>
public sealed class OptiScalerNightlyBuildRow : UserControl
{
    private readonly OptiScalerNightlyBuildPresenter _presenter;
    private readonly ComboBox _combo;
    private readonly TextBlock _note;
    private bool _syncing;

    public OptiScalerNightlyBuildRow(OptiScalerNightlyBuildPresenter presenter)
    {
        _presenter = presenter;
        var label = new TextBlock
        {
            Text = UiLocalizationService.Text("Sync.OptiScalerNightlyBuild"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        ToolTip.SetTip(label, UiLocalizationService.Text("Sync.OptiScalerNightlyBuildTip"));
        ToolTip.SetShowDelay(label, 2000);
        // Sized like the channel combo above it (its 260 px minimum, left-aligned), so the two rows line up.
        _combo = new ComboBox { ItemsSource = presenter.Options, MinWidth = 260 };
        ToolTip.SetTip(_combo, UiLocalizationService.Text("Sync.OptiScalerNightlyBuildTip"));
        ToolTip.SetShowDelay(_combo, 2000);
        _combo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _combo.SelectedItem is string value) _presenter.Selected = value;
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
        Grid.SetColumn(_combo, 1);
        row.Children.Add(label);
        row.Children.Add(_combo);
        _note = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.58 };
        Content = new StackPanel { Spacing = 6, Children = { row, _note } };

        presenter.PropertyChanged += (_, _) => Sync();
        presenter.Options.CollectionChanged += (_, _) => Sync();
        Sync();
        AttachedToVisualTree += (_, _) => UiExceptionGuard.Forget(_presenter.RefreshAsync(fetchList: true), "optiscaler-nightly-build");
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            IsVisible = _presenter.IsVisible;
            _combo.IsEnabled = _presenter.IsEnabled;
            _note.Text = _presenter.Note;
            if (!Equals(_combo.SelectedItem, _presenter.Selected)) _combo.SelectedItem = _presenter.Selected;
        }
        finally { _syncing = false; }
    }
}
