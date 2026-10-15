using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Network;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Where a component's ⓘ text comes from (upstream <c>InfoSourceType</c>): per-game manifest notes, a wiki/database page for the game, or general text only.</summary>
public enum ComponentInfoSource
{
    Manifest,
    Wiki,
    Fallback
}

/// <summary>What a link in the ⓘ dialog points at; the dialog words its label (a manifest link brings its own label).</summary>
public enum ComponentInfoLinkKind
{
    /// <summary>The manifest note's own link (<see cref="ComponentInfoLink.Label"/> is the manifest's label, when it gives one).</summary>
    Notes,
    /// <summary>The game's page on the component's wiki (RenoDX mod discussion, OptiScaler compatibility entry).</summary>
    WikiPage,
    /// <summary>The game's HDR analysis on the HDR Gaming Database (RenoDX and Luma).</summary>
    HdrDatabase,
    /// <summary>The RenoDX mod's page on Nexus Mods, for a mod RHI cannot download.</summary>
    NexusMods,
    /// <summary>The RenoDX mod's Discord, for a mod RHI cannot download.</summary>
    Discord,
    /// <summary>The component's releases page (ReLimiter, Display Commander).</summary>
    Releases
}

public sealed record ComponentInfoLink(ComponentInfoLinkKind Kind, string Url, string? Label = null);

/// <summary>
/// One component's ⓘ content for one game (#184). <see cref="Texts"/> are shown as given (manifest, wiki, database and
/// Nexus Mods texts, in English as upstream publishes them); <see cref="GeneralText"/> adds RHI's own description of the
/// component and <see cref="NativeHdr"/> the UE-Extended native-HDR note, both localised by the dialog.
/// </summary>
public sealed record ComponentInfo(
    ManagedComponentKind Kind,
    ComponentInfoSource Source,
    IReadOnlyList<string> Texts,
    bool GeneralText,
    bool NativeHdr,
    OptiScalerCompatEntry? Compatibility,
    OptiScalerCompatEntry? Fsr4Compatibility,
    string? Changelog,
    IReadOnlyList<ComponentInfoLink> Links);

/// <summary>
/// The content of a component's ⓘ dialog (#184; upstream <c>AddonInfoResolver</c> + <c>ShowAddonInfoDialogAsync</c>),
/// in upstream's three tiers: the manifest's per-game note for that component (<c>gameNotes</c> for RenoDX,
/// <c>lumaGameInfo</c>, <c>optiScalerGameInfo</c>, <c>relimiterGameInfo</c>, <c>displayCommanderGameInfo</c>,
/// <c>reframeworkGameInfo</c>), then the game's wiki/database content (RenoDX: the matched mod's notes, discussion page and
/// the RHI databases' comments, or the Nexus Mods summary of a mod published only there; Luma: the Luma wiki's notes and
/// <c>lumaGameNotes</c>; OptiScaler: the wiki's compatibility and FSR4 rows under <c>optiScalerWikiNames</c>), then
/// general text. RenoDX and Luma also link the HDR Gaming Database; ReLimiter and Display Commander show the change log
/// from the installed version (three entries, as upstream). Every remote source is optional: one that cannot be read
/// within its time limit is left out, never fails the dialog.
/// </summary>
public sealed partial class ComponentInfoCatalogue
{
    public const string ReLimiterChangelogUrl = "https://raw.githubusercontent.com/RankFTW/ReLimiter/main/CHANGELOG.md";
    public const string DisplayCommanderChangelogUrl = "https://raw.githubusercontent.com/pmnoxx/display-commander/main/CHANGELOG.md";
    public const string ReLimiterReleasesUrl = "https://github.com/RankFTW/ReLimiter/releases";
    public const string DisplayCommanderReleasesUrl = "https://github.com/pmnoxx/display-commander/releases";
    public const string NexusGraphQlUrl = "https://api.nexusmods.com/v2/graphql";
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);
    private const int MaxRemoteText = 4_000;

    private readonly HttpClient _http;
    private readonly UpstreamRhiManifestService? _manifest;
    private readonly RenoDxCatalogService? _renoDx;
    private readonly LumaCatalogService? _luma;
    private readonly OptiScalerWikiCatalogue? _optiScalerWiki;
    private readonly RenoDxUnrealDatabaseService? _unreal;
    private readonly RenoDxUnityDatabaseService? _unity;
    private readonly GameLinkCatalogue? _links;
    private readonly Dictionary<string, Task<string?>> _texts = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ComponentInfoCatalogue(
        HttpClient http,
        UpstreamRhiManifestService? manifest = null,
        RenoDxCatalogService? renoDx = null,
        LumaCatalogService? luma = null,
        OptiScalerWikiCatalogue? optiScalerWiki = null,
        RenoDxUnrealDatabaseService? unreal = null,
        RenoDxUnityDatabaseService? unity = null,
        GameLinkCatalogue? links = null)
    {
        _http = http;
        _manifest = manifest;
        _renoDx = renoDx;
        _luma = luma;
        _optiScalerWiki = optiScalerWiki;
        _unreal = unreal;
        _unity = unity;
        _links = links;
    }

    /// <summary>The components with an ⓘ dialog (upstream's Info buttons).</summary>
    public static bool HasInfo(ManagedComponentKind kind) => kind is ManagedComponentKind.ReShade or ManagedComponentKind.RenoDX
        or ManagedComponentKind.LumaFramework or ManagedComponentKind.OptiScaler or ManagedComponentKind.ReLimiter
        or ManagedComponentKind.DisplayCommander or ManagedComponentKind.REFramework;

    /// <summary>Works out the dialog's content. Never throws for a remote source; <paramref name="cancellationToken"/> stops waiting.</summary>
    public async Task<ComponentInfo> ResolveAsync(DetectedGame game, ManagedComponentKind kind, string? installedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var policy = _manifest is null ? null
            : await Optional<UpstreamLibraryPolicySnapshot>(async () => await _manifest.FetchLibraryPolicyAsync(cancellationToken).ConfigureAwait(false),
                "manifest", cancellationToken).ConfigureAwait(false);
        var info = kind switch
        {
            // Only RenoDX and Luma link the HDR Gaming Database (upstream LookupHdrUrl).
            ManagedComponentKind.RenoDX => await RenoDxAsync(game, _links?.HdrDatabasePageFor(game), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.LumaFramework => await LumaAsync(game, policy, _links?.HdrDatabasePageFor(game), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.OptiScaler => await OptiScalerAsync(game, policy, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ReLimiter or ManagedComponentKind.DisplayCommander
                => await ReleaseNotesAsync(game, kind, policy, installedVersion, cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.REFramework => ManifestOrGeneral(kind, ManifestNote(policy, ComponentNoteKeys.ReFramework, game)),
            _ => General(kind)
        };
        return info;
    }

    // ── RenoDX ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ComponentInfo> RenoDxAsync(DetectedGame game, string? hdr, CancellationToken cancellationToken)
    {
        var kind = ManagedComponentKind.RenoDX;
        var nativeHdr = game.NativeHdrByManifest;
        var dbComment = await DatabaseCommentAsync(game, cancellationToken).ConfigureAwait(false);
        var links = new List<ComponentInfoLink>();
        if (!string.IsNullOrWhiteSpace(game.ManifestGameNote))
        {
            AddNoteLink(links, game.ManifestGameNoteUrl, game.ManifestGameNoteUrlLabel);
            AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
            return new(kind, ComponentInfoSource.Manifest, [game.ManifestGameNote.Trim()], false, false, null, null, null, links);
        }

        RenoDxModEntry? listed = null;
        if (!nativeHdr && _renoDx is not null)
        {
            RenoDxModEntry? match = null;
            if (!_renoDx.TryGetResolvedMatch(game, out match))
                match = await Optional<RenoDxModEntry>(async () => await _renoDx.ResolveAsync(game, [], cancellationToken).ConfigureAwait(false),
                    "RenoDX catalogue", cancellationToken).ConfigureAwait(false);
            listed = await Optional<RenoDxModEntry>(async () => await _renoDx.FindListedAsync(game, cancellationToken).ConfigureAwait(false),
                "RenoDX catalogue", cancellationToken).ConfigureAwait(false);
            var mod = match is { IsGeneric: false } ? match : listed;
            var notes = mod?.Notes is { } text && !IsRhiOwnNote(text) ? text.Trim() : null;
            var page = GameLinkCatalogue.SafePage(mod?.DiscussionUrl);
            if (notes is not null || page is not null || game.WikiStatusOverride is { Length: > 0 })
            {
                AddLink(links, ComponentInfoLinkKind.WikiPage, page);
                AddPublisherLinks(links, listed);
                AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
                var texts = new List<string>();
                if (notes is not null) texts.Add(notes);
                if (dbComment is not null) texts.Add(dbComment);
                return new(kind, ComponentInfoSource.Wiki, texts, texts.Count == 0, false, null, null, null, links);
            }
        }

        // Upstream tier 2b: the Nexus Mods summary of a mod published only there.
        if (!nativeHdr && listed is { SnapshotUrl64: null, SnapshotUrl32: null, NexusUrl: { } nexus }
            && await NexusSummaryAsync(nexus, cancellationToken).ConfigureAwait(false) is { } summary)
        {
            AddPublisherLinks(links, listed);
            AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
            return new(kind, ComponentInfoSource.Wiki, dbComment is null ? [summary] : [summary, dbComment], false, false, null, null, null, links);
        }

        if (!nativeHdr) AddPublisherLinks(links, listed);
        AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
        var source = hdr is not null || nativeHdr ? ComponentInfoSource.Wiki : ComponentInfoSource.Fallback;
        return new(kind, source, dbComment is null ? [] : [dbComment], !nativeHdr, nativeHdr, null, null, null, links);
    }

    /// <summary>Notes the fork itself writes on catalogue entries (snapshot overrides, generic add-ons) are not game notes.</summary>
    internal static bool IsRhiOwnNote(string text)
        => text.StartsWith("Upstream RHI manifest snapshot override", StringComparison.Ordinal)
           || text.StartsWith("Generic RenoDX ", StringComparison.Ordinal);

    /// <summary>The UE-Extended or Unity database comment for the game (upstream's "generic notes"), else null.</summary>
    private async Task<string?> DatabaseCommentAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var unreal = _unreal is null ? null
            : await Optional<RenoDxUnrealDbEntry>(async () => await _unreal.FindAsync(game.Name).ConfigureAwait(false),
                "RenoDX UE-Extended database", cancellationToken).ConfigureAwait(false);
        if (Bounded(unreal?.Comments) is { } comment) return comment;
        var unity = _unity is null ? null
            : await Optional<RenoDxUnityDatabase>(async () => await _unity.GetAsync(cancellationToken).ConfigureAwait(false),
                "RenoDX Unity database", cancellationToken).ConfigureAwait(false);
        return Bounded(unity?.Find(game.Name)?.Comments);
    }

    private static void AddPublisherLinks(List<ComponentInfoLink> links, RenoDxModEntry? listed)
    {
        if (listed is null) return;
        AddLink(links, ComponentInfoLinkKind.NexusMods, listed.NexusUrl);
        if (listed.NexusUrl is null) AddLink(links, ComponentInfoLinkKind.Discord, listed.DiscordUrl);
    }

    /// <summary>
    /// Upstream <c>NexusUpdateService.FetchSummaryAsync</c>: Nexus Mods' public GraphQL <c>legacyModsByDomain</c> summary
    /// for a <c>nexusmods.com/&lt;game&gt;/mods/&lt;id&gt;</c> page (no account needed), under the Nexus API policy.
    /// </summary>
    private Task<string?> NexusSummaryAsync(string nexusUrl, CancellationToken cancellationToken)
    {
        var match = NexusModPage().Match(nexusUrl);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var modId)) return Task.FromResult<string?>(null);
        var domain = match.Groups[1].Value.ToLowerInvariant();
        return Cached("nexus:" + domain + "/" + modId, async ct =>
        {
            var query = JsonSerializer.Serialize(new
            {
                query = "query legacyModsByDomain($ids: [CompositeDomainWithIdInput!]!) { legacyModsByDomain(ids: $ids) { nodes { modId summary } } }",
                variables = new { ids = new[] { new { gameDomain = domain, modId } } }
            });
            var raw = await TrustedDownload.PostJsonAsync(_http, NexusGraphQlUrl, DownloadSources.NexusApi, query, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("legacyModsByDomain", out var mods) && mods.ValueKind == JsonValueKind.Object
                && mods.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                    if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("summary", out var summary)
                        && summary.ValueKind == JsonValueKind.String && Bounded(summary.GetString()) is { } text)
                        return text;
            return null;
        }, cancellationToken);
    }

    // ── Luma ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ComponentInfo> LumaAsync(DetectedGame game, UpstreamLibraryPolicySnapshot? policy, string? hdr, CancellationToken cancellationToken)
    {
        var kind = ManagedComponentKind.LumaFramework;
        var links = new List<ComponentInfoLink>();
        if (ManifestNote(policy, ComponentNoteKeys.Luma, game) is { } manifest)
        {
            AddNoteLink(links, manifest.Url, manifest.UrlLabel);
            AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
            return new(kind, ComponentInfoSource.Manifest, [manifest.Notes!.Trim()], false, false, null, null, null, links);
        }
        var texts = new List<string>();
        var entry = _luma is null ? null
            : await Optional<LumaCatalogEntry>(async () => await _luma.ResolveAsync(game, cancellationToken).ConfigureAwait(false),
                "Luma catalogue", cancellationToken).ConfigureAwait(false);
        if (Bounded(entry?.SpecialNotes) is { } special) texts.Add(special);
        if (Bounded(entry?.FeatureNotes) is { } feature) texts.Add(feature);
        if (Lookup(policy?.LumaGameNotes, game) is { Notes: { Length: > 0 } lumaNote } note && !string.IsNullOrWhiteSpace(lumaNote))
        {
            texts.Add(lumaNote.Trim());
            AddNoteLink(links, note.Url, note.UrlLabel);
        }
        AddLink(links, ComponentInfoLinkKind.HdrDatabase, hdr);
        if (texts.Count > 0) return new(kind, ComponentInfoSource.Wiki, texts, false, false, null, null, null, links);
        return new(kind, hdr is null ? ComponentInfoSource.Fallback : ComponentInfoSource.Wiki, [], true, false, null, null, null, links);
    }

    // ── OptiScaler ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ComponentInfo> OptiScalerAsync(DetectedGame game, UpstreamLibraryPolicySnapshot? policy, CancellationToken cancellationToken)
    {
        var kind = ManagedComponentKind.OptiScaler;
        if (ManifestNote(policy, ComponentNoteKeys.OptiScaler, game) is { } manifest) return ManifestOrGeneral(kind, manifest);
        if (_optiScalerWiki is not null)
        {
            await Optional<object>(async () => { await _optiScalerWiki.LoadAsync().ConfigureAwait(false); return null; }, "OptiScaler wiki", cancellationToken)
                .ConfigureAwait(false);
            var wikiName = OptiScalerWikiCatalogue.WikiNameFor(game.Name, Lookup(policy?.OptiScalerWikiNames, game));
            var (standard, fsr4) = _optiScalerWiki.Find(wikiName);
            if (standard is not null || fsr4 is not null)
            {
                var links = new List<ComponentInfoLink>();
                AddLink(links, ComponentInfoLinkKind.WikiPage, standard?.DetailPageUrl ?? fsr4?.DetailPageUrl);
                return new(kind, ComponentInfoSource.Wiki, [], false, false, standard, fsr4, null, links);
            }
        }
        return General(kind);
    }

    // ── ReLimiter / Display Commander ───────────────────────────────────────────────────────────────────────────

    private async Task<ComponentInfo> ReleaseNotesAsync(DetectedGame game, ManagedComponentKind kind, UpstreamLibraryPolicySnapshot? policy,
        string? installedVersion, CancellationToken cancellationToken)
    {
        var limiter = kind == ManagedComponentKind.ReLimiter;
        var links = new List<ComponentInfoLink>();
        if (ManifestNote(policy, limiter ? ComponentNoteKeys.ReLimiter : ComponentNoteKeys.DisplayCommander, game) is { } manifest)
        {
            AddNoteLink(links, manifest.Url, manifest.UrlLabel);
            AddLink(links, ComponentInfoLinkKind.Releases, limiter ? ReLimiterReleasesUrl : DisplayCommanderReleasesUrl);
            return new(kind, ComponentInfoSource.Manifest, [manifest.Notes!.Trim()], false, false, null, null, null, links);
        }
        var url = limiter ? ReLimiterChangelogUrl : DisplayCommanderChangelogUrl;
        var changelog = await Cached("changelog:" + url, async ct =>
            await TrustedDownload.GetStringAsync(_http, url, DownloadSources.ComponentNotes, cancellationToken: ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        AddLink(links, ComponentInfoLinkKind.Releases, limiter ? ReLimiterReleasesUrl : DisplayCommanderReleasesUrl);
        var entries = changelog is null ? null : ChangelogEntries(changelog, installedVersion, 3);
        // Upstream always highlights these two (release notes, or the description and the releases link).
        return new(kind, ComponentInfoSource.Wiki, [], entries is null, false, null, null, entries, links);
    }

    /// <summary>
    /// Upstream <c>ExtractDcChangelogEntries</c>: the <paramref name="count"/> <c>## &lt;version&gt;</c> sections starting at the
    /// installed version (matched on its first numbers), else the newest ones; null when the file has none. Bold markers
    /// are dropped (the dialog shows plain text); the result is capped.
    /// </summary>
    public static string? ChangelogEntries(string changelog, string? installedVersion, int count)
    {
        var sections = new List<(string Header, string Version, StringBuilder Body)>();
        foreach (var raw in changelog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var header = VersionHeader().Match(line);
            if (header.Success) sections.Add((line.TrimStart('#', ' ').Trim(), header.Groups[1].Value, new StringBuilder()));
            else if (sections.Count > 0) sections[^1].Body.AppendLine(line);
        }
        if (sections.Count == 0) return null;
        var start = 0;
        var wanted = installedVersion?.Trim().TrimStart('v', 'V');
        if (!string.IsNullOrEmpty(wanted))
        {
            var index = sections.FindIndex(s => s.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                                 || wanted.StartsWith(s.Version + ".", StringComparison.OrdinalIgnoreCase)
                                                 || s.Version.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase));
            if (index >= 0) start = index;
        }
        var text = new StringBuilder();
        foreach (var (header, _, body) in sections.Skip(start).Take(count))
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine(header);
            text.AppendLine(body.ToString().Replace("**", "", StringComparison.Ordinal).Trim());
        }
        var result = text.ToString().Trim();
        return result.Length <= 3 * MaxRemoteText ? result : result[..(3 * MaxRemoteText)].TrimEnd() + "…";
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static ComponentInfo General(ManagedComponentKind kind) => new(kind, ComponentInfoSource.Fallback, [], true, false, null, null, null, []);

    private static ComponentInfo ManifestOrGeneral(ManagedComponentKind kind, UpstreamGameNoteDefinition? note)
    {
        if (note is null) return General(kind);
        var links = new List<ComponentInfoLink>();
        AddNoteLink(links, note.Url, note.UrlLabel);
        return new(kind, ComponentInfoSource.Manifest, [note.Notes!.Trim()], false, false, null, null, null, links);
    }

    private static UpstreamGameNoteDefinition? ManifestNote(UpstreamLibraryPolicySnapshot? policy, string key, DetectedGame game)
        => policy?.ComponentGameNotes.TryGetValue(key, out var notes) == true && Lookup(notes, game) is { Notes: { } text } note
           && !string.IsNullOrWhiteSpace(text)
            ? note
            : null;

    /// <summary>The manifest's entry for the game: its canonical manifest name first, then its own name.</summary>
    private static T? Lookup<T>(IReadOnlyDictionary<string, T>? map, DetectedGame game) where T : class
    {
        if (map is null) return null;
        if (!string.IsNullOrWhiteSpace(game.CanonicalManifestName) && map.TryGetValue(game.CanonicalManifestName.Trim(), out var canonical)) return canonical;
        return !string.IsNullOrWhiteSpace(game.Name) && map.TryGetValue(game.Name.Trim(), out var named) ? named : null;
    }

    private static void AddNoteLink(List<ComponentInfoLink> links, string? url, string? label)
    {
        if (GameLinkCatalogue.SafePage(url) is { } safe)
            links.Add(new(ComponentInfoLinkKind.Notes, safe, string.IsNullOrWhiteSpace(label) ? null : label.Trim()));
    }

    private static void AddLink(List<ComponentInfoLink> links, ComponentInfoLinkKind kind, string? url)
    {
        if (GameLinkCatalogue.SafePage(url) is { } safe && links.All(l => l.Url != safe)) links.Add(new(kind, safe));
    }

    private static string? Bounded(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxRemoteText ? trimmed : trimmed[..MaxRemoteText].TrimEnd() + "…";
    }

    /// <summary>A remote text fetched at most once per session at a time; a failed fetch is forgotten so the next dialog asks again.</summary>
    private async Task<string?> Cached(string key, Func<CancellationToken, Task<string?>> fetch, CancellationToken cancellationToken)
    {
        Task<string?> task;
        lock (_sync)
        {
            if (!_texts.TryGetValue(key, out task!))
            {
                task = Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(SourceTimeout);
                    try { return await fetch(timeout.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UntrustedDownloadException
                                                   or OperationCanceledException or InvalidDataException)
                    {
                        AppLog.Warn("component-info", $"ⓘ source unavailable: {key}", ex);
                        lock (_sync) _texts.Remove(key);
                        return null;
                    }
                }, CancellationToken.None);
                _texts[key] = task;
            }
        }
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for an optional source within its time limit; a failure or timeout is logged and gives null.</summary>
    private static async Task<T?> Optional<T>(Func<Task<T?>> load, string what, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await load().WaitAsync(SourceTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("component-info", $"ⓘ source unavailable: {what}", ex);
            return null;
        }
    }

    [GeneratedRegex(@"^##\s+v?(\d[\w.\-]*)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex VersionHeader();

    [GeneratedRegex(@"^https://(?:www\.)?nexusmods\.com/([A-Za-z0-9_-]+)/mods/(\d+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex NexusModPage();
}
