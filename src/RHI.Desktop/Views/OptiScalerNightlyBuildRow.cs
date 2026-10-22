using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The OptiScaler ⚙ dialog's \"Nightly build\" row (#156), laid out like the channel row above it (label column, combo,
/// note). #232: an \"Apply\" button beside the combo (shown while the chosen build differs from the installed Nightly's)
/// and, while the switch can still stop, a Cancel in its place; both wrap under the combo, right-aligned, when the row is
/// too narrow for one line (w11 plan §8). All state lives in <see cref="OptiScalerNightlyBuildPresenter"/>; the list is
/// fetched when the row is shown.
/// </summary>
public sealed class OptiScalerNightlyBuildRow : UserControl
{
    private readonly OptiScalerNightlyBuildPresenter _presenter;
    private readonly ComboBox _combo;
    private readonly Button _apply;
    private readonly Button _cancel;
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
        _combo = new ComboBox { ItemsSource = presenter.Options, MinWidth = 260, Name = "OptiScalerNightlyBuildCombo" };
        ToolTip.SetTip(_combo, UiLocalizationService.Text("Sync.OptiScalerNightlyBuildTip"));
        ToolTip.SetShowDelay(_combo, 2000);
        _combo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _combo.SelectedItem is string value) _presenter.Selected = value;
        };
        _apply = new Button { Content = UiLocalizationService.Text("Win.Apply"), Padding = new Thickness(12, 6), Name = "OptiScalerNightlyApply" };
        ToolTip.SetTip(_apply, UiLocalizationService.Text("Nsw.ApplyTip"));
        ToolTip.SetShowDelay(_apply, 2000);
        _apply.Click += (_, _) => UiExceptionGuard.Forget(_presenter.ApplyAsync(), "optiscaler-nightly-switch");
        _cancel = new Button
        {
            Content = UiLocalizationService.Text("Ui.CancelOperation"),
            Padding = new Thickness(12, 6),
            Command = presenter.Operations.CancelCommand,
            Name = "OptiScalerNightlyCancel"
        };
        _cancel.Classes.Add("rhi");
        var line = new ComboWithActions(_combo, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _apply, _cancel } });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
        Grid.SetColumn(line, 1);
        row.Children.Add(label);
        row.Children.Add(line);
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
            _combo.IsEnabled = _presenter.IsEnabled && !_presenter.IsApplying;
            _apply.IsVisible = _presenter.CanApply;
            _cancel.IsVisible = _presenter.CanCancel;
            _note.Text = _presenter.Note;
            if (!Equals(_combo.SelectedItem, _presenter.Selected)) _combo.SelectedItem = _presenter.Selected;
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// The combo and its buttons (w11 plan §8): on one line while both fit, the combo at its own width and the buttons
    /// right beside it; otherwise the buttons go under the combo, their right edge on the combo's. Decided from the
    /// children's natural widths (measured unbounded, never re-measured at a trial width) and the width the label column
    /// leaves, so the same width always gives the same layout whichever way the dialog is resized.
    /// </summary>
    internal sealed class ComboWithActions : Panel
    {
        public const double Spacing = 8;
        private readonly Control _combo;
        private readonly Control _actions;

        public ComboWithActions(Control combo, Control actions)
        {
            _combo = combo;
            _actions = actions;
            Children.Add(combo);
            Children.Add(actions);
        }

        /// <summary>True while the buttons sit under the combo (harness check).</summary>
        public bool IsWrapped { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            _actions.Measure(unbounded);
            _combo.Measure(unbounded);
            var combo = _combo.DesiredSize;
            var actions = _actions.IsVisible ? _actions.DesiredSize : default;
            if (combo.Width > availableSize.Width)
            {
                // Narrower than the combo's own width (never at the dialog's minimum): the combo takes what there is.
                _combo.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                combo = _combo.DesiredSize;
            }
            IsWrapped = actions.Width > 0 && combo.Width + Spacing + actions.Width > availableSize.Width;
            return IsWrapped
                ? new Size(Math.Max(combo.Width, actions.Width), combo.Height + Spacing + actions.Height)
                : new Size(combo.Width + (actions.Width > 0 ? Spacing + actions.Width : 0), Math.Max(combo.Height, actions.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var combo = _combo.DesiredSize;
            var actions = _actions.IsVisible ? _actions.DesiredSize : default;
            if (IsWrapped)
            {
                _combo.Arrange(new Rect(0, 0, combo.Width, combo.Height));
                // Right-aligned under the combo (the buttons are never wider than it at any offered width).
                _actions.Arrange(new Rect(Math.Max(0, combo.Width - actions.Width), combo.Height + Spacing, actions.Width, actions.Height));
            }
            else
            {
                var height = Math.Max(combo.Height, actions.Height);
                _combo.Arrange(new Rect(0, (height - combo.Height) / 2, combo.Width, combo.Height));
                _actions.Arrange(new Rect(combo.Width + Spacing, (height - actions.Height) / 2, actions.Width, actions.Height));
            }
            return finalSize;
        }
    }
}
