using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #309 (upstream 2.9.2 What's New): a thin strip (hide / show the list, bigger and smaller text), the search box and version
/// list, and the notes. Built in code over a <see cref="WhatsNewPresenter"/>, so tests can build it without a window. Colours
/// come from App.axaml (the <c>whatsNewItem</c> styles).
/// </summary>
internal static class WhatsNewView
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>How long the search waits after the last keystroke (upstream: 250 ms); Enter searches at once.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    public sealed record Parts(Grid Root, Button ToggleNav, Button Bigger, Button Smaller, TextBox Search, StackPanel NavList, StackPanel Notes,
        Control NavPanel);

    public static Grid Build(WhatsNewPresenter presenter) => BuildParts(presenter).Root;

    public static Parts BuildParts(WhatsNewPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        Button StripButton(string content, string name)
            => new() { Content = content, Name = name, Width = 32, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var toggle = StripButton("", "WhatsNewToggleNav");
        var bigger = StripButton("+", "WhatsNewBigger");
        var smaller = StripButton("−", "WhatsNewSmaller");
        ToolTip.SetTip(bigger, T("Wn.BiggerTip"));
        ToolTip.SetTip(smaller, T("Wn.SmallerTip"));
        var strip = new StackPanel { Spacing = 6 };
        strip.Children.Add(toggle);
        strip.Children.Add(bigger);
        strip.Children.Add(smaller);

        var search = new TextBox { Name = "WhatsNewSearch", PlaceholderText = T("Wn.SearchPlaceholder"), Margin = new Thickness(0, 0, 0, 10) };
        ToolTip.SetTip(search, T("Wn.SearchTip"));
        var navList = new StackPanel { Spacing = 2, Name = "WhatsNewVersions" };
        foreach (var item in presenter.NavItems) navList.Children.Add(NavButton(presenter, item));
        var navPanel = new Grid { Width = 190, RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        navPanel.Children.Add(search);
        var heading = new TextBlock { Text = T("Win.Versions"), Classes = { "muted" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 0, 0, 6) };
        Grid.SetRow(heading, 1);
        navPanel.Children.Add(heading);
        var navScroll = new ScrollViewer { Content = navList, Padding = new Thickness(0, 0, 10, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(navScroll, 2);
        navPanel.Children.Add(navScroll);

        var notes = new StackPanel { Spacing = 8, Margin = new Thickness(4, 0, 16, 0), Name = "WhatsNewNotes" };
        var notesScroll = new ScrollViewer { Content = notes, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var intro = new TextBlock { Text = T("Wn.Intro"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var notesColumn = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        notesColumn.Children.Add(intro);
        Grid.SetRow(notesScroll, 1);
        notesColumn.Children.Add(notesScroll);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 14, Margin = new Thickness(18, 18, 18, 0) };
        root.Children.Add(strip);
        Grid.SetColumn(navPanel, 1);
        root.Children.Add(navPanel);
        Grid.SetColumn(notesColumn, 2);
        root.Children.Add(notesColumn);

        void RenderNotes()
        {
            notes.Children.Clear();
            foreach (var (level, text) in ChangelogText.Blocks(presenter.Text))
                notes.Children.Add(ChangelogText.View(level, text, presenter.FontSize));
            notesScroll.Offset = default;
        }
        void SyncChrome()
        {
            var showNav = presenter.HasNav && !presenter.NavHidden;
            navPanel.IsVisible = showNav;
            toggle.IsVisible = presenter.HasNav;
            toggle.Content = presenter.NavHidden ? "▶" : "◀";
            ToolTip.SetTip(toggle, presenter.NavToggleTip);
            bigger.IsEnabled = presenter.CanMakeBigger;
            smaller.IsEnabled = presenter.CanMakeSmaller;
        }
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WhatsNewPresenter.Text) or nameof(WhatsNewPresenter.FontSize)) RenderNotes();
            if (e.PropertyName is nameof(WhatsNewPresenter.NavHidden) or nameof(WhatsNewPresenter.FontSize)) SyncChrome();
        };
        toggle.Click += (_, _) => presenter.ToggleNav();
        bigger.Click += (_, _) => presenter.MakeBigger();
        smaller.Click += (_, _) => presenter.MakeSmaller();

        DispatcherTimer? searchTimer = null;
        search.TextChanged += (_, _) =>
        {
            searchTimer?.Stop();
            searchTimer = new DispatcherTimer { Interval = SearchDelay };
            searchTimer.Tick += (_, _) =>
            {
                searchTimer?.Stop();
                presenter.ApplySearch(search.Text);
            };
            searchTimer.Start();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(search.Text))
            {
                search.Text = ""; // clears the search, not the window
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                searchTimer?.Stop();
                presenter.ApplySearch(search.Text);
                e.Handled = true;
            }
        };

        RenderNotes();
        SyncChrome();
        return new Parts(root, toggle, bigger, smaller, search, navList, notes, navPanel);
    }

    private static Button NavButton(WhatsNewPresenter presenter, WhatsNewNavItem item)
    {
        var label = new TextBlock { Text = item.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(label);
        Grid.SetColumn(count, 1);
        row.Children.Add(count);
        var button = new Button { Classes = { "whatsNewItem" }, Content = row, Tag = item.Index };
        ToolTip.SetTip(button, presenter.Sections[item.Index].Title);
        void Sync()
        {
            button.IsVisible = item.IsVisible;
            button.Classes.Set("selected", item.IsSelected);
            count.Text = item.Count;
        }
        item.PropertyChanged += (_, _) => Sync();
        button.Click += (_, _) => { if (!item.IsSelected) presenter.ShowVersion(item.Index); };
        Sync();
        return button;
    }
}
