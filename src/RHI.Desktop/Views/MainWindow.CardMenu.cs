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

    /// <summary>
    /// Shift+F10 on a focused card: Avalonia raises ContextRequested only for its platform's context-menu gestures, which
    /// on Linux is the menu key alone, so the window opens the card menu itself for the other common gesture.
    /// </summary>
    private void GameLibrary_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel vm || e.Source is not Control source) return;
        if (!CardMenuKeys.OpensMenuItself(e.Key, e.KeyModifiers, Application.Current?.PlatformSettings?.HotkeyConfiguration.OpenContextMenu)) return;
        var item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not GameRowViewModel row) return;
        e.Handled = true;
        ShowCardMenu(vm, row, item, atPointer: false);
    }

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
            menu.Items.Add(CardMenuEntry(entry));
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(OpenCardMenu, menu)) OpenCardMenu = null; };
        OpenCardMenu = menu;
        menu.ShowAt(anchor, showAtPointer: atPointer);
    }

    private static MenuItem CardMenuEntry(CardMenuItem entry)
    {
        var item = new MenuItem { Header = entry.Label, Name = "CardMenu-" + entry.Id };
        item.Classes.Add(OverflowMenuClass);
        if (entry.Children is { Count: > 0 } children)
            foreach (var child in children) item.Items.Add(CardMenuEntry(child));
        else
            item.Command = entry.Command;
        if (entry.Tip is { } tip)
        {
            ToolTip.SetTip(item, tip);
            ToolTip.SetShowDelay(item, MiddleEllipsisText.TooltipDelay);
        }
        return item;
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

/// <summary>The keys that open a library card's menu from the keyboard (Refs #161): the menu key and Shift+F10.</summary>
internal static class CardMenuKeys
{
    /// <summary>The gestures the card menu answers to, whatever the platform's own context-menu list holds.</summary>
    internal static readonly IReadOnlyList<KeyGesture> Gestures = [new(Key.Apps), new(Key.F10, KeyModifiers.Shift)];

    /// <summary>
    /// True when <paramref name="key"/> + <paramref name="modifiers"/> is a card-menu gesture the platform does not turn into
    /// a ContextRequested itself (so the window opens the menu; never both, which would open it twice).
    /// </summary>
    internal static bool OpensMenuItself(Key key, KeyModifiers modifiers, IEnumerable<KeyGesture>? platformGestures)
    {
        static bool Is(KeyGesture gesture, Key key, KeyModifiers modifiers) => gesture.Key == key && gesture.KeyModifiers == modifiers;
        return Gestures.Any(g => Is(g, key, modifiers)) && !(platformGestures ?? []).Any(g => Is(g, key, modifiers));
    }
}
