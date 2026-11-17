using System.Globalization;
using System.Text;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// w14/layouts-b (#305, upstream "Delete (to the Recycle Bin)"): moves a file to the desktop's Trash under the
/// freedesktop.org Trash specification 1.0, so it can be restored from the file manager. Nothing is ever deleted
/// permanently: when no trash can take the file, it stays where it is and the reason is returned.
/// <list type="bullet">
/// <item>A file on the same file system as the home trash (<c>$XDG_DATA_HOME/Trash</c>) goes there; its
/// <c>.trashinfo</c> names it by its absolute path.</item>
/// <item>A file on another file system (a second drive with the game library) goes to that drive's
/// <c>$topdir/.Trash/$uid</c> when <c>$topdir/.Trash</c> is a real directory with the sticky bit, else to
/// <c>$topdir/.Trash-$uid</c> (created 0700); its <c>.trashinfo</c> names it relative to <c>$topdir</c>.</item>
/// <item>w14 final review nit: the move is <c>rename(2)</c>. Only when the kernel says the two paths are on different file
/// systems (EXDEV: a bind mount or subvolume the mount table did not tell apart) is it copied and the original removed
/// after the whole copy; any other failure leaves the file where it is.</item>
/// <item>The <c>.trashinfo</c> is created first (exclusively, so two trashings never share a name); the file is renamed into
/// <c>files/</c> after it; when that fails the <c>.trashinfo</c> is removed again.</item>
/// </list>
/// </summary>
public sealed class FreedesktopTrash
{
    private readonly string _homeTrash;
    private readonly Func<string, string> _mountOf;
    private readonly Func<DateTime> _now;
    private readonly string _uid;

    /// <summary>Tests: stands in for <c>rename(2)</c>; returns 0 or the errno.</summary>
    internal Func<string, string, int>? RenameForTesting { get; set; }

    private const int Exdev = 18;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int SysRename(string oldPath, string newPath);

    private int Rename(string from, string to)
    {
        if (RenameForTesting is { } rename) return rename(from, to);
        return SysRename(from, to) == 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
    }

    // EXDEV only: copied whole, then the original goes. A copy that fails is removed again and the original stays.
    private static void CopyAcross(string from, string to)
    {
        try
        {
            if (Directory.Exists(from) && new DirectoryInfo(from).LinkTarget is null)
            {
                // Symlinks are copied as links, never followed (a Wine prefix's z: leads to /).
                Directory.CreateDirectory(to);
                foreach (var entry in SafeFileEnumeration.Entries(from).OrderBy(e => e.FullName.Length))
                {
                    var target = Path.Combine(to, Path.GetRelativePath(from, entry.FullName));
                    if (entry.LinkTarget is { } link) File.CreateSymbolicLink(target, link);
                    else if (entry is DirectoryInfo) Directory.CreateDirectory(target);
                    else File.Copy(entry.FullName, target);
                }
            }
            else if (new FileInfo(from).LinkTarget is { } link) File.CreateSymbolicLink(to, link);
            else File.Copy(from, to);
        }
        catch
        {
            try
            {
                if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
                else if (File.Exists(to)) File.Delete(to);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { AppLog.Warn("trash", $"Partial copy {to} left", cleanup); }
            throw;
        }
        if (Directory.Exists(from)) Directory.Delete(from, recursive: true);
        else File.Delete(from);
    }

    /// <param name="homeTrash">The home trash (default <c>$XDG_DATA_HOME/Trash</c>).</param>
    /// <param name="mountOf">The mount point a path is on (default: <c>/proc/self/mountinfo</c>).</param>
    /// <param name="now">Local time for <c>DeletionDate</c>.</param>
    /// <param name="uid">The user id for a drive's trash (default: the running user's).</param>
    public FreedesktopTrash(string? homeTrash = null, Func<string, string>? mountOf = null, Func<DateTime>? now = null, string? uid = null)
    {
        _homeTrash = homeTrash ?? Path.Combine(LinuxPaths.DataHome, "Trash");
        _mountOf = mountOf ?? MountPointOf;
        _now = now ?? (() => DateTime.Now);
        _uid = uid ?? CurrentUid();
    }

    /// <summary>The fixed part of the message when no trash could take a file (the file is left in place).</summary>
    public const string NotTrashedPrefix = "Couldn't move the file to the Trash: ";

    /// <summary>Moves <paramref name="path"/> to the Trash. The trashed file's new path, or null with the reason.</summary>
    public string? Trash(string path, out string? error)
    {
        error = null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) { error = NotTrashedPrefix + LinuxDesktopOpener.FileMissingPrefix + full; return null; }
            var fileMount = _mountOf(full);
            Directory.CreateDirectory(_homeTrash);
            if (string.Equals(_mountOf(_homeTrash), fileMount, StringComparison.Ordinal))
                return MoveInto(_homeTrash, full, full, out error);
            if (TopdirTrash(fileMount) is not { } trash) { error = NotTrashedPrefix + "no trash on " + fileMount; return null; }
            return MoveInto(trash, full, Path.GetRelativePath(fileMount, full), out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn("trash", $"{path} not trashed", ex);
            error = NotTrashedPrefix + ex.Message;
            return null;
        }
    }

    /// <summary>The drive's trash: <c>.Trash/$uid</c> under a valid shared <c>.Trash</c>, else <c>.Trash-$uid</c>.</summary>
    private string? TopdirTrash(string topdir)
    {
        var shared = Path.Combine(topdir, ".Trash");
        try
        {
            var info = new DirectoryInfo(shared);
            if (info.Exists && info.LinkTarget is null && (info.UnixFileMode & UnixFileMode.StickyBit) != 0)
            {
                var mine = Path.Combine(shared, _uid);
                Directory.CreateDirectory(mine);
                return mine;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the spec's fallback below */ }
        var own = Path.Combine(topdir, ".Trash-" + _uid);
        try
        {
            var info = new DirectoryInfo(own);
            if (info.Exists) return info.LinkTarget is null ? own : null; // a link here is not a trash RHI may use
            Directory.CreateDirectory(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return own;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("trash", $"No trash on {topdir}", ex);
            return null;
        }
    }

    private string? MoveInto(string trash, string full, string infoPath, out string? error)
    {
        error = null;
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        var name = Path.GetFileName(full);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? name : $"{stem}.{n}{ext}";
            var infoFile = Path.Combine(info, candidate + ".trashinfo");
            var target = Path.Combine(files, candidate);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                using (var stream = new FileStream(infoFile, FileMode.CreateNew, FileAccess.Write))
                    stream.Write(Encoding.UTF8.GetBytes(TrashInfo(infoPath, _now())));
            }
            catch (IOException) when (File.Exists(infoFile))
            {
                continue; // taken by another trashing: next name
            }
            try
            {
                var errno = Rename(full, target);
                if (errno == Exdev) CopyAcross(full, target);
                else if (errno != 0) throw new IOException(new System.ComponentModel.Win32Exception(errno).Message); // the system's reason, as other I/O refusals
                AppLog.Warn("trash", $"{full} moved to {target}");
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(infoFile); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                error = NotTrashedPrefix + ex.Message;
                return null;
            }
        }
        error = NotTrashedPrefix + "no free name in " + trash;
        return null;
    }

    /// <summary>A <c>.trashinfo</c> file's text: the path percent-encoded (RFC 2396, '/' kept) and the local deletion time.</summary>
    public static string TrashInfo(string path, DateTime deletedAt)
        => "[Trash Info]\nPath=" + EncodePath(path) + "\nDeletionDate=" + deletedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

    /// <summary>Percent-encodes a path for a <c>.trashinfo</c> (UTF-8 bytes; unreserved characters and '/' kept).</summary>
    public static string EncodePath(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~' or '!' or '*' or '\'' or '(' or ')'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The mount point <paramref name="path"/> is on, from <c>/proc/self/mountinfo</c> ("/" when unreadable).</summary>
    public static string MountPointOf(string path)
    {
        try { return MountPointOf(path, File.ReadLines("/proc/self/mountinfo")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "/"; }
    }

    /// <summary>The longest mount point in <c>mountinfo</c> lines that contains <paramref name="path"/>.</summary>
    public static string MountPointOf(string path, IEnumerable<string> mountinfo)
    {
        var full = Path.GetFullPath(path);
        var best = "/";
        foreach (var line in mountinfo)
        {
            var fields = line.Split(' ');
            if (fields.Length < 5) continue;
            var mount = Unescape(fields[4]);
            var inside = mount == "/" || full == mount || full.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (inside && mount.Length > best.Length) best = mount;
        }
        return best;
    }

    // mountinfo escapes space, tab, newline and backslash as \040 \011 \012 \134.
    private static string Unescape(string field)
        => field.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    private static string CurrentUid()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Environment.UserName;
    }
}
