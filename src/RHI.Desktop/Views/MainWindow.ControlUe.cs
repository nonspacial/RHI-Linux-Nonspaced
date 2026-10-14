using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w3/dllnames (#136): the Control UE RenoDX confirmation with upstream 2.8.1's two choices under the text — "Install
/// OptiScaler FG" and "Using HDR?" (open only with FG = Yes, back to No when FG goes to No). All text and state live in
/// <see cref="ControlUeInstallDialogModel"/>; this only lays it out like the plain confirmation.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> ShowControlUeConfirmationAsync(ControlUeInstallDialogModel model)
    {
        var accept = new Button { Content = model.AcceptText, Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);

        var frameGeneration = new ComboBox
        {
            Name = "ControlUeFrameGeneration",
            ItemsSource = model.Choices,
            SelectedIndex = model.InstallOptiScalerFg ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(frameGeneration, model.FrameGenerationTip);
        var hdr = new ComboBox
        {
            Name = "ControlUeHdr",
            ItemsSource = model.Choices,
            SelectedIndex = model.UseHdr ? 1 : 0,
            IsEnabled = model.CanChooseHdr,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(hdr, model.HdrTip);
        frameGeneration.SelectionChanged += (_, _) =>
        {
            model.InstallOptiScalerFg = frameGeneration.SelectedIndex == 1;
            hdr.IsEnabled = model.CanChooseHdr;
            if (!model.CanChooseHdr) hdr.SelectedIndex = 0;
        };
        hdr.SelectionChanged += (_, _) => model.UseHdr = hdr.SelectedIndex == 1;

        static StackPanel Choice(string label, Control control)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(control);
            return panel;
        }

        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, MaxWidth = 560 };
        var fgChoice = Choice(model.FrameGenerationLabel, frameGeneration);
        var hdrChoice = Choice(model.HdrLabel, hdr);
        choices.Children.Add(fgChoice);
        choices.Children.Add(hdrChoice);
        Grid.SetColumn(hdrChoice, 1);

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = model.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        panel.Children.Add(choices);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = model.Title,
            Width = 600,
            Height = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        return await ShowFittedDialogAsync<bool>(dialog);
    }
}
