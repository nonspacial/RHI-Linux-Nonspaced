using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

public sealed record RenoDxModEntry(
    string Name,
    string? SnapshotUrl64,
    string? SnapshotUrl32,
    string? Notes,
    bool IsGeneric = false,
    bool IsWorkInProgress = false,
    string? Author = null,
    // #183: the mod's discussion / instructions page (RHI database discussionUrl, the wiki's name link), https only.
    string? DiscussionUrl = null,
    // #184/#197: where a mod without an RHI download is published (RHI database nexusUrl / discordUrl), https only.
    string? NexusUrl = null,
    string? DiscordUrl = null)
{
    public string? UrlFor(PeArchitecture architecture)
        => RenoDxCatalogService.ResolveAddonUrl(
            architecture == PeArchitecture.X86 ? SnapshotUrl32 ?? SnapshotUrl64 : SnapshotUrl64 ?? SnapshotUrl32);
}

public sealed class RenoDxCatalogService
{
    public const string WikiUrl = "https://github.com/clshortfuse/renodx/wiki/Mods";
    public const string StructuredDbUrl = "https://raw.githubusercontent.com/RankFTW/rhi-repo/main/database/RenoDXdb.json";
    public const string PinnedStructuredDbUrl = "https://raw.githubusercontent.com/RankFTW/RHI/0eddd366be9a8c9c183374565d20598cc8fabbcb/docs/RenoDXdb.json";
    public const string GenericUnrealUrl = "https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-unrealengine.addon64";
    public const string GenericUnityUrl64 = "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon64";
    public const string GenericUnityUrl32 = "https://github.com/NotVoosh/renodx-unity/releases/download/snapshot/renodx-unityengine.addon32";

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _resolvedMatchGate = new();
    private readonly Dictionary<string, RenoDxModEntry?> _resolvedMatches = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RenoDxModEntry>? _cache;
    // Every RHI database entry that names an author, including those without a download of their own. Upstream
    // keeps such an entry's maintainer on the card when a manifest snapshot override supplies the URL (CODE VEIN II).
    private IReadOnlyList<RenoDxModEntry> _authorEntries = [];
    // #184/#197: every named RHI database entry, also those published only on Nexus Mods or Discord (no RHI download).
    private IReadOnlyList<RenoDxModEntry> _listing = [];

    public RenoDxCatalogService(HttpClient http, UpstreamRhiManifestService? upstreamManifest = null)
    {
        _http = http;
        _upstreamManifest = upstreamManifest;
    }

    public async Task<IReadOnlyList<RenoDxModEntry>> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;

            // The maintained rhi-repo database is the primary authority. Keep the
            // last-known-good bundled snapshot as an offline/remote-failure fallback. The public wiki
            // is intentionally the tertiary source only; it no longer overrides reviewed RHI DB
            // entries when the current database is healthy.
            IReadOnlyList<RenoDxModEntry> structured = Array.Empty<RenoDxModEntry>();
            IReadOnlyList<RenoDxModEntry> authorEntries = Array.Empty<RenoDxModEntry>();
            IReadOnlyList<RenoDxModEntry> listing = Array.Empty<RenoDxModEntry>();
            try
            {
                var json = await _http.GetStringAsync(StructuredDbUrl, cancellationToken).ConfigureAwait(false);
                structured = ParseStructuredDatabase(json);
                authorEntries = ParseStructuredAuthors(json);
                listing = ParseStructuredListing(json);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Pinned fallback below.
            }

            if (structured.Count == 0)
            {
                try
                {
                    var json = await _http.GetStringAsync(PinnedStructuredDbUrl, cancellationToken).ConfigureAwait(false);
                    structured = ParseStructuredDatabase(json);
                    authorEntries = ParseStructuredAuthors(json);
                    listing = ParseStructuredListing(json);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    // Wiki fallback below.
                }
            }

            IReadOnlyList<RenoDxModEntry> wikiFallback = Array.Empty<RenoDxModEntry>();
            if (structured.Count == 0)
            {
                try
                {
                    var html = await _http.GetStringAsync(WikiUrl, cancellationToken).ConfigureAwait(false);
                    wikiFallback = ParseWiki(html);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    // No catalog authority is currently reachable; generic detection still works.
                }
            }

            _authorEntries = authorEntries;
            _cache = MergeCatalogs(structured, wikiFallback);
            _listing = listing.Count > 0 ? listing : _cache;
            return _cache;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// #184/#197: every named mod the RHI database lists — also those with no RHI download, published only on Nexus Mods
    /// or Discord (upstream lists them in Available HDR Mods and links them on the card) — else the install catalogue.
    /// </summary>
    public async Task<IReadOnlyList<RenoDxModEntry>> FetchListingAsync(CancellationToken cancellationToken = default)
    {
        await FetchAsync(cancellationToken).ConfigureAwait(false);
        return _listing;
    }

    /// <summary>#184: the listing entry named like the game (its wiki name first), by the install catalogue's name match.</summary>
    public async Task<RenoDxModEntry?> FindListedAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        var listing = await FetchListingAsync(cancellationToken).ConfigureAwait(false);
        var lookupName = string.IsNullOrWhiteSpace(game.WikiNameOverride) ? game.Name : game.WikiNameOverride;
        return MatchNamed(listing, NormalizeName(lookupName!));
    }

    public async Task<RenoDxModEntry?> PrimeResolvedMatchAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        var match = await ResolveAsync(game, indexedFiles, cancellationToken).ConfigureAwait(false);
        lock (_resolvedMatchGate)
            _resolvedMatches[ResolvedMatchKey(game)] = match;
        return match;
    }

    public bool TryGetResolvedMatch(DetectedGame game, out RenoDxModEntry? match)
    {
        lock (_resolvedMatchGate)
            return _resolvedMatches.TryGetValue(ResolvedMatchKey(game), out match);
    }

    private static string ResolvedMatchKey(DetectedGame game)
        => string.Join("|",
            NormalizeName(game.Name),
            NormalizeName(game.WikiNameOverride ?? ""),
            game.ExecutablePath ?? "",
            game.EngineHint ?? "",
            game.WikiUnlinkedByManifest ? "unlinked" : "linked",
            game.ExternalModUrl ?? "");

    public async Task<RenoDxModEntry?> ResolveAsync(
        DetectedGame game,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default)
    {
        // Upstream RHI manifest snapshot overrides are authoritative over wiki/generic matching for the download.
        // This is what links titles such as Hogwarts Legacy to Marat's UE-Extended build.
        string? overrideUrl = null;
        string? overrideEngineHint = null;
        if (_upstreamManifest is not null)
        {
            try
            {
                var gameOverride = await _upstreamManifest.ResolveGameOverrideAsync(game, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(gameOverride?.SnapshotUrl))
                {
                    overrideUrl = gameOverride.SnapshotUrl;
                    overrideEngineHint = gameOverride.EngineHint;
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Manifest metadata is an enrichment layer; fall back to the RenoDX wiki/generic path offline.
            }
        }

        // Upstream wikiUnlinks severs the wiki/database match and the generic fallbacks, but a manifest snapshot override
        // still gives the card a mod (MainViewModel.BuildCards.cs / Install.cs 2.8.5: effectiveMod = new GameMod { Name,
        // SnapshotUrl } with no maintainer), e.g. Death's Door, Outer Wilds, Haste (#141). No override: no RenoDX at all.
        if (game.WikiUnlinkedByManifest)
            return overrideUrl is null
                ? null
                : SnapshotOverrideEntry(game, overrideUrl, overrideEngineHint,
                    ModAuthorCredit.IsUeExtendedUrl(overrideUrl) ? ModAuthorCredit.UeExtendedAuthor : null);

        // UE-Extended is always Marat's build: no catalogue lookup needed for its credit.
        if (overrideUrl is not null && ModAuthorCredit.IsUeExtendedUrl(overrideUrl))
            return SnapshotOverrideEntry(game, overrideUrl, overrideEngineHint, ModAuthorCredit.UeExtendedAuthor);

        var mods = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var lookupName = string.IsNullOrWhiteSpace(game.WikiNameOverride) ? game.Name : game.WikiNameOverride;
        var gameNorm = NormalizeName(lookupName!);
        var named = MatchNamed(mods, gameNorm);

        if (overrideUrl is not null)
        {
            // Upstream (MainViewModel.BuildCards.cs, "Manifest snapshot override") only swaps the download URL of the
            // card's mod and keeps its maintainer: the database author of the named match — also an entry that has
            // no download of its own — else the generic engine fallback's maintainer.
            var credited = named ?? MatchNamed(_authorEntries, gameNorm) ?? GenericFallback(indexedFiles);
            return SnapshotOverrideEntry(game, overrideUrl, overrideEngineHint, credited?.Author, credited?.DiscussionUrl);
        }

        return named ?? GenericFallback(indexedFiles);
    }

    private static RenoDxModEntry SnapshotOverrideEntry(DetectedGame game, string url, string? engineHint, string? author, string? discussionUrl = null)
    {
        var is32 = url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
        var notes = string.IsNullOrWhiteSpace(engineHint)
            ? "Upstream RHI manifest snapshot override"
            : $"Upstream RHI manifest snapshot override · {engineHint}";
        return new RenoDxModEntry(game.Name, is32 ? null : url, is32 ? url : null, notes, Author: author, DiscussionUrl: discussionUrl);
    }

    private static RenoDxModEntry? MatchNamed(IReadOnlyList<RenoDxModEntry> mods, string gameNorm)
    {
        var normalized = mods.Select(m => (Mod: m, Norm: NormalizeName(m.Name))).ToArray();
        var exact = normalized.FirstOrDefault(x => x.Norm == gameNorm).Mod;
        if (exact is not null) return exact;

        var contained = normalized
            .Where(x => x.Norm.Length >= 4 && gameNorm.Contains(x.Norm, StringComparison.Ordinal))
            .OrderByDescending(x => x.Norm.Length)
            .FirstOrDefault().Mod;
        if (contained is not null) return contained;

        foreach (var candidate in normalized.Where(x => gameNorm.Length >= 4 && x.Norm.StartsWith(gameNorm, StringComparison.Ordinal)).OrderBy(x => x.Norm.Length))
        {
            var suffix = candidate.Norm[gameNorm.Length..];
            if (!Regex.IsMatch(suffix, "^[ivxlcdm0-9]+$", RegexOptions.IgnoreCase)) return candidate.Mod;
        }
        return null;
    }

    // Upstream's generic fallback mods carry their engine maintainer (MainViewModel.Init.cs: ShortFuse / Voosh).
    private static RenoDxModEntry? GenericFallback(IReadOnlyList<string> indexedFiles)
    {
        if (LooksLikeUnity(indexedFiles))
            return new RenoDxModEntry("Generic Unity", GenericUnityUrl64, GenericUnityUrl32, "Generic RenoDX Unity addon", true, Author: ModAuthorCredit.GenericUnityAuthor);
        if (LooksLikeUnreal(indexedFiles))
            return new RenoDxModEntry("Generic Unreal", GenericUnrealUrl, null, "Generic RenoDX Unreal addon", true, Author: ModAuthorCredit.GenericUnrealAuthor);
        return null;
    }

    internal static IReadOnlyList<RenoDxModEntry> MergeCatalogs(
        IReadOnlyList<RenoDxModEntry> structured,
        IReadOnlyList<RenoDxModEntry> live)
    {
        var merged = new Dictionary<string, RenoDxModEntry>(StringComparer.Ordinal);
        foreach (var entry in structured)
            merged[NormalizeName(entry.Name)] = entry;

        foreach (var entry in live)
        {
            var key = NormalizeName(entry.Name);
            if (!merged.TryGetValue(key, out var prior))
            {
                merged[key] = entry;
                continue;
            }

            // The reviewed RHI database is authoritative. A fallback source may only fill fields
            // the primary entry does not contain; it must never replace an existing reviewed URL.
            merged[key] = new RenoDxModEntry(
                prior.Name,
                prior.SnapshotUrl64 ?? entry.SnapshotUrl64,
                prior.SnapshotUrl32 ?? entry.SnapshotUrl32,
                prior.Notes ?? entry.Notes,
                entry.IsGeneric || prior.IsGeneric,
                prior.IsWorkInProgress,
                prior.Author ?? entry.Author,
                prior.DiscussionUrl ?? entry.DiscussionUrl);
        }

        return merged.Values.ToArray();
    }

    /// <summary>Every database entry with a name and an author, with or without a download (author lookup only).</summary>
    internal static IReadOnlyList<RenoDxModEntry> ParseStructuredAuthors(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var result = new List<RenoDxModEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()?.Trim() : null;
            var author = item.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.String ? authorEl.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(author)) continue;
            result.Add(new RenoDxModEntry(name!, null, null, null, Author: author, DiscussionUrl: DiscussionUrlOf(item)));
        }
        return result
            .GroupBy(entry => NormalizeName(entry.Name), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    /// <summary>#184/#197: every named database entry with all it publishes (download URLs only when they are add-ons).</summary>
    internal static IReadOnlyList<RenoDxModEntry> ParseStructuredListing(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var result = new List<RenoDxModEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = Text(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var url64 = Text(item, "snapshotUrl") is { } u64 && LooksLikeAddonUrl(u64) ? u64 : null;
            var url32 = Text(item, "snapshotUrl32") is { } u32 && LooksLikeAddonUrl(u32) ? u32 : null;
            var notes = Text(item, "notes")?.Trim();
            var author = Text(item, "author")?.Trim();
            result.Add(new RenoDxModEntry(name!, url64, url32, string.IsNullOrWhiteSpace(notes) ? null : notes,
                IsWorkInProgress: string.Equals(Text(item, "status")?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase),
                Author: string.IsNullOrWhiteSpace(author) ? null : author,
                DiscussionUrl: DiscussionUrlOf(item), NexusUrl: HttpsLink(Text(item, "nexusUrl")), DiscordUrl: HttpsLink(Text(item, "discordUrl"))));
        }
        return result
            .GroupBy(entry => NormalizeName(entry.Name), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IReadOnlyList<RenoDxModEntry> ParseStructuredDatabase(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RenoDX structured database root is not an array.");

        var result = new List<RenoDxModEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var url64 = item.TryGetProperty("snapshotUrl", out var url64El) ? url64El.GetString() : null;
            var url32 = item.TryGetProperty("snapshotUrl32", out var url32El) ? url32El.GetString() : null;
            if (!string.IsNullOrWhiteSpace(url64) && !LooksLikeAddonUrl(url64)) url64 = null;
            if (!string.IsNullOrWhiteSpace(url32) && !LooksLikeAddonUrl(url32)) url32 = null;
            if (url64 is null && url32 is null) continue;
            var notes = item.TryGetProperty("notes", out var notesEl) ? notesEl.GetString()?.Trim() : null;
            // Upstream RenoDXDbService.MapStatus: only "WIP" marks a mod as work in progress.
            var wip = item.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                && string.Equals(statusEl.GetString()?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase);
            var author = item.TryGetProperty("author", out var authorEl) && authorEl.ValueKind == JsonValueKind.String
                ? authorEl.GetString()?.Trim()
                : null;
            result.Add(new RenoDxModEntry(name!, url64, url32, string.IsNullOrWhiteSpace(notes) ? null : notes, IsWorkInProgress: wip,
                Author: string.IsNullOrWhiteSpace(author) ? null : author, DiscussionUrl: DiscussionUrlOf(item)));
        }

        return result
            .GroupBy(entry => NormalizeName(entry.Name), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    internal static IReadOnlyList<RenoDxModEntry> ParseWiki(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var result = new List<RenoDxModEntry>();
        var deprecatedHeading = doc.DocumentNode.SelectSingleNode(
            "//*[self::h1 or self::h2 or self::h3][contains(translate(., 'DEPRECATED', 'deprecated'), 'deprecated')]");
        foreach (var row in doc.DocumentNode.SelectNodes("//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            if (deprecatedHeading is not null && row.StreamPosition > deprecatedHeading.StreamPosition) continue;
            var cells = row.SelectNodes("td");
            if (cells is null || cells.Count < 2) continue;
            var name = Clean(cells[0].InnerText);
            if (string.IsNullOrWhiteSpace(name)) continue;
            string? url64 = null, url32 = null;
            foreach (var link in row.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", "")).Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;
                href = MakeAbsolute(href);
                if (!LooksLikeAddonUrl(href)) continue;
                if (href.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)) url32 ??= href;
                else if (href.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)) url64 ??= href;
            }
            if (url64 is null && url32 is null) continue;
            var notes = cells.Count > 3 ? Clean(cells[cells.Count - 1].InnerText) : null;
            // #183: the name cell's own link is the mod's discussion / instructions page (upstream WikiService NameUrl).
            var nameLink = cells[0].SelectSingleNode(".//a[@href]") is { } a
                ? HttpsLink(MakeAbsolute(HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")).Trim()))
                : null;
            result.Add(new RenoDxModEntry(name, url64, url32, string.IsNullOrWhiteSpace(notes) ? null : notes, DiscussionUrl: nameLink));
        }
        return result
            .GroupBy(x => NormalizeName(x.Name), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
    }

    public static string NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal);
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToLowerInvariant(), "[^a-z0-9]", "");
    }

    // Upstream 2.7.6: the Unity add-on always comes from GitHub Releases, whatever URL the manifest,
    // database or wiki carries. GitHub Pages omits Content-Length on HEAD, which defeats the cache.
    public static string? ResolveAddonUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var fileName = Path.GetFileName(uri.LocalPath);
        if (fileName.Equals("renodx-unityengine.addon64", StringComparison.OrdinalIgnoreCase)) return GenericUnityUrl64;
        if (fileName.Equals("renodx-unityengine.addon32", StringComparison.OrdinalIgnoreCase)) return GenericUnityUrl32;
        return url;
    }

    internal static bool LooksLikeUnity(IReadOnlyList<string> files)
        => files.Any(f => Path.GetFileName(f).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));

    internal static bool LooksLikeUnreal(IReadOnlyList<string> files)
        => files.Any(f =>
        {
            var normalized = f.Replace('\\', '/');
            return normalized.Contains("/Engine/", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("/Binaries/Win64/", StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(f).Contains("Shipping", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>#183: the entry's <c>discussionUrl</c> when it is an https link, else null.</summary>
    private static string? DiscussionUrlOf(JsonElement item)
        => item.TryGetProperty("discussionUrl", out var value) && value.ValueKind == JsonValueKind.String ? HttpsLink(value.GetString()) : null;

    /// <summary>An https link with a host and no credentials (the desktop opener receives it), else null.</summary>
    internal static string? HttpsLink(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
           && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort
            ? uri.AbsoluteUri
            : null;

    private static bool LooksLikeAddonUrl(string href)
        => href.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) ||
           href.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);

    private static string MakeAbsolute(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        if (href.StartsWith('/')) return "https://github.com" + href;
        return "https://github.com/clshortfuse/renodx/wiki/" + href.TrimStart('.', '/');
    }

    private static string Clean(string value)
        => Regex.Replace(HtmlEntity.DeEntitize(value ?? ""), "\\s+", " ").Trim();
}
