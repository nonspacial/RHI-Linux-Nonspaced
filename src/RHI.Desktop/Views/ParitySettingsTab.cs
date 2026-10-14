using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Settings → "Accounts &amp; updates" (w3/parity): GitHub token (#163). Built in code like the other Settings tabs, from
/// the parity presenters only; the Settings dialog adds it with one line. Hidden when the app was built without the
/// parity features (tests that build the window over a bare view model).
/// </summary>
internal static class ParitySettingsTab
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static TabItem Create(ParityFeatures? parity)
    {
        var tab = new TabItem { Header = T("Parity.AccountsTab"), IsVisible = parity is not null };
        if (parity is null) return tab;
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Parity.AccountsTab"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(GitHubTokenSection(parity.GitHubToken));
        tab.Content = new ScrollViewer { Content = panel };
        return tab;
    }

    internal static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15 };

    internal static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

    internal static TextBlock Status() => new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

    internal static Button Button(string text, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6) };
        if (tip is not null)
        {
            ToolTip.SetTip(button, tip);
            ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
        }
        return button;
    }

    internal static WrapPanel Buttons(params Control[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    private static Control GitHubTokenSection(GitHubTokenPresenter presenter)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Heading(T("Parity.GitHubTokenHeading")));
        section.Children.Add(Note(T("Parity.GitHubTokenNote")));
        var status = Status();
        var box = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = T("Parity.GitHubTokenPlaceholder"),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Name = "GitHubTokenBox"
        };
        var save = Button(T("Parity.GitHubTokenSave"), T("Parity.GitHubTokenSaveTip"));
        var check = Button(T("Parity.GitHubTokenCheck"));
        var clear = Button(T("Parity.GitHubTokenClear"));

        async Task SaveAsync()
        {
            if (await presenter.SaveAsync(box.Text)) box.Text = "";
        }

        void Sync()
        {
            status.Text = presenter.StatusText;
            check.IsVisible = clear.IsVisible = presenter.HasSavedToken;
            save.IsEnabled = check.IsEnabled = clear.IsEnabled = box.IsEnabled = !presenter.IsBusy;
        }
        presenter.PropertyChanged += (_, _) => Sync();
        Sync();

        save.Click += (_, _) => UiExceptionGuard.Forget(SaveAsync(), "GitHubTokenSave");
        check.Click += (_, _) => UiExceptionGuard.Forget(presenter.CheckAsync(), "GitHubTokenCheck");
        clear.Click += (_, _) => presenter.Clear();

        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        entry.Children.Add(box);
        entry.Children.Add(save);
        Grid.SetColumn(save, 1);
        section.Children.Add(entry);
        section.Children.Add(Buttons(check, clear));
        section.Children.Add(status);
        return section;
    }
}
