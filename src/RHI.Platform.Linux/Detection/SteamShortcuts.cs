using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// One non-Steam game in a Steam user's <c>config/shortcuts.vdf</c> (#176): Steam's id for it, its name, the program and
/// folder Steam starts it with. <see cref="GameId"/> is the id <c>steam://rungameid/</c> takes for a shortcut.
/// </summary>
internal sealed record SteamShortcut(uint AppId, string Name, string Exe, string? StartDir)
{
    /// <summary><c>(appid &lt;&lt; 32) | 0x02000000</c>: Steam's 64-bit game id of a shortcut (type 2 = shortcut).</summary>
    public ulong GameId => ((ulong)AppId << 32) | 0x02000000UL;
}

/// <summary>
/// Reads Steam's non-Steam shortcuts (#176): <c>&lt;Steam root&gt;/userdata/&lt;user&gt;/config/shortcuts.vdf</c>, Valve's
/// binary key/value format (a map named <c>shortcuts</c> holding one map per shortcut; key case varies between Steam
/// versions). Read only; a file that is too big or malformed is a scan warning and yields nothing.
/// </summary>
internal static class SteamShortcuts
{
    /// <summary>A shortcuts file larger than this is not read (Steam's are a few KiB).</summary>
    internal const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxDepth = 16;
    private const int MaxUsers = 64;

    /// <summary>Every user's shortcuts under <paramref name="steamRoot"/>, in user-folder order.</summary>
    public static IReadOnlyList<SteamShortcut> ReadAll(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        string[] users;
        try { users = Directory.GetDirectories(userdata).Order(StringComparer.Ordinal).Take(MaxUsers).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DetectorDiagnostics.Warn($"Steam user folders unreadable: {userdata}; non-Steam shortcuts not listed", ex);
            return [];
        }
        var shortcuts = new List<SteamShortcut>();
        foreach (var user in users)
        {
            var file = Path.Combine(user, "config", "shortcuts.vdf");
            if (!File.Exists(file)) continue;
            // One bad file is a warning and never fails the Steam detector (w6 review 2): whatever reading or parsing it
            // throws, the other users' shortcuts and every Steam app are still listed.
            try
            {
                if (ReadFaultForTesting.Value?.Invoke(file) is { } fault) throw fault;
                if (ReadBounded(file) is { } data) shortcuts.AddRange(Parse(data));
            }
            catch (Exception ex)
            {
                DetectorDiagnostics.Warn($"Steam shortcuts file {file} skipped", ex);
            }
        }
        return shortcuts;
    }

    /// <summary>Test seam: throws the exception it returns while one user's file is read (null = read it).</summary>
    internal static readonly AsyncLocal<Func<string, Exception?>?> ReadFaultForTesting = new();

    /// <summary>
    /// The file's bytes, or null (with a scan warning) when it is not a regular file (a FIFO would block the scan, a
    /// device never ends; a link is judged by what it leads to), is empty, or is larger than <see cref="MaxFileBytes"/>.
    /// Read through a stream that stops one byte past the limit, so a file that grows after the size check is still
    /// bounded.
    /// </summary>
    internal static byte[]? ReadBounded(string file)
    {
        if (FileKind.IsRegularFile(file) == false)
        {
            DetectorDiagnostics.Warn($"Steam shortcuts file {file} is not a regular file; not read");
            return null;
        }
        FileSystemInfo info = new FileInfo(file);
        if (info.LinkTarget is not null) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        if (info is not FileInfo { Exists: true } target) return null;
        if (target.Length == 0)
        {
            DetectorDiagnostics.Warn($"Steam shortcuts file {file} is empty; not read");
            return null;
        }
        if (target.Length > MaxFileBytes)
        {
            DetectorDiagnostics.Warn($"Steam shortcuts file {file} is larger than {MaxFileBytes} bytes; not read");
            return null;
        }
        using var stream = new FileStream(target.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (ReadAtMost(stream, MaxFileBytes) is { } data) return data;
        DetectorDiagnostics.Warn($"Steam shortcuts file {file} grew past {MaxFileBytes} bytes while it was read; not read");
        return null;
    }

    /// <summary>At most <paramref name="limit"/> bytes of <paramref name="stream"/>; null when it holds more (reads limit + 1 at most).</summary>
    internal static byte[]? ReadAtMost(Stream stream, long limit)
    {
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        while (data.Length <= limit)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit + 1 - data.Length));
            if (read == 0) return data.ToArray();
            data.Write(buffer, 0, read);
        }
        return null;
    }

    /// <summary>The shortcuts in one file's bytes; throws <see cref="InvalidDataException"/> on a malformed file.</summary>
    internal static IReadOnlyList<SteamShortcut> Parse(byte[] data)
    {
        var position = 0;
        var root = ReadMap(data, ref position, 0);
        if (root.FirstOrDefault(pair => pair.Key.Equals("shortcuts", StringComparison.OrdinalIgnoreCase)).Value is not Dictionary<string, object> list)
            return [];
        var shortcuts = new List<SteamShortcut>();
        foreach (var entry in list.Values.OfType<Dictionary<string, object>>())
        {
            var name = Text(entry, "AppName");
            var exe = Unquote(Text(entry, "Exe"));
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exe)) continue;
            // Older files carry no appid: Steam then derives it from the program and name.
            var appId = Value(entry, "appid") switch
            {
                int signed => unchecked((uint)signed),
                _ => LegacyAppId(Text(entry, "Exe") ?? "", name)
            };
            shortcuts.Add(new SteamShortcut(appId, name.Trim(), exe, Unquote(Text(entry, "StartDir"))));
        }
        return shortcuts;
    }

    /// <summary>Steam's id for a shortcut file without one: CRC-32 of the quoted program and the name, top bit set.</summary>
    internal static uint LegacyAppId(string exe, string name)
        => ReShadeArchiveExtractor.Crc32.Compute(Encoding.UTF8.GetBytes(exe + name)) | 0x80000000;

    private static object? Value(Dictionary<string, object> map, string key)
        => map.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(Dictionary<string, object> map, string key) => Value(map, key) as string;

    private static string? Unquote(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    // Binary key/values: <type byte> <NUL-terminated key> <value>, a map ends with 0x08 (0x0B in some writers).
    private static Dictionary<string, object> ReadMap(byte[] data, ref int position, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Steam shortcuts file nests too deeply.");
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        while (true)
        {
            var type = Byte(data, ref position);
            if (type is 0x08 or 0x0B) return map;
            var key = CString(data, ref position);
            object value = type switch
            {
                0x00 => ReadMap(data, ref position, depth + 1),
                0x01 => CString(data, ref position),
                0x02 => BitConverter.ToInt32(Take(data, ref position, 4)),
                0x03 => BitConverter.ToSingle(Take(data, ref position, 4)),
                0x04 or 0x06 => BitConverter.ToInt32(Take(data, ref position, 4)),
                0x05 => WideString(data, ref position),
                0x07 => BitConverter.ToUInt64(Take(data, ref position, 8)),
                0x0A => BitConverter.ToInt64(Take(data, ref position, 8)),
                _ => throw new InvalidDataException($"Steam shortcuts file has an unknown value type 0x{type:X2}.")
            };
            map[key] = value;
        }
    }

    private static byte Byte(byte[] data, ref int position)
        => position < data.Length ? data[position++] : throw new InvalidDataException("Steam shortcuts file ends early.");

    private static ReadOnlySpan<byte> Take(byte[] data, ref int position, int count)
    {
        if (position + count > data.Length) throw new InvalidDataException("Steam shortcuts file ends early.");
        var span = data.AsSpan(position, count);
        position += count;
        return span;
    }

    private static string CString(byte[] data, ref int position)
    {
        var end = Array.IndexOf(data, (byte)0, position);
        if (end < 0) throw new InvalidDataException("Steam shortcuts file ends inside a text value.");
        var text = Encoding.UTF8.GetString(data, position, end - position);
        position = end + 1;
        return text;
    }

    private static string WideString(byte[] data, ref int position)
    {
        var start = position;
        while (true)
        {
            if (position + 1 >= data.Length) throw new InvalidDataException("Steam shortcuts file ends inside a text value.");
            if (data[position] == 0 && data[position + 1] == 0) break;
            position += 2;
        }
        var text = Encoding.Unicode.GetString(data, start, position - start);
        position += 2;
        return text;
    }
}
