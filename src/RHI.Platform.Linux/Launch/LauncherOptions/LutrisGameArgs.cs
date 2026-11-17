using RHI.Platform.Linux.Detection;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>
/// #318: a Lutris game's arguments, <c>game: args:</c> in its <c>games/&lt;slug&gt;-&lt;id&gt;.yml</c> (Lutris' Game options ›
/// Arguments; the wine runner splits them with <c>shlex</c>). Only the top-level <c>game:</c> mapping counts: an installer
/// <c>script:</c> repeats <c>game:</c> keys, <c>args</c> included, and is never touched. The value is read as Lutris writes it
/// (plain, single- or double-quoted, folded over several lines) and written as one single-quoted line, the way PyYAML
/// writes a scalar it must quote; a new key goes where PyYAML's sorted keys put it. Every other line stays byte for byte.
/// </summary>
public static class LutrisGameArgs
{
    public const string Section = "game";
    public const string Key = "args";

    /// <summary>The game's arguments (null: no <c>args</c> key). Throws <see cref="FormatException"/> without a top-level <c>game:</c> mapping.</summary>
    public static string? Read(byte[] file)
    {
        var text = ByteText.From(file);
        _ = Locate(Lines(text)); // a config without game: is not one RHI writes to
        return LutrisConfigFile.Parse(text).Section(Section, Key) is { } value ? ByteText.Utf8(value) : null;
    }

    /// <summary>The file's bytes with the arguments set to <paramref name="value"/> (null: the key is taken out).</summary>
    public static byte[] Write(byte[] file, string? value)
    {
        var text = ByteText.From(file);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Lines(text);
        var section = Locate(lines);
        var replacement = value is null ? null : new string(' ', section.ChildIndent) + Key + ": " + ByteText.FromUtf8(SingleQuoted(value));
        var output = new List<string>(lines);
        if (section.Args is { } args)
        {
            output.RemoveRange(args.Start, args.End - args.Start);
            if (replacement is not null) output.Insert(args.Start, replacement);
        }
        else if (replacement is not null)
        {
            output.Insert(section.InsertAt, replacement);
        }
        else return file;
        return ByteText.ToBytes(string.Join(newline, output));
    }

    /// <summary>A YAML single-quoted scalar (<c>'</c> doubled).</summary>
    public static string SingleQuoted(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Lines without their line breaks; a final line break leaves an empty last entry, so joining gives the file back.
    private static string[] Lines(string text) => text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToArray();

    private sealed record Span(int Start, int End);

    private sealed record GameSection(int ChildIndent, Span? Args, int InsertAt);

    private static GameSection Locate(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsKeyLine(lines[i], 0, out var key, out var rest) || key != Section) continue;
            if (rest.Trim().Length > 0 && !rest.TrimStart().StartsWith('#'))
                throw new FormatException("The Lutris config's game: is not a mapping.");
            var end = i + 1;
            while (end < lines.Length && (IsBlankOrComment(lines[end]) || Indent(lines[end]) > 0)) end++;
            // Trailing blank lines belong after the section.
            while (end > i + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
            var childIndent = -1;
            for (var n = i + 1; n < end; n++)
                if (!IsBlankOrComment(lines[n])) { childIndent = Indent(lines[n]); break; }
            if (childIndent <= 0) return new GameSection(2, null, i + 1);
            Span? args = null;
            var insertAt = end;
            for (var n = i + 1; n < end; n++)
            {
                if (IsBlankOrComment(lines[n]) || Indent(lines[n]) != childIndent || !IsKeyLine(lines[n], childIndent, out var child, out var value)) continue;
                if (child == Key)
                {
                    var stop = n + 1;
                    while (stop < end && (string.IsNullOrWhiteSpace(lines[stop]) || Indent(lines[stop]) > childIndent)) stop++;
                    while (stop > n + 1 && string.IsNullOrWhiteSpace(lines[stop - 1])) stop--;
                    var inline = value.Trim();
                    if (inline.StartsWith('|') || inline.StartsWith('>') || inline.StartsWith('&') || inline.StartsWith('*') || inline.StartsWith('!'))
                        throw new FormatException("The Lutris config's game: args: is written in a YAML form RHI does not change (block, anchor or tag).");
                    args = new Span(n, stop);
                }
                else if (args is null && insertAt == end && string.CompareOrdinal(child, Key) > 0) insertAt = n;
            }
            return new GameSection(childIndent, args, insertAt);
        }
        throw new FormatException("The Lutris config has no top-level game: section.");
    }

    private static bool IsKeyLine(string line, int indent, out string key, out string rest)
    {
        key = rest = "";
        if (Indent(line) != indent || IsBlankOrComment(line)) return false;
        var body = line[indent..];
        var colon = body.IndexOf(':');
        while (colon >= 0 && colon + 1 < body.Length && body[colon + 1] is not (' ' or '\t'))
            colon = body.IndexOf(':', colon + 1);
        if (colon <= 0 || body.StartsWith('-')) return false;
        key = body[..colon].Trim().Trim('"', '\'');
        rest = body[(colon + 1)..];
        return key.Length > 0;
    }

    private static bool IsBlankOrComment(string line) => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
}
