using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;

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
/// <para>
/// Threat model: this protects against accidental or foreign pollution of the cache (files left by
/// another tool, an older RHI build, a test run, a truncated download). It is not tamper-proof: the
/// stamp is unkeyed and certifies itself, so a process running as the same user that writes both a
/// payload and a matching stamp is accepted. Defending against a same-user attacker would need a
/// secret key, which RHI does not have.
/// </para>
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
        => TryValidate(entry, sourceUrl, version, publishedSha256, out _);

    /// <summary>
    /// <see cref="IsValid"/>, also handing back the content SHA-256 the stamp recorded (and that was just
    /// re-verified), so callers can bind dependent entries to it without hashing the entry again.
    /// </summary>
    public static bool TryValidate(string entry, string sourceUrl, string? version, string? publishedSha256, out string contentSha256)
    {
        contentSha256 = "";
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

            if (!string.Equals(ContentSha256(entry, isDirectory), stamp.ContentSha256, StringComparison.OrdinalIgnoreCase)) return false;
            contentSha256 = stamp.ContentSha256;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validation for a derived entry (an extracted tree) bound to the source it came from: the stamp must
    /// have recorded exactly <paramref name="boundSha256"/> as its published digest. Unlike
    /// <see cref="IsValid"/>, where a blank digest means "the publisher announced none" and is skipped, a
    /// blank binding here is rejected: a derived entry with nothing to bind to is never reused.
    /// </summary>
    public static bool IsValidBoundTo(string entry, string sourceUrl, string? version, string? boundSha256)
        => !string.IsNullOrWhiteSpace(boundSha256) && IsValid(entry, sourceUrl, version, boundSha256);

    /// <summary><see cref="Write"/> for a derived entry; refuses to stamp it without the source hash it is bound to.</summary>
    public static string WriteBoundTo(string entry, string sourceUrl, string? version, string? boundSha256)
    {
        if (string.IsNullOrWhiteSpace(boundSha256))
            throw new ArgumentException("A derived cache entry must be bound to its source's SHA-256.", nameof(boundSha256));
        return Write(entry, sourceUrl, version, boundSha256);
    }

    /// <summary>Records provenance for an entry RHI just downloaded (and validated) into the cache.</summary>
    /// <returns>The content SHA-256 recorded in the stamp (tree hash for a directory).</returns>
    public static string Write(string entry, string sourceUrl, string? version = null, string? publishedSha256 = null)
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

        // Durable and verified (temp + fsync + rename + directory fsync), so a power loss never
        // leaves a truncated stamp beside a valid entry.
        AtomicFile.WriteAllText(StampPath(entry), JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return document.ContentSha256;
    }

    /// <summary>Drops the stamp so the entry is treated as unverified (call before replacing the entry).</summary>
    public static void Invalidate(string entry) => FileCleanup.TryDelete(StampPath(entry));

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
        foreach (var info in SafeFileEnumeration.Entries(entry)
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
