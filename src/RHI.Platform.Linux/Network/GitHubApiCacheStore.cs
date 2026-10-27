using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Network;

/// <summary>
/// #244 (upstream 2.8.8 <c>feb126c</c>, <c>github_etag_cache.json</c>): the GitHub API answers
/// <see cref="GitHubConditionalCacheHandler"/> keeps, saved between launches so the first lookups of a start are
/// conditional too. One JSON file under the cache folder (<see cref="JsonStoreKind.Cache"/>: an unreadable file is copied
/// aside and rebuilt). Each entry is the request URL and Accept value, the validators GitHub sent, the body and the time
/// GitHub last confirmed it; never a request header, so never a token. Entries GitHub has not confirmed for 30 days, and
/// entries that do not read as a valid answer, are dropped on load.
/// </summary>
public sealed class GitHubApiCacheStore
{
    public const string FileName = "github-api-cache.json";

    /// <summary>Entries GitHub has not confirmed (200 or 304) for this long are dropped on load (upstream <c>MaxAge</c>).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>A saved time further ahead than this (a clock set back since) is not trusted.</summary>
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromDays(1);

    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly JsonFileStore<GitHubApiCacheFile> _store;

    /// <param name="path">The file (default <c>~/.cache/rhi-linux/github-api-cache.json</c>).</param>
    public GitHubApiCacheStore(string? path = null)
        => _store = new JsonFileStore<GitHubApiCacheFile>(path ?? DefaultPath, Options, JsonStoreKind.Cache, parse: Parse);

    public static string DefaultPath => System.IO.Path.Combine(LinuxPaths.CacheHome, "rhi-linux", FileName);

    public string FilePath => _store.FilePath;

    /// <summary>The usable saved entries (newest confirmation first, at most <see cref="GitHubConditionalCacheHandler.MaxEntries"/>).</summary>
    internal IReadOnlyList<GitHubApiCacheEntry> Load(DateTimeOffset now)
    {
        var file = _store.Load().Value;
        if (file is null) return [];
        return file.Entries
            .Where(entry => entry is not null && IsUsable(entry, now))
            .OrderByDescending(entry => entry.SavedUtc)
            .Take(GitHubConditionalCacheHandler.MaxEntries)
            .ToList();
    }

    /// <summary>Replaces the file with <paramref name="entries"/> (atomic; an unreadable file is copied aside first).</summary>
    internal void Save(IReadOnlyList<GitHubApiCacheEntry> entries)
        => _store.Save(new GitHubApiCacheFile { Version = SchemaVersion, Entries = [.. entries] });

    private static GitHubApiCacheFile? Parse(string json)
    {
        var file = JsonSerializer.Deserialize<GitHubApiCacheFile>(json, Options);
        // A newer layout this build does not know is unreadable: copied aside and rebuilt (a cache loses nothing by it).
        if (file is null || file.Version != SchemaVersion) return null;
        file.Entries ??= [];
        return file;
    }

    private static bool IsUsable(GitHubApiCacheEntry entry, DateTimeOffset now)
    {
        if (now - entry.SavedUtc > MaxAge || entry.SavedUtc - now > MaxClockSkew) return false;
        if (entry.Body is null || entry.Accept is null) return false;
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || !GitHubConditionalCacheHandler.IsApiHost(uri)) return false;
        if (entry.ETag is null && entry.LastModified is null) return false;
        if (entry.ETag is not null && !EntityTagHeaderValue.TryParse(entry.ETag, out _)) return false;
        if (entry.ContentType is not null && !MediaTypeHeaderValue.TryParse(entry.ContentType, out _)) return false;
        return System.Text.Encoding.UTF8.GetByteCount(entry.Body) <= GitHubConditionalCacheHandler.MaxCachedBodyBytes;
    }
}

/// <summary>The saved file: a schema version and the entries.</summary>
public sealed class GitHubApiCacheFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("entries")] public List<GitHubApiCacheEntry> Entries { get; set; } = [];
}

/// <summary>One saved GitHub API answer.</summary>
public sealed class GitHubApiCacheEntry
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>The request's Accept value (GitHub varies the body by media type); part of the key.</summary>
    [JsonPropertyName("accept")] public string? Accept { get; set; }
    [JsonPropertyName("etag")] public string? ETag { get; set; }
    [JsonPropertyName("lastModified")] public DateTimeOffset? LastModified { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    /// <summary>When GitHub last confirmed this body (a 200 that brought it, or a 304).</summary>
    [JsonPropertyName("savedUtc")] public DateTimeOffset SavedUtc { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
}
