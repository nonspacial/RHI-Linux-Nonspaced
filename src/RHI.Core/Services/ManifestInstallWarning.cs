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

    /// <summary>True when an install action on a component in this state adds it (the install upstream warns about).</summary>
    /// <remarks>Unknown counts as an install: the action falls through to the installer, so it must not skip the prompt.</remarks>
    public static bool IsFirstInstall(ComponentAvailability availability)
        => availability is not (ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable);

    /// <summary>
    /// Every manifest warning a user-initiated install must show: the managed dependencies the install adds
    /// automatically (in install order, e.g. RE Framework → ReShade for RenoDX on an RE Engine game), then the
    /// target itself when <paramref name="installsTarget"/>. Empty means no prompt.
    /// </summary>
    public static IReadOnlyList<ComponentInstallWarning> ForInstall(
        DetectedGame game,
        ManagedComponentKind target,
        bool installsTarget,
        IEnumerable<ManagedComponentKind> automaticDependencies)
    {
        var result = new List<ComponentInstallWarning>();
        var seen = new HashSet<ManagedComponentKind> { target };
        foreach (var dependency in automaticDependencies)
        {
            if (seen.Add(dependency) && Find(game, dependency) is { } warning)
                result.Add(new ComponentInstallWarning(dependency, AddedAutomatically: true, warning));
        }
        if (installsTarget && Find(game, target) is { } own)
            result.Add(new ComponentInstallWarning(target, AddedAutomatically: false, own));
        return result;
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
            // Sentence punctuation after an inline link is not part of it ("… at https://x.test/a."); a closing
            // bracket stays when it closes one opened inside the link (Wikipedia-style "/wiki/Foo_(bar)").
            while (length > 0 && ".,;:!?)]}'\"".Contains(text[start + length - 1]) && !ClosesInnerBracket(text, start, length)) length--;
            if (Uri.TryCreate(text.Substring(start, length), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host))
            {
                if (!links.Contains(uri)) links.Add(uri);
                // Only a link that really ends the text moves out of it; "(see https://x.test)" keeps its bracket pair.
                if (end == text.Length && text.AsSpan(start + length).IndexOfAny(")]}") < 0) trailingStart = start;
            }
            index = end;
        }

        // A link on its own at the end is shown as the link row only; inline links keep their place in the text.
        if (trailingStart >= 0) text = text[..trailingStart].TrimEnd();
        return new ManifestInstallWarning(text, links);
    }

    private static bool ClosesInnerBracket(string text, int start, int length)
    {
        var close = text[start + length - 1];
        var open = close switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
        if (open == '\0') return false;
        var depth = 0;
        for (var i = start; i < start + length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close) depth--;
        }
        return depth >= 0;
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

/// <summary>A manifest warning for one component of an install (the target, or a dependency it adds automatically).</summary>
public sealed record ComponentInstallWarning(ManagedComponentKind Kind, bool AddedAutomatically, ManifestInstallWarning Warning);
