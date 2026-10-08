using System.Runtime.InteropServices;

namespace RHI.Core.Deployment;

/// <summary>
/// Inode change time (ctime) of a file, which .NET does not expose. Unlike the modification time it
/// cannot be set back by a program: a rename, a new copy (<see cref="File.Copy(string, string)"/>
/// keeps the source's mtime on Unix) or a <c>touch -d</c> all move it to "now". That makes it the
/// right clock for "has this leftover been sitting here untouched for a while".
/// Read with statx(2), whose buffer layout is the same on every Linux architecture.
/// </summary>
public static class FileChangeTime
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxCtime = 0x80;
    private const int StatxBufferSize = 256;
    private const int MaskOffset = 0;
    private const int CtimeSecondsOffset = 96;
    private const int CtimeNanosecondsOffset = 104;

    /// <summary>
    /// The file's ctime in UTC (the link itself for a symlink), or null where it cannot be read
    /// (not Linux, no statx in the C library, or the call failed).
    /// </summary>
    public static DateTime? TryGetUtc(string path) => TryGetUtc(path, followLinks: false);

    /// <summary>
    /// The ctime of <paramref name="path"/>, of the file a symlink chain ends at when <paramref name="followLinks"/>
    /// (what a reader of the path sees), or null where it cannot be read.
    /// </summary>
    public static DateTime? TryGetUtc(string path, bool followLinks)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var buffer = new byte[StatxBufferSize];
        var flags = followLinks ? 0 : AtSymlinkNoFollow;
        int result;
        try
        {
            result = Libc.statx(AtFdCwd, path, flags, StatxCtime, buffer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            try
            {
                result = Glibc.statx(AtFdCwd, path, flags, StatxCtime, buffer);
            }
            catch (Exception inner) when (inner is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
        if (result != 0) return null;
        var mask = MemoryMarshal.Read<uint>(buffer.AsSpan(MaskOffset));
        if ((mask & StatxCtime) == 0) return null;
        var seconds = MemoryMarshal.Read<long>(buffer.AsSpan(CtimeSecondsOffset));
        var nanoseconds = MemoryMarshal.Read<uint>(buffer.AsSpan(CtimeNanosecondsOffset));
        return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100);
    }

    /// <summary>
    /// When the file last changed in any way: its ctime, or, where ctime cannot be read, the later
    /// of its modification and creation times (the best .NET offers).
    /// </summary>
    public static DateTime LastChangeUtc(string path)
    {
        if (TryGetUtc(path) is { } ctime) return ctime;
        var info = new FileInfo(path);
        return info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
    }

    private static class Libc
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [In, Out] byte[] buffer);
    }

    private static class Glibc
    {
        [DllImport("libc.so.6", SetLastError = true)]
        public static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [In, Out] byte[] buffer);
    }
}
