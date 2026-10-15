using System.Globalization;
using System.Text;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>
/// Read-only view of the string values in a Wine registry hive (<c>system.reg</c> = HKLM, <c>user.reg</c> = HKCU), for
/// store launchers' install records (Refs #90). The hive is read whole (capped); only keys under the requested roots are
/// decoded and kept, matched on the raw bytes first so the rest of a large hive is skipped cheaply. Writing the hive is <see cref="Deployment.WineRegistryEditor"/>'s job alone; this never writes.
/// </summary>
internal sealed class WineRegistryReader
{
    /// <summary>Hives larger than this are not read (a real system.reg is tens of MiB at most).</summary>
    internal const long MaxHiveBytes = 512L * 1024 * 1024;

    private readonly Dictionary<string, Dictionary<string, string>> _keys;

    private WineRegistryReader(Dictionary<string, Dictionary<string, string>> keys) => _keys = keys;

    public static WineRegistryReader Empty { get; } = new(new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The string values of every key at or below one of <paramref name="roots"/> (key paths as the hive spells them, relative
    /// to the hive's root, single backslashes, any case). A missing hive is empty; an unreadable one throws.
    /// </summary>
    public static WineRegistryReader Load(string hivePath, IReadOnlyCollection<string> roots)
    {
        if (!File.Exists(hivePath)) return Empty;
        if (new FileInfo(hivePath).Length > MaxHiveBytes)
            throw new IOException($"Wine registry {hivePath} is larger than {MaxHiveBytes} bytes; not read.");
        return Parse(File.ReadAllBytes(hivePath), roots);
    }

    internal static WineRegistryReader Parse(TextReader reader, IReadOnlyCollection<string> roots)
        => Parse(Encoding.Latin1.GetBytes(reader.ReadToEnd()), roots);

    /// <summary>
    /// One pass over the hive's bytes (Latin-1, one entry per line): a key line is matched against the roots on its raw
    /// bytes (the hive doubles backslashes), so the many keys outside them (Software\Classes…) are never decoded; value
    /// lines are decoded only inside a wanted key.
    /// </summary>
    internal static WineRegistryReader Parse(ReadOnlySpan<byte> hive, IReadOnlyCollection<string> roots)
    {
        var keys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var spelled = roots.Select(root => Encoding.Latin1.GetBytes(root.Replace("\\", "\\\\", StringComparison.Ordinal))).ToArray();
        Dictionary<string, string>? current = null;
        while (!hive.IsEmpty)
        {
            var newline = hive.IndexOf((byte)'\n');
            var bytes = newline < 0 ? hive : hive[..newline];
            hive = newline < 0 ? default : hive[(newline + 1)..];
            if (bytes.IsEmpty) continue;
            if (bytes[0] == (byte)'[')
            {
                var end = bytes.LastIndexOf((byte)']');
                var raw = end > 1 ? bytes[1..end] : default;
                current = null;
                var wanted = false;
                foreach (var root in spelled)
                    if (StartsWithIgnoringAsciiCase(raw, root)) { wanted = true; break; }
                if (!wanted) continue;
                var name = Unescape(Encoding.Latin1.GetString(raw));
                if (!roots.Any(root => IsSameOrBelow(name, root))) continue;
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                keys[name] = current;
                continue;
            }
            if (current is null || bytes[0] is not ((byte)'"' or (byte)'@')) continue;
            if (TryValue(Encoding.Latin1.GetString(bytes), out var valueName, out var value)) current[valueName] = value;
        }
        return new WineRegistryReader(keys);
    }

    // Prefilter only (the decoded name is checked against the roots after it): the raw key text starts with the spelled
    // root and the root ends there or at a (doubled) path separator.
    private static bool StartsWithIgnoringAsciiCase(ReadOnlySpan<byte> raw, byte[] root)
    {
        if (raw.Length < root.Length) return false;
        for (var i = 0; i < root.Length; i++)
            if (AsciiLower(raw[i]) != AsciiLower(root[i])) return false;
        return raw.Length == root.Length || raw[root.Length] == (byte)'\\';
    }

    private static byte AsciiLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    /// <summary>One string value, or null when the key or value is absent.</summary>
    public string? Value(string key, string name)
        => _keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;

    /// <summary>The value names and string values of one key (empty when absent).</summary>
    public IReadOnlyDictionary<string, string> Values(string key)
        => _keys.TryGetValue(key, out var values) ? values : new Dictionary<string, string>();

    /// <summary>Names of the direct subkeys of <paramref name="key"/>, in hive order.</summary>
    public IEnumerable<string> SubkeyNames(string key)
    {
        var prefix = key.TrimEnd('\\') + "\\";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _keys.Keys)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var child = name[prefix.Length..];
            var slash = child.IndexOf('\\');
            if (slash >= 0) child = child[..slash];
            if (child.Length > 0 && seen.Add(child)) yield return child;
        }
    }

    private static bool IsSameOrBelow(string key, string root)
        => key.Equals(root, StringComparison.OrdinalIgnoreCase) ||
           (key.Length > root.Length && key.StartsWith(root, StringComparison.OrdinalIgnoreCase) && key[root.Length] == '\\');

    /// <summary><c>"name"=…</c> / <c>@=…</c> with a quoted <c>REG_SZ</c> or <c>str(2):</c> value; other types are skipped.</summary>
    private static bool TryValue(string line, out string name, out string value)
    {
        name = value = "";
        int position;
        if (line.StartsWith("@=", StringComparison.Ordinal))
        {
            position = 2;
        }
        else if (line[0] == '"')
        {
            var close = ClosingQuote(line, 1);
            if (close < 0 || close + 1 >= line.Length || line[close + 1] != '=') return false;
            name = Unescape(line[1..close]);
            position = close + 2;
        }
        else return false;

        var rest = line.AsSpan(position);
        if (rest.StartsWith("str(2):", StringComparison.Ordinal)) { position += 7; rest = line.AsSpan(position); }
        if (rest.Length < 2 || rest[0] != '"') return false;
        var end = ClosingQuote(line, position + 1);
        if (end < 0) return false;
        value = Unescape(line[(position + 1)..end]);
        return true;
    }

    private static int ClosingQuote(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"') return i;
        }
        return -1;
    }

    /// <summary>Wine's escapes: <c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\0</c> and <c>\x</c> + up to four hex digits.</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\\' || i + 1 >= text.Length)
            {
                result.Append(ch);
                continue;
            }
            var next = text[++i];
            switch (next)
            {
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case '0': result.Append('\0'); break;
                case 'x':
                    var digits = 0;
                    while (digits < 4 && i + 1 + digits < text.Length && Uri.IsHexDigit(text[i + 1 + digits])) digits++;
                    if (digits == 0) { result.Append('x'); break; }
                    result.Append((char)int.Parse(text.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += digits;
                    break;
                default: result.Append(next); break;
            }
        }
        return result.ToString();
    }
}
