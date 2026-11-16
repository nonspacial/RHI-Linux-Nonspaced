using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// One game of upstream RHI's <c>database/RenoDXdb-source.json</c> (upstream 2.8.8 <c>RenoDXDbSourceEntry</c>): every game
/// there uses RenoDX's generic Source engine add-on. The file's <c>Bitness</c> field is not read (upstream ignores it too).
/// </summary>
public sealed class RenoDxSourceDbEntry
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>"Done" or "WIP".</summary>
    [JsonPropertyName("Status")] public string? Status { get; set; }
    /// <summary>Game-specific notes from the RenoDX wiki.</summary>
    [JsonPropertyName("Comments")] public string? Comments { get; set; }
}

/// <summary>The Source engine database: one entry per name (the last duplicate wins, as upstream).</summary>
public sealed class RenoDxSourceDatabase
{
    private RenoDxSourceDatabase(IReadOnlyList<RenoDxSourceDbEntry> entries) => Entries = entries;

    public IReadOnlyList<RenoDxSourceDbEntry> Entries { get; }

    /// <summary>Parses the JSON array; null when it holds no named entries.</summary>
    public static RenoDxSourceDatabase? Parse(ReadOnlySpan<byte> json)
    {
        var list = JsonSerializer.Deserialize<List<RenoDxSourceDbEntry?>>(json) ?? [];
        var byName = new Dictionary<string, RenoDxSourceDbEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            {
                entry.Name = entry.Name.Trim();
                byName[entry.Name] = entry;
            }
        return byName.Count == 0 ? null : new RenoDxSourceDatabase(byName.Values.ToArray());
    }
}

/// <summary>
/// #257 (upstream 2.8.8 <c>698956d</c>, <c>RenoDXDbService.FetchSourceEntriesAsync</c>): RenoDX's Source engine games for
/// the Available mods list. Read as the Unity database is (#15): the live file on RankFTW/rhi-repo revalidated with its
/// ETag and kept 1 h, the last good copy under <c>~/.cache/rhi-linux/renodx/</c>, then the copy pinned at RHI 2.8.8
/// (rhi-repo <c>9a9cf39</c>, the file's state when upstream released 2.8.8). HTTPS on raw.githubusercontent.com only.
/// Installing the Source add-on is not part of this (upstream added it in 2.9.3).
/// </summary>
public sealed class RenoDxSourceDatabaseService
{
    public const string LiveUrl =
        "https://raw.githubusercontent.com/RankFTW/rhi-repo/main/database/RenoDXdb-source.json";
    public const string PinnedUrl =
        "https://raw.githubusercontent.com/RankFTW/rhi-repo/9a9cf39e403039cbee843bd39cb69c3020bc4d08/database/RenoDXdb-source.json";

    private readonly PinnedGitHubDatabaseFile<RenoDxSourceDatabase> _file;

    public RenoDxSourceDatabaseService(
        HttpClient http,
        string? cachePath = null,
        string? liveUrl = null,
        string? fallbackUrl = PinnedUrl,
        TimeSpan? freshness = null,
        TimeProvider? timeProvider = null)
        => _file = new("Source database", http, cachePath ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "renodx", "RenoDXdb-source.json"),
            liveUrl ?? LiveUrl, fallbackUrl, raw => RenoDxSourceDatabase.Parse(raw), freshness, timeProvider);

    /// <summary>The database, or null when no source is reachable (retried after a pause).</summary>
    public Task<RenoDxSourceDatabase?> GetAsync(CancellationToken cancellationToken = default) => _file.GetAsync(cancellationToken);
}
