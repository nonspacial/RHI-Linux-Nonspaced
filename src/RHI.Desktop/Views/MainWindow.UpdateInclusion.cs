using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #178: a game's update inclusion (card menu → Update inclusion…, upstream's Update Inclusion dialog): one box per
/// component, ticked = Update All and the background updates may change it in this game. Save returns the boxes left
/// clear; Cancel (or closing the window) returns null and nothing is saved. While the inclusion file cannot be read the
/// dialog says so first, every box is cleared and disabled (everything is left out) and only Close is offered.
/// </summary>
public sealed partial class MainWindow
{
    internal async Task<IReadOnlySet<ManagedComponentKind>?> ShowUpdateInclusionAsync(UpdateInclusionDialogModel model)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18, 18, 18, 6) };
        panel.Children.Add(new TextBlock { Text = model.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var unreadable = model.UnreadableNote is not null;
        if (model.UnreadableNote is { } problem)
            panel.Children.Add(new TextBlock { Name = "UpdateInclusionUnreadable", Text = problem, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = model.Intro, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 });
        var boxes = new List<(ManagedComponentKind Kind, CheckBox Box)>();
        foreach (var choice in model.Choices)
        {
            var box = new CheckBox
            {
                Name = "UpdateInclusion" + choice.Kind, Content = choice.Label, IsChecked = choice.Included && !unreadable, IsEnabled = !unreadable
            };
            boxes.Add((choice.Kind, box));
            panel.Children.Add(box);
        }
        if (model.AllGamesNote is { } note)
            panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });

        var save = new Button { Content = T("Ui.Save"), Padding = new Thickness(14, 7), IsVisible = !unreadable };
        var cancel = new Button { Content = T(unreadable ? "Win.Close" : "Win.Cancel"), Padding = new Thickness(14, 7) };
        // The buttons stay below the scrolling list, so they are reachable at the dialog's minimum size.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        root.Children.Add(buttons);
        Grid.SetRow(buttons, 1);

        var dialog = new Window
        {
            Name = "UpdateInclusionDialog",
            Title = model.Title,
            Width = 480,
            Height = 420,
            MinWidth = 320,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        IReadOnlySet<ManagedComponentKind>? result = null;
        save.Click += (_, _) =>
        {
            result = boxes.Where(entry => entry.Box.IsChecked != true).Select(entry => entry.Kind).ToHashSet();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
        return result;
    }
}
