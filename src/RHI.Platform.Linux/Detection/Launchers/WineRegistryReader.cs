using System.Globalization;
using System.Text;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>
/// Read-only view of the string values in a Wine registry hive (<c>system.reg</c> = HKLM, <c>user.reg</c> = HKCU), for
/// store launchers' install records (Refs #90). Only keys under the requested roots are kept, so a large hive costs one
/// streamed pass. Writing the hive is <see cref="Deployment.WineRegistryEditor"/>'s job alone; this never writes.
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
        using var reader = new StreamReader(hivePath, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        return Parse(reader, roots);
    }

    internal static WineRegistryReader Parse(TextReader reader, IReadOnlyCollection<string> roots)
    {
        var keys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                var end = line.LastIndexOf(']');
                var name = end > 1 ? Unescape(line[1..end]) : null;
                current = name is not null && roots.Any(root => IsSameOrBelow(name, root)) ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
                if (current is not null) keys[name!] = current;
                continue;
            }
            if (current is null) continue;
            if (TryValue(line, out var valueName, out var value)) current[valueName] = value;
        }
        return new WineRegistryReader(keys);
    }

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
