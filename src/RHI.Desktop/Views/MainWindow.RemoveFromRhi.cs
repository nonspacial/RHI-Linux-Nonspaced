using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #293 (upstream 2.9.0): the Remove from RHI confirmation — the question, how to undo it and, when RHI installed
/// components in the game, which ones with "Also uninstall all RHI-managed components" (off by default, as upstream).
/// </summary>
public partial class MainWindow
{
    private async Task<bool?> ShowRemoveFromRhiAsync(MainWindowViewModel.RemoveFromRhiPrompt prompt)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt.Question, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = prompt.UndoNote, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        CheckBox? uninstall = null;
        if (prompt.Installed is { } installed)
        {
            panel.Children.Add(new TextBlock { Text = installed, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            uninstall = new CheckBox { Content = prompt.UninstallText, IsChecked = false, Name = "RemoveFromRhiUninstall" };
            panel.Children.Add(uninstall);
        }
        var remove = new Button { Content = prompt.RemoveText, Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = prompt.Title,
            Width = 520,
            MinWidth = 300,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        cancel.Click += (_, _) => dialog.Close(null);
        remove.Click += (_, _) => dialog.Close((bool?)(uninstall?.IsChecked == true));
        return await ShowFittedDialogAsync<bool?>(dialog);
    }
}
