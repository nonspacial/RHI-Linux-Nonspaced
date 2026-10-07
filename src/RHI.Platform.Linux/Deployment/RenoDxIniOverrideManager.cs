using System.Text;
using System.Text.Json;
using RHI.Core.Services;
using RHI.Core.Services.RenoDxSafety;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

internal sealed record RenoDxIniOverrideEntry(
    string Key,
    string AppliedValue,
    bool HadPreviousValue,
    string? PreviousValue);

internal sealed record RenoDxIniOverrideState(
    string IniPath,
    bool SectionCreatedByRhi,
    IReadOnlyList<RenoDxIniOverrideEntry> Entries);

internal sealed record RenoDxIniOverrideApplyResult(
    RenoDxIniOverrideState? State,
    int AppliedCount,
    int PreservedUserEdits);

/// <summary>
/// The one grammar every remote [renodx] key/value must meet before it reaches a ReShade.ini (#122): the RHI
/// Unity database, manifest <c>renodxIniOverrides</c> and <c>renodxExtraSettings</c>, and the settings UI. Keys are
/// plain INI names ([A-Za-z0-9_]); values are short tokens of [A-Za-z0-9_.,+-] (or empty, for a bare Unity key), so
/// neither can start a new line, a section, a comment or a shell command if the file is ever read as something else.
/// Upstream data (2.8.5 manifest, live rhi-repo Unity database) only uses "", 0, 1, 2 and 3; an unsafe pair is
/// dropped whole.
/// </summary>
internal static class RenoDxIniGrammar
{
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 256;

    public static bool IsSafeKey(string? key)
        => key is { Length: > 0 and <= MaxKeyLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static bool IsSafeValue(string? value)
        => value is { Length: <= MaxValueLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ',' or '+' or '-');

    public static bool IsSafePair(string? key, string? value) => IsSafeKey(key) && IsSafeValue(value);
}

/// <summary>
/// Conservative [renodx] manifest override editor. The upstream manifest is allowed to
/// provide per-game compatibility values, but Linux teardown must remain reversible: original
/// values are remembered, keys created by RHI are removed on teardown, and user edits made after
/// deployment are never overwritten during a later update or rollback. Every write replaces the
/// owner's ReShade.ini atomically (temp file, fsync, rename, mode kept), so a crash or a full disk
/// leaves the previous bytes in place (#121). A read-only ReShade.ini is refused, and a symlinked one is written
/// through only to a reshade.ini inside the game folder or the user's ReShade config folder.
/// </summary>
internal static class RenoDxIniOverrideManager
{
    internal const string MetadataKey = "RenoDx.ManifestIniOverrides";
    private const string SectionName = "renodx";

    /// <summary>Fixed text before "link -> target" when a symlinked ReShade.ini is refused (localisation key).</summary>
    internal const string LinkRefusalPrefix =
        "ReShade.ini link not followed: its target must be a file named reshade.ini inside the game folder or the ReShade folder in your config directory. Link: ";

    /// <summary>Folders outside the game where a user may keep a shared ReShade.ini a game's link points at.</summary>
    internal static IReadOnlyList<string> UserReShadeConfigDirectories =>
    [
        Path.Combine(LinuxPaths.ConfigHome, "ReShade"),
        Path.Combine(LinuxPaths.ConfigHome, "reshade")
    ];

    /// <summary>
    /// The only ReShade.ini symlinks RHI writes through: final target named reshade.ini (any case) inside the game
    /// root, its binary folder or <see cref="UserReShadeConfigDirectories"/>.
    /// </summary>
    internal static UserFileLinkPolicy LinkPolicy(string binaryDirectory, string? gameRoot)
    {
        var roots = new List<string> { binaryDirectory };
        if (!string.IsNullOrWhiteSpace(gameRoot)) roots.Add(gameRoot);
        roots.AddRange(UserReShadeConfigDirectories);
        return new UserFileLinkPolicy("reshade.ini", roots, LinkRefusalPrefix);
    }

    public static RenoDxIniOverrideApplyResult Apply(
        string binaryDirectory,
        IReadOnlyDictionary<string, string> requested,
        RenoDxIniOverrideState? previousState = null,
        Func<Stream, Stream>? wrapOutput = null,
        string? gameRoot = null)
    {
        if (requested.Count == 0)
            return new(previousState, 0, 0);

        var iniPath = ResolveIniPath(binaryDirectory);
        if (iniPath is null)
            return new(previousState, 0, 0);

        var lines = File.ReadAllLines(iniPath).ToList();
        var previousByKey = (previousState?.Entries ?? Array.Empty<RenoDxIniOverrideEntry>())
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var nextEntries = new Dictionary<string, RenoDxIniOverrideEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previousState?.Entries ?? Array.Empty<RenoDxIniOverrideEntry>())
            nextEntries[entry.Key] = entry;

        var sectionCreated = previousState?.SectionCreatedByRhi == true;
        var changed = false;
        var applied = 0;
        var preservedUserEdits = 0;

        foreach (var pair in requested.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = pair.Key.Trim();
            var requestedValue = pair.Value?.Trim() ?? "";
            // Last line of defence for both remote sources: never write a pair that could add lines or sections.
            if (!RenoDxIniGrammar.IsSafePair(key, requestedValue)) continue;

            // Re-evaluate the section boundary because prior insertions may have shifted it.
            var (sectionStart, sectionEnd) = FindSection(lines, SectionName);
            var keyIndex = sectionStart >= 0 ? FindKey(lines, sectionStart + 1, sectionEnd, key) : -1;
            var currentValue = keyIndex >= 0 ? GetValue(lines[keyIndex]) : null;

            if (previousByKey.TryGetValue(key, out var previous))
            {
                // A missing key/section or a value different from the last RHI-applied value is a user edit.
                // Preserve it instead of forcing the manifest again during an update.
                if (keyIndex < 0 || !string.Equals(currentValue, previous.AppliedValue, StringComparison.Ordinal))
                {
                    preservedUserEdits++;
                    nextEntries[key] = previous;
                    continue;
                }

                if (!string.Equals(currentValue, requestedValue, StringComparison.Ordinal))
                {
                    lines[keyIndex] = $"{key}={requestedValue}";
                    changed = true;
                }
                nextEntries[key] = previous with { AppliedValue = requestedValue };
                applied++;
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

            var entry = new RenoDxIniOverrideEntry(
                key,
                requestedValue,
                keyIndex >= 0,
                keyIndex >= 0 ? currentValue : null);

            if (keyIndex >= 0)
            {
                if (!string.Equals(currentValue, requestedValue, StringComparison.Ordinal))
                {
                    lines[keyIndex] = $"{key}={requestedValue}";
                    changed = true;
                }
            }
            else
            {
                lines.Insert(sectionEnd, $"{key}={requestedValue}");
                changed = true;
            }

            nextEntries[key] = entry;
            applied++;
        }

        if (changed)
            WriteLines(iniPath, lines, wrapOutput, LinkPolicy(binaryDirectory, gameRoot));

        var state = new RenoDxIniOverrideState(
            iniPath,
            sectionCreated,
            nextEntries.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray());
        return new(state, applied, preservedUserEdits);
    }

    public static void Cleanup(RenoDxIniOverrideState? state, Func<Stream, Stream>? wrapOutput = null, string? gameRoot = null)
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

            var currentValue = GetValue(lines[keyIndex]);
            if (!string.Equals(currentValue, entry.AppliedValue, StringComparison.Ordinal))
                continue; // user changed the key after deployment; preserve it.

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
                // Remove one adjacent blank line as well to avoid leaving RHI-only whitespace.
                lines.RemoveAt(start);
                if (start < lines.Count && string.IsNullOrWhiteSpace(lines[start])) lines.RemoveAt(start);
                else if (start > 0 && string.IsNullOrWhiteSpace(lines[start - 1])) lines.RemoveAt(start - 1);
                changed = true;
            }
        }

        if (changed)
            WriteLines(state.IniPath, lines, wrapOutput, LinkPolicy(Path.GetDirectoryName(state.IniPath) ?? state.IniPath, gameRoot));
    }


    public static IReadOnlyDictionary<string, string>? ReadRenoDxSection(string binaryDirectory)
    {
        var iniPath = ResolveIniPath(binaryDirectory);
        if (iniPath is null || !File.Exists(iniPath)) return null;

        var lines = File.ReadAllLines(iniPath);
        var (start, end) = FindSection(lines, SectionName);
        if (start < 0) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = start + 1; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            result[key] = trimmed[(eq + 1)..].Trim();
        }
        return result;
    }

    public static bool SetUserValue(string binaryDirectory, string key, string value, out string? iniPath, string? gameRoot = null)
    {
        iniPath = ResolveIniPath(binaryDirectory);
        if (!RenoDxIniGrammar.IsSafePair(key?.Trim(), value?.Trim() ?? ""))
            throw new ArgumentException($"RenoDX setting refused: not a plain INI key and value: {key}");
        if (iniPath is null || !File.Exists(iniPath) || string.IsNullOrWhiteSpace(key)) return false;

        var lines = File.ReadAllLines(iniPath).ToList();
        var (start, end) = FindSection(lines, SectionName);
        if (start < 0) return false;

        var normalizedKey = key.Trim();
        var normalizedValue = value?.Trim() ?? "";
        var keyIndex = FindKey(lines, start + 1, end, normalizedKey);
        if (keyIndex >= 0)
        {
            var existing = lines[keyIndex].TrimStart();
            var eq = existing.IndexOf('=');
            var existingKey = eq > 0 ? existing[..eq].Trim() : normalizedKey;
            lines[keyIndex] = $"{existingKey}={normalizedValue}";
        }
        else
        {
            lines.Insert(end, $"{normalizedKey}={normalizedValue}");
        }

        WriteLines(iniPath, lines, wrapOutput: null, LinkPolicy(binaryDirectory, gameRoot));
        return true;
    }

    // Same bytes File.WriteAllLines produced (UTF-8 without BOM, "\n" after every line), written atomically.
    private static void WriteLines(string path, IReadOnlyList<string> lines, Func<Stream, Stream>? wrapOutput, UserFileLinkPolicy linkPolicy)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append('\n');
        UserFileWriter.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text.ToString()), linkPolicy, wrapOutput);
    }

    public static string Serialize(RenoDxIniOverrideState state)
        => JsonSerializer.Serialize(state);

    public static RenoDxIniOverrideState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RenoDxIniOverrideState>(json); }
        catch { return null; }
    }

    public static string? ResolveIniPath(string binaryDirectory)
    {
        if (!Directory.Exists(binaryDirectory)) return null;
        var preferred = Path.Combine(binaryDirectory, "ReShade.ini");
        if (File.Exists(preferred)) return preferred;
        var lower = Path.Combine(binaryDirectory, "reshade.ini");
        if (File.Exists(lower)) return lower;
        return Directory.EnumerateFiles(binaryDirectory, "*.ini", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(x => Path.GetFileName(x).Equals("reshade.ini", StringComparison.OrdinalIgnoreCase));
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
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            // Keep a section that gained comments or any user content.
            return false;
        }
        return true;
    }
}
