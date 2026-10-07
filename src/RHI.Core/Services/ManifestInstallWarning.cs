using RHI.Core.Models;

namespace RHI.Core.Services;

/// <summary>
/// An upstream manifest <c>installWarnings[game][component]</c> note, split for display: the text without a trailing
/// link, and every http(s) link in it (upstream 2.8.0 renders a trailing URL as a clickable link).
/// </summary>
public sealed record ManifestInstallWarning(string Text, IReadOnlyList<Uri> Links)
{
    /// <summary>Upstream manifest component key for a Linux component; null when upstream defines no warning key.</summary>
    /// <remarks>Upstream also uses <c>dxvk</c>, which has no Linux component (Proton owns DXVK).</remarks>
    public static string? ManifestKey(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReShade => "reshade",
        ManagedComponentKind.RenoDX => "renodx",
        ManagedComponentKind.LumaFramework => "luma",
        ManagedComponentKind.OptiScaler => "optiscaler",
        ManagedComponentKind.REFramework => "reframework",
        ManagedComponentKind.ReLimiter => "relimiter",
        ManagedComponentKind.DisplayCommander => "dc",
        _ => null
    };

    /// <summary>The warning the manifest attaches to installing <paramref name="kind"/> in <paramref name="game"/>, if any.</summary>
    /// <remarks>
    /// The game's warnings were attached under its canonical manifest name during the library policy pass
    /// (<c>DetectedGame.ManifestInstallWarnings</c>); keys compare case-insensitively like upstream's.
    /// </remarks>
    public static ManifestInstallWarning? Find(DetectedGame game, ManagedComponentKind kind)
    {
        if (ManifestKey(kind) is not { } key) return null;
        foreach (var (component, message) in game.ManifestInstallWarnings)
        {
            if (component.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(message))
                return Parse(message);
        }
        return null;
    }

    public static ManifestInstallWarning Parse(string message)
    {
        var text = message.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var links = new List<Uri>();
        var trailingStart = -1;
        var index = 0;
        while (index < text.Length)
        {
            var start = NextLinkStart(text, index);
            if (start < 0) break;
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            var length = end - start;
            // Sentence punctuation after an inline link is not part of it ("… at https://x.test/a.").
            while (length > 0 && ".,;:!?)]}'\"".Contains(text[start + length - 1])) length--;
            if (Uri.TryCreate(text.Substring(start, length), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host))
            {
                if (!links.Contains(uri)) links.Add(uri);
                if (end == text.Length) trailingStart = start;
            }
            index = end;
        }

        // A link on its own at the end is shown as the link row only; inline links keep their place in the text.
        if (trailingStart >= 0) text = text[..trailingStart].TrimEnd();
        return new ManifestInstallWarning(text, links);
    }

    private static int NextLinkStart(string text, int from)
    {
        var http = text.IndexOf("http://", from, StringComparison.OrdinalIgnoreCase);
        var https = text.IndexOf("https://", from, StringComparison.OrdinalIgnoreCase);
        if (http < 0) return https;
        if (https < 0) return http;
        return Math.Min(http, https);
    }
}
