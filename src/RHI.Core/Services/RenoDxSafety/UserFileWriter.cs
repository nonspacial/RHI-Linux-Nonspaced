using System.Runtime.InteropServices;

namespace RHI.Core.Services.RenoDxSafety;

/// <summary>
/// Which symlinked user file <see cref="UserFileWriter"/> may write through: the link's final target must be named
/// <paramref name="RequiredFileName"/> (case-insensitive) and lie, with every symlink resolved, inside one of
/// <paramref name="AllowedRoots"/>. <paramref name="RefusalPrefix"/> starts the refusal message (localisation key);
/// the link and its target follow it.
/// </summary>
public sealed record UserFileLinkPolicy(string RequiredFileName, IReadOnlyList<string> AllowedRoots, string RefusalPrefix);

/// <summary>What a <see cref="UserFileWriter"/> write did: the file actually replaced and whether its folder was fsynced.</summary>
public sealed record UserFileWriteResult(string WrittenPath, bool DirectorySynced);

/// <summary>
/// Crash-safe replacement of a file the user owns (RenoDX's ReShade.ini edits and rollback restores). The new bytes go
/// to a temp file in the same folder, are flushed to disk (fsync), read back and compared in full, take the original's
/// Unix mode and are renamed over the original; the folder is then fsynced so the rename survives a power loss. A
/// failure at any point leaves the original bytes untouched and removes the temp file, or says it could not.
/// A read-only target is refused, never replaced. A symlinked path is written through only when a
/// <see cref="UserFileLinkPolicy"/> allows its final target; any other link is refused.
/// Lane-local (w1/renodx-p0): the integrator folds this into <c>RHI.Core.Deployment.AtomicFile</c> as its user-file API.
/// </summary>
public static class UserFileWriter
{
    // Fixed message fragments (localisation keys); the path always follows them.
    public const string ReadOnlyRefusalPrefix = "Read-only file not replaced (RHI never overwrites a file you locked; make it writable to let RHI change it): ";
    public const string LinkRefusalPrefix = "Symbolic link not followed (RHI does not write through this link): ";
    public const string VerifyFailurePrefix = "Written content did not verify; the original file was kept: ";
    public const string TempLeftoverFragment = " A temporary file could not be removed: ";

    private const int MaxLinkHops = 40;

    /// <param name="linkPolicy">Required to write through a symlinked <paramref name="path"/>; null refuses any link.</param>
    /// <param name="wrapOutput">Test seam: wraps the temp-file stream (e.g. to fail mid-write like a full disk).</param>
    public static UserFileWriteResult WriteAllBytes(
        string path,
        byte[] bytes,
        UserFileLinkPolicy? linkPolicy = null,
        Func<Stream, Stream>? wrapOutput = null)
    {
        var target = ResolveWriteTarget(path, linkPolicy);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) throw new IOException($"Cannot resolve the folder of {path}.");
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

        var temp = Path.Combine(directory, "." + Path.GetFileName(target) + $".rhi-tmp-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var output = wrapOutput?.Invoke(file) ?? file;
                try
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
                finally
                {
                    if (!ReferenceEquals(output, file)) output.Dispose();
                }
                file.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(bytes))
                throw new IOException(VerifyFailurePrefix + target);
            if (mode is { } unixMode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, unixMode);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception failure)
        {
            if (TryDeleteTemp(temp) is { } leftover)
                throw new IOException($"{failure.Message}{TempLeftoverFragment}{temp} ({leftover})", failure);
            throw;
        }
        return new UserFileWriteResult(target, FsyncDirectory(directory));
    }

    /// <summary>
    /// The file a write to <paramref name="path"/> replaces: the path itself, or the final target of a symlink the
    /// policy allows. Throws <see cref="IOException"/> for any other link.
    /// </summary>
    public static string ResolveWriteTarget(string path, UserFileLinkPolicy? linkPolicy)
    {
        if (new FileInfo(path).LinkTarget is null) return path;
        if (linkPolicy is null) throw new IOException(LinkRefusalPrefix + path);

        var final = CanonicalPath(path);
        var nameAllowed = Path.GetFileName(final).Equals(linkPolicy.RequiredFileName, StringComparison.OrdinalIgnoreCase);
        var inside = linkPolicy.AllowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => CanonicalPath(root))
            .Any(root => IsSameOrInside(final, root));
        if (!nameAllowed || !inside || Directory.Exists(final))
            throw new IOException($"{linkPolicy.RefusalPrefix}{path} -> {final}");
        return final;
    }

    /// <summary>Absolute path with every symlinked component resolved (the final file need not exist).</summary>
    public static string CanonicalPath(string path) => CanonicalPath(Path.GetFullPath(path), 0);

    private static string CanonicalPath(string full, int hops)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var link = Directory.Exists(next) ? new DirectoryInfo(next).LinkTarget : new FileInfo(next).LinkTarget;
            if (link is not null)
            {
                if (++hops > MaxLinkHops) throw new IOException($"Too many levels of symbolic links: {full}");
                var resolved = Path.GetFullPath(Path.IsPathRooted(link) ? link : Path.Combine(current, link));
                var rest = parts[(i + 1)..];
                return CanonicalPath(rest.Length == 0 ? resolved : Path.Combine([resolved, .. rest]), hops);
            }
            current = next;
        }
        return current;
    }

    private static bool IsSameOrInside(string path, string root)
    {
        if (path.Equals(root, StringComparison.Ordinal)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string? TryDeleteTemp(string temp)
    {
        if (!File.Exists(temp)) return null;
        try
        {
            File.Delete(temp);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// fsync on a folder so a rename in it is durable. False where it cannot be done (no C library, or a file system
    /// that refuses directory fsync); the file data itself was already flushed. No-op (true) outside Linux.
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
            // "libc" does not resolve everywhere; glibc's soname does.
        }
        try
        {
            return FsyncDirectory(directory, Glibc.open, Glibc.fsync, Glibc.close);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
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
