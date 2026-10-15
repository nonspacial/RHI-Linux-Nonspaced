using RHI.Core.Deployment;
using RHI.Core.Services;
using System.Text;
using System.Text.Json;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#189: Luma's "HDR on first boot" (upstream Luma ⚙): Default leaves both keys to Luma, Off / On set them.</summary>
public enum LumaHdrMode
{
    Default,
    Off,
    On
}

internal sealed record LumaIniOverrideEntry(
    string Key,
    string AppliedValue,
    bool HadPreviousValue,
    string? PreviousValue);

internal sealed record LumaIniOverrideState(
    string IniPath,
    bool SectionCreatedByRhi,
    IReadOnlyList<LumaIniOverrideEntry> Entries);

/// <summary>
/// Reversible ownership for the two [Luma] values written by upstream RHI when Luma mode is enabled.
/// Existing values are restored on teardown, RHI-created values are removed, and a value edited by the
/// user after installation is left untouched rather than being forced back during update/remove.
/// </summary>
internal static class LumaIniOverrideManager
{
    internal const string MetadataKey = "LumaFramework.ReShadeIni";
    private const string SectionName = "Luma";

    private static readonly IReadOnlyDictionary<string, string> RequestedValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableHDR"] = "1",
            ["DisplayMode"] = "1"
        };

    public static LumaIniOverrideState Apply(string binaryDirectory, LumaIniOverrideState? previousState = null)
    {
        var iniPath = RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory)
            ?? throw new InvalidOperationException("ReShade.ini is required before enabling Luma Framework.");
        if (!File.Exists(iniPath))
            throw new InvalidOperationException("ReShade.ini is required before enabling Luma Framework.");

        var lines = File.ReadAllLines(iniPath).ToList();
        var previousByKey = (previousState?.Entries ?? Array.Empty<LumaIniOverrideEntry>())
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var nextEntries = new Dictionary<string, LumaIniOverrideEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previousState?.Entries ?? Array.Empty<LumaIniOverrideEntry>())
            nextEntries[entry.Key] = entry;

        var sectionCreated = previousState?.SectionCreatedByRhi == true;
        var changed = false;

        foreach (var pair in RequestedValues)
        {
            var (sectionStart, sectionEnd) = FindSection(lines, SectionName);
            var keyIndex = sectionStart >= 0 ? FindKey(lines, sectionStart + 1, sectionEnd, pair.Key) : -1;
            var current = keyIndex >= 0 ? GetValue(lines[keyIndex]) : null;

            if (previousByKey.TryGetValue(pair.Key, out var previous))
            {
                // A missing key/section or a value changed away from our last value is a user edit.
                if (keyIndex < 0 || !string.Equals(current, previous.AppliedValue, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(current, pair.Value, StringComparison.Ordinal))
                {
                    lines[keyIndex] = $"{pair.Key}={pair.Value}";
                    changed = true;
                }
                nextEntries[pair.Key] = previous with { AppliedValue = pair.Value };
                continue;
            }

            if (sectionStart < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
                lines.Add($"[{SectionName}]");
                sectionStart = lines.Count - 1;
                sectionEnd = lines.Count;
                sectionCreated = true;
            }

            nextEntries[pair.Key] = new LumaIniOverrideEntry(
                pair.Key,
                pair.Value,
                keyIndex >= 0,
                keyIndex >= 0 ? current : null);

            if (keyIndex >= 0)
            {
                if (!string.Equals(current, pair.Value, StringComparison.Ordinal))
                {
                    lines[keyIndex] = $"{pair.Key}={pair.Value}";
                    changed = true;
                }
            }
            else
            {
                // Re-evaluate because the section may just have been appended.
                (_, sectionEnd) = FindSection(lines, SectionName);
                lines.Insert(sectionEnd, $"{pair.Key}={pair.Value}");
                changed = true;
            }
        }

        if (changed)
            AtomicFile.ReplaceUserFileLines(iniPath, lines);

        return new LumaIniOverrideState(
            iniPath,
            sectionCreated,
            nextEntries.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static void Cleanup(LumaIniOverrideState? state)
    {
        if (state is null || !File.Exists(state.IniPath)) return;

        var lines = File.ReadAllLines(state.IniPath).ToList();
        var changed = false;
        foreach (var entry in state.Entries)
        {
            var (start, end) = FindSection(lines, SectionName);
            if (start < 0) break;
            var keyIndex = FindKey(lines, start + 1, end, entry.Key);
            if (keyIndex < 0) continue;
            var current = GetValue(lines[keyIndex]);
            if (!string.Equals(current, entry.AppliedValue, StringComparison.Ordinal))
                continue; // User changed the value after install.

            if (entry.HadPreviousValue)
                lines[keyIndex] = $"{entry.Key}={entry.PreviousValue ?? ""}";
            else
                lines.RemoveAt(keyIndex);
            changed = true;
        }

        if (state.SectionCreatedByRhi)
        {
            var (start, end) = FindSection(lines, SectionName);
            if (start >= 0 && SectionBodyIsEmpty(lines, start + 1, end))
            {
                lines.RemoveAt(start);
                if (start < lines.Count && string.IsNullOrWhiteSpace(lines[start])) lines.RemoveAt(start);
                else if (start > 0 && string.IsNullOrWhiteSpace(lines[start - 1])) lines.RemoveAt(start - 1);
                changed = true;
            }
        }

        if (changed)
            AtomicFile.ReplaceUserFileLines(state.IniPath, lines, readOnly: ReadOnlyUserFile.WriteKeepingLock); // removal: a lock never blocks it
    }

    /// <summary>
    /// #189: the HDR-on-first-boot setting as the game's ReShade.ini holds it (upstream <c>LumaCogButton_Click</c>):
    /// <c>EnableHDR</c> absent = Default, <c>0</c> = Off, anything else = On.
    /// </summary>
    public static LumaHdrMode ReadHdrMode(string binaryDirectory)
    {
        var iniPath = RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory);
        if (iniPath is null || !File.Exists(iniPath)) return LumaHdrMode.Default;
        return IniLines.Get(File.ReadAllLines(iniPath), SectionName, "EnableHDR") switch
        {
            null => LumaHdrMode.Default,
            "0" => LumaHdrMode.Off,
            _ => LumaHdrMode.On
        };
    }

    /// <summary>
    /// #189: writes the HDR-on-first-boot choice into <c>[Luma]</c> as upstream's ⚙ does (Default removes
    /// <c>EnableHDR</c>/<c>DisplayMode</c> so Luma decides; Off/On set both to 0/1) and returns the ownership state to
    /// keep on the Luma record: an Off/On value becomes RHI's applied value (removing Luma puts back what was there
    /// before), and Default leaves the keys missing, which later Luma updates treat as the user's choice and keep.
    /// </summary>
    public static LumaIniOverrideState SetHdrMode(string binaryDirectory, LumaIniOverrideState? state, LumaHdrMode mode)
    {
        var iniPath = RenoDxIniOverrideManager.ResolveIniPath(binaryDirectory);
        if (iniPath is null || !File.Exists(iniPath))
            throw new InvalidOperationException("ReShade.ini is required before enabling Luma Framework.");
        var lines = File.ReadAllLines(iniPath).ToList();
        var before = lines.ToList();
        var entries = (state?.Entries ?? []).ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var sectionCreated = state?.SectionCreatedByRhi == true;
        foreach (var key in RequestedValues.Keys)
        {
            if (mode == LumaHdrMode.Default)
            {
                IniLines.Remove(lines, SectionName, key);
                continue;
            }
            var value = mode == LumaHdrMode.On ? "1" : "0";
            if (!entries.TryGetValue(key, out var entry))
            {
                var current = IniLines.Get(lines, SectionName, key);
                if (current is null && IniLines.FindSection(lines, SectionName).Start < 0) sectionCreated = true;
                entry = new LumaIniOverrideEntry(key, value, current is not null, current);
            }
            IniLines.Set(lines, SectionName, key, value);
            entries[key] = entry with { AppliedValue = value };
        }
        if (!lines.SequenceEqual(before, StringComparer.Ordinal))
            AtomicFile.ReplaceUserFileLines(iniPath, lines, readOnly: ReadOnlyUserFile.WriteKeepingLock);
        return new LumaIniOverrideState(iniPath, sectionCreated, entries.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static string Serialize(LumaIniOverrideState state) => JsonSerializer.Serialize(state);

    public static LumaIniOverrideState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LumaIniOverrideState>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("luma-ini", "Stored deployment state could not be read; it is treated as absent", ex);
            return null;
        }
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    end = j;
                    break;
                }
            }
            return (i, end);
        }
        return (-1, -1);
    }

    private static int FindKey(IReadOnlyList<string> lines, int start, int end, string key)
    {
        for (var i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            if (trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static string GetValue(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? "" : line[(eq + 1)..].Trim();
    }

    private static bool SectionBodyIsEmpty(IReadOnlyList<string> lines, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!string.IsNullOrWhiteSpace(lines[i])) return false;
        return true;
    }
}
