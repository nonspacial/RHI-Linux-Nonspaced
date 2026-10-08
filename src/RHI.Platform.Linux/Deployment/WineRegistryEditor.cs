using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The one writer of a Wine prefix's registry hive files (#24, DEP-05). Every edit RHI makes to
/// <c>user.reg</c> goes through <see cref="EditUserHive{T}"/>; <c>system.reg</c> has no RHI writer, and one added
/// later must come here too. An edit:
/// <list type="number">
/// <item>is refused while a wineserver runs for the prefix: wineserver holds the registry in memory and rewrites the
/// hive when it flushes or exits, so an edit made meanwhile is lost (the record would then claim a value that is gone)
/// or overwrites Wine's own changes;</item>
/// <item>holds the prefix's in-process gate and an exclusive <c>flock</c> on <c>user.reg.rhi-lock</c>, so two RHI
/// tasks or processes never read-modify-write the same hive at once;</item>
/// <item>reads and rewrites the file byte-exactly (Latin-1 maps every byte to one char; Wine itself writes ASCII and
/// escapes everything else, see <see cref="EscapeKeyName"/>);</item>
/// <item>copies the hive as it was to <c>user.reg.rhi-backup-&lt;utc&gt;</c> before each change (the newest
/// <see cref="BackupsKept"/> are kept), then replaces it through <see cref="AtomicFile.ReplaceUserFile"/>: unique
/// temp, fsync, full read-back verify, rename, folder fsync. The hive is never truncated in place.</item>
/// </list>
/// </summary>
public sealed class WineRegistryEditor
{
    public const string UserHiveName = "user.reg";
    public const string LockSuffix = ".rhi-lock";
    public const string BackupMarker = ".rhi-backup-";
    public const int BackupsKept = 3;

    // Fixed text first, the prefix path last (PT-BR presentation pairs match the fixed part).
    public const string PrefixInUseRefusalPrefix =
        "Wine is running in this prefix and rewrites its registry when it stops, so RHI changed nothing. Close the game and anything else using this prefix, wait a few seconds and try again. Prefix: ";
    public const string BusyRefusalPrefix =
        "Another RHI task is changing this prefix's Wine registry, so RHI changed nothing. Try again in a moment. Prefix: ";
    public const string MissingHiveMessage =
        "Wine/Proton user.reg was not found. Start the game once so the prefix is initialized.";

    /// <summary>How long an edit waits for another RHI task or process to finish with the same prefix.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly Encoding ByteExact = Encoding.Latin1;

    private readonly Func<string, bool> _isPrefixInUse;
    private readonly TimeSpan _lockTimeout;

    /// <param name="isPrefixInUse">Test seam; default: a wineserver for the prefix in <c>/proc</c> (<see cref="WineServerProbe"/>).</param>
    public WineRegistryEditor(Func<string, bool>? isPrefixInUse = null, TimeSpan? lockTimeout = null)
    {
        _isPrefixInUse = isPrefixInUse ?? (prefix => WineServerProbe.IsRunningFor(prefix));
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>Test seam: runs inside the lock, after the in-use check, before the hive is read.</summary>
    internal Action<string>? InsideLockForTesting { get; set; }

    public static string UserHivePath(string prefixPath) => Path.Combine(prefixPath, UserHiveName);

    public static bool HasUserHive(string? prefixPath)
        => !string.IsNullOrWhiteSpace(prefixPath) && File.Exists(UserHivePath(prefixPath));

    public bool IsPrefixInUse(string prefixPath) => _isPrefixInUse(prefixPath);

    /// <summary>
    /// Why an edit of <paramref name="prefixPath"/>'s registry would be refused right now, or null. Services call it
    /// before their first change, so a refusal leaves the game folder untouched as well; the edit itself checks again.
    /// </summary>
    public string? EditRefusal(string? prefixPath)
    {
        if (string.IsNullOrWhiteSpace(prefixPath) || !Directory.Exists(prefixPath)) return null;
        return _isPrefixInUse(prefixPath) ? PrefixInUseRefusalPrefix + prefixPath : null;
    }

    /// <summary>
    /// Waits until no wineserver runs for the prefix (a tool such as winetricks has just used it and wineserver
    /// lingers a few seconds after the last Wine process). False when it is still running at the deadline.
    /// </summary>
    public async Task<bool> WaitUntilIdleAsync(string prefixPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_isPrefixInUse(prefixPath))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>The hive's text, byte-exact (no lock: a read never changes anything).</summary>
    public static string ReadUserHive(string prefixPath) => ByteExact.GetString(File.ReadAllBytes(UserHivePath(prefixPath)));

    /// <summary>
    /// Read-modify-write of <c>user.reg</c> under the prefix's lock. <paramref name="edit"/> gets the current text and
    /// returns the new text (null or unchanged: nothing is written) and a result. Throws
    /// <see cref="WineRegistryEditRefusedException"/> while Wine runs in the prefix or another edit holds the lock,
    /// <see cref="FileNotFoundException"/> when the prefix has no <c>user.reg</c>.
    /// </summary>
    public T EditUserHive<T>(string prefixPath, Func<string, (string? Text, T Result)> edit)
    {
        var hive = UserHivePath(prefixPath);
        if (!File.Exists(hive)) throw new FileNotFoundException(MissingHiveMessage, hive); // no lock file in an uninitialised prefix
        var gate = Gates.GetOrAdd(PathIdentity.Canonicalize(prefixPath), _ => new SemaphoreSlim(1, 1));
        var deadline = DateTime.UtcNow + _lockTimeout;
        if (!gate.Wait(_lockTimeout))
            throw new WineRegistryEditRefusedException(BusyRefusalPrefix + prefixPath, inUse: false);
        try
        {
            using var fileLock = AcquireFileLock(hive + LockSuffix, prefixPath, deadline);
            if (_isPrefixInUse(prefixPath))
                throw new WineRegistryEditRefusedException(PrefixInUseRefusalPrefix + prefixPath, inUse: true);
            InsideLockForTesting?.Invoke(prefixPath);
            if (!File.Exists(hive)) throw new FileNotFoundException(MissingHiveMessage, hive);

            var before = File.ReadAllBytes(hive);
            var text = ByteExact.GetString(before);
            var (updated, result) = edit(text);
            if (updated is null || string.Equals(updated, text, StringComparison.Ordinal)) return result;

            WriteBackup(hive, before);
            AtomicFile.ReplaceUserFile(hive, ByteExact.GetBytes(updated));
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static FileStream AcquireFileLock(string lockPath, string prefixPath, DateTime deadline)
    {
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive flock on Unix: held until disposed, released by the kernel if RHI dies.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new WineRegistryEditRefusedException(BusyRefusalPrefix + prefixPath, inUse: false, ex);
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>The hive as it was before this edit; older RHI backups beyond <see cref="BackupsKept"/> are removed.</summary>
    private static void WriteBackup(string hive, byte[] before)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backup = hive + BackupMarker + stamp;
        for (var n = 2; File.Exists(backup); n++) backup = $"{hive}{BackupMarker}{stamp}-{n}";
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(hive);
        AtomicFile.WriteNew(backup, before, mode);

        var directory = Path.GetDirectoryName(hive)!;
        var older = Directory.EnumerateFiles(directory, Path.GetFileName(hive) + BackupMarker + "*")
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var stale in older.Take(Math.Max(0, older.Length - BackupsKept)))
            if (!FileCleanup.TryDelete(stale, out var error))
                AppLog.Warn("wine-registry", $"Could not remove an older registry backup: {stale}", error);
    }

    /// <summary>
    /// A registry key name component as wineserver writes it in a hive file (<c>dump_strW</c> with the <c>[]</c>
    /// escape set): <c>\</c>, <c>[</c> and <c>]</c> get a backslash, UTF-16 units above 127 become <c>\x</c> hex
    /// (four digits when a hex digit follows), control characters C escapes or octal. Hive files stay ASCII.
    /// </summary>
    public static string EscapeKeyName(string name)
    {
        const string escapes = ".......abtnvfr.............e....";
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var next = i + 1 < name.Length ? name[i + 1] : '\0';
            if (c > 127)
            {
                builder.Append(Uri.IsHexDigit(next) && i + 1 < name.Length
                    ? string.Create(CultureInfo.InvariantCulture, $"\\x{(int)c:x4}")
                    : string.Create(CultureInfo.InvariantCulture, $"\\x{(int)c:x}"));
                continue;
            }
            if (c < 32)
            {
                if (escapes[c] != '.') builder.Append('\\').Append(escapes[c]);
                else if (i + 1 < name.Length && next is >= '0' and <= '7') builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                else builder.Append('\\').Append(Convert.ToString(c, 8));
                continue;
            }
            if (c is '\\' or '[' or ']') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }
}

/// <summary>A registry edit RHI refused to make: Wine runs in the prefix, or another RHI edit holds it.</summary>
public sealed class WineRegistryEditRefusedException : IOException
{
    public WineRegistryEditRefusedException(string message, bool inUse, Exception? inner = null) : base(message, inner)
        => PrefixInUse = inUse;

    /// <summary>True when Wine runs in the prefix; false when another RHI edit held the lock past the timeout.</summary>
    public bool PrefixInUse { get; }
}
