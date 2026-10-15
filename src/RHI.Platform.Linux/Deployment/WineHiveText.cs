using System.Globalization;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Line-level edits of a Wine hive file's text (<c>user.reg</c> or <c>system.reg</c>), shared by the DLL override
/// service and the registry value service (#92). A section is named by its header as the hive spells it
/// (<c>[Software\\Wine\\DllOverrides]</c>, keys escaped by <see cref="WineRegistryEditor.EscapeKeyName"/>), a value by
/// its quoted, escaped name (<c>"dxgi"</c>, <see cref="WineRegistryEditor.EscapeValueName"/>); names compare without
/// case, as Wine compares them. Edits keep the hive's layout, line endings and final newline, so undoing one gives the
/// original bytes back.
/// </summary>
internal static class WineHiveText
{
    /// <summary>
    /// A value as the hive holds it: a string value unquoted (<see cref="WineDllOverrideService.OverrideValue.Raw"/>
    /// false), any other type (<c>hex(2):…</c>, <c>dword:…</c>) as its raw right-hand side with its continuation lines
    /// (joined with a line feed).
    /// </summary>
    internal static WineDllOverrideService.OverrideValue? Read(string text, string sectionName, string quotedName)
    {
        var lines = SplitLines(text);
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return null;
        var prefix = quotedName + "=";
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rhs = line[prefix.Length..].Trim();
            if (rhs.Length >= 2 && rhs[0] == '"' && rhs[^1] == '"') return new(rhs[1..^1], Raw: false);
            // Not a string: keep it exactly, continuation lines included, so it can be written back as it was.
            var continuation = lines.Skip(i + 1).Take(ContinuationLines(lines, i, end));
            var first = lines[i].TrimStart();
            return new(string.Join('\n', continuation.Prepend(first[prefix.Length..])), Raw: true);
        }
        return null;
    }

    /// <summary>Whether the section exists (any content).</summary>
    internal static bool HasSection(string text, string sectionName) => FindSection(SplitLines(text), sectionName).Start >= 0;

    /// <summary>
    /// Whether the section exists and every value in it has a quoted name starting with <paramref name="quotedNamePrefix"/>
    /// (comments and blank lines aside); an empty section qualifies too.
    /// </summary>
    internal static bool HoldsOnlyValuesNamed(string text, string sectionName, string quotedNamePrefix)
    {
        var lines = SplitLines(text);
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return false;
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!line.StartsWith(quotedNamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
            i += ContinuationLines(lines, i, end);
        }
        return true;
    }

    /// <summary>
    /// Sets <paramref name="quotedName"/> to <paramref name="rightHandSide"/> (written as it is: <c>"value"</c> for a
    /// string, <c>dword:00000000</c>, or raw text whose line feeds become the hive's line ending), creating the section
    /// at the end of the hive when it is missing.
    /// </summary>
    internal static string Set(string text, string sectionName, string quotedName, string rightHandSide)
    {
        var newline = NewlineOf(text);
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        var entry = quotedName + "=" + rightHandSide.Replace("\n", newline, StringComparison.Ordinal);
        if (start < 0)
        {
            // A hive without a final newline (Wine always writes one) gets none after the new section either, so
            // RemoveSectionIfEmpty can give back exactly the bytes it had.
            var finalNewline = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r');
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fileTime = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10_000_000L;
            lines.Add($"{sectionName} {now}");
            lines.Add($"#time={fileTime.ToString("x", CultureInfo.InvariantCulture)}");
            lines.Add(entry);
            return string.Join(newline, lines) + (finalNewline ? newline : "");
        }

        var prefix = quotedName + "=";
        for (var i = start + 1; i < end; i++)
        {
            var existing = lines[i].TrimStart();
            if (!existing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveRange(i + 1, ContinuationLines(lines, i, end));
            // Wine compares value names without case; the name keeps the spelling the hive has ("DXGI"), so a restore
            // gives back the user's line byte for byte (w2 final review, nit 4).
            lines[i] = existing[..prefix.Length] + entry[prefix.Length..];
            return string.Join(newline, lines);
        }
        // After the section's last value, not after the blank separator line (or the empty string that follows a
        // final newline), so the hive keeps its layout and its final newline.
        var at = end;
        while (at > start + 1 && lines[at - 1].Length == 0) at--;
        lines.Insert(at, entry);
        return string.Join(newline, lines);
    }

    internal static string Remove(string text, string sectionName, string quotedName)
    {
        var newline = NewlineOf(text);
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return text;
        var prefix = quotedName + "=";
        for (var i = start + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveRange(i, 1 + ContinuationLines(lines, i, end));
            break;
        }
        return string.Join(newline, lines);
    }

    internal static string RemoveSectionIfEmpty(string text, string sectionName)
    {
        var newline = NewlineOf(text);
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return text;

        // Be deliberately conservative: comments (#time), blank lines and the section
        // header itself are metadata. Any other content means somebody added data to
        // the key while RHI was installed, so the section must survive uninstall.
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            return text;
        }

        var removeEnd = end;
        // Consume the separator blank line after the section when present, but never
        // touch the next registry section. This avoids leaving extra blank blocks.
        while (removeEnd < lines.Count && lines[removeEnd].Length == 0) removeEnd++;
        // The last section of a hive without a final newline (as Set leaves one it created there): its
        // separator blank line goes too, so the hive ends where it ended before the section was added.
        var lastWithoutFinalNewline = removeEnd == lines.Count && lines[^1].Length != 0;
        if (lastWithoutFinalNewline && start > 0 && lines[start - 1].Length == 0) start--;
        lines.RemoveRange(start, removeEnd - start);

        // Avoid creating a trailing stack of empty lines after section removal.
        while (lines.Count > 1 && lines[^1].Length == 0 && lines[^2].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return string.Join(newline, lines);
    }

    private static string NewlineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// How many lines after <paramref name="valueLine"/> continue its value: Wine wraps long hex values with a trailing
    /// backslash (a quoted string value always ends with its closing quote).
    /// </summary>
    private static int ContinuationLines(IReadOnlyList<string> lines, int valueLine, int sectionEnd)
    {
        var count = 0;
        for (var i = valueLine; i + 1 < sectionEnd && lines[i].TrimEnd().EndsWith('\\'); i++) count++;
        return count;
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string sectionName)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (start < 0)
            {
                if (line.StartsWith(sectionName, StringComparison.OrdinalIgnoreCase)) start = i;
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal)) return (start, i);
        }
        return start < 0 ? (-1, -1) : (start, lines.Count);
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
