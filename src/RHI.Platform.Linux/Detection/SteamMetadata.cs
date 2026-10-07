using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Steam's text KeyValues (<c>config.vdf</c>, <c>appmanifest_*.acf</c>): quoted or bare tokens, nested
/// <c>{ }</c> blocks, <c>//</c> comments. Keys are case-insensitive, as Steam treats them.
/// </summary>
internal sealed class SteamKeyValues
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SteamKeyValues> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SteamKeyValues? Child(params string[] path)
    {
        var current = this;
        foreach (var key in path)
            if (!current.Children.TryGetValue(key, out current!)) return null;
        return current;
    }

    public string? Value(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public static SteamKeyValues Parse(string text)
    {
        var root = new SteamKeyValues();
        var stack = new Stack<SteamKeyValues>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;
        while (true)
        {
            var token = NextToken(text, ref i, out var quoted);
            if (token is null) break;
            if (!quoted && token == "{")
            {
                var child = new SteamKeyValues();
                if (pendingKey is not null) stack.Peek().Children[pendingKey] = child;
                stack.Push(child);
                pendingKey = null;
            }
            else if (!quoted && token == "}")
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
            }
            else if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Values[pendingKey] = token;
                pendingKey = null;
            }
        }
        return root;
    }

    private static string? NextToken(string text, ref int i, out bool quoted)
    {
        quoted = false;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= text.Length) return null;
        if (text[i] is '{' or '}') return text[i++].ToString();

        var builder = new StringBuilder();
        if (text[i] == '"')
        {
            quoted = true;
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    builder.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                }
                else builder.Append(text[i]);
                i++;
            }
            i++; // closing quote
            return builder.ToString();
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
            builder.Append(text[i++]);
        return builder.ToString();
    }
}

/// <summary>
/// The parts of Steam's binary <c>appcache/appinfo.vdf</c> that classify an installed app: its type
/// (Game, Tool, Application, Config, DLC…) and the platforms it ships for. Supports format versions
/// 27, 28 and 29 (29 stores keys in a string table at the end of the file). Unknown versions or a
/// damaged file yield nothing, and callers fall back to manifest-only rules.
/// </summary>
internal static class SteamAppInfo
{
    internal sealed record Entry(string? Type, string? OsList);

    private const uint V27 = 0x07564427, V28 = 0x07564428, V29 = 0x07564429;

    public static IReadOnlyDictionary<int, Entry> Read(string path, IReadOnlySet<int> appIds)
    {
        var result = new Dictionary<int, Entry>();
        if (appIds.Count == 0 || !File.Exists(path)) return result;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var magic = reader.ReadUInt32();
            if (magic is not (V27 or V28 or V29)) return result;
            reader.ReadUInt32(); // universe
            string[]? strings = null;
            if (magic == V29)
            {
                var tableOffset = reader.ReadInt64();
                var entriesStart = stream.Position;
                stream.Position = tableOffset;
                var count = reader.ReadUInt32();
                if (count > 1_000_000) return result;
                strings = new string[count];
                for (var n = 0; n < count; n++) strings[n] = ReadCString(reader);
                stream.Position = entriesStart;
            }

            while (result.Count < appIds.Count)
            {
                var appId = reader.ReadUInt32();
                if (appId == 0) break;
                var size = reader.ReadUInt32();
                var end = stream.Position + size;
                if (end > stream.Length) break;
                if (appIds.Contains((int)appId))
                {
                    // infoState, lastUpdated, picsToken, text SHA-1, changeNumber, binary SHA-1 (v28+).
                    stream.Position += 4 + 4 + 8 + 20 + 4 + (magic == V27 ? 0 : 20);
                    var root = ReadObject(reader, strings, end);
                    var common = Find(root, "appinfo", "common");
                    result[(int)appId] = new Entry(
                        common?.GetValueOrDefault("type") as string,
                        common?.GetValueOrDefault("oslist") as string);
                }
                stream.Position = end;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException or UnauthorizedAccessException)
        {
            // A damaged or newer cache only loses the extra classification; keep what was read.
        }
        return result;
    }

    private static Dictionary<string, object>? Find(Dictionary<string, object> root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
            if (current.GetValueOrDefault(key) is not Dictionary<string, object> next) return null;
            else current = next;
        return current;
    }

    private static Dictionary<string, object> ReadObject(BinaryReader reader, string[]? strings, long end)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (reader.BaseStream.Position < end)
        {
            var type = reader.ReadByte();
            if (type is 0x08 or 0x0B) return result;
            var key = strings is null ? ReadCString(reader) : strings[reader.ReadInt32()];
            object value = type switch
            {
                0x00 => ReadObject(reader, strings, end),
                0x01 => ReadCString(reader),
                0x02 or 0x03 or 0x04 or 0x06 => reader.ReadInt32(),
                0x07 or 0x0A => reader.ReadInt64(),
                _ => throw new InvalidDataException($"appinfo value type {type}")
            };
            result[key] = value;
        }
        return result;
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = reader.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
