using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The game collections row above the game list (#300, upstream 2.9.2: under the All / Installed / Favourites switch):
/// one chip per collection (click: show its games / all games again; context menu, menu key or Shift+F10: Rename, Move
/// left, Move right, Delete), then "+ Collection". One AXAML element (<c>&lt;views:CollectionsRow/&gt;</c>, DataContext
/// the shell). All state lives in <see cref="CollectionsPresenter"/>.
/// </summary>
public sealed class CollectionsRow : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
    private readonly Button _add = new() { Classes = { "chip", "collectionNew" }, Content = T("Coll.NewChip"), Name = "NewCollection" };
    private CollectionsPresenter? _presenter;

    public CollectionsRow()
    {
        Margin = new Thickness(0, 4, 0, 0);
        ToolTip.SetTip(_add, T("Coll.NewChipTip"));
        ToolTip.SetShowDelay(_add, MiddleEllipsisText.TooltipDelay);
        _add.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window) UiExceptionGuard.Forget(window.NewCollectionAsync(null), "NewCollection");
        };
        Content = _panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        _presenter = (DataContext as MainWindowViewModel)?.Collections;
        Attach();
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Detach();
        Attach();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    private void Attach()
    {
        if (_presenter is not null) _presenter.Chips.CollectionChanged += OnChipsChanged;
    }

    private void Detach()
    {
        if (_presenter is null) return;
        _presenter.Chips.CollectionChanged -= OnChipsChanged;
        foreach (var chip in _presenter.Chips) chip.PropertyChanged -= OnChipChanged;
    }

    private void OnChipsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CollectionChip chip || e.PropertyName != nameof(CollectionChip.IsActive)) return;
        foreach (var button in _panel.Children.OfType<Button>().Where(b => ReferenceEquals(b.Tag, chip)))
        {
            button.Classes.Set("active", chip.IsActive);
            ToolTip.SetTip(button, chip.Tip);
        }
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        if (_presenter is not null)
            foreach (var chip in _presenter.Chips)
            {
                chip.PropertyChanged -= OnChipChanged;
                chip.PropertyChanged += OnChipChanged;
                _panel.Children.Add(ChipButton(_presenter, chip));
            }
        _panel.Children.Add(_add);
    }

    private Button ChipButton(CollectionsPresenter presenter, CollectionChip chip)
    {
        // A string content treats the first '_' as an access-key marker: doubled so names show as typed.
        var button = new Button
        {
            Classes = { "chip", "collection" },
            Content = chip.Name.Replace("_", "__", StringComparison.Ordinal),
            Tag = chip,
            MaxWidth = 220,
            Command = presenter.ToggleCommand,
            CommandParameter = chip.Name
        };
        button.Classes.Set("active", chip.IsActive);
        ToolTip.SetTip(button, chip.Tip);
        ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
        // The menu is built when it opens (it lists Move left / right only where the chip can move).
        void OpenMenu()
        {
            var menu = new ContextMenu();
            foreach (var item in MenuItemsFor(presenter, chip)) menu.Items.Add(item);
            button.ContextMenu = menu;
            menu.Open(button);
        }
        button.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            OpenMenu();
        };
        button.KeyUp += (_, e) =>
        {
            if (!CardMenuKeys.OpensMenuItself(e.Key, e.KeyModifiers, Application.Current?.PlatformSettings?.HotkeyConfiguration.OpenContextMenu)) return;
            e.Handled = true;
            OpenMenu();
        };
        return button;
    }

    /// <summary>A chip's menu (upstream: Rename…, Move left, Move right, Delete…); a move it cannot make is left out.</summary>
    internal IReadOnlyList<Control> MenuItemsFor(CollectionsPresenter presenter, CollectionChip chip)
    {
        var items = new List<Control>();
        var rename = new MenuItem { Header = T("Coll.Rename") };
        rename.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window) UiExceptionGuard.Forget(window.RenameCollectionAsync(chip.Name), "RenameCollection");
        };
        items.Add(rename);
        if (chip.CanMoveLeft) items.Add(new MenuItem { Header = T("Coll.MoveLeft"), Command = presenter.MoveLeftCommand, CommandParameter = chip.Name });
        if (chip.CanMoveRight) items.Add(new MenuItem { Header = T("Coll.MoveRight"), Command = presenter.MoveRightCommand, CommandParameter = chip.Name });
        items.Add(new Separator());
        var delete = new MenuItem { Header = T("Coll.Delete") };
        delete.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window) UiExceptionGuard.Forget(window.DeleteCollectionAsync(chip.Name), "DeleteCollection");
        };
        items.Add(delete);
        return items;
    }
}

public sealed partial class MainWindow
{
    /// <summary>"+ Collection" and the game menu's "New collection…": asks for the name; <paramref name="firstGame"/> goes in it.</summary>
    internal Task NewCollectionAsync(DetectedGame? firstGame)
    {
        if (DataContext is not MainWindowViewModel vm) return Task.CompletedTask;
        var prompt = firstGame is null ? T("Coll.NewPrompt") : TF("Coll.NewPromptWithGame", firstGame.DisplayName());
        return ShowCollectionNameDialogAsync(vm.Collections, T("Coll.NewTitle"), prompt, T("Coll.Create"), "", null,
            name => vm.Collections.Create(name, firstGame));
    }

    internal Task RenameCollectionAsync(string name)
    {
        if (DataContext is not MainWindowViewModel vm) return Task.CompletedTask;
        return ShowCollectionNameDialogAsync(vm.Collections, T("Coll.RenameTitle"), TF("Coll.RenamePrompt", name), T("Coll.RenameButton"), name, name,
            typed => string.Equals(typed?.Trim(), name, StringComparison.Ordinal) ? (true, "") : vm.Collections.Rename(name, typed));
    }

    /// <summary>Delete asks first (Cancel is the safe answer); only the collection goes, never its games.</summary>
    internal async Task DeleteCollectionAsync(string name)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var count = vm.Collections.Chips.FirstOrDefault(chip => string.Equals(chip.Name, name, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
        if (!await ShowConfirmationAsync(T("Coll.DeleteTitle"), TF("Coll.DeletePrompt", name, count), T("Coll.DeleteButton"))) return;
        vm.Collections.Delete(name);
    }

    /// <summary>
    /// The collection name dialog (upstream <c>PromptCollectionNameAsync</c>): the name is checked as it is typed (empty,
    /// too long, a built-in filter name, already used) and a refused name keeps the dialog open with the reason.
    /// </summary>
    private async Task ShowCollectionNameDialogAsync(CollectionsPresenter presenter, string title, string prompt, string accept,
        string initial, string? renaming, Func<string?, (bool Done, string Message)> submit)
    {
        var name = new TextBox
        {
            Text = initial,
            PlaceholderText = T("Coll.Placeholder"),
            MaxLength = RHI.Platform.Linux.Settings.LinuxGameCollections.MaxNameLength,
            MinWidth = 260,
            Name = "CollectionNameBox"
        };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "error" }, IsVisible = false };
        var save = new Button { Content = accept, Padding = new Thickness(14, 7), Name = "CollectionNameAccept" };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(name);
        panel.Children.Add(error);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            MinWidth = 320,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        void Check(bool showEmpty)
        {
            var problem = presenter.NameProblem(name.Text, renaming);
            save.IsEnabled = problem is null;
            error.Text = problem ?? "";
            error.IsVisible = problem is not null && (showEmpty || !string.IsNullOrWhiteSpace(name.Text));
        }
        void Submit()
        {
            var (done, message) = submit(name.Text);
            if (done) dialog.Close();
            else
            {
                error.Text = message;
                error.IsVisible = true;
            }
        }
        name.TextChanged += (_, _) => Check(false);
        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => dialog.Close();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (save.IsEnabled) Submit();
            else Check(true);
        };
        dialog.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        Check(false);
        await ShowFittedDialogAsync(dialog);
    }
}
