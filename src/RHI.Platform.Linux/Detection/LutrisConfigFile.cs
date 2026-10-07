using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// The subset of YAML that Lutris writes for game configs: top-level keys with scalar values or nested
/// mappings, plain/quoted scalars that Lutris folds over several lines, and the odd block scalar.
/// Values are read per section, so an installer <c>script:</c> that repeats <c>game:</c>/<c>exe:</c>/<c>wine:</c>
/// keys never overrides the top-level <c>game:</c> mapping.
/// </summary>
internal sealed class LutrisConfigFile
{
    private readonly string[] _lines;

    private LutrisConfigFile(string text)
        => _lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

    public static LutrisConfigFile Parse(string text) => new(text);

    /// <summary>A top-level scalar such as <c>name:</c> or <c>runner:</c>.</summary>
    public string? Top(string key) => ScalarIn(0, _lines.Length, key, indent: 0);

    /// <summary>A scalar directly inside a top-level mapping, e.g. <c>Section("game", "exe")</c>.</summary>
    public string? Section(string section, string key)
    {
        for (var i = 0; i < _lines.Length; i++)
        {
            if (Indent(_lines[i]) != 0 || !TryKey(_lines[i], 0, out var name, out var rest) || name != section
                || StripComment(rest).Trim().Length > 0)
                continue;
            var end = i + 1;
            while (end < _lines.Length && (IsBlankOrComment(_lines[end]) || Indent(_lines[end]) > 0)) end++;
            var childIndent = Enumerable.Range(i + 1, end - i - 1)
                .Where(n => !IsBlankOrComment(_lines[n]))
                .Select(n => Indent(_lines[n]))
                .DefaultIfEmpty(-1)
                .First();
            return childIndent <= 0 ? null : ScalarIn(i + 1, end, key, childIndent);
        }
        return null;
    }

    private string? ScalarIn(int start, int end, string key, int indent)
    {
        for (var i = start; i < end; i++)
        {
            var line = _lines[i];
            if (IsBlankOrComment(line) || Indent(line) != indent) continue;
            if (!TryKey(line, indent, out var name, out var rest) || name != key) continue;
            return ReadValue(rest, i, end, indent);
        }
        return null;
    }

    /// <summary>
    /// The value after <c>key:</c>, joining continuation lines. A key with an inline value cannot own
    /// children, so every following line indented deeper than the key is part of the value.
    /// </summary>
    private string? ReadValue(string inline, int keyLine, int end, int keyIndent)
    {
        var continuation = new List<string>();
        for (var n = keyLine + 1; n < end; n++)
        {
            if (string.IsNullOrWhiteSpace(_lines[n])) { continuation.Add(string.Empty); continue; }
            if (Indent(_lines[n]) <= keyIndent) break;
            continuation.Add(_lines[n].Trim());
        }
        while (continuation.Count > 0 && continuation[^1].Length == 0) continuation.RemoveAt(continuation.Count - 1);

        var value = StripComment(inline).Trim();
        if (value.StartsWith('|') || value.StartsWith('>'))
        {
            var literal = value.StartsWith('|');
            return literal ? string.Join("\n", continuation) : FoldLines(continuation);
        }
        if (value.Length == 0 && continuation.Count == 0) return null;
        var joined = continuation.Count == 0 ? value : FoldLines([value, .. continuation]);
        return Unquote(joined);
    }

    // Plain and quoted multi-line scalars fold single line breaks into spaces and blank lines into newlines.
    private static string FoldLines(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        for (var n = 0; n < lines.Count; n++)
        {
            if (lines[n].Length == 0) { builder.Append('\n'); continue; }
            if (builder.Length > 0 && builder[^1] != '\n') builder.Append(' ');
            builder.Append(lines[n]);
        }
        return builder.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return value;
    }

    // " #" starts a comment in a plain scalar; quoted scalars keep everything.
    private static string StripComment(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('\'') || trimmed.StartsWith('"')) return value;
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? value[..hash] : value;
    }

    private static bool TryKey(string line, int indent, out string key, out string rest)
    {
        key = rest = string.Empty;
        var body = line[indent..];
        var colon = body.IndexOf(':');
        while (colon >= 0 && colon + 1 < body.Length && body[colon + 1] != ' ' && body[colon + 1] != '\t')
            colon = body.IndexOf(':', colon + 1);
        if (colon <= 0) return false;
        key = body[..colon].Trim().Trim('"', '\'');
        if (key.Length == 0 || key.StartsWith('-') || key.StartsWith('#')) return false;
        rest = body[(colon + 1)..];
        return true;
    }

    private static bool IsBlankOrComment(string line) => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart(' ', '\t').Length;
}
