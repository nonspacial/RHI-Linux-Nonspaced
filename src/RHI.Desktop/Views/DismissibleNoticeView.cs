using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/extras-fg (#285): the content of a notice with "Don't show this again" (upstream's ContentDialog: text, an optional
/// GitHub link, the tick box, Cancel and the accept button, accept on the right as the default). Built in code so tests can
/// build it without a window.
/// </summary>
internal static class DismissibleNoticeView
{
    internal sealed record Parts(StackPanel Root, CheckBox DontShow, Button Accept, Button Cancel, Button? Link);

    /// <param name="openLink">Opens the notice's link (only http(s) links are offered).</param>
    internal static Parts Build(DismissibleNotice notice, Action<string> openLink)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        foreach (var paragraph in notice.Paragraphs)
            root.Children.Add(new TextBlock { Text = paragraph, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });

        Button? link = null;
        if (notice.LinkUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            link = new Button
            {
                Name = "NoticeLink",
                Classes = { "rhi" },
                Content = url.Replace("https://", "", StringComparison.Ordinal),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5)
            };
            link.Click += (_, _) => openLink(url);
            root.Children.Add(link);
        }

        var dontShow = new CheckBox { Name = "NoticeDontShowAgain", Content = UiLocalizationService.Text("Notice.DontShowAgain"), FontSize = 12 };
        root.Children.Add(dontShow);

        var cancel = new Button { Name = "NoticeCancel", Content = UiLocalizationService.Text("Win.Cancel"), Padding = new Thickness(14, 7) };
        var accept = new Button { Name = "NoticeAccept", Content = notice.AcceptText, Padding = new Thickness(14, 7), IsDefault = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        root.Children.Add(buttons);
        return new Parts(root, dontShow, accept, cancel, link);
    }
}
