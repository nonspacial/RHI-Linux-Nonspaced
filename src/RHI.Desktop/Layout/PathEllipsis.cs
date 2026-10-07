namespace RHI.Desktop.Layout;

/// <summary>
/// One-line display form of a path for a given width: keeps the first folder(s) and the file / last folder and
/// replaces the middle segments with an ellipsis, e.g. <c>/home/nope/…/games/Prince of Persia/PrinceOfPersia_TLC.exe</c>.
/// Text that is not a path (status messages) is trimmed at the end instead. Pure: the caller supplies the
/// text-width function, so the result depends only on the arguments.
/// </summary>
public static class PathEllipsis
{
    public const string Ellipsis = "…";

    /// <summary>True for absolute or home-relative Unix paths and Windows drive paths.</summary>
    public static bool LooksLikePath(string text)
        => text.Length > 1 && (text[0] == '/' || text[0] == '~'
            || (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/')));

    /// <summary>Returns <paramref name="text"/> unchanged when it fits <paramref name="maxWidth"/>, otherwise the
    /// widest compacted form that fits (never wider than <paramref name="maxWidth"/> unless even "…" is wider).
    /// Text that is not a path is trimmed at the end, or in the middle when <paramref name="middleForText"/> is set
    /// (one-line lists such as "renodx-x.addon64 · by Marat", where both ends matter).</summary>
    public static string Compact(string? text, double maxWidth, Func<string, double> measure, bool middleForText = false)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (double.IsNaN(maxWidth) || double.IsPositiveInfinity(maxWidth) || measure(text) <= maxWidth) return text;
        if (measure(Ellipsis) > maxWidth) return Ellipsis;
        if (LooksLikePath(text)) return CompactPath(text, maxWidth, measure) ?? MiddleChars(text, maxWidth, measure);
        return middleForText ? MiddleChars(text, maxWidth, measure) : EndChars(text, maxWidth, measure);
    }

    // Segment-level ellipsis. Head keeps the root and the first folder; tail keeps the last segment; then they
    // grow alternately (tail first) while the result still fits. Null when not even "head/…/last" fits.
    private static string? CompactPath(string text, double maxWidth, Func<string, double> measure)
    {
        var separator = text.Count(c => c == '\\') > text.Count(c => c == '/') ? '\\' : '/';
        var segments = text.Split(separator);
        var n = segments.Length;
        // "/a/b" → ["", "a", "b"]: keep the empty root segment plus the first folder.
        var head = segments[0].Length == 0 ? 2 : 1;
        var tail = 1;
        if (head + tail >= n) return null;

        string Build(int h, int t)
            => string.Join(separator, segments, 0, h) + separator + Ellipsis + separator + string.Join(separator, segments, n - t, t);

        var best = Build(head, tail);
        if (measure(best) > maxWidth)
        {
            // Drop the head: "…/last".
            var tailOnly = Ellipsis + separator + segments[n - 1];
            return measure(tailOnly) <= maxWidth ? tailOnly : null;
        }

        var growTail = true;
        var stuck = 0;
        while (stuck < 2 && head + tail + 1 < n)
        {
            var candidate = growTail ? Build(head, tail + 1) : Build(head + 1, tail);
            if (measure(candidate) <= maxWidth)
            {
                best = candidate;
                if (growTail) tail++; else head++;
                stuck = 0;
            }
            else
            {
                stuck++;
            }
            growTail = !growTail;
        }
        return best;
    }

    // Keeps the start and the end of the text (slightly more of the end, where the file name is).
    private static string MiddleChars(string text, double maxWidth, Func<string, double> measure)
    {
        string Build(int keep) => text[..(keep / 2)] + Ellipsis + text[(text.Length - (keep - keep / 2))..];
        var lo = 0;
        var hi = text.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (measure(Build(mid)) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return Build(lo);
    }

    private static string EndChars(string text, double maxWidth, Func<string, double> measure)
    {
        var lo = 0;
        var hi = text.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (measure(text[..mid].TrimEnd() + Ellipsis) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return text[..lo].TrimEnd() + Ellipsis;
    }
}
