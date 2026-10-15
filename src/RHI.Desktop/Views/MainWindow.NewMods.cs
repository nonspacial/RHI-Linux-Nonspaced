using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #185: the toolbar's "New mods" button (shown only while a list has mods the user has not marked as seen) opens the
/// list: one scrolling section per catalogue, "Mark as seen" (hides the button until more appear) and Close (keeps it).
/// </summary>
public partial class MainWindow
{
    /// <summary>The "New mods" dialog currently open (null when closed); the harness reads it.</summary>
    internal Window? OpenNewModsDialog { get; private set; }

    private void NewMods_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.NewMods.Show ??= ShowNewModsAsync;
        if (await vm.NewMods.OpenAsync() is { } notSaved) vm.StatusText = notSaved;
    });

    private async Task<bool> ShowNewModsAsync(IReadOnlyList<NewModsSection> sections)
    {
        var title = T("Cat.NewModsTitle");
        var list = new StackPanel { Spacing = 16 };
        foreach (var section in sections)
        {
            var names = new StackPanel { Spacing = 4 };
            foreach (var name in section.Names)
                names.Children.Add(new TextBlock { Text = "• " + name, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (section.More is { } more) names.Children.Add(new TextBlock { Text = more, FontSize = 12, FontStyle = FontStyle.Italic, Opacity = 0.7 });
            var block = new StackPanel { Spacing = 8 };
            block.Children.Add(new TextBlock { Text = section.Heading, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            block.Children.Add(names);
            list.Children.Add(block);
        }
        var markSeen = new Button { Classes = { "rhi" }, Content = T("Cat.NewModsMarkSeen"), Padding = new Thickness(14, 7) };
        ToolTip.SetTip(markSeen, T("Cat.NewModsMarkSeenTip"));
        ToolTip.SetShowDelay(markSeen, 2000);
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7) };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8,
            Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(markSeen);
        buttons.Children.Add(close);
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        panel.Children.Add(heading);
        panel.Children.Add(scroll); Grid.SetRow(scroll, 1);
        panel.Children.Add(buttons); Grid.SetRow(buttons, 2);
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 520,
            MinWidth = 320,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        var seen = false;
        markSeen.Click += (_, _) => { seen = true; dialog.Close(); };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { if (ReferenceEquals(OpenNewModsDialog, dialog)) OpenNewModsDialog = null; };
        OpenNewModsDialog = dialog;
        await ShowFittedDialogAsync(dialog);
        return seen;
    }
}
