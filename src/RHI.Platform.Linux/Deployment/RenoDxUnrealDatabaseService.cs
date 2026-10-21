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

    /// <summary>
    /// #218 (upstream <c>RenoDXDbUnrealEntry.Method</c>): how UE-Extended is set up for the game — "native" (HDR path, no
    /// Engine.ini HDR keys), "ini" (HDR path and Engine.ini HDR keys), "upgrade" (SDR path plus one forced upgrade key from
    /// <see cref="Upgrades"/>), none (SDR path).
    /// </summary>
    [JsonPropertyName("Method")] public string? Method { get; set; }

    /// <summary>#218: for "upgrade", the format and size, e.g. "`B8G8R8A8_TYPELESS` `Output Size`".</summary>
    [JsonPropertyName("Upgrades")] public string? Upgrades { get; set; }

    /// <summary>#218: <c>Set_Path=1</c> (the SDR upgrade path): no method, or "upgrade".</summary>
    [JsonIgnore] public bool UsesSdrPath => Method is null || string.Equals(Method, "upgrade", StringComparison.OrdinalIgnoreCase);

    /// <summary>#218: the Engine.ini HDR keys go in ("ini").</summary>
    [JsonIgnore] public bool DeployEngineIniHdr => string.Equals(Method, "ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>#218: the <c>[renodx]</c> key "upgrade" forces (<c>Upgrade_</c> + the first quoted format), else null.</summary>
    [JsonIgnore]
    public string? UpgradeIniKey
        => string.Equals(Method, "upgrade", StringComparison.OrdinalIgnoreCase) && QuotedTokens(Upgrades) is [var format, ..]
            ? "Upgrade_" + format.Replace(' ', '_')
            : null;

    /// <summary>#218: the value of <see cref="UpgradeIniKey"/>: "Output size" 1, "Output ratio" 2, "Any size" 3, else null.</summary>
    [JsonIgnore]
    public string? UpgradeIniValue
        => string.Equals(Method, "upgrade", StringComparison.OrdinalIgnoreCase) && QuotedTokens(Upgrades) is [_, var size, ..]
            ? size.ToLowerInvariant() switch { "output size" => "1", "output ratio" => "2", "any size" => "3", _ => null }
            : null;

    private static List<string> QuotedTokens(string? input)
    {
        var tokens = new List<string>();
        for (var i = 0; input is not null && i < input.Length;)
        {
            var start = input.IndexOf('`', i);
            if (start < 0) break;
            var end = input.IndexOf('`', start + 1);
            if (end < 0) break;
            var token = input[(start + 1)..end].Trim();
            if (token.Length > 0) tokens.Add(token);
            i = end + 1;
        }
        return tokens;
    }
}

/// <summary>
/// #184/#197: upstream RHI's UE-Extended database (<c>RenoDXdb-unreal.json</c>, RankFTW/rhi-repo, as upstream 2.8.7
/// <c>RenoDXDbService</c> reads it) for the RenoDX ⓘ dialog's notes and the Available HDR Mods list. Read under
/// <see cref="DownloadSources.ComponentNotes"/>, kept 1 h in RHI's cache folder (upstream revalidates every session),
/// the last good copy used when it cannot be fetched. Names, status and comments for the ⓘ dialog; since #218 also the
/// UE-Extended install settings (<c>Method</c>, <c>Upgrades</c>) RenoDX's install reads (upstream's priority: this entry
/// over the manifest's <c>ueExtendedCompatibility</c>).
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
