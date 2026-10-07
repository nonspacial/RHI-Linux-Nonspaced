using System.Runtime.InteropServices;

namespace RHI.Platform.Linux.Persistence;

/// <summary>
/// Crash-safe whole-file replacement: write a uniquely named sibling temp file, fsync it,
/// rename it over the target (atomic on one filesystem), then fsync the directory so the
/// rename itself survives a power loss. A reader sees either the old or the new file, never
/// a truncated one.
/// </summary>
public static class DurableFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new IOException($"{fullPath} has no parent directory.");
        Directory.CreateDirectory(directory);

        // Unique per write: two store instances (or processes) saving the same file must not
        // share, truncate or rename each other's half-written temp file.
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (mode is { } unixMode && !OperatingSystem.IsWindows())
                options.UnixCreateMode = unixMode;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            // Only the temp file is cleaned up; the target was either replaced whole or untouched.
            TryDeleteTemp(temp);
            throw;
        }

        FsyncDirectory(directory);
    }

    /// <summary>Copies <paramref name="bytes"/> to a new file that must not exist yet, durably.</summary>
    public static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) FsyncDirectory(directory);
    }

    private static void TryDeleteTemp(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (IOException) { /* a stray hidden .tmp is harmless and never read */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    // The data and the rename are already done when this runs; a directory fsync failure only
    // weakens durability across a power cut and must not turn a completed save into a reported
    // failure, so its return code is deliberately not raised.
    private static void FsyncDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var fd = open(directory, 0 /* O_RDONLY */);
        if (fd < 0) return;
        try { fsync(fd); }
        finally { close(fd); }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
