using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #184: a component row's ⓘ button opens that component's notes for the selected game (<see cref="ComponentInfoPresenter"/>)
/// in a small scrolling dialog: headings, paragraphs as text, link buttons (each link in a 2 s tooltip) and Close. The dialog
/// opens at once with a loading line and is filled in when the notes are in.
/// </summary>
public partial class MainWindow
{
    /// <summary>The ⓘ dialog currently open (null when closed); the harness reads it.</summary>
    internal Window? OpenComponentInfoDialog { get; private set; }

    private void ComponentInfo_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasInfo) return;
        if (DataContext is not MainWindowViewModel vm || vm.Parity?.ComponentInfo is not { } info) return;
        await info.OpenAsync(vm.SelectedGame, row.Kind, row.Name, row.InstalledVersion);
    });

    /// <summary>Opens the ⓘ dialog (modal, fitted to the window) showing <paramref name="view"/>; the presenter fills it in later.</summary>
    private IComponentInfoDialog CreateComponentInfoDialog(ComponentInfoView view)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        var dialog = new Window
        {
            Width = 600,
            Height = 460,
            MinWidth = 360,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        var shown = new InfoDialog(dialog, panel, this);
        shown.Show(view);
        dialog.Closed += (_, _) => { if (ReferenceEquals(OpenComponentInfoDialog, dialog)) OpenComponentInfoDialog = null; };
        OpenComponentInfoDialog = dialog;
        shown.Closed = ShowFittedDialogAsync(dialog);
        return shown;
    }

    private sealed class InfoDialog(Window dialog, StackPanel panel, MainWindow owner) : IComponentInfoDialog
    {
        public Task Closed { get; set; } = Task.CompletedTask;

        public void Activate() => dialog.Activate();

        public void Show(ComponentInfoView view)
        {
            dialog.Title = view.Title;
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = view.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var block in view.Blocks)
                panel.Children.Add(new TextBlock
                {
                    Text = block.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = block.IsHeading ? FontWeight.SemiBold : FontWeight.Normal,
                    Margin = block.IsHeading ? new Thickness(0, 6, 0, 0) : default
                });
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
            foreach (var link in view.Links)
            {
                var button = new Button { Classes = { "rhi" }, Content = link.Label, Padding = new Thickness(12, 6) };
                ToolTip.SetTip(button, link.Url);
                ToolTip.SetShowDelay(button, 2000);
                var url = link.Url;
                button.Click += (_, _) => owner.OpenProjectLink(url);
                buttons.Children.Add(button);
            }
            var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7) };
            close.Click += (_, _) => dialog.Close();
            buttons.Children.Add(close);
            panel.Children.Add(buttons);
        }
    }
}
