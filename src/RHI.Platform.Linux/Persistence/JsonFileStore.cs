using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Persistence;

public enum JsonStoreLoadStatus
{
    /// <summary>No file (or an empty one): nothing the user saved can be lost by writing.</summary>
    Missing,
    Loaded,
    /// <summary>The file exists but cannot be read or parsed. It has been copied aside when possible.</summary>
    Unreadable
}

/// <summary>What a store does once its file turned out unreadable.</summary>
public enum JsonStoreKind
{
    /// <summary>User data: the file is copied aside and never overwritten until it reads again (fixed or deleted).</summary>
    UserData,
    /// <summary>Rebuildable cache: the file is copied aside, then a fresh copy may replace it.</summary>
    Cache
}

public readonly record struct JsonStoreLoad<T>(JsonStoreLoadStatus Status, T? Value, string? QuarantinePath) where T : class
{
    public bool IsUnreadable => Status == JsonStoreLoadStatus.Unreadable;
}

/// <summary>Raised once per unreadable file version so the user can be told which file was set aside.</summary>
/// <param name="FilePath">The store file that could not be read.</param>
/// <param name="QuarantinePath">The copy holding its original bytes; null when even a copy was impossible.</param>
/// <param name="WritesRefused">True for user data (RHI will not save over it); false for caches that are rebuilt.</param>
public sealed record JsonStoreQuarantineNotice(string FilePath, string? QuarantinePath, bool WritesRefused);

/// <summary>A save was refused because the file on disk is unreadable and holds the user's data.</summary>
public sealed class JsonStoreUnavailableException : IOException
{
    public JsonStoreUnavailableException(string filePath, string? quarantinePath, Exception? inner = null)
        : base(quarantinePath is null
            ? $"{filePath} cannot be read and could not be copied aside. RHI will not overwrite it; fix or delete the file."
            : $"{filePath} cannot be read; a copy was kept at {quarantinePath}. RHI will not overwrite it; fix or delete the file.",
            inner)
    {
        FilePath = filePath;
        QuarantinePath = quarantinePath;
    }

    public string FilePath { get; }
    public string? QuarantinePath { get; }
}

/// <summary>
/// Process-wide record of quarantined store files. A file version (path, mtime, length) is copied
/// aside at most once, an identical earlier copy is reused rather than duplicated, and listeners
/// hear about it once. Notices raised while nobody listened are kept (at most 32) and handed to
/// the next listener only: once delivered they are dropped, so a later listener (another view
/// model in the same process) never receives them again.
/// </summary>
public static class JsonStoreQuarantine
{
    private const int MaxPending = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Mtime, long Length, string? Copy)> Known = new(StringComparer.Ordinal);
    private static readonly List<JsonStoreQuarantineNotice> Pending = [];
    private static readonly List<Action<JsonStoreQuarantineNotice>> Listeners = [];

    /// <summary>
    /// Attaches a listener (any thread may call it back) and hands it the notices nobody has
    /// received yet. Dispose the result to detach it.
    /// </summary>
    public static IDisposable Listen(Action<JsonStoreQuarantineNotice> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        JsonStoreQuarantineNotice[] replay;
        lock (Gate)
        {
            Listeners.Add(listener);
            replay = Pending.ToArray();
            Pending.Clear();
        }
        foreach (var notice in replay) listener(notice);
        return new Subscription(listener);
    }

    /// <summary>
    /// Copies an unreadable file aside (once per file version) and returns the copy, or null when
    /// the file could not even be read. <paramref name="bytes"/> are the bytes that failed to parse.
    /// </summary>
    internal static string? Quarantine(string path, byte[]? bytes, bool writesRefused)
    {
        var fullPath = Path.GetFullPath(path);
        (DateTime Mtime, long Length) stamp;
        try
        {
            var info = new FileInfo(fullPath);
            stamp = (info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = (DateTime.MinValue, -1);
        }

        JsonStoreQuarantineNotice notice;
        Action<JsonStoreQuarantineNotice>[] listeners;
        lock (Gate)
        {
            if (Known.TryGetValue(fullPath, out var known) && known.Mtime == stamp.Mtime && known.Length == stamp.Length)
                return known.Copy;

            var copy = bytes is null ? null : CopyAside(fullPath, bytes);
            Known[fullPath] = (stamp.Mtime, stamp.Length, copy);
            notice = new JsonStoreQuarantineNotice(fullPath, copy, writesRefused);
            listeners = Listeners.ToArray();
            if (listeners.Length == 0)
            {
                // Kept for the first listener to attach (e.g. settings read before the UI exists).
                Pending.Add(notice);
                if (Pending.Count > MaxPending) Pending.RemoveAt(0);
            }
        }

        foreach (var listener in listeners) listener(notice);
        return notice.QuarantinePath;
    }

    /// <summary>The file reads again (fixed, replaced or deleted): a later corruption is a new event.</summary>
    internal static void Forget(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (Gate) Known.Remove(fullPath);
    }

    /// <summary>
    /// Renames a file RHI could not read (or copy) aside to <c>&lt;file&gt;.corrupt-&lt;utc&gt;</c>:
    /// every byte is kept without having to read it. Returns the new name; throws
    /// <see cref="JsonStoreUnavailableException"/> when even the rename is impossible.
    /// </summary>
    internal static string MoveAside(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var baseName = fullPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        Exception? last = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var target = attempt == 0 ? baseName : $"{baseName}-{attempt}";
            if (File.Exists(target)) continue;
            try
            {
                File.Move(fullPath, target, overwrite: false);
                Forget(fullPath);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (!File.Exists(target)) break; // not a name race: the rename itself is impossible
            }
        }
        throw new JsonStoreUnavailableException(fullPath, null, last);
    }

    private static string? CopyAside(string fullPath, byte[] bytes)
    {
        try
        {
            // An earlier run may already hold these exact bytes; a second copy would only clutter.
            var directory = Path.GetDirectoryName(fullPath)!;
            var prefix = Path.GetFileName(fullPath) + ".corrupt-";
            foreach (var existing in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                var info = new FileInfo(existing);
                if (info.Length == bytes.Length && File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
                    return existing;
            }

            var baseName = fullPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var target = attempt == 0 ? baseName : $"{baseName}-{attempt}";
                if (File.Exists(target)) continue;
                try
                {
                    AtomicFile.WriteNew(target, bytes);
                    return target;
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Lost a race for this name; try the next suffix.
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported to the caller as "no copy" (null): writes stay refused for user data.
            return null;
        }
    }

    private sealed class Subscription(Action<JsonStoreQuarantineNotice> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate) Listeners.Remove(listener);
        }
    }
}

/// <summary>
/// One JSON file holding a store's whole state. Missing (or empty) means a fresh default; a file
/// that cannot be read or parsed is copied aside to <c>&lt;file&gt;.corrupt-&lt;utc timestamp&gt;</c>
/// and, for user data, never overwritten: <see cref="Save"/> throws
/// <see cref="JsonStoreUnavailableException"/> until the file reads again. Saves are atomic and
/// durable (<see cref="AtomicFile.WriteStoreFile"/>). Callers keep their own lock around load-modify-save.
/// <para>A leading UTF-8 byte order mark (editors offer to add one) is ignored, as
/// <see cref="File.ReadAllText(string)"/> did before.</para>
/// <para>Reading is fail-closed: content the parser or the <c>parse:</c> callback rejects counts as
/// unreadable. That includes values a newer build may write and this one does not know (an unknown
/// enum member, a higher schema version): an older build then copies such a file aside and refuses
/// to save over it rather than silently dropping what it cannot represent.</para>
/// </summary>
public sealed class JsonFileStore<T> where T : class
{
    private readonly Func<byte[], T?> _parse;
    private readonly Func<T, byte[]> _serialize;
    private readonly JsonStoreKind _kind;

    public JsonFileStore(
        string path,
        JsonSerializerOptions? options = null,
        JsonStoreKind kind = JsonStoreKind.UserData,
        Func<string, T?>? parse = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        _kind = kind;
        var serializerOptions = options ?? new JsonSerializerOptions { WriteIndented = true };
        _parse = parse is null
            ? bytes => JsonSerializer.Deserialize<T>(WithoutBom(bytes), serializerOptions)
            : bytes => parse(Encoding.UTF8.GetString(WithoutBom(bytes)));
        _serialize = value => JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
    }

    public string FilePath { get; }

    public JsonStoreLoad<T> Load() => Read(out _);

    /// <summary>
    /// Loads the file and hands back the exact bytes the result was parsed from (null when the
    /// file is missing), so a staged change can later check nothing rewrote the file meanwhile.
    /// </summary>
    private JsonStoreLoad<T> Read(out byte[]? bytes)
    {
        bytes = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                JsonStoreQuarantine.Forget(FilePath);
                return new(JsonStoreLoadStatus.Missing, null, null);
            }
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present but unreadable (permissions, I/O error): nothing can be copied, and nothing
            // may be written over it either.
            return new(JsonStoreLoadStatus.Unreadable, null, JsonStoreQuarantine.Quarantine(FilePath, null, WritesRefused));
        }

        // Zero bytes or whitespace holds no user data (an interrupted non-atomic write of an older
        // build); treating it as missing loses nothing and avoids blocking every later save.
        if (IsBlank(WithoutBom(bytes)))
        {
            JsonStoreQuarantine.Forget(FilePath);
            return new(JsonStoreLoadStatus.Missing, null, null);
        }

        T? value;
        try { value = _parse(bytes); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any parse/validation failure of the stored bytes means "unreadable"; the bytes are kept.
            value = null;
        }

        if (value is null)
            return new(JsonStoreLoadStatus.Unreadable, null, JsonStoreQuarantine.Quarantine(FilePath, bytes, WritesRefused));

        JsonStoreQuarantine.Forget(FilePath);
        return new(JsonStoreLoadStatus.Loaded, value, null);
    }

    /// <summary>The loaded value, or <paramref name="fallback"/> when the file is missing or unreadable.</summary>
    public T LoadOr(Func<T> fallback) => Load().Value ?? fallback();

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> when a save would replace unreadable user data.</summary>
    public void EnsureWritable()
    {
        if (_kind != JsonStoreKind.UserData) return;
        var current = Load();
        if (current.IsUnreadable)
            throw new JsonStoreUnavailableException(Path.GetFullPath(FilePath), current.QuarantinePath);
    }

    /// <summary>Atomically replaces the file. Refused while unreadable user data is on disk.</summary>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureWritable();
        if (_kind == JsonStoreKind.Cache) Load(); // copies an unreadable cache aside before replacing it
        AtomicFile.WriteStoreFile(FilePath, _serialize(value));
        JsonStoreQuarantine.Forget(FilePath);
    }

    /// <summary>
    /// Prepares a change for <see cref="StagedFileCommit"/> (several files written all or nothing):
    /// <paramref name="change"/> edits the loaded value (null when the file is missing) and returns
    /// the value to write, or null when there is nothing to change. Nothing is written here.
    /// Throws <see cref="JsonStoreUnavailableException"/> when the file holds unreadable user data.
    /// </summary>
    public StagedFileWrite? Stage(Func<T?, T?> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = Read(out var bytes);
        if (current.IsUnreadable && _kind == JsonStoreKind.UserData)
            throw new JsonStoreUnavailableException(Path.GetFullPath(FilePath), current.QuarantinePath);
        var next = change(current.Value);
        return next is null ? null : new StagedFileWrite(Path.GetFullPath(FilePath), bytes, _serialize(next));
    }

    /// <summary>
    /// Removes the file for an explicit user reset without losing it: a file that reads, or whose
    /// unreadable bytes were already copied aside, is deleted; one that could not even be copied is
    /// renamed aside instead. Throws <see cref="JsonStoreUnavailableException"/> (nothing deleted)
    /// when that rename fails too.
    /// </summary>
    public void ResetKeepingUnreadable()
    {
        var current = Load();
        if (!File.Exists(FilePath)) return;
        if (current.IsUnreadable && current.QuarantinePath is null)
        {
            JsonStoreQuarantine.MoveAside(FilePath);
            return;
        }
        File.Delete(FilePath);
        JsonStoreQuarantine.Forget(FilePath);
    }

    private bool WritesRefused => _kind == JsonStoreKind.UserData;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] WithoutBom(byte[] bytes)
        => bytes.AsSpan().StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;

    private static bool IsBlank(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        return true;
    }
}
