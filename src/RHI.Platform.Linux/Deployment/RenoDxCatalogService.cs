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
    string? Author = null)
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
    // Upstream credits the generic engine add-ons and Marat's UE-Extended build by name.
    public const string GenericUnrealAuthor = "ShortFuse";
    public const string GenericUnityAuthor = "Voosh";
    public const string UeExtendedAuthor = "Marat";

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService? _upstreamManifest;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _resolvedMatchGate = new();
    private readonly Dictionary<string, RenoDxModEntry?> _resolvedMatches = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RenoDxModEntry>? _cache;

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
            try
            {
                var json = await _http.GetStringAsync(StructuredDbUrl, cancellationToken).ConfigureAwait(false);
                structured = ParseStructuredDatabase(json);
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

            _cache = MergeCatalogs(structured, wikiFallback);
            return _cache;
        }
        finally { _gate.Release(); }
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
        // Upstream wikiUnlinks is a hard sever from RenoDX matching, including generic fallbacks.
        if (game.WikiUnlinkedByManifest) return null;

        // Upstream RHI manifest snapshot overrides are authoritative over wiki/generic matching.
        // This is what links titles such as Hogwarts Legacy to Marat's UE-Extended build.
        if (_upstreamManifest is not null)
        {
            try
            {
                var gameOverride = await _upstreamManifest.ResolveGameOverrideAsync(game, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(gameOverride?.SnapshotUrl))
                {
                    var url = gameOverride.SnapshotUrl!;
                    var is32 = url.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase);
                    var notes = string.IsNullOrWhiteSpace(gameOverride.EngineHint)
                        ? "Upstream RHI manifest snapshot override"
                        : $"Upstream RHI manifest snapshot override · {gameOverride.EngineHint}";
                    return new RenoDxModEntry(
                        game.Name,
                        is32 ? null : url,
                        is32 ? url : null,
                        notes,
                        Author: SnapshotOverrideAuthor(url));
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Manifest metadata is an enrichment layer; fall back to the RenoDX wiki/generic path offline.
            }
        }

        var mods = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var lookupName = string.IsNullOrWhiteSpace(game.WikiNameOverride) ? game.Name : game.WikiNameOverride;
        var gameNorm = NormalizeName(lookupName!);
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

        if (LooksLikeUnity(indexedFiles))
            return new RenoDxModEntry("Generic Unity", GenericUnityUrl64, GenericUnityUrl32, "Generic RenoDX Unity addon", true, Author: GenericUnityAuthor);
        if (LooksLikeUnreal(indexedFiles))
            return new RenoDxModEntry("Generic Unreal", GenericUnrealUrl, null, "Generic RenoDX Unreal addon", true, Author: GenericUnrealAuthor);
        return null;
    }

    // Manifest snapshot overrides carry only a URL; credit the builds whose author is known from the URL. Others fall
    // back to the manifest authorOverrides entry (ModAuthorCredit.ForRenoDx).
    internal static string? SnapshotOverrideAuthor(string url)
    {
        var file = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : "";
        if (file.StartsWith("renodx-ue-extended.", StringComparison.OrdinalIgnoreCase)) return UeExtendedAuthor;
        if (file.StartsWith("renodx-unityengine.", StringComparison.OrdinalIgnoreCase)) return GenericUnityAuthor;
        if (file.StartsWith("renodx-unrealengine.", StringComparison.OrdinalIgnoreCase)) return GenericUnrealAuthor;
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
                prior.Author ?? entry.Author);
        }

        return merged.Values.ToArray();
    }

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
                Author: string.IsNullOrWhiteSpace(author) ? null : author));
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
            result.Add(new RenoDxModEntry(name, url64, url32, string.IsNullOrWhiteSpace(notes) ? null : notes));
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
