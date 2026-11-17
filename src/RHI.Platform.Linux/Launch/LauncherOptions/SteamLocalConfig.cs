using System.Globalization;
using System.Text;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>
/// #318: a game's launch options in Steam's <c>userdata/&lt;account&gt;/config/localconfig.vdf</c>, at
/// <c>UserLocalConfigStore › Software › Valve › Steam › apps › &lt;appid&gt; › LaunchOptions</c> (text KeyValues; keys are
/// case-insensitive, as Steam reads them). The file is edited in place: only the value's quoted token changes, or one line
/// is added or taken out, so every other byte stays as Steam wrote it. The text is handled as bytes (Latin-1 maps each byte
/// to one char) and values are UTF-8, as Steam writes them. Other <c>apps</c> blocks (WebStorage's, say) are never touched.
/// </summary>
public static class SteamLocalConfig
{
    public const string FileName = "localconfig.vdf";
    public const string ValueKey = "LaunchOptions";

    private static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];

    /// <summary>What the file says about the app: whether it has an entry, and its launch options (null: no such key).</summary>
    public readonly record struct Reading(bool AppListed, string? LaunchOptions);

    /// <summary>Reads the app's launch options from the file's bytes. Throws <see cref="FormatException"/> for a file without the apps block.</summary>
    public static Reading Read(byte[] file, int appId)
    {
        var text = ByteText.From(file);
        var apps = Find(Parse(text), AppsPath) ?? throw new FormatException("localconfig.vdf has no Software › Valve › Steam › apps block.");
        var app = apps.Child(Id(appId));
        if (app is null) return new(false, null);
        return new(true, app.Value(ValueKey) is { } value ? ByteText.Utf8(value.Decoded) : null);
    }

    /// <summary>The file's bytes with the app's launch options set to <paramref name="value"/> (null: the key is taken out).</summary>
    public static byte[] Write(byte[] file, int appId, string? value)
    {
        var text = ByteText.From(file);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var apps = Find(Parse(text), AppsPath) ?? throw new FormatException("localconfig.vdf has no Software › Valve › Steam › apps block.");
        var app = apps.Child(Id(appId));
        string result;
        if (app is null)
        {
            if (value is null) return file;
            var indent = IndentOfLine(text, apps.Close);
            var inner = indent + "\t";
            var block = $"{inner}\"{Id(appId)}\"{newline}{inner}{{{newline}{inner}\t\"{ValueKey}\"\t\t\"{Escaped(value)}\"{newline}{inner}}}{newline}";
            result = InsertBeforeClose(text, apps.Close, block, newline);
        }
        else if (app.Value(ValueKey) is { } existing)
        {
            if (value is null)
            {
                var (start, end) = WholeLine(text, existing.KeyStart, existing.ValueEnd);
                result = text[..start] + text[end..];
            }
            else
            {
                result = text[..existing.ValueStart] + "\"" + Escaped(value) + "\"" + text[existing.ValueEnd..];
            }
        }
        else
        {
            if (value is null) return file;
            var line = $"{IndentOfLine(text, app.Close)}\t\"{ValueKey}\"\t\t\"{Escaped(value)}\"{newline}";
            result = InsertBeforeClose(text, app.Close, line, newline);
        }
        return ByteText.ToBytes(result);
    }

    private static string Id(int appId) => appId.ToString(CultureInfo.InvariantCulture);

    // The value as Steam escapes it, as bytes-as-chars (UTF-8).
    private static string Escaped(string value) => ByteText.FromUtf8(LauncherOptionsText.VdfEscape(value));

    private static string IndentOfLine(string text, int position)
    {
        var lineStart = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
        var indent = text[lineStart..position];
        return indent.All(c => c is ' ' or '\t') ? indent : "";
    }

    // Inserts whole lines before the block's closing brace: at the brace's line start when the brace begins its line.
    private static string InsertBeforeClose(string text, int close, string lines, string newline)
    {
        var lineStart = close == 0 ? 0 : text.LastIndexOf('\n', close - 1) + 1;
        if (text[lineStart..close].All(c => c is ' ' or '\t')) return text[..lineStart] + lines + text[lineStart..];
        return text[..close] + newline + lines + text[close..];
    }

    // The span of a key/value pair, widened to its whole line (and line break) when nothing else is on that line.
    private static (int Start, int End) WholeLine(string text, int start, int end)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', end);
        var restEnd = lineEnd < 0 ? text.Length : lineEnd;
        var before = text[lineStart..start];
        var after = text[end..restEnd].TrimEnd('\r');
        if (before.All(c => c is ' ' or '\t') && after.All(c => c is ' ' or '\t'))
            return (lineStart, lineEnd < 0 ? text.Length : lineEnd + 1);
        return (start, end);
    }

    private static Node? Find(Node root, string[] path)
    {
        var current = root;
        foreach (var key in path)
            if (current.Child(key) is not { } next) return null;
            else current = next;
        return current;
    }

    // ── the token tree, with positions ────────────────────────────────────────────────────

    private sealed record Pair(string Key, int KeyStart, string Decoded, int ValueStart, int ValueEnd);

    private sealed class Node
    {
        public List<(string Key, Node Node)> Children { get; } = [];
        public List<Pair> Values { get; } = [];
        public int Close { get; set; }

        // Steam keeps the first of duplicate keys; so does RHI.
        public Node? Child(string key) => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Node;
        public Pair? Value(string key) => Values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static Node Parse(string text)
    {
        var root = new Node { Close = text.Length };
        var stack = new Stack<Node>();
        stack.Push(root);
        (string Text, int Start)? pending = null;
        var i = 0;
        while (Next(text, ref i) is { } token)
        {
            if (!token.Quoted && token.Text == "{")
            {
                if (pending is null) throw new FormatException($"localconfig.vdf: a block without a name at byte {token.Start}.");
                var child = new Node();
                stack.Peek().Children.Add((pending.Value.Text, child));
                stack.Push(child);
                pending = null;
            }
            else if (!token.Quoted && token.Text == "}")
            {
                if (stack.Count == 1) throw new FormatException($"localconfig.vdf: an unmatched }} at byte {token.Start}.");
                stack.Pop().Close = token.Start;
                pending = null;
            }
            else if (pending is null) pending = (token.Text, token.Start);
            else
            {
                stack.Peek().Values.Add(new Pair(pending.Value.Text, pending.Value.Start, token.Text, token.Start, token.End));
                pending = null;
            }
        }
        if (stack.Count != 1) throw new FormatException("localconfig.vdf ends inside a block (a cut-off file).");
        return root;
    }

    private sealed record Token(string Text, bool Quoted, int Start, int End);

    private static Token? Next(string text, ref int i)
    {
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
        var start = i;
        if (text[i] is '{' or '}') return new Token(text[i++].ToString(), false, start, i);
        var builder = new StringBuilder();
        if (text[i] == '"')
        {
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
            if (i >= text.Length) throw new FormatException($"localconfig.vdf: a quoted value from byte {start} is not closed.");
            i++;
            return new Token(builder.ToString(), true, start, i);
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) builder.Append(text[i++]);
        return new Token(builder.ToString(), false, start, i);
    }
}

/// <summary>Bytes held as a string with one char per byte (Latin-1), so editing a file never changes the bytes it does not touch.</summary>
internal static class ByteText
{
    public static string From(byte[] bytes) => Encoding.Latin1.GetString(bytes);
    public static byte[] ToBytes(string text) => Encoding.Latin1.GetBytes(text);
    /// <summary>A UTF-8 string as bytes-as-chars.</summary>
    public static string FromUtf8(string value) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));
    /// <summary>Bytes-as-chars read as UTF-8.</summary>
    public static string Utf8(string byteText) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(byteText));
}
