using System.Text.RegularExpressions;

namespace RHI.Desktop.ViewModels;

/// <summary>One run of the message of the day: plain text, or a link (<see cref="Url"/> set) shown as <see cref="Text"/>.</summary>
public sealed record MotdSegment(string Text, string? Url = null);

/// <summary>
/// #309 (upstream 2.9.0 "Links in the message can now be clicked"): the message of the day's text split into plain runs and
/// links, for Markdown links (<c>[text](https://…)</c>) and bare web addresses. Only absolute http/https links without
/// credentials become links (they open through the desktop opener); any other target stays plain text, so a message can
/// never make RHI open a file or another scheme. Line breaks are kept as they are (upstream keeps single line breaks).
/// </summary>
public static partial class MotdText
{
    // A Markdown link, or a bare http(s) address up to whitespace or a bracket.
    [GeneratedRegex(@"\[(?<text>[^\]\r\n]+)\]\((?<url>[^)\s]+)\)|(?<bare>https?://[^\s<>()\[\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    // Sentence punctuation after a bare address belongs to the sentence (upstream's auto-links do the same).
    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '\'', '"'];

    public static IReadOnlyList<MotdSegment> Segments(string? text)
    {
        var segments = new List<MotdSegment>();
        if (string.IsNullOrEmpty(text)) return segments;
        var plain = new System.Text.StringBuilder();
        void Plain(string value) => plain.Append(value);
        void Link(string label, string url)
        {
            if (plain.Length > 0) segments.Add(new MotdSegment(plain.ToString()));
            plain.Clear();
            segments.Add(new MotdSegment(label, url));
        }

        var at = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            Plain(text[at..match.Index]);
            at = match.Index + match.Length;
            if (match.Groups["bare"].Success)
            {
                var address = match.Groups["bare"].Value;
                var trimmed = address.TrimEnd(TrailingPunctuation);
                if (WebLink(trimmed) is { } url) Link(trimmed, url);
                else Plain(trimmed);
                Plain(address[trimmed.Length..]);
            }
            else
            {
                var label = match.Groups["text"].Value;
                if (WebLink(match.Groups["url"].Value) is { } url) Link(label, url);
                else Plain(label); // not a web link: its text only, never its target
            }
        }
        Plain(text[at..]);
        if (plain.Length > 0) segments.Add(new MotdSegment(plain.ToString()));
        return segments;
    }

    /// <summary>The absolute http/https address, or null for anything else (another scheme, credentials, relative).</summary>
    public static string? WebLink(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host)
            ? uri.AbsoluteUri
            : null;
}
