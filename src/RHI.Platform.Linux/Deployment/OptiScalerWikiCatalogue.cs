using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One game of the OptiScaler wiki's compatibility list (#184; upstream <c>OptiScalerCompatEntry</c>).</summary>
/// <param name="Status"><see cref="OptiScalerWikiCatalogue.Working"/>, <see cref="OptiScalerWikiCatalogue.NotWorking"/>,
/// <see cref="OptiScalerWikiCatalogue.Partial"/> or <see cref="OptiScalerWikiCatalogue.Unknown"/>.</param>
public sealed record OptiScalerCompatEntry(
    [property: JsonPropertyName("game")] string GameName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("upscalers")] IReadOnlyList<string> Upscalers,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("page")] string? DetailPageUrl);

/// <summary>
/// The OptiScaler wiki's compatibility lists (#184; upstream <c>OptiScalerWikiService</c>): the general list and the FSR4
/// list (merged into the general one on the wiki today; still read, as upstream). The GitHub wiki pages are read under
/// <see cref="DownloadSources.OptiScalerWiki"/> (github.com only, 8 MB cap, 30 s), parsed as text only — table cells'
/// text, the game cell's link to its wiki page; no scripts, images or other markup are kept — and the result is kept
/// 24 h in RHI's cache folder. Columns are found by their headings (the wiki added an OptiPatcher column that
/// upstream's fixed column numbers now misread). A list that cannot be read leaves that part of the dialog out.
/// </summary>
public sealed partial class OptiScalerWikiCatalogue
{
    public const string CompatibilityListUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
    public const string Fsr4CompatibilityListUrl = "https://github.com/optiscaler/OptiScaler/wiki/FSR4-Compatibility-List";
    private const string WikiBase = "https://github.com/optiscaler/OptiScaler/wiki/";
    public const string Working = "working";
    public const string NotWorking = "not-working";
    public const string Partial = "partial";
    public const string Unknown = "unknown";
    private const int MaxRows = 10_000;
    private const int MaxCellText = 2_000;

    private readonly CachedRemoteList<OptiScalerCompatEntry> _standard;
    private readonly CachedRemoteList<OptiScalerCompatEntry> _fsr4;

    public OptiScalerWikiCatalogue(HttpClient http, string? cacheDirectory = null, TimeSpan? freshness = null, TimeProvider? timeProvider = null)
    {
        var directory = cacheDirectory ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "links");
        var time = timeProvider ?? TimeProvider.System;
        var fresh = freshness ?? TimeSpan.FromHours(24);
        _standard = new("OptiScaler compatibility list", CompatibilityListUrl, DownloadSources.OptiScalerWiki, Parse, e => e.GameName, Usable,
            Path.Combine(directory, "optiscaler-wiki.json"), fresh, time, http, Key);
        _fsr4 = new("OptiScaler FSR4 compatibility list", Fsr4CompatibilityListUrl, DownloadSources.OptiScalerWiki, Parse, e => e.GameName, Usable,
            Path.Combine(directory, "optiscaler-wiki-fsr4.json"), fresh, time, http, Key);
    }

    /// <summary>Loads (or joins the load of) both lists; never faults.</summary>
    public Task LoadAsync() => Task.WhenAll(_standard.LoadAsync(), _fsr4.LoadAsync());

    /// <summary>The game's rows on the general and FSR4 lists by its wiki name (case and quote style ignored); null until loaded.</summary>
    public (OptiScalerCompatEntry? Standard, OptiScalerCompatEntry? Fsr4) Find(string wikiName)
        => (_standard.Find(Key(wikiName)), _fsr4.Find(Key(wikiName)));

    /// <summary>
    /// Upstream <c>ResolveOptiScalerWikiName</c>: the manifest's <c>optiScalerWikiNames</c> entry for the game, else its
    /// name without ™/®/©.
    /// </summary>
    public static string WikiNameFor(string gameName, string? manifestWikiName)
        => !string.IsNullOrWhiteSpace(manifestWikiName)
            ? manifestWikiName.Trim()
            : gameName.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal).Trim();

    private static string Key(string? name) => StraightQuotes(name ?? "").Trim().ToLowerInvariant();

    private static bool Usable(OptiScalerCompatEntry entry)
        => entry.GameName.Length > 0 && entry.Status is Working or NotWorking or Partial or Unknown && entry.Upscalers is not null
           && (entry.DetailPageUrl is null || IsWikiPage(entry.DetailPageUrl));

    /// <summary>Every row of every table whose headings name a Game column, first row per game winning (upstream).</summary>
    public static IReadOnlyList<OptiScalerCompatEntry> Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var result = new List<OptiScalerCompatEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in document.DocumentNode.SelectNodes("//table") ?? Enumerable.Empty<HtmlNode>())
        {
            var headings = table.SelectNodes(".//tr[th]")?.FirstOrDefault()?.SelectNodes("th")?.Select(th => Clean(th.InnerText).ToLowerInvariant()).ToList();
            if (headings is null) continue;
            var game = headings.FindIndex(h => h == "game" || h.StartsWith("game ", StringComparison.Ordinal));
            if (game < 0) continue;
            var status = headings.FindIndex(h => h.Contains("compatibility", StringComparison.Ordinal) || h == "status");
            var upscalers = headings.FindIndex(h => h.Contains("upscaler", StringComparison.Ordinal));
            var notes = headings.FindIndex(h => h.StartsWith("notes", StringComparison.Ordinal));
            foreach (var row in table.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
            {
                if (result.Count >= MaxRows) return result;
                var cells = row.SelectNodes("td");
                if (cells is null || cells.Count <= game) continue;
                var name = Clean(cells[game].InnerText);
                if (name.Length == 0 || name.Length > 200 || !seen.Add(Key(name))) continue;
                var link = cells[game].SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", "");
                result.Add(new OptiScalerCompatEntry(
                    name,
                    status >= 0 && status < cells.Count ? StatusOf(cells[status].InnerText) : Unknown,
                    upscalers >= 0 && upscalers < cells.Count ? UpscalersOf(Clean(cells[upscalers].InnerText)) : [],
                    notes >= 0 && notes < cells.Count && Clean(cells[notes].InnerText) is { Length: > 0 } text ? Cap(text) : null,
                    PageOf(link)));
            }
        }
        return result;
    }

    private static string StatusOf(string cell)
    {
        var text = HtmlEntity.DeEntitize(cell);
        if (text.Contains('❌')) return NotWorking;
        if (text.Contains('⚠')) return Partial;
        if (text.Contains('✅') || text.Contains('✔')) return Working;
        return Unknown;
    }

    private static IReadOnlyList<string> UpscalersOf(string cell)
        => cell.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(u => u.Length is > 0 and <= 40).Take(12).ToArray();

    /// <summary>The game's own wiki page (a link inside the OptiScaler wiki, https), else null.</summary>
    private static string? PageOf(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        href = HtmlEntity.DeEntitize(href).Trim();
        if (href.StartsWith('#') || href.Contains("..", StringComparison.Ordinal)) return null;
        var absolute = href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? href
            : href.StartsWith('/') ? "https://github.com" + href
            : href.Contains(':', StringComparison.Ordinal) ? null
            : WikiBase + href.TrimStart('.', '/');
        return absolute is not null && IsWikiPage(absolute) ? GameLinkCatalogue.SafePage(absolute, LinkHosts.Wiki) : null;
    }

    private static bool IsWikiPage(string url)
        => GameLinkCatalogue.SafePage(url, LinkHosts.Wiki) is { } safe
           && new Uri(safe) is { Host: "github.com" } uri
           && uri.AbsolutePath.StartsWith("/optiscaler/OptiScaler/wiki/", StringComparison.OrdinalIgnoreCase);

    private static string Cap(string text) => text.Length <= MaxCellText ? text : text[..MaxCellText].TrimEnd() + "…";

    private static string Clean(string? html) => StraightQuotes(Whitespace().Replace(HtmlEntity.DeEntitize(html ?? ""), " ").Trim());

    private static string StraightQuotes(string s)
        => s.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"');

    [GeneratedRegex(@"\s+", RegexOptions.None, 2000)]
    private static partial Regex Whitespace();
}
