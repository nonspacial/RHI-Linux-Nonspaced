using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RHI.Core.Deployment;

/// <summary>
/// Durable, verified file writes for the deployment layer. Every write goes to a uniquely named
/// temporary file in the target's directory (<c>&lt;target&gt;.rhi-tmp-&lt;id&gt;</c>), is flushed to disk
/// (fsync), re-read and compared in full against what was meant to be written (never length only),
/// renamed over the target in one step and followed by an fsync of the directory so the rename
/// itself survives a power loss. A failure removes the temporary file and leaves the target as it was.
/// </summary>
public static class AtomicFile
{
    /// <summary>Marker of a toolkit temporary file; a crash can leave one behind for the start-up sweep.</summary>
    public const string TempMarker = ".rhi-tmp-";

    private const int BufferSize = FileHash.BufferSize;

    public static string TempPathFor(string target, string? id = null)
        => target + TempMarker + (string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id);

    // ---- whole-content writes -------------------------------------------------------------------

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = TempPathFor(path);
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.None))
            {
                using var output = IoMeter.Wrap(file);
                output.Write(bytes);
                output.Flush();
                file.Flush(flushToDisk: true);
            }
            var expected = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(expected, FileHash.Sha256(temp)))
                throw new IOException($"Written content did not verify for {path}.");
            MoveIntoPlace(temp, path);
        }
        catch
        {
            FileCleanup.TryDelete(temp);
            throw;
        }
    }

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var temp = TempPathFor(path);
        try
        {
            var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                var output = IoMeter.Wrap(file);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                await output.DisposeAsync().ConfigureAwait(false);
            }
            var expected = SHA256.HashData(bytes.Span);
            var written = await FileHash.Sha256Async(temp, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, written))
                throw new IOException($"Written content did not verify for {path}.");
            MoveIntoPlace(temp, path);
        }
        catch
        {
            FileCleanup.TryDelete(temp);
            throw;
        }
    }

    /// <summary>Same encoding semantics as <see cref="File.WriteAllText(string, string?)"/> (UTF-8, no BOM).</summary>
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Same semantics as <see cref="File.WriteAllText(string, string?, Encoding)"/>: the encoding's preamble is written.</summary>
    public static void WriteAllText(string path, string text, Encoding encoding) => WriteAllBytes(path, Encode(text, encoding));

    public static Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default)
        => WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    private static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    // ---- copies ----------------------------------------------------------------------------------

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="target"/> durably, reading the source once
    /// (hashed while copying) and verifying the written copy in full.
    /// </summary>
    /// <param name="expectedSha256">When given, the source content must hash to it.</param>
    /// <returns>SHA-256 (upper-case hex) of the content now at <paramref name="target"/>.</returns>
    public static async Task<string> CopyAsync(string source, string target, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        var staged = await StageCopyAsync(source, target, expectedSha256, null, cancellationToken).ConfigureAwait(false);
        try
        {
            MoveIntoPlace(staged.Path, target);
        }
        catch
        {
            FileCleanup.TryDelete(staged.Path);
            throw;
        }
        return staged.Sha256;
    }

    /// <summary>Synchronous <see cref="CopyAsync"/>.</summary>
    public static string Copy(string source, string target, string? expectedSha256 = null)
    {
        EnsureDistinct(source, target);
        using var input = FileHash.OpenRead(source);
        var staged = StageStream(input, target, expectedSha256, null, long.MaxValue);
        try
        {
            MoveIntoPlace(staged.Path, target);
        }
        catch
        {
            FileCleanup.TryDelete(staged.Path);
            throw;
        }
        return staged.Sha256;
    }

    /// <summary>
    /// Writes a verified, fsynced copy of <paramref name="source"/> beside <paramref name="target"/>
    /// without touching the target. The caller places it with <see cref="MoveIntoPlace"/> (or deletes it).
    /// </summary>
    /// <param name="tempId">Suffix of the temporary name; pass a transaction id so a crash recovery can find it.</param>
    public static async Task<StagedFile> StageCopyAsync(
        string source, string target, string? expectedSha256, string? tempId, CancellationToken cancellationToken)
    {
        EnsureDistinct(source, target);
        await using var input = FileHash.OpenRead(source, asynchronous: true);
        return await StageStreamAsync(input, target, expectedSha256, tempId, long.MaxValue, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a verified, fsynced temporary file beside
    /// <paramref name="target"/> (download bodies, archive members). Fails if more than
    /// <paramref name="maxBytes"/> arrive or the content does not hash to <paramref name="expectedSha256"/>.
    /// </summary>
    public static async Task<StagedFile> StageStreamAsync(
        Stream content, string target, string? expectedSha256, string? tempId, long maxBytes, CancellationToken cancellationToken)
    {
        var temp = TempPathFor(target, tempId);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (file.ConfigureAwait(false))
                {
                    var output = IoMeter.Wrap(file);
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        length += read;
                        if (length > maxBytes)
                            throw new InvalidDataException($"Content for {Path.GetFileName(target)} exceeds the {maxBytes}-byte limit.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    await output.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var sha = Convert.ToHexString(hash.GetHashAndReset());
            CheckExpected(target, sha, expectedSha256);
            var written = await FileHash.Sha256HexAsync(temp, cancellationToken).ConfigureAwait(false);
            if (!FileHash.HexEquals(sha, written))
                throw new IOException($"Copy verification failed for {target}: the written file does not match what was read.");
            return new StagedFile(temp, sha, length);
        }
        catch
        {
            FileCleanup.TryDelete(temp);
            throw;
        }
    }

    private static StagedFile StageStream(Stream content, string target, string? expectedSha256, string? tempId, long maxBytes)
    {
        var temp = TempPathFor(target, tempId);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
                var output = IoMeter.Wrap(file);
                int read;
                while ((read = content.Read(buffer, 0, BufferSize)) > 0)
                {
                    length += read;
                    if (length > maxBytes)
                        throw new InvalidDataException($"Content for {Path.GetFileName(target)} exceeds the {maxBytes}-byte limit.");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                output.Flush();
                file.Flush(flushToDisk: true);
                output.Dispose();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var sha = Convert.ToHexString(hash.GetHashAndReset());
            CheckExpected(target, sha, expectedSha256);
            if (!FileHash.HexEquals(sha, FileHash.Sha256Hex(temp)))
                throw new IOException($"Copy verification failed for {target}: the written file does not match what was read.");
            return new StagedFile(temp, sha, length);
        }
        catch
        {
            FileCleanup.TryDelete(temp);
            throw;
        }
    }

    private static void CheckExpected(string target, string actual, string? expected)
    {
        if (!string.IsNullOrWhiteSpace(expected) && !FileHash.HexEquals(actual, expected))
            throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(target)}: expected {expected}, got {actual}.");
    }

    private static void EnsureDistinct(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal))
            throw new ArgumentException($"Source and target are the same file: {source}");
    }

    // ---- placing and renaming --------------------------------------------------------------------

    /// <summary>Renames <paramref name="staged"/> onto <paramref name="target"/> and fsyncs the directory.</summary>
    public static void MoveIntoPlace(string staged, string target, bool overwrite = true)
    {
        File.Move(staged, target, overwrite);
        FsyncDirectoryOf(target);
    }

    /// <summary>Durable rename within one directory (or file system): rename, then fsync both directories.</summary>
    public static void Rename(string from, string to, bool overwrite = false)
    {
        File.Move(from, to, overwrite);
        FsyncDirectoryOf(to);
        var fromDir = Path.GetDirectoryName(Path.GetFullPath(from));
        var toDir = Path.GetDirectoryName(Path.GetFullPath(to));
        if (fromDir is not null && !string.Equals(fromDir, toDir, StringComparison.Ordinal))
            FsyncDirectory(fromDir);
    }

    /// <summary>Deletes a file and makes the removal durable.</summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        FsyncDirectoryOf(path);
    }

    public static bool FsyncDirectoryOf(string path)
        => Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir && FsyncDirectory(dir);

    /// <summary>
    /// fsync on a directory so renames, creations and deletions in it are durable. Returns false where
    /// the file system refuses directory fsync (some FUSE/network mounts return EINVAL); the data
    /// itself was already flushed, so callers carry on. No-op outside Linux.
    /// </summary>
    public static bool FsyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            return FsyncDirectory(directory, Libc.open, Libc.fsync, Libc.close);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // "libc" does not resolve everywhere; glibc's soname always does.
        }
        try
        {
            return FsyncDirectory(directory, Glibc.open, Glibc.fsync, Glibc.close);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // no C library to call: the file data itself was already flushed
        }
    }

    private static bool FsyncDirectory(string directory, Func<string, int, int> open, Func<int, int> fsync, Func<int, int> close)
    {
        var fd = open(directory, 0 /* O_RDONLY */);
        if (fd < 0) return false;
        try
        {
            return fsync(fd) == 0;
        }
        finally
        {
            close(fd);
        }
    }

    private static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int fsync(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);
    }

    private static class Glibc
    {
        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int fsync(int fd);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int close(int fd);
    }
}

/// <summary>A verified, fsynced temporary file waiting to be placed.</summary>
public sealed record StagedFile(string Path, string Sha256, long Length);
