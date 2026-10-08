using System.Text;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

internal sealed record ReShadeIniApplyResult(
    string Path,
    bool ExistedBefore,
    bool CreatedByRhi,
    bool EffectKeyCreated,
    bool TextureKeyCreated,
    IReadOnlyList<string> AddedEffectPaths,
    IReadOnlyList<string> AddedTexturePaths,
    string Sha256);

/// <summary>
/// Conservative ReShade.ini editor. Existing user settings are preserved byte-for-byte except
/// for RHI-owned shader search-path tokens that need to be appended/removed.
/// </summary>
internal static class ReShadeIniManager
{
    private const string IniName = "ReShade.ini";

    public static ReShadeIniApplyResult Apply(
        string gameDirectory,
        IReadOnlyList<string> effectPaths,
        IReadOnlyList<string> texturePaths)
    {
        var path = ResolveIniPath(gameDirectory);
        var existed = File.Exists(path);
        var created = !existed;
        var effectAdded = new List<string>();
        var textureAdded = new List<string>();
        var effectKeyCreated = false;
        var textureKeyCreated = false;

        if (!existed)
        {
            var effects = effectPaths.Count > 0 ? string.Join(',', effectPaths) : @".\reshade-shaders\Shaders\**";
            var textures = texturePaths.Count > 0 ? string.Join(',', texturePaths) : @".\reshade-shaders\Textures\**";
            effectAdded.AddRange(effectPaths);
            textureAdded.AddRange(texturePaths);
            var text = "[ADDON]\n" +
                       "DisabledAddons=Generic Depth,Effect Runtime Sync\n\n" +
                       "[GENERAL]\n" +
                       $"EffectSearchPaths={effects}\n" +
                       $"TextureSearchPaths={textures}\n\n" +
                       "[OVERLAY]\n" +
                       "TutorialProgress=4\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return new(path, false, true, true, true, effectAdded, textureAdded, FileHash.Sha256Hex(path));
        }

        var lines = File.ReadAllLines(path).ToList();
        var changed = false;
        changed |= MergeSearchPath(lines, "GENERAL", "EffectSearchPaths", effectPaths, effectAdded, out effectKeyCreated);
        changed |= MergeSearchPath(lines, "GENERAL", "TextureSearchPaths", texturePaths, textureAdded, out textureKeyCreated);
        if (changed)
            File.WriteAllLines(path, lines, new UTF8Encoding(false));

        return new(path, true, false, effectKeyCreated, textureKeyCreated, effectAdded, textureAdded, FileHash.Sha256Hex(path));
    }

    public static void Cleanup(
        string path,
        bool createdByRhi,
        string? lastManagedHash,
        IReadOnlyList<string> effectPathsAdded,
        IReadOnlyList<string> texturePathsAdded,
        bool effectKeyCreated,
        bool textureKeyCreated)
    {
        if (!File.Exists(path)) return;

        // If RHI created the file and it has not changed since our last write, remove it entirely.
        if (createdByRhi && !string.IsNullOrWhiteSpace(lastManagedHash) &&
            string.Equals(FileHash.Sha256Hex(path), lastManagedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            return;
        }

        // User modified the file (or it pre-dated RHI): remove only path tokens inserted by RHI.
        var lines = File.ReadAllLines(path).ToList();
        var changed = false;
        changed |= RemoveSearchPaths(lines, "GENERAL", "EffectSearchPaths", effectPathsAdded, effectKeyCreated);
        changed |= RemoveSearchPaths(lines, "GENERAL", "TextureSearchPaths", texturePathsAdded, textureKeyCreated);
        if (changed)
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    internal static string ResolveIniPath(string gameDirectory)
    {
        var canonical = Path.Combine(gameDirectory, IniName);
        if (File.Exists(canonical)) return canonical;
        if (!Directory.Exists(gameDirectory)) return canonical;
        try
        {
            return Directory.EnumerateFiles(gameDirectory)
                       .FirstOrDefault(path => Path.GetFileName(path).Equals(IniName, StringComparison.OrdinalIgnoreCase))
                   ?? canonical;
        }
        catch
        {
            return canonical;
        }
    }

    private static bool MergeSearchPath(
        List<string> lines,
        string section,
        string key,
        IReadOnlyList<string> requested,
        List<string> actuallyAdded,
        out bool keyCreated)
    {
        keyCreated = false;
        if (requested.Count == 0) return false;
        var (start, end) = FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={string.Join(',', requested)}");
            actuallyAdded.AddRange(requested);
            keyCreated = true;
            return true;
        }

        var keyIndex = FindKey(lines, start + 1, end, key);
        if (keyIndex < 0)
        {
            lines.Insert(end, $"{key}={string.Join(',', requested)}");
            actuallyAdded.AddRange(requested);
            keyCreated = true;
            return true;
        }

        var eq = lines[keyIndex].IndexOf('=');
        var raw = eq >= 0 ? lines[keyIndex][(eq + 1)..] : "";
        var values = SplitList(raw);
        var changed = false;
        foreach (var item in requested)
        {
            if (values.Any(v => v.Equals(item, StringComparison.OrdinalIgnoreCase))) continue;
            values.Add(item);
            actuallyAdded.Add(item);
            changed = true;
        }
        if (changed) lines[keyIndex] = $"{key}={string.Join(',', values)}";
        return changed;
    }

    private static bool RemoveSearchPaths(
        List<string> lines,
        string section,
        string key,
        IReadOnlyList<string> remove,
        bool keyCreated)
    {
        if (remove.Count == 0) return false;
        var (start, end) = FindSection(lines, section);
        if (start < 0) return false;
        var keyIndex = FindKey(lines, start + 1, end, key);
        if (keyIndex < 0) return false;
        var eq = lines[keyIndex].IndexOf('=');
        var raw = eq >= 0 ? lines[keyIndex][(eq + 1)..] : "";
        var values = SplitList(raw);
        var originalCount = values.Count;
        values.RemoveAll(value => remove.Any(item => value.Equals(item, StringComparison.OrdinalIgnoreCase)));
        if (values.Count == originalCount) return false;
        if (values.Count == 0 && keyCreated)
            lines.RemoveAt(keyIndex);
        else
            lines[keyIndex] = $"{key}={string.Join(',', values)}";
        return true;
    }

    private static List<string> SplitList(string raw)
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
