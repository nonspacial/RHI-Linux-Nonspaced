using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// #251: the page reads the HDR support lookup (<see cref="Hdr.HdrSupportService"/>) needs, through the same request gate,
/// pause and minimum gap as every other PCGamingWiki request (PCGW allows 60 requests a minute). Anonymous Cargo queries
/// are refused, so the page's wikitext is read through <c>action=parse</c>.
/// </summary>
public sealed partial class PcGamingWikiGraphicsApiService
{
    /// <summary>
    /// The PCGW page title for a game: the manifest's page URL, else the page Steam's AppID leads to (<c>appid.php</c>),
    /// else PCGW's own search for the name. <c>DefinitelyMissing</c> is true only when PCGW answered and has no page.
    /// </summary>
    internal async Task<(string? Title, bool DefinitelyMissing)> ResolvePageTitleAsync(
        string gameName, int? steamAppId, string? pageUrlOverride, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(pageUrlOverride))
            return (TitleFromPageUrl(pageUrlOverride), false);

        if (steamAppId is > 0)
        {
            var appIdUrl = new Uri($"https://www.pcgamingwiki.com/api/appid.php?appid={steamAppId.Value}");
            var (html, outcome) = await GetTextAsync(appIdUrl, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (html is not null && PageNameRegex().Match(html) is { Success: true } page)
                return (Regex.Unescape(page.Groups[1].Value).Replace('_', ' '), false);
            if (html is null && outcome == FetchOutcome.Unavailable)
                return (null, false);
            // No page for the AppID: PCGW's search may still know the name.
        }

        var (url, missing) = await SearchPageAsync(gameName, cancellationToken).ConfigureAwait(false);
        return url is null ? (null, missing) : (TitleFromPageUrl(url), false);
    }

    /// <summary>
    /// The wikitext of a PCGW page (redirects followed). <c>DefinitelyMissing</c> is true only when PCGW answered that the
    /// page does not exist; null text and false = not available now (paused, offline, timed out, unreadable).
    /// </summary>
    internal async Task<(string? Wikitext, bool DefinitelyMissing)> FetchWikitextAsync(string pageTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pageTitle)) return (null, true);
        var uri = new Uri("https://www.pcgamingwiki.com/w/api.php?action=parse&page=" + Uri.EscapeDataString(pageTitle.Trim())
                          + "&prop=wikitext&format=json&formatversion=2&redirects=1");
        var (json, outcome) = await GetTextAsync(uri, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (json is null) return (null, outcome == FetchOutcome.Missing);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, false);
            if (root.TryGetProperty("error", out var error))
                return (null, error.TryGetProperty("code", out var code) && code.GetString() is "missingtitle" or "invalidtitle");
            return root.TryGetProperty("parse", out var parse) && parse.TryGetProperty("wikitext", out var text) && text.ValueKind == JsonValueKind.String
                ? (text.GetString(), false)
                : (null, false);
        }
        catch (JsonException ex)
        {
            AppLog.Warn("hdr-support", $"PCGamingWiki page text unreadable for {pageTitle}", ex);
            return (null, false);
        }
    }

    /// <summary>The page title in a PCGW page URL (<c>…/wiki/Borderlands_3</c> → <c>Borderlands 3</c>); null when it has none.</summary>
    internal static string? TitleFromPageUrl(string pageUrl)
    {
        if (!TryValidatePageUrl(pageUrl, out var uri)) return null;
        const string prefix = "/wiki/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length == prefix.Length) return null;
        return Uri.UnescapeDataString(path[prefix.Length..]).Replace('_', ' ').Trim();
    }

    [GeneratedRegex("\"wgPageName\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)+)\"")]
    private static partial Regex PageNameRegex();
}
