namespace RHI.Platform.Linux.Deployment;

public enum HdrModAvailability
{
    None,
    Available,
    WorkInProgress
}

/// <summary>One row of the "Available HDR Mods" list (upstream 2.7.6 HdrModEntry).</summary>
public sealed record HdrModEntry(
    string Name,
    HdrModAvailability RenoDx,
    string? RenoDxUrl,
    HdrModAvailability Luma,
    string? LumaUrl)
{
    /// <summary>Upstream prefers the RenoDX link and falls back to the Luma link.</summary>
    public string? DownloadUrl => RenoDxUrl ?? LumaUrl;
}

public sealed record AvailableHdrModsSnapshot(
    IReadOnlyList<HdrModEntry> Entries,
    bool RenoDxUnavailable,
    bool LumaUnavailable);

/// <summary>
/// Catalogue-wide list of games with a RenoDX and/or Luma mod, built from the catalogues RHI
/// already uses for installs, plus the RHI Unity database (#126), the RHI UE-Extended database and the RenoDX mods the RHI
/// database lists without a download of RHI's own (published on Nexus Mods or Discord; #197). Like upstream it is not
/// limited to the user's library.
/// </summary>
public sealed class AvailableHdrModsCatalog(RenoDxCatalogService renoDx, LumaCatalogService luma, RenoDxUnityDatabaseService? unity = null,
    RenoDxUnrealDatabaseService? unreal = null)
{
    /// <summary>#197: the UE-Extended database the list reads (the app passes its one instance).</summary>
    internal RenoDxUnrealDatabaseService? UeExtendedDatabase => unreal;

    public async Task<AvailableHdrModsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        // #197: every named mod of the RHI database (also Nexus/Discord-only ones), as upstream's _allMods.
        var renoDxTask = renoDx.FetchListingAsync(cancellationToken);
        var lumaTask = luma.FetchAsync(cancellationToken);
        var unityTask = unity is null ? Task.FromResult<RenoDxUnityDatabase?>(null) : unity.GetAsync(cancellationToken);
        var unrealTask = unreal is null ? Task.FromResult<IReadOnlyList<RenoDxUnrealDbEntry>>([]) : unreal.GetAsync();

        var renoDxEntries = await LoadOrEmptyAsync(renoDxTask, "RenoDX catalogue", [], cancellationToken).ConfigureAwait(false);
        var lumaEntries = await LoadOrEmptyAsync(lumaTask, "Luma catalogue", [], cancellationToken).ConfigureAwait(false);
        var unityDatabase = await LoadOrEmptyAsync(unityTask, "RenoDX Unity database", null, cancellationToken).ConfigureAwait(false);
        var unrealEntries = await LoadOrEmptyAsync(unrealTask, "RenoDX UE-Extended database", [], cancellationToken).ConfigureAwait(false);

        return new AvailableHdrModsSnapshot(
            Merge(renoDxEntries, lumaEntries, unityDatabase?.Entries, unrealEntries),
            renoDxEntries.Count == 0,
            lumaEntries.Count == 0);
    }

    // A source that cannot be loaded leaves its column empty; the dialog says which one (the reason goes to the log).
    private static async Task<T> LoadOrEmptyAsync<T>(Task<T> load, string source, T empty, CancellationToken cancellationToken)
    {
        try { return await load.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RHI.Core.Services.AppLog.Warn("hdr-mods", $"Available HDR mods: the {source} could not be loaded", ex);
            return empty;
        }
    }

    /// <summary>
    /// Merges the catalogues by normalised game name (the identity both catalogues already use); the RenoDX spelling
    /// wins, as upstream keeps the RenoDX entry and adds Luma to it. A named RenoDX mod links its download, else its Nexus
    /// Mods page, else its Discord (upstream <c>SnapshotUrl ?? NexusUrl ?? DiscordUrl</c>; #197). Games of the RHI
    /// UE-Extended database (#197) and then of the Unity database that no named RenoDX mod covers join the RenoDX column
    /// (upstream 2.8.7 <c>GetAllHdrMods</c>, in that order: UE-Extended and the generic Unity add-on are RenoDX mods), ✓ or
    /// 🔨 by their <c>Status</c>, with no RenoDX link, as upstream; Luma then merges into them by name.
    /// </summary>
    public static IReadOnlyList<HdrModEntry> Merge(
        IEnumerable<RenoDxModEntry> renoDxEntries,
        IEnumerable<LumaCatalogEntry> lumaEntries,
        IEnumerable<RenoDxUnityDbEntry>? unityEntries = null,
        IEnumerable<RenoDxUnrealDbEntry>? unrealEntries = null)
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

        return merged.Values
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Upstream search: case-insensitive substring of the game name.</summary>
    public static bool Matches(HdrModEntry entry, string? query)
    {
        var text = query?.Trim();
        return string.IsNullOrEmpty(text) || entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Catalogue links are handed to the desktop opener, so only web links are kept.</summary>
    internal static string? SafeLink(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
}
