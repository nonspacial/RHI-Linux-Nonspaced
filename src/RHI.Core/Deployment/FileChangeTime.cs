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
        var buffer = new byte[Statx.BufferSize];
        if (!Statx.TryRead(path, followSymlinks: followLinks, Statx.MaskCtime, buffer)) return null;
        var mask = MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.MaskOffset));
        if ((mask & Statx.MaskCtime) == 0) return null;
        var seconds = MemoryMarshal.Read<long>(buffer.AsSpan(Statx.CtimeSecondsOffset));
        var nanoseconds = MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.CtimeNanosecondsOffset));
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
}
