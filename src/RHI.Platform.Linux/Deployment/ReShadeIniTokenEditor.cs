using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using static RHI.Platform.Linux.Deployment.Dlss5FeederFullDeploymentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// DLSS5 Feeder Full (DEP-S3, #169): the two ReShade.ini edits the Feeder makes besides search paths — <c>PresetPath</c>
/// in [GENERAL] and the <c>LoadFromDllMain</c> token in [ADDON] — each recording exactly what it changed so removal undoes
/// only that, and only while the value is still RHI's.
/// </summary>
internal static class ReShadeIniTokenEditor
{
    internal sealed record PresetPathMutation(bool PreviousKeyExisted, string? PreviousValue, string SetValue);

    internal static PresetPathMutation SetPresetPath(string iniPath, string value)
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var (sectionStart, sectionEnd) = FindSection(lines, "GENERAL");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("[GENERAL]");
            lines.Add($"PresetPath={value}");
            AtomicFile.ReplaceUserFileLines(iniPath, lines);
            return new(false, null, value);
        }

        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "PresetPath");
        if (keyIndex < 0)
        {
            lines.Insert(sectionEnd, $"PresetPath={value}");
            AtomicFile.ReplaceUserFileLines(iniPath, lines);
            return new(false, null, value);
        }

        var eq = lines[keyIndex].IndexOf('=');
        var previous = eq >= 0 ? lines[keyIndex][(eq + 1)..].Trim() : "";
        lines[keyIndex] = $"PresetPath={value}";
        AtomicFile.ReplaceUserFileLines(iniPath, lines);
        return new(true, previous, value);
    }

    internal static void RestorePresetPath(string iniPath, PresetPathMutation mutation)
    {
        if (!File.Exists(iniPath)) return;
        var lines = File.ReadAllLines(iniPath).ToList();
        var (sectionStart, sectionEnd) = FindSection(lines, "GENERAL");
        if (sectionStart < 0) return;
        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "PresetPath");
        if (keyIndex < 0) return;
        var eq = lines[keyIndex].IndexOf('=');
        var current = eq >= 0 ? lines[keyIndex][(eq + 1)..].Trim() : "";
        if (!current.Equals(mutation.SetValue, StringComparison.OrdinalIgnoreCase))
            return; // User changed the active preset after install. Preserve that choice.

        if (mutation.PreviousKeyExisted)
            lines[keyIndex] = $"PresetPath={mutation.PreviousValue ?? ""}";
        else
            lines.RemoveAt(keyIndex);
        AtomicFile.ReplaceUserFileLines(iniPath, lines, readOnly: ReadOnlyUserFile.WriteKeepingLock); // removal: a lock never blocks it
    }

    internal sealed record IniListTokenMutation(bool AddedByRhi, string Token);

    internal static IniListTokenMutation EnsureLoadFromDllMainToken(string iniPath, string token)
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var (sectionStart, sectionEnd) = FindSection(lines, "ADDON");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("[ADDON]");
            lines.Add($"LoadFromDllMain={token}");
            AtomicFile.ReplaceUserFileLines(iniPath, lines);
            return new(true, token);
        }

        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "LoadFromDllMain");
        if (keyIndex < 0)
        {
            lines.Insert(sectionEnd, $"LoadFromDllMain={token}");
            AtomicFile.ReplaceUserFileLines(iniPath, lines);
            return new(true, token);
        }

        var eq = lines[keyIndex].IndexOf('=');
        var values = SplitIniList(eq >= 0 ? lines[keyIndex][(eq + 1)..] : "");
        if (values.Any(value => value.Equals(token, StringComparison.OrdinalIgnoreCase)))
            return new(false, token);
        values.Add(token);
        lines[keyIndex] = $"LoadFromDllMain={string.Join(',', values)}";
        AtomicFile.ReplaceUserFileLines(iniPath, lines);
        return new(true, token);
    }

    internal static void RemoveLoadFromDllMainToken(string iniPath, IniListTokenMutation mutation)
    {
        if (!mutation.AddedByRhi || !File.Exists(iniPath)) return;
        var lines = File.ReadAllLines(iniPath).ToList();
        var (sectionStart, sectionEnd) = FindSection(lines, "ADDON");
        if (sectionStart < 0) return;
        var keyIndex = FindKey(lines, sectionStart + 1, sectionEnd, "LoadFromDllMain");
        if (keyIndex < 0) return;
        var eq = lines[keyIndex].IndexOf('=');
        var values = SplitIniList(eq >= 0 ? lines[keyIndex][(eq + 1)..] : "");
        var removed = values.RemoveAll(value => value.Equals(mutation.Token, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (values.Count == 0)
            lines.RemoveAt(keyIndex);
        else
            lines[keyIndex] = $"LoadFromDllMain={string.Join(',', values)}";
        AtomicFile.ReplaceUserFileLines(iniPath, lines, readOnly: ReadOnlyUserFile.WriteKeepingLock); // removal: a lock never blocks it
    }

    internal static List<string> SplitIniList(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal)) { end = j; break; }
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
}
