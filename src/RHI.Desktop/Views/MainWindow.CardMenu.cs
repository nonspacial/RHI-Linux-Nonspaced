using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Right-click (or the menu key / Shift+F10) on a library card opens its actions menu (Refs #161, upstream 2.8.6):
/// the entries come from <see cref="MainWindowViewModel.CardMenuFor"/>, so the menu holds no logic of its own.
/// </summary>
public partial class MainWindow
{
    /// <summary>The card menu currently open (null when closed); the harness reads it.</summary>
    internal MenuFlyout? OpenCardMenu { get; private set; }

    private void GameLibrary_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Source is not Control source) return;
        var item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not GameRowViewModel row) return;
        e.Handled = true;
        ShowCardMenu(vm, row, item, atPointer: e.TryGetPosition(item, out _));
    }

    internal void ShowCardMenu(MainWindowViewModel vm, GameRowViewModel row, Control anchor, bool atPointer)
    {
        OpenCardMenu?.Hide();
        var menu = new MenuFlyout
        {
            Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard
        };
        // The app's menu surface and text colour, as the toolbar's '…' menu (App.axaml).
        menu.FlyoutPresenterClasses.Add(OverflowMenuClass);
        foreach (var entry in vm.CardMenuFor(row))
        {
            if (entry.SeparatorBefore && menu.Items.Count > 0) menu.Items.Add(new Separator());
            var item = new MenuItem
            {
                Header = entry.Label,
                Command = entry.Command,
                Name = "CardMenu-" + entry.Id
            };
            item.Classes.Add(OverflowMenuClass);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(OpenCardMenu, menu)) OpenCardMenu = null; };
        OpenCardMenu = menu;
        menu.ShowAt(anchor, showAtPointer: atPointer);
    }

    /// <summary>View notes: the game's note in a small scrolling dialog, with its link when the manifest gives one.</summary>
    private async Task ShowGameNotesAsync(GameNotesViewModel notes)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = notes.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = notes.Notes, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (notes.Url is { } url)
        {
            var link = new Button { Classes = { "rhi" }, Content = notes.LinkLabel, Padding = new Thickness(12, 6) };
            ToolTip.SetTip(link, url);
            ToolTip.SetShowDelay(link, 2000);
            link.Click += (_, _) => OpenProjectLink(url);
            buttons.Children.Add(link);
        }
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7) };
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = notes.Title,
            Width = 560,
            Height = 360,
            MinWidth = 320,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
        close.Click += (_, _) => dialog.Close();
        await ShowFittedDialogAsync(dialog);
    }
}
