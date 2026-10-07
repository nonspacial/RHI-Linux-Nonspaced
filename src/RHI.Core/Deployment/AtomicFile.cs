using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Services;

namespace RHI.Core.Deployment;

/// <summary>
/// Durable, verified file writes for the deployment layer. Every write goes to a uniquely named
/// temporary file in the target's directory (<c>&lt;target&gt;.rhi-tmp-&lt;id&gt;</c>), is flushed to disk
/// (fsync), re-read and compared in full against what was meant to be written (never length only),
/// renamed over the target in one step and followed by an fsync of the directory so the rename
/// itself survives a power loss. A failure removes the temporary file and leaves the target as it was.
/// <para>
/// What the re-read verifies: it runs straight after the fsync, so it is normally served from the
/// page cache. It catches software faults (short or misdirected writes, a FUSE or network file
/// system returning other bytes, a concurrent writer), not media corruption that only shows once
/// the cached pages are evicted. The cost is one extra SHA-256 pass per write.
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>Marker of a toolkit temporary file; a crash can leave one behind for the start-up sweep.</summary>
    public const string TempMarker = ".rhi-tmp-";

    // Fixed message fragments (localisation keys: PT-BR presentation pairs match them); the path follows each.
    public const string ReadOnlyRefusalPrefix = "Read-only file not replaced (RHI never overwrites a file you locked; make it writable to let RHI change it): ";
    public const string LinkRefusalPrefix = "Symbolic link not followed (RHI does not write through this link): ";
    public const string VerifyFailurePrefix = "Written content did not verify; the original file was kept: ";
    public const string TempLeftoverFragment = " A temporary file could not be removed: ";

    private const int BufferSize = FileHash.BufferSize;

    public static string TempPathFor(string target, string? id = null)
        => target + TempMarker + (string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id);

    // ---- whole-content writes -------------------------------------------------------------------

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => WriteVerified(path, bytes, null, null);

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
                throw new IOException(VerifyFailurePrefix + path);
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

    // ---- store files -----------------------------------------------------------------------------

    /// <summary>
    /// Replaces a store file (settings, library, preferences, caches) durably and verified. A symlinked
    /// store file (dotfiles kept in a repository, a config folder shared across machines) stays a link:
    /// the file the chain ends at is replaced. That file keeps its Unix mode unless <paramref name="mode"/>
    /// asks for another (e.g. 0600). The folder is created when missing.
    /// </summary>
    public static void WriteStoreFile(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        var target = FinalLinkTarget(Path.GetFullPath(path));
        mode ??= ExistingMode(target);
        var directory = Path.GetDirectoryName(target) ?? throw new IOException($"{target} has no parent directory.");
        CreateDirectoryDurable(directory);
        WriteVerified(target, bytes, mode, null);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a new file that must not exist yet (a quarantine copy), fsyncs and
    /// verifies it and fsyncs its folder. A copy that does not verify is removed and the write fails.
    /// </summary>
    public static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), FileHash.Sha256(path)))
        {
            FileCleanup.TryDelete(path);
            throw new IOException(VerifyFailurePrefix + path);
        }
        FsyncDirectoryOf(path);
    }

    /// <summary>
    /// The one write path: unique temp beside <paramref name="target"/>, write (through
    /// <paramref name="wrapOutput"/> when given — a test seam), fsync, full verify, exact mode, rename,
    /// directory fsync. Returns whether the directory fsync succeeded (the data is durable either way).
    /// </summary>
    private static bool WriteVerified(string target, ReadOnlySpan<byte> bytes, UnixFileMode? exactMode, Func<Stream, Stream>? wrapOutput)
    {
        var temp = TempPathFor(target);
        try
        {
            var file = CreateTemp(temp, FileOptions.None, exactMode);
            Stream output = file;
            try
            {
                output = IoMeter.Wrap(file);
                if (wrapOutput is not null) output = wrapOutput(output);
                output.Write(bytes);
                output.Flush();
                file.Flush(flushToDisk: true);
            }
            finally
            {
                if (!ReferenceEquals(output, file)) output.Dispose();
                file.Dispose();
            }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), FileHash.Sha256(temp)))
                throw new IOException(VerifyFailurePrefix + target);
            // The create mode is filtered by the umask; set the exact bits asked for.
            if (exactMode is { } mode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, mode);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception failure)
        {
            // The target is untouched; a temp that cannot be removed is reported, never swallowed.
            if (!FileCleanup.TryDelete(temp, out var leftover))
                throw new IOException($"{failure.Message}{TempLeftoverFragment}{temp} ({leftover!.Message})", failure);
            throw;
        }
        return FsyncDirectoryOf(target);
    }

    /// <summary>The file a symlink chain ends at (absolute), or <paramref name="fullPath"/> itself.</summary>
    private static string FinalLinkTarget(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.LinkTarget is null) return fullPath;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        }
        catch (IOException)
        {
            // A link loop or an unreadable link: write the path as given (replaces the link).
            return fullPath;
        }
    }

    private static UnixFileMode? ExistingMode(string fullPath)
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return File.Exists(fullPath) ? File.GetUnixFileMode(fullPath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- user files ------------------------------------------------------------------------------

    /// <summary>
    /// Replaces a file the user owns (a game's ReShade.ini edited by RenoDX, a rollback restore of it) through the
    /// one write path (unique temp, fsync, full read-back verify, rename, directory fsync). The file keeps its Unix
    /// mode. A read-only target is refused, never replaced. A symlinked path is written through only when
    /// <paramref name="linkPolicy"/> allows its final target (name and containment); any other link is refused.
    /// </summary>
    /// <param name="wrapOutput">Test seam: wraps the temp-file stream (e.g. to fail mid-write like a full disk).</param>
    public static UserFileWriteResult ReplaceUserFile(
        string path, byte[] bytes, UserFileLinkPolicy? linkPolicy = null, Func<Stream, Stream>? wrapOutput = null)
    {
        var target = ResolveUserFileTarget(path, linkPolicy);
        if (string.IsNullOrEmpty(Path.GetDirectoryName(target))) throw new IOException($"Cannot resolve the folder of {path}.");
        UnixFileMode? mode = null;
        if (File.Exists(target))
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                    throw new IOException(ReadOnlyRefusalPrefix + target);
            }
            else
            {
                var current = File.GetUnixFileMode(target);
                if (!current.HasFlag(UnixFileMode.UserWrite))
                    throw new IOException(ReadOnlyRefusalPrefix + target);
                mode = current;
            }
        }
        var synced = WriteVerified(target, bytes, mode, wrapOutput);
        return new UserFileWriteResult(target, synced);
    }

    /// <summary>
    /// The file a write to <paramref name="path"/> replaces: the path itself, or the final target of a symlink the
    /// policy allows (name equal to <see cref="UserFileLinkPolicy.RequiredFileName"/>, inside one of its roots with
    /// every symlink resolved on both sides, not a folder). Throws <see cref="IOException"/> for any other link.
    /// </summary>
    public static string ResolveUserFileTarget(string path, UserFileLinkPolicy? linkPolicy)
    {
        if (new FileInfo(path).LinkTarget is null) return path;
        if (linkPolicy is null) throw new IOException(LinkRefusalPrefix + path);

        var final = PathIdentity.Canonicalize(path);
        var nameAllowed = Path.GetFileName(final).Equals(linkPolicy.RequiredFileName, StringComparison.OrdinalIgnoreCase);
        var inside = linkPolicy.AllowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => PathIdentity.IsSameOrInside(final, root));
        // A link loop leaves a link at the end of the "canonical" path: refused like any other unresolvable link.
        if (!nameAllowed || !inside || Directory.Exists(final) || new FileInfo(final).LinkTarget is not null)
            throw new IOException($"{linkPolicy.RefusalPrefix}{path} -> {final}");
        return final;
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
    /// <param name="createMode">Unix mode of the new file (less umask); null keeps the default 0666 less umask.
    /// The content's own mode bits (e.g. an archive member's) are never applied.</param>
    public static async Task<StagedFile> StageStreamAsync(
        Stream content, string target, string? expectedSha256, string? tempId, long maxBytes, CancellationToken cancellationToken,
        UnixFileMode? createMode = null)
    {
        var temp = TempPathFor(target, tempId);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                var file = CreateTemp(temp, FileOptions.Asynchronous | FileOptions.SequentialScan, createMode);
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

    /// <summary>Creates a new temporary file (never an existing one), optionally with a fixed Unix mode.</summary>
    private static FileStream CreateTemp(string temp, FileOptions options, UnixFileMode? createMode)
    {
        var settings = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = options
        };
        if (createMode is { } mode && !OperatingSystem.IsWindows()) settings.UnixCreateMode = mode;
        return new FileStream(temp, settings);
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

    /// <summary>
    /// Moves a file without ever holding it only in unflushed memory. Within one file system this is
    /// a rename (never replacing an existing <paramref name="to"/>) plus an fsync of both directories.
    /// Across file systems (EXDEV), where <see cref="File.Move(string, string)"/> would copy without
    /// fsync and unlink the source, it writes a verified, fsynced copy and deletes the source only
    /// after that copy is in place; a failed copy leaves the source untouched.
    /// </summary>
    public static void MoveDurable(string from, string to)
    {
        if (File.Exists(to) || Directory.Exists(to))
            throw new IOException($"Refusing to move {from}: {to} already exists.");
        if (!SimulateCrossDeviceForTesting.Value && TryRenameNoCopy(from, to))
        {
            FsyncDirectoryOf(to);
            FsyncDirectoryOf(from);
            return;
        }
        Copy(from, to);
        CopyCompletedForTesting.Value?.Invoke(to);
        Delete(from);
    }

    /// <summary>Test seam: makes <see cref="MoveDurable"/> take its cross-file-system path.</summary>
    internal static readonly AsyncLocal<bool> SimulateCrossDeviceForTesting = new();

    /// <summary>Test seam: runs after the cross-file-system copy is placed, before the source is deleted.</summary>
    internal static readonly AsyncLocal<Action<string>?> CopyCompletedForTesting = new();

    /// <summary>
    /// rename(2) that reports EXDEV as false instead of falling back to a copy. Off Linux (or with
    /// no C library to call) it uses <see cref="File.Move(string, string)"/> only when both paths are
    /// in the same directory, where a rename cannot cross a file system.
    /// </summary>
    private static bool TryRenameNoCopy(string from, string to)
    {
        const int exdev = 18;
        if (OperatingSystem.IsLinux())
        {
            int result, errno;
            try
            {
                result = Libc.rename(from, to);
                errno = Marshal.GetLastPInvokeError();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                try
                {
                    result = Glibc.rename(from, to);
                    errno = Marshal.GetLastPInvokeError();
                }
                catch (Exception inner) when (inner is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false; // no C library: take the verified-copy path
                }
            }
            if (result == 0) return true;
            if (errno == exdev) return false;
            throw new IOException($"Could not move {from} to {to} (errno {errno}).");
        }
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(from)), Path.GetDirectoryName(Path.GetFullPath(to)), StringComparison.Ordinal))
            return false;
        File.Move(from, to, overwrite: false);
        return true;
    }

    /// <summary>
    /// <see cref="Directory.CreateDirectory(string)"/> that also fsyncs the parent of every directory
    /// it had to create, so the new folders survive a power loss together with what is put in them.
    /// </summary>
    public static void CreateDirectoryDurable(string directory)
    {
        var full = Path.GetFullPath(directory);
        var missing = new Stack<string>();
        for (var current = full; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
            missing.Push(current);
        if (missing.Count == 0) return;
        Directory.CreateDirectory(full);
        foreach (var created in missing) FsyncDirectoryOf(created);
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

        [DllImport("libc", SetLastError = true)]
        public static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    }

    private static class Glibc
    {
        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int fsync(int fd);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
    }
}

/// <summary>A verified, fsynced temporary file waiting to be placed.</summary>
public sealed record StagedFile(string Path, string Sha256, long Length);

/// <summary>
/// Which symlinked user file <see cref="AtomicFile.ReplaceUserFile"/> may write through: the link's final target must
/// be named <paramref name="RequiredFileName"/> (case-insensitive) and lie, with every symlink resolved, inside one of
/// <paramref name="AllowedRoots"/>. <paramref name="RefusalPrefix"/> starts the refusal message (localisation key); the
/// link and its target follow it.
/// </summary>
public sealed record UserFileLinkPolicy(string RequiredFileName, IReadOnlyList<string> AllowedRoots, string RefusalPrefix);

/// <summary>What <see cref="AtomicFile.ReplaceUserFile"/> did: the file actually replaced and whether its folder was fsynced.</summary>
public sealed record UserFileWriteResult(string WrittenPath, bool DirectorySynced);
