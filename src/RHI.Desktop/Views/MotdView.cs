using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #309 (upstream 2.9.0): the message of the day's text with its links clickable. Plain runs are text; each link is a small
/// link button inside the text, opened through <paramref name="openLink"/> (the desktop opener: http/https only). Built in
/// code so tests can build it without a window.
/// </summary>
internal static class MotdView
{
    public static TextBlock Build(string? text, Action<string> openLink)
    {
        ArgumentNullException.ThrowIfNull(openLink);
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Name = "MotdText" };
        block.Inlines ??= [];
        foreach (var segment in MotdText.Segments(text))
        {
            if (segment.Url is not { } url)
            {
                block.Inlines.Add(new Run(segment.Text));
                continue;
            }
            var link = new Button
            {
                Classes = { "motdLink" },
                Content = new TextBlock { Text = segment.Text, TextDecorations = TextDecorations.Underline }
            };
            ToolTip.SetTip(link, url);
            link.Click += (_, _) => openLink(url);
            block.Inlines.Add(new InlineUIContainer(link));
        }
        return block;
    }
}
