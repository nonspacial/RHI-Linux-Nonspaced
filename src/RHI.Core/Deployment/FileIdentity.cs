using System.Runtime.InteropServices;

namespace RHI.Core.Deployment;

/// <summary>
/// A file's identity on Linux: the device (as a glibc <c>dev_t</c>, <c>makedev(major, minor)</c>) and the inode number,
/// which .NET does not expose. Two paths name the same file exactly when both match, whatever symlinks, bind mounts or
/// relative spellings lead there. Read with statx(2) (symlinks followed).
/// </summary>
public readonly record struct FileIdentity(ulong Device, ulong Inode)
{
    /// <summary>The identity of <paramref name="path"/>, or null where it cannot be read (not Linux, no statx, missing).</summary>
    public static FileIdentity? TryGet(string path)
    {
        var buffer = new byte[Statx.BufferSize];
        if (!Statx.TryRead(path, followSymlinks: true, Statx.MaskIno, buffer)) return null;
        if ((MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.MaskOffset)) & Statx.MaskIno) == 0) return null;
        var inode = MemoryMarshal.Read<ulong>(buffer.AsSpan(Statx.InoOffset));
        ulong major = MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.DevMajorOffset));
        ulong minor = MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.DevMinorOffset));
        // glibc's makedev: the dev_t that stat(2) returns and that Wine prints in its server directory name.
        var device = ((major & 0xfffff000UL) << 32) | ((major & 0xfffUL) << 8) | ((minor & 0xffffff00UL) << 12) | (minor & 0xffUL);
        return new FileIdentity(device, inode);
    }
}

/// <summary>What kind of file a path names on Linux (statx(2), symlinks followed), which .NET does not expose.</summary>
public static class FileKind
{
    private const uint MaskType = 0x1;
    private const int ModeOffset = 28;
    private const int TypeBits = 0xF000;
    private const int RegularFile = 0x8000;

    /// <summary>
    /// True for a regular file, false for anything else (FIFO, device, socket, folder); a link counts as what it leads to.
    /// Null where it cannot be told (not Linux, no statx, missing or dangling path).
    /// </summary>
    public static bool? IsRegularFile(string path)
    {
        var buffer = new byte[Statx.BufferSize];
        if (!Statx.TryRead(path, followSymlinks: true, MaskType, buffer)) return null;
        if ((MemoryMarshal.Read<uint>(buffer.AsSpan(Statx.MaskOffset)) & MaskType) == 0) return null;
        return (MemoryMarshal.Read<ushort>(buffer.AsSpan(ModeOffset)) & TypeBits) == RegularFile;
    }
}

/// <summary>statx(2) through the C library; its buffer layout is the same on every Linux architecture.</summary>
internal static class Statx
{
    public const int BufferSize = 256;
    public const int MaskOffset = 0;
    public const int InoOffset = 32;
    public const int CtimeSecondsOffset = 96;
    public const int CtimeNanosecondsOffset = 104;
    public const int DevMajorOffset = 136;
    public const int DevMinorOffset = 140;
    public const uint MaskCtime = 0x80;
    public const uint MaskIno = 0x100;

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;

    /// <summary>Fills <paramref name="buffer"/>; false when not on Linux, statx is missing from the C library, or the call failed.</summary>
    public static bool TryRead(string path, bool followSymlinks, uint mask, byte[] buffer)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var flags = followSymlinks ? 0 : AtSymlinkNoFollow;
        try
        {
            return Libc.statx(AtFdCwd, path, flags, mask, buffer) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            try
            {
                return Glibc.statx(AtFdCwd, path, flags, mask, buffer) == 0;
            }
            catch (Exception inner) when (inner is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
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
