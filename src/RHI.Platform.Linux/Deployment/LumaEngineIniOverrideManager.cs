using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

internal sealed record LumaEngineIniOwnedEntry(string Section, string Key, string AppliedValue)
{
    /// <summary>#189: written by the Luma ⚙ "TAA" setting, not by the wiki keys an install reconciles (kept by installs).</summary>
    public bool Taa { get; init; }
}

internal sealed record LumaEngineIniOverrideState(
    string IniPath,
    bool FileCreatedByRhi,
    IReadOnlyList<LumaEngineIniOwnedEntry> Entries);

/// <summary>
/// Linux/Wine ownership layer for the generic-Luma Engine.ini tweaks used by upstream RHI.
/// Upstream semantics are preserved: an already-present key is never overwritten. Linux teardown is
/// stricter: only keys RHI actually inserted are removed, and user edits after install are retained.
/// </summary>
internal static class LumaEngineIniOverrideManager
{
    internal const string MetadataKey = "LumaFramework.EngineIni";

    public static LumaEngineIniOverrideState? Apply(
        DetectedGame game,
        IReadOnlyList<LumaEngineIniEntry> requested,
        LumaEngineIniOverrideState? previousState = null)
    {
        if (requested.Count == 0)
        {
            if (previousState is null) return null;
            // #189: the ⚙'s TAA keys are the user's setting; only the install's own keys go.
            var taa = previousState.Entries.Where(entry => entry.Taa).ToArray();
            Cleanup(previousState with { Entries = previousState.Entries.Where(entry => !entry.Taa).ToArray() });
            return taa.Length == 0 ? null : previousState with { Entries = taa };
        }

        var iniPath = ResolveEngineIniPath(game);
        if (iniPath is null)
            return previousState; // Metadata is advisory when no safe Wine/UE config path can be proven.

        if (previousState is not null &&
            !Path.GetFullPath(previousState.IniPath).Equals(Path.GetFullPath(iniPath), StringComparison.OrdinalIgnoreCase))
        {
            Cleanup(previousState);
            previousState = null;
        }

        var existed = File.Exists(iniPath);
        var lines = existed ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var owned = new Dictionary<string, LumaEngineIniOwnedEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previousState?.Entries ?? Array.Empty<LumaEngineIniOwnedEntry>())
            owned[Identity(item.Section, item.Key)] = item;

        var requestedByIdentity = requested
            .Where(item => !string.IsNullOrWhiteSpace(item.Section) && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => Identity(item.Section, item.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // Reconcile old RHI-owned keys first. Obsolete keys are removed only while still equal to
        // the value RHI wrote; a user edit relinquishes ownership and is never clobbered.
        foreach (var old in owned.Values.ToArray())
        {
            var identity = Identity(old.Section, old.Key);
            var index = FindKeyInSection(lines, old.Section, old.Key);
            if (index < 0)
            {
                owned.Remove(identity);
                continue;
            }

            var current = GetValue(lines[index]);
            if (!string.Equals(current, old.AppliedValue, StringComparison.Ordinal))
            {
                owned.Remove(identity);
                continue;
            }

            if (old.Taa) continue; // #189: the ⚙'s TAA keys are the user's setting, not the install's request
            if (!requestedByIdentity.TryGetValue(identity, out var next))
            {
                lines.RemoveAt(index);
                RemoveSectionIfEmpty(lines, old.Section);
                owned.Remove(identity);
                changed = true;
                continue;
            }

            if (!string.Equals(current, next.Value, StringComparison.Ordinal))
            {
                lines[index] = $"{next.Key}={next.Value}";
                owned[identity] = new LumaEngineIniOwnedEntry(next.Section, next.Key, next.Value);
                changed = true;
            }
        }

        foreach (var item in requestedByIdentity.Values)
        {
            var identity = Identity(item.Section, item.Key);
            if (owned.ContainsKey(identity)) continue;

            // Pinned RHI checks the key globally and only appends it when completely absent.
            if (FindKeyAnywhere(lines, item.Key) >= 0) continue;

            InsertIntoSection(lines, item.Section, $"{item.Key}={item.Value}");
            owned[identity] = new LumaEngineIniOwnedEntry(item.Section, item.Key, item.Value);
            changed = true;
        }

        if (changed || (!existed && owned.Count > 0))
        {
            var parent = Path.GetDirectoryName(iniPath)
                ?? throw new InvalidOperationException("Resolved Engine.ini parent is invalid.");
            Directory.CreateDirectory(parent);
            WriteIni(iniPath, lines);
        }

        if (owned.Count == 0) return null;
        return new LumaEngineIniOverrideState(
            iniPath,
            previousState?.FileCreatedByRhi == true || !existed,
            owned.Values
                .OrderBy(value => value.Section, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public static void Cleanup(LumaEngineIniOverrideState? state)
    {
        if (state is null || !File.Exists(state.IniPath)) return;
        var lines = File.ReadAllLines(state.IniPath).ToList();
        var changed = false;

        foreach (var item in state.Entries)
        {
            var index = FindKeyInSection(lines, item.Section, item.Key);
            if (index < 0) continue;
            if (!string.Equals(GetValue(lines[index]), item.AppliedValue, StringComparison.Ordinal))
                continue;
            lines.RemoveAt(index);
            RemoveSectionIfEmpty(lines, item.Section);
            changed = true;
        }

        if (!changed) return;
        TrimTrailingBlankLines(lines);
        if (state.FileCreatedByRhi && lines.All(string.IsNullOrWhiteSpace))
        {
            File.Delete(state.IniPath);
            return;
        }
        WriteIni(state.IniPath, lines);
    }

    /// <summary>#189: upstream's Luma ⚙ "TAA" keys (forces TAA at high quality for Luma's HDR).</summary>
    internal static readonly IReadOnlyList<LumaEngineIniEntry> TaaKeys =
    [
        new("SystemSettings", "r.DefaultFeature.AntiAliasing", "2"),
        new("SystemSettings", "r.PostProcessAAQuality", "4")
    ];

    /// <summary>#189: TAA counts as on when RHI wrote its keys, or when Engine.ini already holds both with the TAA values.</summary>
    public static bool IsTaaOn(LumaEngineIniOverrideState? state, string? iniPath)
    {
        if (state?.Entries.Any(entry => entry.Taa) == true) return true;
        if (iniPath is null || !File.Exists(iniPath)) return false;
        var lines = File.ReadAllLines(iniPath);
        return TaaKeys.All(item => FindKeyAnywhere(lines, item.Key) is var index and >= 0 && GetValue(lines[index]) == item.Value);
    }

    /// <summary>
    /// #189: TAA on writes upstream's two keys where Engine.ini does not have them yet (a key already there is never
    /// overwritten, as upstream <c>ApplyEngineIniCustomKeys</c>); off removes only the TAA keys RHI wrote and that still
    /// hold its value (upstream removes the keys whoever wrote them). Returns the state to keep on the Luma record; the
    /// install's wiki keys in it are untouched.
    /// </summary>
    public static LumaEngineIniOverrideState? SetTaa(string iniPath, LumaEngineIniOverrideState? state, bool on)
    {
        var existed = File.Exists(iniPath);
        var lines = existed ? File.ReadAllLines(iniPath).ToList() : new List<string>();
        var owned = (state?.Entries ?? []).ToList();
        var changed = false;
        if (on)
        {
            foreach (var item in TaaKeys)
            {
                if (owned.Any(entry => entry.Taa && entry.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) continue;
                if (FindKeyAnywhere(lines, item.Key) >= 0) continue;
                InsertIntoSection(lines, item.Section, $"{item.Key}={item.Value}");
                owned.Add(new LumaEngineIniOwnedEntry(item.Section, item.Key, item.Value) { Taa = true });
                changed = true;
            }
        }
        else
        {
            foreach (var entry in owned.Where(entry => entry.Taa).ToList())
            {
                owned.Remove(entry);
                var index = FindKeyInSection(lines, entry.Section, entry.Key);
                if (index < 0 || !string.Equals(GetValue(lines[index]), entry.AppliedValue, StringComparison.Ordinal)) continue;
                lines.RemoveAt(index);
                RemoveSectionIfEmpty(lines, entry.Section);
                changed = true;
            }
        }
        if (changed) WriteIni(iniPath, lines);
        var created = state?.FileCreatedByRhi == true || (!existed && changed);
        return owned.Count == 0 && !created ? null : new LumaEngineIniOverrideState(iniPath, created, owned);
    }

    internal static string? ResolveEngineIniPath(DetectedGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var localAppDataRoots = ResolveLocalAppDataRoots(game.PrefixPath).ToArray();
        var overrideValue = game.EngineIniProjectOverride?.Trim();

        if (!string.IsNullOrWhiteSpace(overrideValue) && localAppDataRoots.Length > 0)
        {
            foreach (var rawCandidate in overrideValue.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var localAppData in localAppDataRoots)
                {
                    var mapped = MapManifestOverride(localAppData, rawCandidate);
                    if (mapped is null) continue;
                    var resolved = ResolveEngineIniFromCandidate(mapped, allowCreate: true);
                    if (resolved is not null) return resolved;
                }
            }
        }

        // Prefer already-created UE config trees inside the game directory itself.
        foreach (var projectRoot in ProjectRootCandidates(game))
        {
            var savedConfig = Path.Combine(projectRoot, "Saved", "Config");
            var existing = ResolvePlatformConfig(savedConfig, allowCreate: false);
            if (existing is not null) return Path.Combine(existing, "Engine.ini");
        }

        if (localAppDataRoots.Length == 0) return null;
        foreach (var projectName in ProjectNameCandidates(game))
        {
            foreach (var localAppData in localAppDataRoots)
            {
                var projectRoot = Path.Combine(localAppData, projectName, "Saved", "Config");
                var existing = ResolvePlatformConfig(projectRoot, allowCreate: false);
                if (existing is not null) return Path.Combine(existing, "Engine.ini");
            }
        }

        return null; // No safe/authoritative config destination was proven.
    }

    public static string Serialize(LumaEngineIniOverrideState state) => JsonSerializer.Serialize(state);

    public static LumaEngineIniOverrideState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LumaEngineIniOverrideState>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("luma-engine-ini", "Stored deployment state could not be read; it is treated as absent", ex);
            return null;
        }
    }

    private static IEnumerable<string> ResolveLocalAppDataRoots(string? prefixPath)
    {
        if (string.IsNullOrWhiteSpace(prefixPath) || !Directory.Exists(prefixPath)) yield break;
        var users = Path.Combine(prefixPath, "drive_c", "users");
        if (!Directory.Exists(users)) yield break;

        IEnumerable<string> userDirs;
        try { userDirs = Directory.EnumerateDirectories(users); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("luma-engine-ini", $"Could not list the prefix users in {users}", ex);
            yield break;
        }

        foreach (var user in userDirs
                     .OrderByDescending(path => Path.GetFileName(path).Equals("steamuser", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var local = Path.Combine(user, "AppData", "Local");
            if (Directory.Exists(local)) yield return local;
        }
    }

    private static string? MapManifestOverride(string localAppData, string value)
    {
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Trim();
        const string localToken = "%LOCALAPPDATA%";
        if (normalized.StartsWith(localToken, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = normalized[localToken.Length..].TrimStart(Path.DirectorySeparatorChar);
            return CombineInsideRoot(localAppData, suffix);
        }

        // A plain project name is authoritative and maps to the conventional UE Saved/Config tree.
        if (!Path.IsPathRooted(normalized) && !Regex.IsMatch(normalized, @"^[A-Za-z]:"))
            return CombineInsideRoot(localAppData, Path.Combine(normalized, "Saved", "Config"));

        // Never let a Windows absolute path escape the selected Wine prefix.
        return null;
    }

    private static string? CombineInsideRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        return candidate.StartsWith(fullRoot, StringComparison.Ordinal) ? candidate : null;
    }

    private static string? ResolveEngineIniFromCandidate(string candidate, bool allowCreate)
    {
        if (Path.GetFileName(candidate).Equals("Engine.ini", StringComparison.OrdinalIgnoreCase))
            return File.Exists(candidate) || allowCreate ? candidate : null;

        var leaf = Path.GetFileName(candidate);
        if (leaf.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("WindowsNoEditor", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("WinGDK", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate) || allowCreate) return Path.Combine(candidate, "Engine.ini");
        }

        var platform = ResolvePlatformConfig(candidate, allowCreate);
        return platform is null ? null : Path.Combine(platform, "Engine.ini");
    }

    private static string? ResolvePlatformConfig(string configRoot, bool allowCreate)
    {
        foreach (var platform in new[] { "WinGDK", "Windows", "WindowsNoEditor" })
        {
            var candidate = Path.Combine(configRoot, platform);
            if (Directory.Exists(candidate)) return candidate;
        }
        if (!allowCreate) return null;
        return Path.Combine(configRoot, "Windows");
    }

    private static IEnumerable<string> ProjectRootCandidates(DetectedGame game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { game.BinaryDirectory, Path.GetDirectoryName(game.ExecutablePath ?? "") })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var current = new DirectoryInfo(path);
            while (current is not null)
            {
                if (current.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) && current.Parent is not null)
                {
                    if (seen.Add(current.Parent.FullName)) yield return current.Parent.FullName;
                    break;
                }
                current = current.Parent;
            }
        }
    }

    private static IEnumerable<string> ProjectNameCandidates(DetectedGame game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ProjectRootCandidates(game))
        {
            var name = Path.GetFileName(root);
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) yield return name;
        }

        if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            var stem = Path.GetFileNameWithoutExtension(game.ExecutablePath);
            stem = Regex.Replace(stem, @"-(?:Win64|WinGDK)?-?Shipping$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(stem) && seen.Add(stem)) yield return stem;
        }
    }

    private static string Identity(string section, string key) => section.Trim() + "\0" + key.Trim();

    private static int FindKeyAnywhere(IReadOnlyList<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var parsed = ParseKey(lines[i]);
            if (parsed.Key is not null && parsed.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static int FindKeyInSection(IReadOnlyList<string> lines, string section, string key)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return -1;
        for (var i = start + 1; i < end; i++)
        {
            var parsed = ParseKey(lines[i]);
            if (parsed.Key is not null && parsed.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static (string? Key, string? Value) ParseKey(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('[')) return (null, null);
        var equals = trimmed.IndexOf('=');
        if (equals <= 0) return (null, null);
        return (trimmed[..equals].Trim(), trimmed[(equals + 1)..].Trim());
    }

    private static string GetValue(string line) => ParseKey(line).Value ?? "";

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var header = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
            var end = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var value = lines[j].Trim();
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    end = j;
                    break;
                }
            }
            return (i, end);
        }
        return (-1, -1);
    }

    private static void InsertIntoSection(List<string> lines, string section, string value)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(value);
            return;
        }

        var insert = end;
        while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
        lines.Insert(insert, value);
    }

    private static void RemoveSectionIfEmpty(List<string> lines, string section)
    {
        var (start, end) = FindSection(lines, section);
        if (start < 0) return;
        for (var i = start + 1; i < end; i++)
        {
            // Any non-blank line is user-visible content, including comments. Keep the section
            // header rather than orphaning that content during conservative cleanup.
            if (lines[i].Trim().Length > 0) return;
        }

        lines.RemoveAt(start);
        if (start < lines.Count && string.IsNullOrWhiteSpace(lines[start])) lines.RemoveAt(start);
        else if (start > 0 && string.IsNullOrWhiteSpace(lines[start - 1])) lines.RemoveAt(start - 1);
    }

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
    }

    /// <summary>Engine.ini is the user's file: the one user-file write path (mode kept, verified).</summary>
    private static void WriteIni(string path, IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Engine.ini parent is invalid."));
        WriteUserEngineIni(path, lines);
    }

    /// <summary>
    /// Fixed text before "path -> target" when an Engine.ini reached through a symlink (the file or a folder above it)
    /// lands outside <see cref="LinkPolicy"/> (localisation key).
    /// </summary>
    internal const string LinkRefusalPrefix =
        "Engine.ini link not followed: its target must be a file named Engine.ini inside the same Wine prefix or game project folder. Link: ";

    /// <summary>
    /// Every Engine.ini write (Luma tweaks, upstream templates, their removal and rollback) goes through here, as
    /// upstream 2.8.5 does it: a read-only Engine.ini is written and stays read-only (users lock it so the game cannot
    /// rewrite it on launch; upstream clears read-only, writes, locks again), so the lock never blocks install or
    /// removal. An Engine.ini reached through symlinks (the file itself or any folder on its path) is written only
    /// where <see cref="LinkPolicy"/> allows the resolved file: inside the same prefix or UE project folder.
    /// </summary>
    internal static UserFileWriteResult WriteUserEngineIni(string path, IEnumerable<string> lines)
        => AtomicFile.ReplaceUserFileLines(path, lines, linkPolicy: LinkPolicy(path), readOnly: ReadOnlyUserFile.WriteKeepingLock);

    /// <summary>
    /// Where an Engine.ini write may land once every symlink on its path is resolved (the file itself or any folder
    /// above it): a file named Engine.ini (any case) inside the Wine prefix the path is in (the folder holding the
    /// nearest <c>drive_c</c>) or the UE project folder (the folder holding the nearest <c>Saved</c>). The walk up stops
    /// at the prefix, so a library under, say, <c>/mnt/Saved/…</c> never makes <c>/mnt</c> a root. A path through no
    /// link at all is written as it is.
    /// </summary>
    internal static UserFileLinkPolicy LinkPolicy(string iniPath)
    {
        var roots = new List<string>();
        var savedFound = false;
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(iniPath)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var name = Path.GetFileName(dir);
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent)) break;
            if (!savedFound && name.Equals("Saved", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(parent);
                savedFound = true;
            }
            if (name.Equals("drive_c", StringComparison.Ordinal))
            {
                roots.Add(parent);
                break;
            }
        }
        if (roots.Count == 0) roots.Add(Path.GetDirectoryName(Path.GetFullPath(iniPath))!);
        return new UserFileLinkPolicy("Engine.ini", roots, LinkRefusalPrefix);
    }
}
