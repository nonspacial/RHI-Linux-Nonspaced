using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RHI.Core.Services;

/// <summary>
/// Provenance stamp written next to a downloaded cache entry (file or extracted directory) at download
/// time: the source URL, the release version, the digest the publisher announced (if any) and the
/// SHA-256 of what RHI actually stored. A cache entry is reused only when its stamp exists, names the
/// same source and version, and the content still hashes to the recorded value — so a file that got
/// into the cache any other way (a foreign or truncated file, an older RHI build, a test run) is
/// re-downloaded and never deployed. The stamp lives beside the entry (<c>entry + ".rhi-stamp.json"</c>)
/// and is written after the entry is in place; a crash in between leaves an unstamped entry, which is
/// simply downloaded again.
/// </summary>
public static class CacheStamp
{
    public const string Suffix = ".rhi-stamp.json";
    private const int FormatVersion = 1;

    public static string StampPath(string entry)
        => Path.TrimEndingDirectorySeparator(entry) + Suffix;

    /// <summary>True when <paramref name="entry"/> exists, is stamped for this source/version and still matches its recorded hash.</summary>
    /// <param name="publishedSha256">When the publisher currently announces a digest, the stamp must have recorded the same one.</param>
    public static bool IsValid(string entry, string sourceUrl, string? version = null, string? publishedSha256 = null)
    {
        try
        {
            var stampPath = StampPath(entry);
            if (!File.Exists(stampPath)) return false;
            var isDirectory = Directory.Exists(entry);
            if (!isDirectory && !File.Exists(entry)) return false;

            var stamp = JsonSerializer.Deserialize<Document>(File.ReadAllText(stampPath));
            if (stamp is null || stamp.Format != FormatVersion) return false;
            if (stamp.IsDirectory != isDirectory) return false;
            if (!string.Equals(stamp.SourceUrl, sourceUrl, StringComparison.Ordinal)) return false;
            if (version is not null && !string.Equals(stamp.Version, version, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrWhiteSpace(publishedSha256) &&
                !string.Equals(stamp.PublishedSha256, publishedSha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(stamp.ContentSha256)) return false;
            if (!isDirectory && new FileInfo(entry).Length != stamp.Size) return false;

            return string.Equals(ContentSha256(entry, isDirectory), stamp.ContentSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records provenance for an entry RHI just downloaded (and validated) into the cache.</summary>
    public static void Write(string entry, string sourceUrl, string? version = null, string? publishedSha256 = null)
    {
        var isDirectory = Directory.Exists(entry);
        if (!isDirectory && !File.Exists(entry))
            throw new FileNotFoundException("Cannot stamp a cache entry that does not exist.", entry);

        var document = new Document
        {
            Format = FormatVersion,
            SourceUrl = sourceUrl,
            Version = version,
            PublishedSha256 = string.IsNullOrWhiteSpace(publishedSha256) ? null : publishedSha256,
            IsDirectory = isDirectory,
            Size = isDirectory ? 0 : new FileInfo(entry).Length,
            ContentSha256 = ContentSha256(entry, isDirectory),
            WrittenAtUtc = DateTime.UtcNow
        };

        var stampPath = StampPath(entry);
        var temp = stampPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, stampPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>Drops the stamp so the entry is treated as unverified (call before replacing the entry).</summary>
    public static void Invalidate(string entry)
    {
        try
        {
            var stampPath = StampPath(entry);
            if (File.Exists(stampPath)) File.Delete(stampPath);
        }
        catch { }
    }

    private static string ContentSha256(string entry, bool isDirectory)
    {
        if (!isDirectory)
        {
            using var stream = File.OpenRead(entry);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        // Tree hash: every regular file's relative path and content hash, in ordinal path order.
        // Symlinks are refused rather than followed so a stamped directory cannot point elsewhere.
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = new DirectoryInfo(entry);
        foreach (var info in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                     .OrderBy(info => Path.GetRelativePath(entry, info.FullName), StringComparer.Ordinal))
        {
            if (info.LinkTarget is not null)
                throw new InvalidDataException($"Cache directory contains a symbolic link: {info.FullName}");
            if (info is not FileInfo file) continue;
            var relative = Path.GetRelativePath(entry, file.FullName).Replace('\\', '/');
            using var stream = file.OpenRead();
            tree.AppendData(Encoding.UTF8.GetBytes(relative));
            tree.AppendData([0]);
            tree.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(tree.GetHashAndReset());
    }

    private sealed class Document
    {
        public int Format { get; set; }
        public string SourceUrl { get; set; } = "";
        public string? Version { get; set; }
        public string? PublishedSha256 { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string ContentSha256 { get; set; } = "";
        public DateTime WrittenAtUtc { get; set; }
    }
}
