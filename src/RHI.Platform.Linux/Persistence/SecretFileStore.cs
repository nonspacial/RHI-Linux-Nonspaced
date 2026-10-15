using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Persistence;

/// <summary>What <see cref="SecretFileStore.Read"/> found.</summary>
public enum SecretFileState
{
    /// <summary>No file: nothing saved.</summary>
    Missing,

    /// <summary>A well-formed secret, private to the user (mode 0600, restricted first when it was not).</summary>
    Present,

    /// <summary>Other users can read or write the file and RHI could not restrict it: the secret is not used.</summary>
    Exposed,

    /// <summary>The file holds no usable secret (blank, too long, or with whitespace inside).</summary>
    Malformed,

    /// <summary>The file could not be read (permissions, I/O error); logged.</summary>
    Unreadable
}

/// <summary>A read of a secret file; <see cref="Value"/> is set only for <see cref="SecretFileState.Present"/>.</summary>
/// <param name="Restricted">True when the file was readable by other users and RHI set it to 0600 on this read.</param>
public sealed record SecretFileRead(SecretFileState State, string? Value, bool Restricted = false);

/// <summary>
/// One secret (an API key or access token) in a file private to the user — the one 0600 secret-file store
/// (GitHub token #163, Nexus Mods API key #99). Writes go through <see cref="AtomicFile.WriteStoreFile"/> with mode
/// 0600 (the temporary file is created 0600, so the secret is never readable by others, even briefly); a symlinked
/// file stays a link and the file it ends at is replaced. A file other users can access is restricted to 0600 before
/// the secret is used and is not used at all when that fails. Clearing deletes the file. The secret itself is never
/// logged and never part of an exception message.
/// </summary>
public sealed class SecretFileStore
{
    /// <summary>Longest secret accepted (GitHub tokens are under 100 characters, Nexus keys under 400).</summary>
    public const int MaxLength = 512;

    /// <summary>Owner read/write only.</summary>
    public const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Group/other permission bits; any of them set exposes the secret to other local users.</summary>
    public const UnixFileMode ExposingBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _logArea;

    /// <param name="path">The secret file.</param>
    /// <param name="logArea">Area name for log lines (never with the secret).</param>
    public SecretFileStore(string path, string logArea = "secret-file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        _logArea = logArea;
    }

    public string FilePath { get; }

    /// <summary>Test seam: chmod (production: <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>).</summary>
    internal Action<string, UnixFileMode> SetUnixFileMode { get; set; } = static (path, mode) =>
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    };

    /// <summary>A usable secret: not blank, at most <see cref="MaxLength"/> characters, no whitespace or control characters.</summary>
    public static bool IsWellFormed(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxLength && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>Whether a file is saved (whatever it holds).</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>Reads the secret (surrounding whitespace trimmed), restricting an exposed file to 0600 first.</summary>
    public SecretFileRead Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new SecretFileRead(SecretFileState.Missing, null);
            var restricted = false;
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(FilePath) & ExposingBits) != 0)
            {
                try
                {
                    SetUnixFileMode(FilePath, PrivateMode);
                    restricted = (File.GetUnixFileMode(FilePath) & ExposingBits) == 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn(_logArea, $"Could not restrict {FilePath} to mode 0600; its contents are not used", ex);
                }
                if (!restricted) return new SecretFileRead(SecretFileState.Exposed, null);
            }

            var value = File.ReadAllText(FilePath).Trim();
            return IsWellFormed(value)
                ? new SecretFileRead(SecretFileState.Present, value, restricted)
                : new SecretFileRead(SecretFileState.Malformed, null, restricted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn(_logArea, $"Could not read {FilePath}", ex);
            return new SecretFileRead(SecretFileState.Unreadable, null);
        }
    }

    /// <summary>
    /// The saved text (trimmed) for masking it out of text that leaves the machine (#192 support bundle), whatever the file's
    /// mode or shape; null when there is none or it cannot be read. Never changes the file and never logs the value.
    /// </summary>
    public string? ValueForMasking()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var info = new FileInfo(FilePath);
            if (info.Length > MaxLength * 4L) return null;
            var value = File.ReadAllText(FilePath).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves <paramref name="secret"/> atomically and durably at mode 0600 (throws an <see cref="IOException"/> when it cannot).</summary>
    public void Write(string secret)
    {
        if (!IsWellFormed(secret)) throw new ArgumentException("The secret is blank, too long or contains whitespace.", nameof(secret));
        AtomicFile.WriteStoreFile(FilePath, Encoding.UTF8.GetBytes(secret), PrivateMode);
    }

    /// <summary>Deletes the file; true when one was removed, false when none was saved. Throws when it cannot be removed.</summary>
    public bool Delete()
    {
        if (!File.Exists(FilePath) && !IsDanglingLink(FilePath)) return false;
        File.Delete(FilePath);
        AtomicFile.FsyncDirectoryOf(FilePath);
        return true;
    }

    private static bool IsDanglingLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch (IOException) { return false; }
    }
}
