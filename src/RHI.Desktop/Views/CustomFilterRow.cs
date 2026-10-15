using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The saved custom filters row under the library's filter tabs (Refs #193): one AXAML element
/// (<c>&lt;views:CustomFilterRow/&gt;</c>, DataContext the shell). "Save as filter" while the search box holds a search,
/// then one chip per saved filter (click: on/off; context menu, menu key or Shift+F10: Delete). Hidden while there is
/// neither. All state lives in <see cref="CustomFiltersPresenter"/>.
/// </summary>
public sealed class CustomFilterRow : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
    private readonly Button _save = new() { Classes = { "chip", "customFilterSave" }, Content = T("Shell.SaveAsFilter"), Name = "SaveCustomFilter" };
    private CustomFiltersPresenter? _presenter;

    public CustomFilterRow()
    {
        IsVisible = false;
        Margin = new Thickness(0, 4, 0, 0);
        Styles.Add(new Style(x => x.OfType<Button>().Class("chip").Class("active"))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.Parse("#2B4A78"))),
                new Setter(Button.ForegroundProperty, Brushes.White)
            }
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("chip").Class("customFilterSave"))
        {
            Setters = { new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.Parse("#8FB8F0"))) }
        });
        ToolTip.SetTip(_save, T("Shell.SaveAsFilterTip"));
        ToolTip.SetShowDelay(_save, MiddleEllipsisText.TooltipDelay);
        _save.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow window) UiExceptionGuard.Forget(window.ShowSaveCustomFilterDialogAsync(), "SaveCustomFilter");
        };
        Content = _panel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        _presenter = (DataContext as MainWindowViewModel)?.CustomFilters;
        if (_presenter is not null)
        {
            _presenter.PropertyChanged += OnPresenterChanged;
            _presenter.Chips.CollectionChanged += OnChipsChanged;
        }
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_presenter is null) return;
        Detach();
        _presenter.PropertyChanged += OnPresenterChanged;
        _presenter.Chips.CollectionChanged += OnChipsChanged;
        Rebuild();
    }

    private void Detach()
    {
        if (_presenter is null) return;
        _presenter.PropertyChanged -= OnPresenterChanged;
        _presenter.Chips.CollectionChanged -= OnChipsChanged;
        foreach (var chip in _presenter.Chips) chip.PropertyChanged -= OnChipChanged;
    }

    private void OnPresenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomFiltersPresenter.CanSave) or nameof(CustomFiltersPresenter.IsShown)) Sync();
    }

    private void OnChipsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is CustomFilterChip chip && e.PropertyName == nameof(CustomFilterChip.IsActive))
            foreach (var button in _panel.Children.OfType<Button>().Where(b => ReferenceEquals(b.Tag, chip)))
                button.Classes.Set("active", chip.IsActive);
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        _panel.Children.Add(_save);
        if (_presenter is not null)
            foreach (var chip in _presenter.Chips)
            {
                chip.PropertyChanged -= OnChipChanged;
                chip.PropertyChanged += OnChipChanged;
                _panel.Children.Add(ChipButton(_presenter, chip));
            }
        Sync();
    }

    private void Sync()
    {
        _save.IsVisible = _presenter?.CanSave == true;
        IsVisible = _presenter?.IsShown == true;
    }

    private static Button ChipButton(CustomFiltersPresenter presenter, CustomFilterChip chip)
    {
        // A string content treats the first '_' as an access-key marker: doubled so names show as typed.
        var button = new Button
        {
            Classes = { "chip", "customFilter" },
            Content = chip.Name.Replace("_", "__", StringComparison.Ordinal),
            Tag = chip,
            MaxWidth = 260,
            Command = presenter.ToggleCommand,
            CommandParameter = chip.Name
        };
        button.Classes.Set("active", chip.IsActive);
        ToolTip.SetTip(button, chip.Tip);
        ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = T("Shell.DeleteFilter"), Command = presenter.DeleteCommand, CommandParameter = chip.Name });
        button.ContextMenu = menu;
        button.KeyUp += (_, e) =>
        {
            if (!CardMenuKeys.OpensMenuItself(e.Key, e.KeyModifiers, Application.Current?.PlatformSettings?.HotkeyConfiguration.OpenContextMenu)) return;
            menu.Open(button);
            e.Handled = true;
        };
        return button;
    }
}

public sealed partial class MainWindow
{
    /// <summary>
    /// "Save as filter" (Refs #193, upstream <c>SaveFilterButton_Click</c>): asks for a name (the search itself is the
    /// suggestion); an empty or already used name is refused in the dialog, which stays open.
    /// </summary>
    internal async Task ShowSaveCustomFilterDialogAsync()
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrWhiteSpace(vm.SearchText)) return;
        var query = vm.SearchText.Trim();
        var name = new TextBox { Text = query, PlaceholderText = T("Shell.CustomFilterName"), MinWidth = 220, Name = "CustomFilterNameBox" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#F08A8A")), IsVisible = false };
        var save = new Button { Content = T("Win.Save"), Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = TF("Shell.SaveFilterPrompt", query), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(name);
        panel.Children.Add(error);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = T("Shell.SaveFilterTitle"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinWidth = 320,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        void Submit()
        {
            var (saved, message) = vm.CustomFilters.Save(name.Text, query);
            if (saved) dialog.Close();
            else
            {
                error.Text = message;
                error.IsVisible = true;
            }
        }
        save.Click += (_, _) => Submit();
        cancel.Click += (_, _) => dialog.Close();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Submit();
        };
        await ShowFittedDialogAsync(dialog);
    }
}
