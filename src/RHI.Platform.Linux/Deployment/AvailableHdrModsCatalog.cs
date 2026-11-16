using RHI.Platform.Linux.Network;

namespace RHI.Platform.Linux.Deployment;

public enum HdrModAvailability
{
    None,
    Available,
    WorkInProgress
}

/// <summary>Which mod a search filters by (#256, upstream 2.8.8: a leading "renodx", "luma" or "ultra+").</summary>
public enum AvailableModKind
{
    RenoDx,
    Luma,
    UltraPlus
}

/// <summary>
/// One row of the "Available mods" list (upstream 2.8.8 <c>HdrModEntry</c>): a game with a RenoDX mod (named, UE-Extended,
/// Unity or Source), a Luma mod, an Ultra+ mod, or any combination.
/// </summary>
public sealed record HdrModEntry(
    string Name,
    HdrModAvailability RenoDx,
    string? RenoDxUrl,
    HdrModAvailability Luma,
    string? LumaUrl,
    string? UltraPlusUrl = null)
{
    /// <summary>
    /// The page RenoDX's tick opens (#256, upstream 2.8.8): the mod's own link, else — for a game covered only by a generic
    /// add-on (UE-Extended, Unity, Source) — the RenoDX wiki's mod list; null when the game has no RenoDX mod.
    /// </summary>
    public string? RenoDxLink => RenoDx == HdrModAvailability.None ? null : RenoDxUrl ?? AvailableHdrModsCatalog.RenoDxWikiModsUrl;

    public bool HasRenoDx => RenoDx != HdrModAvailability.None;
    public bool HasLuma => Luma != HdrModAvailability.None;
    public bool HasUltraPlus => UltraPlusUrl is not null;
}

public sealed record AvailableHdrModsSnapshot(
    IReadOnlyList<HdrModEntry> Entries,
    bool RenoDxUnavailable,
    bool LumaUnavailable,
    bool UltraPlusUnavailable = false,
    bool SourceUnavailable = false)
{
    public int RenoDxCount => Entries.Count(entry => entry.HasRenoDx);
    public int LumaCount => Entries.Count(entry => entry.HasLuma);
    public int UltraPlusCount => Entries.Count(entry => entry.HasUltraPlus);
}

/// <summary>
/// #256 "Available mods" (upstream 2.8.8; "Available HDR mods" before): every game with a RenoDX, Luma or Ultra+ mod, built
/// from the catalogues RHI already uses for installs, plus the RHI Unity database (#126), the RHI UE-Extended database, the
/// RenoDX mods the RHI database lists without a download of RHI's own (published on Nexus Mods or Discord; #197), the
/// RenoDX Source engine database (#257) and the Ultra+ games list the card links read (#183). Like upstream it is not
/// limited to the user's library.
/// </summary>
public sealed class AvailableHdrModsCatalog(RenoDxCatalogService renoDx, LumaCatalogService luma, RenoDxUnityDatabaseService? unity = null,
    RenoDxUnrealDatabaseService? unreal = null, RenoDxSourceDatabaseService? source = null,
    Func<CancellationToken, Task<IReadOnlyList<GameLinkEntry>?>>? ultraPlus = null)
{
    /// <summary>Upstream 2.8.8: where RenoDX's tick goes for a game covered only by a generic add-on.</summary>
    public const string RenoDxWikiModsUrl = "https://github.com/clshortfuse/renodx/wiki/Mods";

    /// <summary>#197: the UE-Extended database the list reads (the app passes its one instance).</summary>
    internal RenoDxUnrealDatabaseService? UeExtendedDatabase => unreal;

    public async Task<AvailableHdrModsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        // #197: every named mod of the RHI database (also Nexus/Discord-only ones), as upstream's _allMods.
        var renoDxTask = renoDx.FetchListingAsync(cancellationToken);
        var lumaTask = luma.FetchAsync(cancellationToken);
        var unityTask = unity is null ? Task.FromResult<RenoDxUnityDatabase?>(null) : unity.GetAsync(cancellationToken);
        var unrealTask = unreal is null ? Task.FromResult<IReadOnlyList<RenoDxUnrealDbEntry>>([]) : unreal.GetAsync();
        var sourceTask = source is null ? Task.FromResult<RenoDxSourceDatabase?>(null) : source.GetAsync(cancellationToken);
        var ultraPlusTask = ultraPlus is null ? Task.FromResult<IReadOnlyList<GameLinkEntry>?>([]) : ultraPlus(cancellationToken);

        var renoDxEntries = await LoadOrEmptyAsync(renoDxTask, "RenoDX catalogue", [], cancellationToken).ConfigureAwait(false);
        var lumaEntries = await LoadOrEmptyAsync(lumaTask, "Luma catalogue", [], cancellationToken).ConfigureAwait(false);
        var unityDatabase = await LoadOrEmptyAsync(unityTask, "RenoDX Unity database", null, cancellationToken).ConfigureAwait(false);
        var unrealEntries = await LoadOrEmptyAsync(unrealTask, "RenoDX UE-Extended database", [], cancellationToken).ConfigureAwait(false);
        var sourceDatabase = await LoadOrEmptyAsync(sourceTask, "RenoDX Source database", null, cancellationToken).ConfigureAwait(false);
        var ultraPlusGames = await LoadOrEmptyAsync(ultraPlusTask, "Ultra+ games list", null, cancellationToken).ConfigureAwait(false);

        return new AvailableHdrModsSnapshot(
            Merge(renoDxEntries, lumaEntries, unityDatabase?.Entries, unrealEntries, sourceDatabase?.Entries, ultraPlusGames),
            renoDxEntries.Count == 0,
            lumaEntries.Count == 0,
            ultraPlus is not null && ultraPlusGames is not { Count: > 0 },
            source is not null && sourceDatabase is null);
    }

    // A source that cannot be loaded leaves its column (or rows) empty; the dialog says which one (the reason goes to the log).
    private static async Task<T> LoadOrEmptyAsync<T>(Task<T> load, string source, T empty, CancellationToken cancellationToken)
    {
        try { return await load.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RHI.Core.Services.AppLog.Warn("hdr-mods", $"Available mods: the {source} could not be loaded", ex);
            return empty;
        }
    }

    /// <summary>
    /// Merges the catalogues by normalised game name (the identity both catalogues already use); the RenoDX spelling
    /// wins, as upstream keeps the RenoDX entry and adds Luma to it. A named RenoDX mod links its download, else its Nexus
    /// Mods page, else its Discord (upstream <c>SnapshotUrl ?? NexusUrl ?? DiscordUrl</c>; #197). Games of the RHI
    /// UE-Extended database (#197), then of the Unity database, then of the Source database (#257) that no named RenoDX mod
    /// covers join the RenoDX column (upstream 2.8.8 <c>GetAllHdrMods</c>, in that order: UE-Extended and the generic Unity
    /// and Source add-ons are RenoDX mods), ✓ or 🔨 by their <c>Status</c>, with no link of their own (the tick opens the
    /// RenoDX wiki's mod list); Luma then merges into them by name, then Ultra+ (#256): a game on the Ultra+ list gets its
    /// page in the Ultra+ column, and an Ultra+-only game gets a row of its own.
    /// </summary>
    public static IReadOnlyList<HdrModEntry> Merge(
        IEnumerable<RenoDxModEntry> renoDxEntries,
        IEnumerable<LumaCatalogEntry> lumaEntries,
        IEnumerable<RenoDxUnityDbEntry>? unityEntries = null,
        IEnumerable<RenoDxUnrealDbEntry>? unrealEntries = null,
        IEnumerable<RenoDxSourceDbEntry>? sourceEntries = null,
        IEnumerable<GameLinkEntry>? ultraPlusGames = null)
    {
        var merged = new Dictionary<string, HdrModEntry>(StringComparer.Ordinal);
        foreach (var mod in renoDxEntries)
        {
            if (mod.IsGeneric || string.IsNullOrWhiteSpace(mod.Name)) continue;
            var key = RenoDxCatalogService.NormalizeName(mod.Name);
            if (key.Length == 0 || merged.ContainsKey(key)) continue;
            merged[key] = new HdrModEntry(
                mod.Name.Trim(),
                mod.IsWorkInProgress ? HdrModAvailability.WorkInProgress : HdrModAvailability.Available,
                SafeLink(RenoDxCatalogService.ResolveAddonUrl(mod.SnapshotUrl64 ?? mod.SnapshotUrl32)) ?? SafeLink(mod.NexusUrl) ?? SafeLink(mod.DiscordUrl),
                HdrModAvailability.None,
                null);
        }

        // #197: UE-Extended database games, before the Unity ones (upstream's order).
        foreach (var game in unrealEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            var key = RenoDxCatalogService.NormalizeName(game.Name);
            if (key.Length == 0 || merged.ContainsKey(key)) continue; // a named mod already covers it
            var status = string.Equals(game.Status?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase)
                ? HdrModAvailability.WorkInProgress
                : HdrModAvailability.Available;
            merged[key] = new HdrModEntry(game.Name.Trim(), status, null, HdrModAvailability.None, null);
        }

        foreach (var game in unityEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            var key = RenoDxCatalogService.NormalizeName(game.Name);
            if (key.Length == 0 || merged.ContainsKey(key)) continue; // a named mod already covers it
            var status = string.Equals(game.Status?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase)
                ? HdrModAvailability.WorkInProgress
                : HdrModAvailability.Available;
            merged[key] = new HdrModEntry(game.Name.Trim(), status, null, HdrModAvailability.None, null);
        }

        // #257: Source engine database games, after the Unity ones (upstream's order).
        foreach (var game in sourceEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            var key = RenoDxCatalogService.NormalizeName(game.Name);
            if (key.Length == 0 || merged.ContainsKey(key)) continue; // a named mod already covers it
            merged[key] = new HdrModEntry(game.Name.Trim(), StatusOf(game.Status), null, HdrModAvailability.None, null);
        }

        foreach (var mod in lumaEntries)
        {
            if (string.IsNullOrWhiteSpace(mod.Name)) continue;
            var key = LumaCatalogService.NormalizeName(mod.Name);
            if (key.Length == 0) continue;
            var status = mod.Status.Contains("🚧", StringComparison.Ordinal) ? HdrModAvailability.WorkInProgress : HdrModAvailability.Available;
            var url = SafeLink(mod.DownloadUrl);
            merged[key] = merged.TryGetValue(key, out var existing)
                ? existing.Luma == HdrModAvailability.None ? existing with { Luma = status, LumaUrl = url } : existing
                : new HdrModEntry(mod.Name.Trim(), HdrModAvailability.None, null, status, url);
        }

        // #256: Ultra+ (upstream 2.8.8 matches by normalised name, as RHI joins the other columns). Only pages on the Ultra+
        // site are kept; the first entry for a name wins.
        foreach (var game in ultraPlusGames ?? [])
        {
            if (string.IsNullOrWhiteSpace(game.Name)) continue;
            var url = GameLinkCatalogue.SafePage(game.Url, LinkHosts.UltraPlus);
            var key = RenoDxCatalogService.NormalizeName(game.Name);
            if (url is null || key.Length == 0) continue;
            merged[key] = merged.TryGetValue(key, out var existing)
                ? existing.UltraPlusUrl is null ? existing with { UltraPlusUrl = url } : existing
                : new HdrModEntry(game.Name.Trim(), HdrModAvailability.None, null, HdrModAvailability.None, null, url);
        }

        return merged.Values
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HdrModAvailability StatusOf(string? status)
        => string.Equals(status?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase) ? HdrModAvailability.WorkInProgress : HdrModAvailability.Available;

    /// <summary>
    /// #256 (upstream 2.8.8 <c>ApplyFilter</c>): a leading word <c>renodx</c>, <c>luma</c> or <c>ultra+</c> (also
    /// <c>ultra</c> and <c>ultraplus</c>), any case, keeps only the games with that mod, and the rest of the text narrows
    /// by name ("luma dead"). Any other text is a plain name search.
    /// </summary>
    public static (AvailableModKind? Mod, string Name) ParseFilter(string? query)
    {
        var text = query?.Trim() ?? "";
        var firstWord = text.Split(' ', 2)[0];
        AvailableModKind? mod = firstWord.ToLowerInvariant() switch
        {
            "renodx" => AvailableModKind.RenoDx,
            "luma" => AvailableModKind.Luma,
            "ultra+" or "ultra" or "ultraplus" => AvailableModKind.UltraPlus,
            _ => null
        };
        return mod is null ? (null, text) : (mod, text[firstWord.Length..].Trim());
    }

    /// <summary>Upstream search: the mod filter (<see cref="ParseFilter"/>), then a case-insensitive substring of the name.</summary>
    public static bool Matches(HdrModEntry entry, string? query)
    {
        var (mod, name) = ParseFilter(query);
        var hasMod = mod switch
        {
            AvailableModKind.RenoDx => entry.HasRenoDx,
            AvailableModKind.Luma => entry.HasLuma,
            AvailableModKind.UltraPlus => entry.HasUltraPlus,
            _ => true
        };
        return hasMod && (name.Length == 0 || entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Catalogue links are handed to the desktop opener, so only web links are kept.</summary>
    internal static string? SafeLink(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
}
