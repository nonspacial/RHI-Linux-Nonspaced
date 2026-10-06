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
/// already uses for installs. Like upstream it is not limited to the user's library.
/// </summary>
public sealed class AvailableHdrModsCatalog(RenoDxCatalogService renoDx, LumaCatalogService luma)
{
    public async Task<AvailableHdrModsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var renoDxTask = renoDx.FetchAsync(cancellationToken);
        var lumaTask = luma.FetchAsync(cancellationToken);

        IReadOnlyList<RenoDxModEntry> renoDxEntries;
        try { renoDxEntries = await renoDxTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { renoDxEntries = []; }

        IReadOnlyList<LumaCatalogEntry> lumaEntries;
        try { lumaEntries = await lumaTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { lumaEntries = []; }

        return new AvailableHdrModsSnapshot(
            Merge(renoDxEntries, lumaEntries),
            renoDxEntries.Count == 0,
            lumaEntries.Count == 0);
    }

    /// <summary>
    /// Merges both catalogues by normalised game name (the identity both catalogues already use);
    /// the RenoDX spelling wins, as upstream keeps the RenoDX entry and adds Luma to it.
    /// </summary>
    public static IReadOnlyList<HdrModEntry> Merge(
        IEnumerable<RenoDxModEntry> renoDxEntries,
        IEnumerable<LumaCatalogEntry> lumaEntries)
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
                SafeLink(RenoDxCatalogService.ResolveAddonUrl(mod.SnapshotUrl64 ?? mod.SnapshotUrl32)),
                HdrModAvailability.None,
                null);
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
