using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One game of upstream RHI's <c>database/RenoDXdb-unreal.json</c> (every game there uses UE-Extended).</summary>
public sealed class RenoDxUnrealDbEntry
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>"Done" or "WIP".</summary>
    [JsonPropertyName("Status")] public string? Status { get; set; }
    /// <summary>Game-specific notes upstream shows in the RenoDX ⓘ dialog.</summary>
    [JsonPropertyName("Comments")] public string? Comments { get; set; }
}

/// <summary>
/// #184/#197: upstream RHI's UE-Extended database (<c>RenoDXdb-unreal.json</c>, RankFTW/rhi-repo, as upstream 2.8.7
/// <c>RenoDXDbService</c> reads it) for the RenoDX ⓘ dialog's notes and the Available HDR Mods list. Read under
/// <see cref="DownloadSources.ComponentNotes"/>, kept 1 h in RHI's cache folder (upstream revalidates every session),
/// the last good copy used when it cannot be fetched. Only names, status and comments are read: the UE-Extended
/// install settings (<c>Method</c>, <c>Upgrades</c>) stay with the manifest's UE-Extended lists the installer uses.
/// </summary>
public sealed class RenoDxUnrealDatabaseService
{
    public const string LiveUrl = "https://raw.githubusercontent.com/RankFTW/rhi-repo/main/database/RenoDXdb-unreal.json";
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(1);

    private readonly CachedRemoteList<RenoDxUnrealDbEntry> _list;

    public RenoDxUnrealDatabaseService(HttpClient http, string? cachePath = null, TimeSpan? freshness = null, TimeProvider? timeProvider = null)
        => _list = new("RenoDX UE-Extended database", LiveUrl, DownloadSources.ComponentNotes, Parse, entry => entry.Name,
            entry => !string.IsNullOrWhiteSpace(entry.Name),
            cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "renodx", "RenoDXdb-unreal.json"),
            freshness ?? DefaultFreshness, timeProvider ?? TimeProvider.System, http, Key);

    /// <summary>Full Refresh: the database downloaded again (the loaded copy stays in use until then).</summary>
    public Task RefreshAsync() => _list.RefreshAsync();

    /// <summary>Every game in the database once loaded (empty until then or when nothing could be read).</summary>
    public async Task<IReadOnlyList<RenoDxUnrealDbEntry>> GetAsync()
    {
        await _list.LoadAsync().ConfigureAwait(false);
        return _list.Entries;
    }

    /// <summary>Upstream's lookup: exact name ignoring case, then the name without ™/®/©.</summary>
    public async Task<RenoDxUnrealDbEntry?> FindAsync(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;
        await _list.LoadAsync().ConfigureAwait(false);
        return _list.Find(Key(gameName)) ?? _list.Find(Key(StripTrademarks(gameName)));
    }

    public static IReadOnlyList<RenoDxUnrealDbEntry> Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<RenoDxUnrealDbEntry?>>(json) ?? [];
        // Upstream keys the file by name and the last duplicate wins.
        var byName = new Dictionary<string, RenoDxUnrealDbEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            {
                entry.Name = entry.Name.Trim();
                byName[entry.Name] = entry;
            }
        return byName.Values.ToArray();
    }

    private static string Key(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static string StripTrademarks(string name)
        => name.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal).Trim();
}
