using System.Globalization;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Conservative editor for Wine's DLL override registry. RHI uses per-application
/// AppDefaults whenever the executable name is known, which is essential for shared
/// Heroic/Lutris prefixes: enabling dxgi for one game must not affect every game in the prefix.
/// </summary>
public sealed class WineDllOverrideService
{
    internal const string GlobalSectionName = @"[Software\\Wine\\DllOverrides]";

    public WineOverrideRecord EnsureNativeBuiltin(string prefixPath, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        applicationName = NormalizeApplicationName(applicationName);
        var path = RegistryPath(prefixPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Wine/Proton user.reg was not found. Start the game once so the prefix is initialized.", path);

        var text = File.ReadAllText(path);
        var sectionName = SectionFor(applicationName);
        var sectionExistedBefore = FindSection(SplitLines(text), sectionName).Start >= 0;
        var previous = TryReadOverride(text, dllName, applicationName);
        if (string.Equals(previous, "native,builtin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(previous, "native", StringComparison.OrdinalIgnoreCase))
        {
            return new WineOverrideRecord
            {
                DllName = dllName, PreviousValue = previous, ChangedByRhi = false,
                SectionCreatedByRhi = false, ApplicationName = applicationName
            };
        }

        BackupRegistryOnce(path);
        var updated = SetOverride(text, dllName, "native,builtin", applicationName);
        AtomicWrite(path, updated);
        return new WineOverrideRecord
        {
            DllName = dllName, PreviousValue = previous, ChangedByRhi = true,
            SectionCreatedByRhi = !sectionExistedBefore, ApplicationName = applicationName
        };
    }

    public void Restore(string prefixPath, WineOverrideRecord? record)
    {
        if (record is null || !record.ChangedByRhi) return;
        var path = RegistryPath(prefixPath);
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var updated = record.PreviousValue is null
            ? RemoveOverride(text, record.DllName, record.ApplicationName)
            : SetOverride(text, record.DllName, record.PreviousValue, record.ApplicationName);

        // Only remove a now-empty section when this install explicitly recorded that
        // RHI created it. Older deployment records deserialize this flag as false,
        // which intentionally favors leaving an empty section over deleting a key that
        // may have existed before RHI learned to track section ownership.
        if (record.PreviousValue is null && record.SectionCreatedByRhi)
            updated = RemoveSectionIfEmpty(updated, record.ApplicationName);

        AtomicWrite(path, updated);
    }

    public static string? TryReadOverride(string text, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        var sectionName = SectionFor(NormalizeApplicationName(applicationName));
        var lines = SplitLines(text);
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return null;
        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rhs = line[(line.IndexOf('=') + 1)..].Trim();
            if (rhs.Length >= 2 && rhs[0] == '"' && rhs[^1] == '"') return rhs[1..^1];
            return rhs;
        }
        return null;
    }

    internal static string SetOverride(string text, string dllName, string value, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        applicationName = NormalizeApplicationName(applicationName);
        var sectionName = SectionFor(applicationName);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        var entry = $"\"{dllName}\"=\"{value}\"";
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fileTime = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600L) * 10_000_000L;
            lines.Add($"{sectionName} {now}");
            lines.Add($"#time={fileTime.ToString("x", CultureInfo.InvariantCulture)}");
            lines.Add(entry);
            return string.Join(newline, lines) + newline;
        }

        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = entry;
            return string.Join(newline, lines);
        }
        lines.Insert(end, entry);
        return string.Join(newline, lines);
    }

    internal static string RemoveOverride(string text, string dllName, string? applicationName = null)
    {
        dllName = NormalizeDllName(dllName);
        var sectionName = SectionFor(NormalizeApplicationName(applicationName));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = SplitLines(text).ToList();
        var (start, end) = FindSection(lines, sectionName);
        if (start < 0) return text;
        var prefix = $"\"{dllName}\"=";
        for (var i = start + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            lines.RemoveAt(i);
            break;
        }
        return string.Join(newline, lines);
    }


    internal static string RemoveSectionIfEmpty(string text, string? applicationName = null)
    {
        applicationName = NormalizeApplicationName(applicationName);
        var sectionName = SectionFor(applicationName);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
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
        lines.RemoveRange(start, removeEnd - start);

        // Avoid creating a trailing stack of empty lines after section removal.
        while (lines.Count > 1 && lines[^1].Length == 0 && lines[^2].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return string.Join(newline, lines);
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

    private static string SectionFor(string? applicationName)
        => string.IsNullOrWhiteSpace(applicationName)
            ? GlobalSectionName
            : $@"[Software\\Wine\\AppDefaults\\{applicationName}\\DllOverrides]";

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string RegistryPath(string prefixPath) => Path.Combine(prefixPath, "user.reg");
    private static string NormalizeDllName(string name) => Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    private static string? NormalizeApplicationName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name.Trim());

    private static void BackupRegistryOnce(string path)
    {
        var backup = path + ".rhi-before-dll-overrides";
        if (!File.Exists(backup)) File.Copy(path, backup, overwrite: false);
    }

    private static void AtomicWrite(string path, string text)
    {
        var temp = path + $".rhi-tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
