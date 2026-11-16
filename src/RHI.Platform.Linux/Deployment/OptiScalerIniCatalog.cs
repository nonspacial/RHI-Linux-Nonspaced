using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

public enum OptiScalerIniKind { Choice, Bool, Text }

/// <summary>One choice of a setting; <see cref="Label"/> keeps the build's note ("xess (native DX11, Arc only)").</summary>
public sealed record OptiScalerIniOption(string Value, string Label);

/// <summary>A setting of OptiScaler.ini with the help text and options its comment lines give (#272, upstream <c>OsIniSetting</c>).</summary>
/// <param name="InFile">False for a setting the installed build has but the game's file lacks (shown as auto, added when changed).</param>
public sealed record OptiScalerIniEntry(
    string Section,
    string Key,
    string Value,
    string Help,
    OptiScalerIniKind Kind,
    IReadOnlyList<OptiScalerIniOption> Options,
    string? Default,
    bool InFile = true)
{
    /// <summary>The id saved edits use: "Section|Key" (upstream).</summary>
    public string Id => OptiScalerIniCatalog.MakeId(Section, Key);
}

/// <summary>
/// #272 (upstream 2.9.3 <c>OsIniEditor</c>): reads OptiScaler.ini as a list of documented settings. OptiScaler documents
/// each setting in the comment lines above it, ending with an options line
/// (<c>; nofg, fsrfg, xefg, nukems - Default (auto) is nofg</c>); every build ships its own, so the options come from the
/// installed build's own INI (RHI keeps a copy of it at install and update), never from lists in RHI.
/// </summary>
public static partial class OptiScalerIniCatalog
{
    public const string Auto = "auto";

    public static string MakeId(string section, string key) => $"{section}|{key}";

    public static (string Section, string Key) SplitId(string id)
    {
        var at = id.IndexOf('|');
        return at < 0 ? ("", id) : (id[..at], id[(at + 1)..]);
    }

    /// <summary>Every <c>key=value</c> with the comment block above it; keys before any section are ignored, a repeated key reads its first line (as OptiScaler).</summary>
    public static List<OptiScalerIniEntry> Parse(IEnumerable<string> lines)
    {
        var result = new List<OptiScalerIniEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        var comments = new List<string>();
        var lastBlock = new List<string>();
        var lastWasKey = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { comments.Clear(); lastWasKey = false; continue; }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                comments.Clear();
                lastWasKey = false;
                continue;
            }
            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                var comment = line.TrimStart(';', '#').Trim();
                if (comment.Length > 0 && comment.All(ch => ch is '-' or '=' or '*')) continue; // a separator line
                comments.Add(comment);
                lastWasKey = false;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || section is null) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Keys straight after one another share its comments (RectLeft, RectTop, ...).
            var block = comments.Count > 0 ? new List<string>(comments) : lastWasKey ? lastBlock : [];
            comments.Clear();
            lastBlock = block;
            lastWasKey = true;
            if (!seen.Add(MakeId(section, key))) continue;
            result.Add(Describe(section, key, value, block));
        }
        return result;
    }

    public static List<OptiScalerIniEntry> ParseFile(string path) => File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];

    /// <summary>The control and options a setting's comment lines describe (upstream <c>Describe</c>).</summary>
    internal static OptiScalerIniEntry Describe(string section, string key, string value, IReadOnlyList<string> comments)
    {
        var help = string.Join("\n", comments).Trim();
        var kind = OptiScalerIniKind.Text;
        var options = new List<OptiScalerIniOption>();
        string? defaultValue = null;
        // "0: Input | 1: Time between Present calls | 2: Zero" wins over a wrong "true or false" line.
        var numbered = comments.LastOrDefault(comment => NumberedLine().IsMatch(comment) && comment.Contains('|'));
        Match? match = null;
        for (var i = comments.Count - 1; i >= 0 && match is null; i--)
        {
            var candidate = OptionsLine().Match(comments[i]);
            if (candidate.Success) match = candidate;
        }
        if (match is not null) defaultValue = match.Groups["def"].Value.Trim();

        if (numbered is not null)
        {
            foreach (var part in numbered.Split('|'))
            {
                var piece = part.Trim();
                var colon = piece.IndexOf(':');
                if (colon <= 0) continue;
                var optionValue = piece[..colon].Trim();
                if (Token().IsMatch(optionValue)) options.Add(new(optionValue, piece));
            }
            if (options.Count > 0) kind = OptiScalerIniKind.Choice;
        }
        else if (match is not null)
        {
            var listed = match.Groups["opts"].Value.Trim();
            var range = RangeOptions().Match(listed);
            if (listed.Equals("true or false", StringComparison.OrdinalIgnoreCase))
            {
                kind = OptiScalerIniKind.Bool;
                options.Add(new("true", "true"));
                options.Add(new("false", "false"));
            }
            else if (range.Success && int.TryParse(range.Groups["a"].Value, out var from) && int.TryParse(range.Groups["b"].Value, out var to)
                     && to >= from && to - from <= 16)
            {
                kind = OptiScalerIniKind.Choice;
                for (var n = from; n <= to; n++) options.Add(new(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (listed.Contains(','))
            {
                var parsed = new List<OptiScalerIniOption>();
                var ok = true;
                foreach (var item in SplitTopLevel(listed))
                {
                    var label = item.Trim();
                    if (label.Length == 0) continue;
                    var cut = label.IndexOfAny([' ', '(']);
                    var optionValue = (cut > 0 ? label[..cut] : label).Trim();
                    if (!Token().IsMatch(optionValue)) { ok = false; break; }
                    if (!parsed.Any(option => option.Value.Equals(optionValue, StringComparison.OrdinalIgnoreCase))) parsed.Add(new(optionValue, label));
                }
                if (ok && parsed.Count > 1) { kind = OptiScalerIniKind.Choice; options = parsed; }
            }
        }
        // "auto" is always allowed (the build's default).
        if (kind != OptiScalerIniKind.Text && !options.Any(option => option.Value.Equals(Auto, StringComparison.OrdinalIgnoreCase)))
            options.Insert(0, new(Auto, defaultValue is not null ? $"auto (default: {defaultValue})" : "auto (default)"));
        return new OptiScalerIniEntry(section, key, value, help, kind, options, defaultValue);
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0, start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth = Math.Max(0, depth - 1);
            else if (text[i] == ',' && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }

    /// <summary>
    /// The game's settings (values from its INI) described by the installed build's own INI when there is one; settings the
    /// build has but the game's file lacks are added as auto after their section (upstream <c>Merge</c>).
    /// </summary>
    public static List<OptiScalerIniEntry> Merge(List<OptiScalerIniEntry> game, List<OptiScalerIniEntry>? reference)
    {
        if (reference is null || reference.Count == 0) return game;
        var byId = new Dictionary<string, OptiScalerIniEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reference) byId.TryAdd(entry.Id, entry);
        var result = new List<OptiScalerIniEntry>(game.Count + 16);
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in game)
        {
            have.Add(entry.Id);
            result.Add(byId.TryGetValue(entry.Id, out var described) ? described with { Value = entry.Value, InFile = true } : entry);
        }
        foreach (var entry in reference)
        {
            if (!have.Add(entry.Id)) continue;
            var missing = entry with { Value = Auto, InFile = false };
            var at = result.FindLastIndex(candidate => candidate.Section.Equals(entry.Section, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) result.Insert(at + 1, missing);
            else result.Add(missing);
        }
        return result;
    }

    /// <summary>A value RHI writes into OptiScaler.ini: one line, no section or comment syntax at its start, at most 256 characters.</summary>
    public static bool IsWritableValue(string? value)
        => value is { Length: <= 256 } && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('\0');

    /// <summary>A section or key RHI writes: <c>[A-Za-z0-9_.]</c>, at most 128 characters (OptiScaler's own names are).</summary>
    public static bool IsWritableName(string? name) => name is { Length: > 0 and <= 128 } && Name().IsMatch(name);

    // "<options> - Default (auto) is <default>". Lazy, so "0.0 - 1.0 - Default ..." keeps "0.0 - 1.0".
    [GeneratedRegex(@"^(?<opts>.+?)\s+-\s+Default\b[^\n]*?\bis\s+(?<def>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex OptionsLine();

    [GeneratedRegex(@"^(?<a>\d+)\s+to\s+(?<b>\d+)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RangeOptions();

    [GeneratedRegex(@"^\s*\d+\s*:", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Token();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Name();
}
