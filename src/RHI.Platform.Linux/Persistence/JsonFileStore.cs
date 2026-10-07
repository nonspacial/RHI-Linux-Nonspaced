using System.Text;
using System.Text.Json;

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
    public JsonStoreUnavailableException(string filePath, string? quarantinePath)
        : base(quarantinePath is null
            ? $"{filePath} cannot be read and could not be copied aside. RHI will not overwrite it; fix or delete the file."
            : $"{filePath} cannot be read; a copy was kept at {quarantinePath}. RHI will not overwrite it; fix or delete the file.")
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
/// hear about it once. Notices raised before the UI attached are replayed to it.
/// </summary>
public static class JsonStoreQuarantine
{
    private const int MaxPending = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Mtime, long Length, string? Copy)> Known = new(StringComparer.Ordinal);
    private static readonly List<JsonStoreQuarantineNotice> Pending = [];
    private static readonly List<Action<JsonStoreQuarantineNotice>> Listeners = [];

    /// <summary>Attaches a listener (any thread may call it back) and replays notices raised so far.</summary>
    public static IDisposable Listen(Action<JsonStoreQuarantineNotice> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        JsonStoreQuarantineNotice[] replay;
        lock (Gate)
        {
            Listeners.Add(listener);
            replay = Pending.ToArray();
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
            Pending.Add(notice);
            if (Pending.Count > MaxPending) Pending.RemoveAt(0);
            listeners = Listeners.ToArray();
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
                    DurableFile.WriteNew(target, bytes);
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
/// durable (<see cref="DurableFile"/>). Callers keep their own lock around load-modify-save.
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
            ? bytes => JsonSerializer.Deserialize<T>(bytes, serializerOptions)
            : bytes => parse(Encoding.UTF8.GetString(bytes));
        _serialize = value => JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
    }

    public string FilePath { get; }

    public JsonStoreLoad<T> Load()
    {
        byte[] bytes;
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
        if (IsBlank(bytes))
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
        DurableFile.WriteAllBytes(FilePath, _serialize(value));
        JsonStoreQuarantine.Forget(FilePath);
    }

    private bool WritesRefused => _kind == JsonStoreKind.UserData;

    private static bool IsBlank(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        return true;
    }
}
