using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>The small windows of the parity features: a yes/no question and the Nexus login (SSO) window.</summary>
internal static class ParityDialogs
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>A question with Cancel and <paramref name="accept"/>; true when accepted. Sized to fit 640x480 and portrait.</summary>
    public static Task<bool> AskAsync(Window owner, string title, string message, string accept)
    {
        var yes = new Button { Content = accept, Padding = new Thickness(14, 7) };
        var no = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            MinWidth = 280,
            MaxWidth = Math.Max(280, owner.Bounds.Width - 40),
            MaxHeight = Math.Max(200, owner.Bounds.Height),
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        no.Click += (_, _) => dialog.Close(false);
        yes.Click += (_, _) => dialog.Close(true);
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>The Nexus login window (#99): opens the Nexus page in the browser and waits for the authorisation.</summary>
    public static Window NexusLogin(NexusSsoPresenter presenter)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Name = "NexusSsoStatus" };
        var link = new MiddleEllipsisText { WrapNonPath = false, Opacity = 0.75, FontSize = 11 };
        var openAgain = ParitySettingsTab.Button(T("Parity.NexusSsoOpenAgain"), T("Parity.NexusSsoOpenAgainTip"));
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7), Name = "NexusSsoCancel" };
        var close = new Button { Content = T("Win.Close"), Padding = new Thickness(14, 7), Name = "NexusSsoClose" };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Parity.NexusSsoTitle"), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("Parity.NexusSsoExplain"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        panel.Children.Add(status);
        panel.Children.Add(link);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(openAgain);
        buttons.Children.Add(cancel);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = T("Parity.NexusSsoTitle"),
            Width = 520,
            MinWidth = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };

        void Sync()
        {
            status.Text = presenter.StatusText;
            link.FullText = presenter.AuthorisationUrl ?? "";
            link.IsVisible = openAgain.IsVisible = presenter.IsRunning && presenter.AuthorisationUrl is not null;
            cancel.IsVisible = presenter.IsRunning;
            close.IsVisible = !presenter.IsRunning;
        }
        ParitySettingsTab.Bind(panel, presenter, Sync);
        openAgain.Click += (_, _) => presenter.OpenPage();
        cancel.Click += (_, _) => presenter.Cancel();
        close.Click += (_, _) => window.Close(presenter.Succeeded);
        window.Closing += (_, _) => presenter.Cancel(); // closing mid-login cancels it
        window.Opened += (_, _) => UiExceptionGuard.Forget(presenter.RunAsync(), "NexusLogin");
        return window;
    }
}
