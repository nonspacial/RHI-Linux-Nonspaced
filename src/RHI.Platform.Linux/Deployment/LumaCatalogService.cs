using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

/// <param name="NexusUrl">#245: the Nexus Mods link the wiki gives for the mod (as written; opened only through <c>LinkHosts.NexusMod</c>), else null.</param>
public sealed record LumaCatalogEntry(
    string Name,
    string Author,
    string? DownloadUrl,
    string Status,
    string? SpecialNotes,
    string? FeatureNotes,
    string? NexusUrl = null)
{
    /// <summary>
    /// #245 (upstream 2.8.8 <c>20d3297</c>): the wiki lists this mod on Nexus Mods and gives no download link, so RHI
    /// never downloads it (never from a same-named GitHub build): its update check asks Nexus, its update opens the page.
    /// </summary>
    public bool IsNexusOnly => NexusUrl is not null && string.IsNullOrWhiteSpace(DownloadUrl);
}

public sealed record LumaEngineIniEntry(string Section, string Key, string Value);

/// <summary>
/// Per-game metadata from the Luma generic Unreal Engine compatibility table.
/// Mirrors the upstream RHI fields: notes, Engine.ini keys, launch arguments,
/// HDR / DLSS-FSR flags, and the wiki's UE version hint.
/// </summary>
public sealed record LumaGenericGameEntry(
    string Name,
    string? Notes,
    IReadOnlyList<LumaEngineIniEntry> EngineIniKeys,
    string? LaunchArgs,
    bool HdrSupported,
    bool DlssFsrSupported,
    bool DlssFsrBlocked,
    string? UeVersion);

/// <summary>
/// Upstream-compatible reader for both Luma Framework wiki surfaces used by RHI:
/// Completed Mods and the generic Unreal Engine compatibility table. Named-mod resolution is
/// exact after normalization; manifest lumaNameOverrides are authoritative over the launcher-visible
/// game name. Generic metadata also uses exact normalized identity and never substring-adopts a game.
/// </summary>
public sealed class LumaCatalogService
{
    public const string WikiUrl = "https://github.com/Filoppi/Luma-Framework/wiki";

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService? _manifest;
    private readonly string _wikiUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _resolutionLock = new();
    private readonly Dictionary<string, LumaCatalogEntry?> _resolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LumaGenericGameEntry?> _genericResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _wikiHtml;
    private IReadOnlyList<LumaCatalogEntry>? _cache;
    private IReadOnlyDictionary<string, LumaGenericGameEntry>? _genericCache;

    public LumaCatalogService(
        HttpClient http,
        UpstreamRhiManifestService? manifest = null,
        string? wikiUrl = null)
    {
        _http = http;
        _manifest = manifest;
        _wikiUrl = wikiUrl ?? WikiUrl;
    }

    /// <summary>
    /// The Completed Mods table merged with the mods built in Filoppi's latest release but not yet on the wiki (#94,
    /// upstream 2.7.6 <c>748ab76</c>): the wiki wins; a wiki row without a download link takes the release asset's; a
    /// release-only mod is added. Release assets are an enrichment: when they cannot be read the wiki alone is used
    /// (logged); the wiki itself is still required, as before.
    /// </summary>
    public async Task<IReadOnlyList<LumaCatalogEntry>> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache;
        var html = await FetchWikiHtmlAsync(cancellationToken).ConfigureAwait(false);
        var releases = await FetchReleaseModsAsync(cancellationToken).ConfigureAwait(false);
        lock (_resolutionLock)
        {
            _cache ??= MergeReleaseMods(ParseCompletedMods(html), releases);
            return _cache;
        }
    }

    /// <summary>Filoppi's release list (upstream reads the first 5 and uses the newest).</summary>
    public const string ReleasesListApiUrl = "https://api.github.com/repos/Filoppi/Luma-Framework/releases?per_page=5";

    // Generics and developer tools, not game mods (upstream's exclusions).
    private static readonly HashSet<string> ReleaseExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generic Mod", "Unreal Engine", "Unity Engine", "Graphics Analyzer"
    };

    private async Task<IReadOnlyList<LumaCatalogEntry>> FetchReleaseModsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await RHI.Core.Deployment.TrustedDownload.GetStringAsync(_http, ReleasesListApiUrl, DownloadSources.GitHubApi,
                accept: "application/vnd.github+json", cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseReleaseMods(json);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or IOException or System.Text.Json.JsonException
                                       or RHI.Core.Deployment.UntrustedDownloadException or InvalidOperationException or TaskCanceledException)
        {
            RHI.Core.Services.AppLog.Warn("luma-catalog", "Could not read Luma's release assets; using the wiki's mods only", ex);
            return [];
        }
    }

    /// <summary>
    /// Named game mods among the newest release's assets: <c>Luma-&lt;Game_Name&gt;[-x32|-x64].zip</c> (upstream's rule:
    /// <c>-Test</c>/<c>-Dev</c> builds skipped, <c>_</c> → space, <c>.</c> → apostrophe, generics excluded, one per name).
    /// </summary>
    internal static IReadOnlyList<LumaCatalogEntry> ParseReleaseMods(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return [];
        var latest = doc.RootElement[0];
        if (!latest.TryGetProperty("assets", out var assets) || assets.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
        var result = new List<LumaCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.StartsWith("Luma-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("-Test", StringComparison.OrdinalIgnoreCase) || name.Contains("-Dev", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = name[5..^4];
            if (stem.EndsWith("-x32", StringComparison.OrdinalIgnoreCase) || stem.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            var game = stem.Replace('_', ' ').Replace('.', '\'').Trim();
            if (game.Length == 0 || ReleaseExclusions.Contains(game) || !seen.Add(NormalizeName(game))) continue;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            result.Add(new LumaCatalogEntry(game, "", url, "✅", null, null));
        }
        return result;
    }

    /// <summary>
    /// Upstream's wiki-wins merge (<c>LumaService.MergeLumaMods</c>), matched by the catalogue's own name normalisation. A
    /// wiki row takes a release asset only when it has neither a download nor a Nexus link (#245: a Nexus-only mod is
    /// never filled from a same-named GitHub build).
    /// </summary>
    internal static IReadOnlyList<LumaCatalogEntry> MergeReleaseMods(IReadOnlyList<LumaCatalogEntry> wiki, IReadOnlyList<LumaCatalogEntry> releases)
    {
        var byName = new Dictionary<string, LumaCatalogEntry>(StringComparer.Ordinal);
        foreach (var release in releases) byName.TryAdd(NormalizeName(release.Name), release);
        var merged = new List<LumaCatalogEntry>(wiki.Count + releases.Count);
        var wikiNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in wiki)
        {
            var key = NormalizeName(entry.Name);
            wikiNames.Add(key);
            merged.Add(string.IsNullOrWhiteSpace(entry.DownloadUrl) && entry.NexusUrl is null && byName.TryGetValue(key, out var release)
                ? entry with { DownloadUrl = release.DownloadUrl }
                : entry);
        }
        merged.AddRange(releases.Where(release => !wikiNames.Contains(NormalizeName(release.Name))));
        return merged;
    }

    public async Task<IReadOnlyDictionary<string, LumaGenericGameEntry>> FetchGenericUnrealAsync(
        CancellationToken cancellationToken = default)
    {
        if (_genericCache is not null) return _genericCache;
        var html = await FetchWikiHtmlAsync(cancellationToken).ConfigureAwait(false);
        lock (_resolutionLock)
        {
            _genericCache ??= ParseGenericUnrealTable(html);
            return _genericCache;
        }
    }

    private async Task<string> FetchWikiHtmlAsync(CancellationToken cancellationToken)
    {
        if (_wikiHtml is not null) return _wikiHtml;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_wikiHtml is not null) return _wikiHtml;
            using var request = new HttpRequestMessage(HttpMethod.Get, _wikiUrl);
            request.Headers.UserAgent.ParseAdd(RHI.Core.RhiProductInfo.UserAgent);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // #233: the idle limit, so a stalled wiki page never holds the gate (and every later Luma lookup) for good.
            _wikiHtml = await RHI.Core.Deployment.IdleTimeoutStream.ReadStringAsync(response.Content, DownloadSources.Luma.Name, cancellationToken).ConfigureAwait(false);
            return _wikiHtml;
        }
        finally { _gate.Release(); }
    }

    public async Task<LumaCatalogEntry?> ResolveAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var key = ResolutionKey(game);
        lock (_resolutionLock)
            if (_resolutionCache.TryGetValue(key, out var cached)) return cached;

        string? mappedName = null;
        if (_manifest is not null)
        {
            try
            {
                mappedName = await _manifest.ResolveLumaNameOverrideAsync(game, cancellationToken).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Manifest metadata is an enrichment layer. Direct wiki matching remains available offline.
            }
        }

        IReadOnlyList<LumaCatalogEntry> entries;
        try { entries = await FetchAsync(cancellationToken).ConfigureAwait(false); }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // Upstream's order (MatchLumaGame): the manifest's Luma name, then the wiki name (Edit game's, else the manifest's
        // wikiNameOverride, as RenoDX's lookup uses), then the detected name; the first that names a listed mod wins.
        LumaCatalogEntry? resolved = null;
        foreach (var target in new[] { mappedName, game.WikiNameOverride, game.Name })
        {
            if (string.IsNullOrWhiteSpace(target)) continue;
            var normalized = NormalizeName(target);
            resolved = entries.FirstOrDefault(entry => NormalizeName(entry.Name) == normalized);
            if (resolved is not null) break;
        }

        lock (_resolutionLock) _resolutionCache[key] = resolved;
        return resolved;
    }

    public async Task<LumaGenericGameEntry?> ResolveGenericUnrealAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var key = LinuxDeploymentRecordStore.GameKey(game);
        lock (_resolutionLock)
            if (_genericResolutionCache.TryGetValue(key, out var cached)) return cached;

        IReadOnlyDictionary<string, LumaGenericGameEntry> entries;
        try { entries = await FetchGenericUnrealAsync(cancellationToken).ConfigureAwait(false); }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var normalized = NormalizeName(game.Name);
        var resolved = entries.TryGetValue(normalized, out var direct) ? direct : null;
        lock (_resolutionLock) _genericResolutionCache[key] = resolved;
        return resolved;
    }

    public bool TryGetCached(DetectedGame game, out LumaCatalogEntry? entry)
    {
        lock (_resolutionLock) return _resolutionCache.TryGetValue(ResolutionKey(game), out entry);
    }

    // The wiki name is part of the key: an Edit-game wiki name changes the answer without changing the game's key.
    private static string ResolutionKey(DetectedGame game)
        => LinuxDeploymentRecordStore.GameKey(game) + "|" + NormalizeName(game.WikiNameOverride ?? "");

    public bool TryGetGenericCached(DetectedGame game, out LumaGenericGameEntry? entry)
    {
        var key = LinuxDeploymentRecordStore.GameKey(game);
        lock (_resolutionLock) return _genericResolutionCache.TryGetValue(key, out entry);
    }

    internal static IReadOnlyList<LumaCatalogEntry> ParseCompletedMods(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables is null) return Array.Empty<LumaCatalogEntry>();

        HtmlNode? completed = null;
        foreach (var table in tables)
        {
            var header = table.SelectSingleNode(".//tr")?.SelectNodes("th|td");
            if (header is null || header.Count < 6) continue;
            if (Clean(header[0].InnerText).Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                completed = table;
                break;
            }
        }
        if (completed is null) return Array.Empty<LumaCatalogEntry>();

        var raw = new List<(LumaCatalogEntry Entry, string? Anchor)>();
        var allAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in completed.SelectNodes(".//tr")?.Skip(1) ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = row.SelectNodes("td");
            if (cells is null || cells.Count < 4) continue;
            var name = Clean(cells[0].InnerText);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var author = cells.Count > 1 ? Clean(cells[1].InnerText) : "";
            // Upstream's rule (LumaService): the cell holds a download link, a Nexus Mods link, or both (either order); a
            // Nexus link is the mod's page, never its download (#245). It is kept as written: whatever opens or asks
            // Nexus about it checks it against LinkHosts.NexusMod first.
            string? downloadUrl = null;
            string? nexusUrl = null;
            if (cells.Count > 2)
            {
                foreach (var link in cells[2].SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    var href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")).Trim();
                    if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsNexusLink(href)) nexusUrl ??= href;
                    else downloadUrl ??= href;
                }
            }

            var statusText = cells.Count > 3 ? cells[3].InnerText : "";
            var status = statusText.Contains("🚧", StringComparison.Ordinal) ? "🚧" : "✅";
            var specialNotes = cells.Count > 4 ? ParseStructuredText(cells[4]) : null;

            string? anchor = null;
            if (cells.Count > 5)
            {
                foreach (var link in cells[5].SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    var href = link.GetAttributeValue("href", "").Trim();
                    var hash = href.LastIndexOf('#');
                    if (hash >= 0 && hash + 1 < href.Length)
                    {
                        anchor = href[(hash + 1)..];
                        break;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(anchor)) allAnchors.Add(anchor);

            raw.Add((new LumaCatalogEntry(name, author, downloadUrl, status, specialNotes, null, nexusUrl), anchor));
        }

        var result = new List<LumaCatalogEntry>(raw.Count);
        foreach (var pair in raw)
        {
            var features = string.IsNullOrWhiteSpace(pair.Anchor)
                ? null
                : ExtractAnchorSection(doc, pair.Anchor!, allAnchors);
            result.Add(pair.Entry with { FeatureNotes = features });
        }

        return result
            .GroupBy(entry => NormalizeName(entry.Name), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    internal static IReadOnlyDictionary<string, LumaGenericGameEntry> ParseGenericUnrealTable(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables is null)
            return new Dictionary<string, LumaGenericGameEntry>(StringComparer.Ordinal);

        HtmlNode? gameTable = null;
        HtmlNodeCollection? headerCells = null;
        foreach (var table in tables)
        {
            var header = table.SelectSingleNode(".//tr")?.SelectNodes("th|td");
            if (header is null || header.Count < 4) continue;
            var headers = header.Select(cell => Clean(cell.InnerText).ToLowerInvariant()).ToArray();
            var hasName = headers.Any(value => value.Contains("name", StringComparison.Ordinal));
            var hasHdr = headers.Any(value => value.Contains("hdr", StringComparison.Ordinal));
            var hasDlss = headers.Any(value => value.Contains("dlss", StringComparison.Ordinal) ||
                                               value.Contains("fsr", StringComparison.Ordinal) ||
                                               value.Contains("upscal", StringComparison.Ordinal));
            var hasUeVersion = headers.Any(value => value.Contains("ue", StringComparison.Ordinal) ||
                                                    value.Contains("version", StringComparison.Ordinal));
            if (hasName && hasHdr && hasDlss && hasUeVersion)
            {
                gameTable = table;
                headerCells = header;
                break;
            }
        }

        if (gameTable is null || headerCells is null)
            return new Dictionary<string, LumaGenericGameEntry>(StringComparer.Ordinal);

        var headerTexts = headerCells.Select(cell => Clean(cell.InnerText).ToLowerInvariant()).ToList();
        var nameCol = headerTexts.FindIndex(value => value.Contains("name", StringComparison.Ordinal));
        var hdrCol = headerTexts.FindIndex(value => value.Contains("hdr", StringComparison.Ordinal));
        var dlssCol = headerTexts.FindIndex(value => value.Contains("dlss", StringComparison.Ordinal) ||
                                                    value.Contains("fsr", StringComparison.Ordinal) ||
                                                    value.Contains("upscal", StringComparison.Ordinal));
        var notesCol = headerTexts.FindIndex(value => value.Contains("notes", StringComparison.Ordinal) ||
                                                     value.Contains("note", StringComparison.Ordinal));
        var ueVersionCol = headerTexts.FindIndex(value =>
            (value.Contains("ue", StringComparison.Ordinal) && value.Contains("version", StringComparison.Ordinal)) ||
            value == "ue version");
        if (nameCol < 0) nameCol = 0;
        if (ueVersionCol < 0) ueVersionCol = headerTexts.Count - 1;

        var result = new Dictionary<string, LumaGenericGameEntry>(StringComparer.Ordinal);
        foreach (var row in gameTable.SelectNodes(".//tr")?.Skip(1) ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = row.SelectNodes("td");
            if (cells is null || cells.Count < 2 || nameCol >= cells.Count) continue;
            var name = Clean(cells[nameCol].InnerText);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var hdrSupported = hdrCol >= 0 && hdrCol < cells.Count && IsPositiveFlag(cells[hdrCol].InnerText);
            var dlssFsrSupported = dlssCol >= 0 && dlssCol < cells.Count && IsPositiveFlag(cells[dlssCol].InnerText);
            var dlssFsrBlocked = dlssCol >= 0 && dlssCol < cells.Count && IsBlockedFlag(cells[dlssCol].InnerText);

            string? notes = null;
            IReadOnlyList<LumaEngineIniEntry> iniKeys = Array.Empty<LumaEngineIniEntry>();
            string? launchArgs = null;
            if (notesCol >= 0 && notesCol < cells.Count)
            {
                notes = ParseGenericNotes(cells[notesCol], out var parsedIni);
                iniKeys = parsedIni;
                launchArgs = ExtractLaunchArguments(cells[notesCol], notes);
            }

            string? ueVersion = null;
            if (ueVersionCol >= 0 && ueVersionCol < cells.Count)
            {
                ueVersion = Clean(cells[ueVersionCol].InnerText);
                if (string.IsNullOrWhiteSpace(ueVersion)) ueVersion = null;
            }

            var entry = new LumaGenericGameEntry(
                name,
                notes,
                iniKeys,
                launchArgs,
                hdrSupported,
                dlssFsrSupported,
                dlssFsrBlocked,
                ueVersion);
            result.TryAdd(NormalizeName(name), entry);
        }
        return result;
    }

    public static string NormalizeName(string name) => RenoDxCatalogService.NormalizeName(name);

    // Any link to Nexus Mods' site (a page, whichever host form); never taken as a download link.
    private static bool IsNexusLink(string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsPositiveFlag(string? text)
        => (text ?? "").Contains("✅", StringComparison.Ordinal) ||
           (text ?? "").Contains("☑", StringComparison.Ordinal) ||
           (text ?? "").Contains("✓", StringComparison.Ordinal) ||
           (text ?? "").Contains("🟢", StringComparison.Ordinal);

    private static bool IsBlockedFlag(string? text)
        => (text ?? "").Contains("⛔", StringComparison.Ordinal) ||
           (text ?? "").Contains("🚫", StringComparison.Ordinal);

    private static string? ParseGenericNotes(HtmlNode cell, out IReadOnlyList<LumaEngineIniEntry> iniKeys)
    {
        var keys = new List<LumaEngineIniEntry>();
        var textParts = new List<string>();

        foreach (var node in cell.ChildNodes)
        {
            if (node.Name.Equals("details", StringComparison.OrdinalIgnoreCase))
            {
                var summary = node.SelectSingleNode("summary");
                var detailsText = ExtractPreservingBreaks(node, summary);
                if (LooksLikeIniBlock(detailsText))
                    ParseEngineIniBlock(detailsText, keys);
                else if (!string.IsNullOrWhiteSpace(detailsText))
                    textParts.Add(detailsText);
                continue;
            }

            if (node.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                if (textParts.Count > 0 && textParts[^1] != "") textParts.Add("");
                continue;
            }

            var text = Clean(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text)) textParts.Add(text);
        }

        iniKeys = keys
            .GroupBy(item => $"{item.Section}\0{item.Key}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        var notes = Regex.Replace(string.Join('\n', textParts), @"\n{3,}", "\n\n").Trim();
        return string.IsNullOrWhiteSpace(notes) ? null : notes;
    }

    private static string ExtractPreservingBreaks(HtmlNode details, HtmlNode? summary)
    {
        var builder = new StringBuilder();
        foreach (var child in details.ChildNodes)
        {
            if (summary is not null && ReferenceEquals(child, summary)) continue;
            if (child.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\n');
                continue;
            }
            if (child.Name.Equals("code", StringComparison.OrdinalIgnoreCase) || child.Name.Equals("pre", StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != '\n') builder.Append('\n');
                builder.Append(HtmlEntity.DeEntitize(child.InnerText ?? "").Trim());
                builder.Append('\n');
                continue;
            }
            var text = HtmlEntity.DeEntitize(child.InnerText ?? "").Trim();
            if (text.Length == 0) continue;
            if (builder.Length > 0 && builder[builder.Length - 1] != '\n') builder.Append('\n');
            builder.Append(text);
        }
        return builder.ToString().Trim();
    }

    private static bool LooksLikeIniBlock(string value)
        => Regex.IsMatch(value, @"(?m)^\s*\[[^\]]+\]\s*$") && value.Contains('=');

    private static void ParseEngineIniBlock(string value, ICollection<LumaEngineIniEntry> result)
    {
        var section = "SystemSettings";
        foreach (var raw in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var valuePart = line[(equals + 1)..].Trim();
            if (key.Length > 0) result.Add(new LumaEngineIniEntry(section, key, valuePart));
        }
    }

    private static string? ExtractLaunchArguments(HtmlNode cell, string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var lower = notes.ToLowerInvariant();
        if (!lower.Contains("launch", StringComparison.Ordinal) && !lower.Contains("argument", StringComparison.Ordinal))
            return null;

        var parts = new List<string>();
        foreach (var code in cell.SelectNodes(".//code") ?? Enumerable.Empty<HtmlNode>())
        {
            var value = HtmlEntity.DeEntitize(code.InnerText ?? "").Trim();
            if (value.StartsWith('-') ||
                (value.Contains(' ') && value.TrimStart().StartsWith('-')))
                parts.Add(value);
        }
        if (parts.Count > 0)
            return string.Join(' ', parts.Distinct(StringComparer.OrdinalIgnoreCase));

        var match = Regex.Match(
            notes,
            @"(?:launch(?:ing)?\s+using|use|argument)[^\-]*(-[\w=\s-]+?)(?:\.|,|\s+In-game|\s+Recommended|$)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ParseStructuredText(HtmlNode cell)
    {
        var html = Regex.Replace(cell.InnerHtml ?? "", "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        var temp = new HtmlDocument();
        temp.LoadHtml(html);
        var text = HtmlEntity.DeEntitize(temp.DocumentNode.InnerText ?? "").Trim();
        text = Regex.Replace(text, @"(\S)\[", "$1\n[");
        text = text.Replace("•", "\n•", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\n{2,}", "\n\n");
        text = Regex.Replace(text, @"[ \t]+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ExtractAnchorSection(HtmlDocument doc, string anchorId, HashSet<string> allAnchors)
    {
        var heading = doc.DocumentNode.SelectSingleNode($"//*[@id='{anchorId}']")
            ?? doc.DocumentNode.SelectSingleNode($"//*[@id='user-content-{anchorId}']");
        if (heading is null) return null;
        var headingElement = heading.Name == "a" && heading.ParentNode is not null ? heading.ParentNode : heading;
        var parts = new List<string>();
        for (var sibling = headingElement.NextSibling; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") break;
            if (ContainsOtherAnchor(sibling, anchorId, allAnchors)) break;
            if (sibling.Name == "p" && (sibling.FirstChild?.Name is "strong" or "b") && parts.Count > 0) break;

            string text;
            if (sibling.Name is "ul" or "ol")
            {
                var items = sibling.SelectNodes(".//li");
                text = items is null
                    ? Clean(sibling.InnerText)
                    : string.Join('\n', items.Select(item => "• " + Clean(item.InnerText)));
            }
            else text = Clean(sibling.InnerText);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private static bool ContainsOtherAnchor(HtmlNode node, string current, HashSet<string> allAnchors)
    {
        static string Bare(string value) => value.StartsWith("user-content-", StringComparison.OrdinalIgnoreCase)
            ? value["user-content-".Length..]
            : value;

        var id = node.GetAttributeValue("id", "");
        if (!string.IsNullOrWhiteSpace(id) && !Bare(id).Equals(current, StringComparison.OrdinalIgnoreCase) && allAnchors.Contains(Bare(id)))
            return true;
        foreach (var descendant in node.SelectNodes(".//*[@id]") ?? Enumerable.Empty<HtmlNode>())
        {
            var descendantId = Bare(descendant.GetAttributeValue("id", ""));
            if (!descendantId.Equals(current, StringComparison.OrdinalIgnoreCase) && allAnchors.Contains(descendantId))
                return true;
        }
        return false;
    }

    private static string Clean(string? value)
        => Regex.Replace(HtmlEntity.DeEntitize(value ?? ""), "\\s+", " ").Trim();
}
