using System.Globalization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311 (upstream 2.9.3 <c>DisplayModeItems.ReadRdx</c> / <c>WriteRdx</c>, 2.9.3 Beta 3 fix): RenoDX's peak / game / UI
/// brightness in every <c>[renodx-preset*]</c> section of ReShade.ini. The spelling depends on the mod: Unity and generic
/// RenoDX mods save <c>toneMapPeakNits</c>, others (e.g. UE-Extended) <c>ToneMapPeakNits</c>; RHI's global peak
/// brightness writes the capital-T spelling, so a section can hold both. Reads take RenoDX's own spelling first; writes
/// go to every spelling present (line by line, nothing else changes), a section with neither gets both, and
/// <c>[renodx-preset1]</c> is added when there is no preset section.
/// </summary>
public static class RenoDxNitsIni
{
    public const string PeakKey = "toneMapPeakNits";
    public const string GameKey = "toneMapGameNits";
    public const string UiKey = "toneMapUINits";

    /// <summary>The ini key of a RenoDX brightness item (lower-case spelling).</summary>
    public static string KeyFor(string itemKey) => itemKey switch
    {
        DisplayModeItemKeys.RdxPeakNits => PeakKey,
        DisplayModeItemKeys.RdxGameNits => GameKey,
        DisplayModeItemKeys.RdxUiNits => UiKey,
        _ => throw new ArgumentOutOfRangeException(nameof(itemKey), itemKey, "Not a RenoDX brightness item.")
    };

    /// <summary>The value from the first preset section that has it (a whole number); RenoDX's spelling wins. Null = none.</summary>
    public static string? Read(IReadOnlyList<string> lines, string iniKey)
    {
        string? exact = null, other = null;
        var inPreset = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (IniLines.IsSectionHeader(line))
            {
                inPreset = IsPreset(line);
                continue;
            }
            if (!inPreset || line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            if (exact is null && k.Equals(iniKey, StringComparison.Ordinal)) exact = v;
            else if (other is null && k.Equals(iniKey, StringComparison.OrdinalIgnoreCase)) other = v;
            if (exact is not null) break;
        }
        var value = exact ?? other;
        if (value is null) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
            : value;
    }

    /// <summary>Sets the value in every preset section (see the class summary). True when a line changed.</summary>
    public static bool Write(List<string> lines, string iniKey, string value)
    {
        var before = lines.ToList();
        var presets = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IniLines.IsSectionHeader(lines[i]) || !IsPreset(lines[i].Trim())) continue;
            presets++;
            var end = i + 1;
            while (end < lines.Count && !IniLines.IsSectionHeader(lines[end])) end++;
            var found = false;
            var lastKey = i;
            for (var j = i + 1; j < end; j++)
            {
                var t = lines[j].Trim();
                if (t.Length == 0 || t.StartsWith(';') || t.StartsWith('#')) continue;
                lastKey = j;
                var eq = t.IndexOf('=');
                if (eq > 0 && t[..eq].Trim().Equals(iniKey, StringComparison.OrdinalIgnoreCase))
                {
                    lines[j] = t[..eq].Trim() + "=" + value;
                    found = true;
                }
            }
            if (!found)
            {
                var spellings = Spellings(iniKey);
                lines.InsertRange(lastKey + 1, spellings.Select(k => k + "=" + value));
                end += spellings.Count;
            }
            i = end - 1;
        }
        if (presets == 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[renodx-preset1]");
            lines.AddRange(Spellings(iniKey).Select(k => k + "=" + value));
        }
        return !lines.SequenceEqual(before, StringComparer.Ordinal);
    }

    private static bool IsPreset(string header) => header.StartsWith("[renodx-preset", StringComparison.OrdinalIgnoreCase);

    /// <summary>Both spellings RenoDX mods use: "toneMapPeakNits" and "ToneMapPeakNits".</summary>
    private static IReadOnlyList<string> Spellings(string iniKey)
    {
        var lower = char.ToLowerInvariant(iniKey[0]) + iniKey[1..];
        var upper = char.ToUpperInvariant(iniKey[0]) + iniKey[1..];
        return lower == upper ? [iniKey] : [lower, upper];
    }
}
