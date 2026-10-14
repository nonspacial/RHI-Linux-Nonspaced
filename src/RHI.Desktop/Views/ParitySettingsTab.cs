using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.ComponentModel;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Settings → "Accounts &amp; updates" (w3/parity): RHI-Linux updates (#100), GitHub token (#163), Nexus Mods (#99). Built in code like the other Settings tabs, from
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
        panel.Children.Add(AppUpdateSection(parity.AppUpdate));
        panel.Children.Add(GitHubTokenSection(parity.GitHubToken));
        panel.Children.Add(NexusSection(parity.NexusAccount));
        tab.Content = new ScrollViewer { Content = panel };
        return tab;
    }

    /// <summary>
    /// Runs <paramref name="sync"/> now and whenever <paramref name="source"/> changes while <paramref name="owner"/> is
    /// shown; the presenter outlives the dialog, so the handler is removed when the dialog closes (no leak per opening).
    /// </summary>
    internal static void Bind(Control owner, INotifyPropertyChanged source, Action sync)
    {
        PropertyChangedEventHandler handler = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) sync();
            else Dispatcher.UIThread.Post(sync);
        };
        owner.AttachedToVisualTree += (_, _) =>
        {
            source.PropertyChanged -= handler;
            source.PropertyChanged += handler;
            sync();
        };
        owner.DetachedFromVisualTree += (_, _) => source.PropertyChanged -= handler;
        sync();
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

    private static Control AppUpdateSection(AppUpdatePresenter presenter)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Heading(T("Parity.AppUpdateHeading")));
        section.Children.Add(Note(T("Parity.AppUpdateNote")));
        var atStartup = new CheckBox { Content = T("Parity.AppUpdateAtStartup") };
        var checkNow = Button(T("Parity.AppUpdateCheckNow"));
        var openPage = Button(T("Parity.AppUpdateOpenPage"), T("Parity.AppUpdateOpenPageTip"));
        var status = Status();

        void Sync()
        {
            status.Text = presenter.StatusText;
            atStartup.IsChecked = presenter.CheckAtStartup;
            openPage.IsVisible = presenter.ReleasePageUrl is not null;
            checkNow.IsEnabled = !presenter.IsBusy;
        }
        Bind(section, presenter, Sync);

        atStartup.IsCheckedChanged += (_, _) =>
        {
            if (atStartup.IsChecked is bool value && value != presenter.CheckAtStartup) presenter.SetCheckAtStartup(value);
        };
        checkNow.Click += (_, _) => UiExceptionGuard.Forget(presenter.CheckAsync(), "AppUpdateCheckNow");
        openPage.Click += (_, _) => presenter.OpenReleasePage();

        section.Children.Add(atStartup);
        section.Children.Add(Buttons(checkNow, openPage));
        section.Children.Add(status);
        return section;
    }

    private static Control NexusSection(NexusAccountPresenter presenter)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Heading(T("Parity.NexusHeading")));
        section.Children.Add(Note(T("Parity.NexusNote")));
        var account = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var status = Status();
        var box = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = T("Parity.NexusKeyPlaceholder"),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Name = "NexusKeyBox"
        };
        var save = Button(T("Parity.NexusKeySave"), T("Parity.NexusKeySaveTip"));
        // The one way in (owner decision #99): API-key page in the browser + paste box. The SSO window is not offered.
        var connect = Button(T("Parity.NexusConnect"), T("Parity.NexusConnectTip"));
        connect.Name = "NexusConnectButton";
        connect.FontSize = 14;
        connect.FontWeight = FontWeight.SemiBold;
        var disconnect = Button(T("Parity.NexusDisconnect"));
        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Name = "NexusKeyEntry" };
        var handler = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var register = Button(T("Parity.NexusHandlerRegister"), T("Parity.NexusHandlerRegisterTip"));
        var unregister = Button(T("Parity.NexusHandlerUnregister"));

        void Sync()
        {
            account.Text = presenter.AccountText;
            status.Text = presenter.StatusText;
            handler.Text = presenter.HandlerText;
            disconnect.IsVisible = presenter.IsConnected;
            connect.IsVisible = !presenter.IsConnected;
            entry.IsVisible = presenter.IsKeyEntryShown && !presenter.IsConnected;
            register.IsVisible = !presenter.HandlerIsRhi;
            unregister.IsVisible = presenter.HandlerIsRhi;
            save.IsEnabled = connect.IsEnabled = box.IsEnabled = !presenter.IsBusy;
        }
        Bind(section, presenter, Sync);
        section.AttachedToVisualTree += (_, _) => UiExceptionGuard.Forget(presenter.RefreshHandlerAsync(), "NexusHandlerQuery");

        async Task SaveAsync()
        {
            if (await presenter.SaveKeyAsync(box.Text)) box.Text = "";
        }
        async Task RegisterAsync()
        {
            if (presenter.OtherHandler is { } other && TopLevel.GetTopLevel(section) is Window owner
                && !await ParityDialogs.AskAsync(owner, T("Parity.NexusHandlerHeading"), UiLocalizationService.Format("Parity.NexusHandlerReplace", other), T("Parity.NexusHandlerRegister")))
                return;
            await presenter.RegisterHandlerAsync();
        }
        save.Click += (_, _) => UiExceptionGuard.Forget(SaveAsync(), "NexusKeySave");
        connect.Click += (_, _) =>
        {
            presenter.Connect();
            Dispatcher.UIThread.Post(() => box.Focus());
        };
        disconnect.Click += (_, _) => presenter.Disconnect();
        register.Click += (_, _) => UiExceptionGuard.Forget(RegisterAsync(), "NxmRegister");
        unregister.Click += (_, _) => UiExceptionGuard.Forget(presenter.UnregisterHandlerAsync(), "NxmUnregister");

        entry.Children.Add(box);
        entry.Children.Add(save);
        Grid.SetColumn(save, 1);
        section.Children.Add(account);
        section.Children.Add(Buttons(connect, disconnect));
        section.Children.Add(entry);
        section.Children.Add(status);
        section.Children.Add(new TextBlock { Text = T("Parity.NexusHandlerHeading"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        section.Children.Add(Note(T("Parity.NexusHandlerNote")));
        section.Children.Add(handler);
        section.Children.Add(Buttons(register, unregister));
        return section;
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
        Bind(section, presenter, Sync);

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
