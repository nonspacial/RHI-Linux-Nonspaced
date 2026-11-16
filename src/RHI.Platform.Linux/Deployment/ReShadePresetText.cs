namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w14/nr (#274, #275, #277): text edits of a ReShade preset (<c>ReShadePreset.ini</c> or the file <c>[GENERAL] PresetPath</c>
/// names). Pure: no file is read or written here. Ported from upstream RHI 2.9.4 (<c>FeederPreset.GetKey/SetKey</c>,
/// <c>UpliftService.AddTechniqueToPreset/RemoveTechniqueFromPreset/MoveTechniqueFirst</c>). Every other line of the preset
/// (the user's effects, their values, comments) is kept as it is; the line ending of the file is kept too.
/// </summary>
public static class ReShadePresetText
{
    public const string TechniquesKey = "Techniques";
    public const string SortingKey = "TechniqueSorting";

    /// <summary>A key's value in <paramref name="section"/> ("" = the keys before the first [section]), or null.</summary>
    public static string? GetKey(string? text, string section, string key)
    {
        if (text is null) return null;
        var current = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1]; continue; }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Sets a key, keeping every other line. Adds the key at the end of its section, or the section at the end.</summary>
    public static string SetKey(string? text, string section, string key, string value)
    {
        var nl = NewLine(text);
        var lines = Lines(text);
        var entry = key + "=" + value;
        var current = "";
        int sectionStart = section.Length == 0 ? 0 : -1, sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && sectionStart >= 0 && sectionEnd < 0) sectionEnd = i;
                current = line[1..^1];
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionStart = i + 1;
                continue;
            }
            var eq = line.IndexOf('=');
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = entry;
                return Join(lines, nl);
            }
        }

        if (sectionStart >= 0)
        {
            if (sectionEnd < 0) sectionEnd = lines.Count;
            var at = sectionEnd;
            while (at > sectionStart && lines[at - 1].Trim().Length == 0) at--; // after the section's last non-blank line
            lines.Insert(at, entry);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
        }
        return Join(lines, nl);
    }

    /// <summary>
    /// Removes a key from <paramref name="section"/>; with <paramref name="dropEmptySection"/> the section header goes too when
    /// no key is left in it (a section RHI added). Unchanged text when the key is not there.
    /// </summary>
    public static string RemoveKey(string text, string section, string key, bool dropEmptySection = false)
    {
        var nl = NewLine(text);
        var lines = Lines(text);
        var current = "";
        var header = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) header = i;
                continue;
            }
            var eq = line.IndexOf('=');
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase) || eq <= 0 || !line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(i);
            if (dropEmptySection && header >= 0 && SectionIsEmpty(lines, header))
            {
                var end = header + 1;
                while (end < lines.Count && lines[end].Trim().Length == 0) end++;
                lines.RemoveRange(header, end - header);
                while (header > 0 && header <= lines.Count && lines[header - 1].Trim().Length == 0 && (header == lines.Count || lines[header].Trim().StartsWith('[')))
                {
                    lines.RemoveAt(header - 1);
                    header--;
                }
            }
            return Join(lines, nl);
        }
        return text;
    }

    private static bool SectionIsEmpty(List<string> lines, int header)
    {
        for (var i = header + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) return true;
            if (line.Length > 0) return false;
        }
        return true;
    }

    /// <summary>The comma list a top-level key (Techniques / TechniqueSorting) holds; empty when the key is missing.</summary>
    public static List<string> GetList(string? text, string key)
        => Split(GetKey(text, "", key) ?? "");

    public static List<string> Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// <paramref name="token"/> in every Techniques / TechniqueSorting line, first or just before <paramref name="before"/>
    /// when that is in the line (both lines added at the top when missing). A token already in TechniqueSorting but after
    /// <paramref name="before"/> is moved in front of it. AlreadyOn: Techniques had it. Upstream <c>AddTechniqueToPreset</c>.
    /// </summary>
    public static (string Text, bool AlreadyOn, bool Changed) AddTechnique(string text, string token, string? before = null)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Split(nl).ToList();
        bool hadTechniques = false, hadSorting = false, alreadyOn = false, changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var key in new[] { TechniquesKey, SortingKey })
            {
                if (!lines[i].StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var items = Split(lines[i][(key.Length + 1)..]);
                var at = items.FindIndex(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
                var beforeAt = before is null ? -1 : items.FindIndex(t => t.Equals(before, StringComparison.OrdinalIgnoreCase));
                if (key == TechniquesKey) { hadTechniques = true; alreadyOn |= at >= 0; }
                else hadSorting = true;
                if (at >= 0)
                {
                    // Order matters only in TechniqueSorting: keep it in front of `before`.
                    if (key != SortingKey || beforeAt < 0 || at < beforeAt) continue;
                    items.RemoveAt(at);
                    items.Insert(beforeAt, token);
                }
                else items.Insert(Math.Max(0, beforeAt), token);
                lines[i] = key + "=" + string.Join(",", items);
                changed = true;
            }
        }
        // Top-level lines go before the first section, as ReShade writes them.
        if (!hadSorting) { lines.Insert(0, SortingKey + "=" + token); changed = true; }
        if (!hadTechniques) { lines.Insert(0, TechniquesKey + "=" + token); changed = true; }
        if (text.Length == 0) lines.Add(""); // a new file ends with a line break
        return (string.Join(nl, lines), alreadyOn, changed);
    }

    /// <summary><paramref name="token"/> removed from every Techniques / TechniqueSorting line. Upstream <c>RemoveTechniqueFromPreset</c>.</summary>
    public static (string Text, bool Changed) RemoveTechnique(string text, string token)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(nl);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
            foreach (var key in new[] { TechniquesKey, SortingKey })
            {
                if (!lines[i].StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var items = Split(lines[i][(key.Length + 1)..]);
                if (items.RemoveAll(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)) == 0) continue;
                lines[i] = key + "=" + string.Join(",", items);
                changed = true;
            }
        return (string.Join(nl, lines), changed);
    }

    /// <summary>
    /// <paramref name="token"/> moved to the front of every Techniques / TechniqueSorting line that already has it. Never adds or
    /// removes anything: what is ticked stays as it is. Upstream <c>MoveTechniqueFirst</c>.
    /// </summary>
    public static (string Text, bool Changed) MoveTechniqueFirst(string text, string token)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(nl);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
            foreach (var key in new[] { TechniquesKey, SortingKey })
            {
                if (!lines[i].StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var items = Split(lines[i][(key.Length + 1)..]);
                var at = items.FindIndex(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (at <= 0) continue;
                var item = items[at];
                items.RemoveAt(at);
                items.Insert(0, item);
                lines[i] = key + "=" + string.Join(",", items);
                changed = true;
            }
        return (string.Join(nl, lines), changed);
    }

    private static string NewLine(string? text) => text is not null && text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";

    private static List<string> Lines(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Join(List<string> lines, string nl) => string.Join(nl, lines) + nl;
}
