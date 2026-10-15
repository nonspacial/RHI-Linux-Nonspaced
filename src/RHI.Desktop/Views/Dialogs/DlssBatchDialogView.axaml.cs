using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>The DLSS / Streamline batch dialog's layout; below 700 px the versions go under the games.</summary>
public sealed partial class DlssBatchDialogView : UserControl
{
    public DlssBatchDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, args) => ArrangeBody(args.NewSize.Width);
    }

    /// <summary>Narrow dialogs (capped to the main window) stack the two columns; the body scrolls, the footer stays.</summary>
    private void ArrangeBody(double width)
    {
        if (this.FindControl<Grid>("Body") is not { } body || this.FindControl<Control>("GamesColumn") is not { } left
            || this.FindControl<Control>("VersionsColumn") is not { } right || this.FindControl<Border>("Divider") is not { } divider) return;
        var stacked = width < 700;
        body.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,Auto,*");
        body.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto,Auto" : "*");
        body.RowSpacing = stacked ? 14 : 0;
        left.MinWidth = stacked ? 0 : 350;
        right.MinWidth = stacked ? 0 : 270;
        Grid.SetColumn(divider, stacked ? 0 : 1); Grid.SetRow(divider, stacked ? 1 : 0);
        divider.Width = stacked ? double.NaN : 1;
        divider.Height = stacked ? 1 : double.NaN;
        Grid.SetColumn(right, stacked ? 0 : 2); Grid.SetRow(right, stacked ? 2 : 0);
    }
}
