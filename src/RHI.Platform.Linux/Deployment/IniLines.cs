using System.Text;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One file of an <see cref="IniLines.WriteAll"/>: its path, its new lines and the link rule it is written under.</summary>
public sealed record IniFileWrite(string Path, IReadOnlyList<string> Lines, UserFileLinkPolicy Policy);

/// <summary>
/// Line-preserving INI edits for the user files RHI writes settings into (ReShade.ini, relimiter.ini, the user's INI
/// templates; w5/ini-presets). Unlike upstream's parse-and-rewrite (which drops comments, blank lines and duplicate
/// keys), only the lines a setting owns change: comments, order and every other key stay byte for byte. Section and
/// key names compare case-insensitively, as ReShade and upstream read them.
/// </summary>
public static class IniLines
{
    /// <summary>A plain INI write under the default rule (same-named file inside <paramref name="allowedRoots"/>).</summary>
    public static IniFileWrite PlannedWrite(string path, IReadOnlyList<string> lines, IReadOnlyList<string> allowedRoots)
        => new(path, lines, Policy(path, allowedRoots));

    /// <summary>The file's lines (a leading UTF-8 BOM dropped), or an empty list when it does not exist.</summary>
    public static List<string> Read(string path)
        => File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

    /// <summary>Lines of template bytes (UTF-8, a leading BOM dropped, any line ending).</summary>
    public static List<string> FromBytes(byte[] bytes)
    {
        var text = new UTF8Encoding(false).GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']');
    }

    public static string SectionName(string header) => header.Trim().TrimStart('[').TrimEnd(']').Trim();

    /// <summary>The header line of <paramref name="section"/> and the index after its last line, or (-1, -1).</summary>
    public static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsSectionHeader(lines[i]) || !SectionName(lines[i]).Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
                if (IsSectionHeader(lines[j])) { end = j; break; }
            return (i, end);
        }
        return (-1, -1);
    }

    public static int FindKey(IReadOnlyList<string> lines, int start, int end, string key)
    {
        for (var i = start; i < end; i++)
            if (KeyOf(lines[i]) is { } name && name.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The key of a <c>key=value</c> line, or null for comments, blanks and headers.</summary>
    public static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || IsSectionHeader(trimmed)) return null;
        var eq = trimmed.IndexOf('=');
        return eq <= 0 ? null : trimmed[..eq].Trim();
    }

    public static string ValueOf(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? "" : line[(eq + 1)..].Trim();
    }

    public static string? Get(IReadOnlyList<string> lines, string section, string key)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return null;
        var index = FindKey(lines, start + 1, end, key);
        return index < 0 ? null : ValueOf(lines[index]);
    }

    /// <summary>Sets <paramref name="key"/> in <paramref name="section"/> (both created when missing). True when a line changed.</summary>
    public static bool Set(List<string> lines, string section, string key, string value)
    {
        var (start, end) = FindSection(lines, section);
        var line = $"{key}={value}";
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(line);
            return true;
        }
        var index = FindKey(lines, start + 1, end, key);
        if (index >= 0)
        {
            var existingKey = KeyOf(lines[index]) ?? key;
            var replacement = $"{existingKey}={value}";
            if (string.Equals(lines[index], replacement, StringComparison.Ordinal)) return false;
            if (string.Equals(ValueOf(lines[index]), value, StringComparison.Ordinal)) return false;
            lines[index] = replacement;
            return true;
        }
        // Insert after the section's last non-blank line, so a blank separator before the next section stays.
        var insertAt = end;
        while (insertAt > start + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
        lines.Insert(insertAt, line);
        return true;
    }

    /// <summary>Removes every <paramref name="key"/> line from <paramref name="section"/>. True when one was removed.</summary>
    public static bool Remove(List<string> lines, string section, string key)
    {
        var removed = false;
        while (true)
        {
            var (start, end) = FindSection(lines, section);
            if (start < 0) return removed;
            var index = FindKey(lines, start + 1, end, key);
            if (index < 0) return removed;
            lines.RemoveAt(index);
            removed = true;
        }
    }

    /// <summary>Every section in file order (a key before any header belongs to the empty-named section).</summary>
    public static IReadOnlyList<string> Sections(IReadOnlyList<string> lines)
        => lines.Where(IsSectionHeader).Select(SectionName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every <c>section/key=value</c> in file order (keys before the first header have section "").</summary>
    public static IEnumerable<(string Section, string Key, string Value)> Entries(IReadOnlyList<string> lines)
    {
        var section = "";
        foreach (var line in lines)
        {
            if (IsSectionHeader(line)) { section = SectionName(line); continue; }
            if (KeyOf(line) is { } key) yield return (section, key, ValueOf(line));
        }
    }

    /// <summary>
    /// Writes <paramref name="lines"/> through the one user-file path (temp, fsync, verify, rename; mode kept). A
    /// user-locked (read-only) file is written and stays locked (owner rule: a lock never blocks RHI's INI writes); a
    /// symlinked file is followed only to a file of the same name inside <paramref name="allowedRoots"/>.
    /// </summary>
    public static void Write(string path, IReadOnlyList<string> lines, IReadOnlyList<string> allowedRoots)
        => AtomicFile.ReplaceUserFileLines(path, lines, linkPolicy: Policy(path, allowedRoots), readOnly: ReadOnlyUserFile.WriteKeepingLock);

    /// <summary><see cref="Write"/> for bytes kept as they are (a backup put back).</summary>
    public static void WriteBytes(string path, byte[] bytes, IReadOnlyList<string> allowedRoots)
        => AtomicFile.ReplaceUserFile(path, bytes, Policy(path, allowedRoots), readOnly: ReadOnlyUserFile.WriteKeepingLock);

    /// <summary>Throws (the same refusal <see cref="Write"/> would) when a write to <paramref name="path"/> would be refused for its links.</summary>
    public static void CheckLinks(string path, IReadOnlyList<string> allowedRoots) => ResolveTarget(path, allowedRoots);

    /// <summary>The file a <see cref="Write"/> to <paramref name="path"/> replaces (a link's allowed target); throws its refusal.</summary>
    public static string ResolveTarget(string path, IReadOnlyList<string> allowedRoots)
        => AtomicFile.ResolveUserFileTarget(path, Policy(path, allowedRoots));

    private static UserFileLinkPolicy Policy(string path, IReadOnlyList<string> allowedRoots)
        => new(Path.GetFileName(path), allowedRoots, LinkRefusalPrefix);

    /// <summary>
    /// Writes several INI files of one game whole or not at all (review finding 6): every link is checked and every file's
    /// bytes read before the first write; when a write fails, each file already written is put back as it was (one that
    /// did not exist is deleted) and the failure is rethrown, naming any file that could not be put back. Each write is
    /// the one user-file replace (a read-only lock kept). Never cancelled.
    /// </summary>
    public static void WriteAll(IReadOnlyList<IniFileWrite> files)
    {
        var targets = files.Select(file => AtomicFile.ResolveUserFileTarget(file.Path, file.Policy)).ToList();
        var before = targets.Select(target => File.Exists(target) ? File.ReadAllBytes(target) : null).ToList();
        var written = new List<int>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (WriteFaultForTesting.Value?.Invoke(files[i].Path) is { } fault) throw fault;
                AtomicFile.ReplaceUserFileLines(files[i].Path, files[i].Lines, linkPolicy: files[i].Policy, readOnly: ReadOnlyUserFile.WriteKeepingLock);
                written.Add(i);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var notPutBack = new List<string>();
            foreach (var i in Enumerable.Reverse(written))
            {
                try
                {
                    if (before[i] is { } bytes) AtomicFile.ReplaceUserFile(files[i].Path, bytes, files[i].Policy, readOnly: ReadOnlyUserFile.WriteKeepingLock);
                    else AtomicFile.Delete(targets[i]);
                }
                catch (Exception putBack) when (putBack is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    RHI.Core.Services.AppLog.Warn("ini-write", $"Could not put back {targets[i]}", putBack);
                    notPutBack.Add(targets[i]);
                }
            }
            if (notPutBack.Count > 0) throw new IOException(ex.Message + NotPutBackPrefix + string.Join(", ", notPutBack), ex);
            throw;
        }
    }

    /// <summary>Test seam: fails the write of the file whose path it is given (null = write it).</summary>
    internal static readonly AsyncLocal<Func<string, Exception?>?> WriteFaultForTesting = new();

    /// <summary>Fixed text before the files an incomplete <see cref="WriteAll"/> could not put back (localisation key).</summary>
    public const string NotPutBackPrefix = " These files were already written and could not be put back: ";

    /// <summary>Fixed text before "link -> target" when a symlinked INI points outside the game (localisation key).</summary>
    public const string LinkRefusalPrefix = "INI link not followed: its target must be a file of the same name inside the game folder. Link: ";
}
