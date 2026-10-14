using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace RHI.Core.Services;

/// <summary>
/// Bounded streaming search for identification strings in a binary (DEP-15 / #34): the file is read once
/// through one pooled 1 MiB window (a tail of the previous window is kept so a string split across reads
/// is still found), every needle is matched as ASCII and as UTF-16LE text ignoring ASCII case, and the
/// scan stops as soon as the caller has what it needs. Nothing proportional to the file size is allocated
/// (the old reader built a 32 MiB byte array plus a Latin-1 and a UTF-16 copy of it on every call), and a
/// string past the first 32 MiB is no longer missed. Results are cached per (path, length, ctime)
/// so the same DLL probed again (every library refresh, every dependency check) is not read again.
/// </summary>
public static class BinaryStringSearch
{
    internal const int WindowBytes = 1024 * 1024;
    private const int MaxCacheEntries = 4096;

    private static readonly ConcurrentDictionary<CacheKey, int[]> Cache = new();

    /// <summary>
    /// Test seam: number of files actually read (cache misses) on the calling thread. Per thread, so test classes that
    /// scan in parallel never move another test's count (it was process-wide and flaked under the full suite).
    /// </summary>
    internal static int ScansForTesting => _scans;
    [ThreadStatic] private static int _scans;

    /// <summary>True when the file contains any of <paramref name="needles"/> (ASCII or UTF-16LE, ASCII case ignored).</summary>
    public static bool ContainsAny(string path, params string[] needles)
        => Find(path, needles, found => found.Count > 0).Count > 0;

    /// <summary>
    /// The indexes of the <paramref name="needles"/> present in <paramref name="path"/>. The scan stops early once
    /// <paramref name="enough"/> returns true for the set found so far (null: scan the whole file). An unreadable or
    /// missing file yields an empty set. Cached by path, length, ctime (mtime where ctime cannot be read), needle list and stop rule.
    /// </summary>
    public static IReadOnlySet<int> Find(string path, IReadOnlyList<string> needles, Func<IReadOnlySet<int>, bool>? enough = null)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return new HashSet<int>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new HashSet<int>();
        }

        // An early-stopped scan knows only what its own stop rule needed, so the rule is part of the key.
        var rule = enough is null ? "" : $"{enough.Method.DeclaringType?.FullName}.{enough.Method.Name}";
        // Freshness is the ctime, not the mtime: a same-size rewrite that keeps or restores the mtime (cp -p, .NET
        // File.Copy, touch -d) still moves the ctime, which no program can set back, and a replacement by rename gets
        // a new one too. The mtime stands in only where ctime cannot be read (not Linux).
        var changed = RHI.Core.Deployment.FileChangeTime.TryGetUtc(info.FullName, followLinks: true) ?? info.LastWriteTimeUtc;
        var key = new CacheKey(info.FullName, info.Length, changed, string.Join('\u0001', needles), rule);
        if (Cache.TryGetValue(key, out var cached)) return cached.ToHashSet();

        HashSet<int> found;
        try
        {
            found = Scan(info.FullName, needles, enough);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<int>();
        }
        if (Cache.Count >= MaxCacheEntries) Cache.Clear();
        Cache[key] = found.ToArray();
        return found;
    }

    private static HashSet<int> Scan(string path, IReadOnlyList<string> needles, Func<IReadOnlySet<int>, bool>? enough)
    {
        _scans++;
        var patterns = new List<(int Index, byte[] Bytes)>();
        for (var i = 0; i < needles.Count; i++)
        {
            if (string.IsNullOrEmpty(needles[i])) continue;
            patterns.Add((i, Lower(Encoding.ASCII.GetBytes(needles[i]))));
            patterns.Add((i, Lower(Encoding.Unicode.GetBytes(needles[i]))));
        }
        var found = new HashSet<int>();
        if (patterns.Count == 0) return found;
        var overlap = patterns.Max(pattern => pattern.Bytes.Length) - 1;

        var buffer = ArrayPool<byte>.Shared.Rent(WindowBytes + overlap);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var carried = 0;
            while (true)
            {
                var read = stream.Read(buffer, carried, WindowBytes);
                if (read <= 0) break;
                var filled = carried + read;
                // Fold ASCII A-Z to a-z in place; needles were folded the same way. UTF-16LE high bytes are 0 for ASCII text.
                FoldAscii(buffer.AsSpan(carried, read));
                var window = buffer.AsSpan(0, filled);
                foreach (var (index, bytes) in patterns)
                {
                    if (found.Contains(index)) continue;
                    if (window.IndexOf(bytes) >= 0) found.Add(index);
                }
                if (enough is not null && enough(found)) return found;
                if (found.Count == needles.Count) return found;
                carried = Math.Min(overlap, filled);
                buffer.AsSpan(filled - carried, carried).CopyTo(buffer);
            }
            return found;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Lower(byte[] bytes)
    {
        FoldAscii(bytes);
        return bytes;
    }

    private static void FoldAscii(Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if ((uint)(b - 'A') <= 'Z' - 'A') bytes[i] = (byte)(b | 0x20);
        }
    }

    private readonly record struct CacheKey(string Path, long Length, DateTime ChangeUtc, string Needles, string StopRule);
}
