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
/// What an install (or "Deploy ReShade.ini") writes into a game's ReShade.ini besides RHI's shader search paths (#181;
/// upstream 2.8.7 <c>AuxInstallService.MergeRsIni</c>): the user's template and the Settings values. Null members are
/// left alone. Built by <c>ReShadeGlobalSettingsApplier.InstallDefaults</c>.
/// </summary>
public sealed record ReShadeIniDefaults
{
    /// <summary>The template's bytes (<see cref="UserIniTemplates"/>); its keys win over the game's (upstream).</summary>
    public byte[]? Template { get; init; }

    /// <summary>Leave the template's <c>PreprocessorDefinitions</c> out (upstream strips the RDR2 template's Vulkan depth settings on DirectX installs).</summary>
    public bool SkipPreprocessorDefinitions { get; init; }

    /// <summary><c>[SCREENSHOT] SavePath</c>, already in the Windows form the game sees.</summary>
    public string? ScreenshotSavePath { get; init; }

    /// <summary><c>[INPUT] KeyOverlay</c> (upstream writes it at install only when it differs from Home).</summary>
    public string? OverlayHotkey { get; init; }

    /// <summary><c>[INPUT] KeyScreenshot</c> (upstream writes it at install only when it differs from Print Screen).</summary>
    public string? ScreenshotHotkey { get; init; }

    /// <summary><c>[OVERLAY] VariableListUseTabs</c>: true = Tabs, false = Tree.</summary>
    public bool? VariableListUseTabs { get; init; }
}

/// <summary>
/// Conservative ReShade.ini editor. Existing user settings are preserved byte-for-byte except for RHI-owned shader
/// search-path tokens and, when the caller passes <see cref="ReShadeIniDefaults"/>, the keys its template and settings
/// name (#181). Every write goes through the one user-file path (atomic, a read-only file stays read-only).
/// </summary>
internal static class ReShadeIniManager
{
    private const string IniName = "ReShade.ini";
    internal const string EffectKey = "EffectSearchPaths";
    internal const string TextureKey = "TextureSearchPaths";

    public static ReShadeIniApplyResult Apply(
        string gameDirectory,
        IReadOnlyList<string> effectPaths,
        IReadOnlyList<string> texturePaths,
        ReShadeIniDefaults? defaults = null)
    {
        var path = ResolveIniPath(gameDirectory);
        var existed = File.Exists(path);
        var effectAdded = new List<string>();
        var textureAdded = new List<string>();

        if (!existed && defaults?.Template is null)
        {
            var effects = effectPaths.Count > 0 ? string.Join(',', effectPaths) : @".\reshade-shaders\Shaders\**";
            var textures = texturePaths.Count > 0 ? string.Join(',', texturePaths) : @".\reshade-shaders\Textures\**";
            effectAdded.AddRange(effectPaths);
            textureAdded.AddRange(texturePaths);
            List<string> baseline =
            [
                "[ADDON]",
                "DisabledAddons=Generic Depth,Effect Runtime Sync",
                "",
                "[GENERAL]",
                $"EffectSearchPaths={effects}",
                $"TextureSearchPaths={textures}",
                "",
                "[OVERLAY]",
                "TutorialProgress=4"
            ];
            if (defaults is not null) ApplySettings(baseline, defaults);
            Write(path, baseline, gameDirectory);
            return new(path, false, true, true, true, effectAdded, textureAdded, FileHash.Sha256Hex(path));
        }

        // A new file starts as a copy of the template (upstream copies it as-is); an existing one gets the template merged.
        var lines = existed ? File.ReadAllLines(path).ToList()
            : TemplateLines(defaults!.Template!, defaults.SkipPreprocessorDefinitions);
        var before = existed ? lines.ToList() : null;
        var effectKeyWasMissing = !existed || IniLines.Get(lines, "GENERAL", EffectKey) is null;
        var textureKeyWasMissing = !existed || IniLines.Get(lines, "GENERAL", TextureKey) is null;
        if (existed && defaults?.Template is { } template)
            MergeTemplate(lines, IniLines.FromBytes(template), defaults.SkipPreprocessorDefinitions, effectAdded, textureAdded, recordTemplatePaths: true);
        if (defaults is not null) ApplySettings(lines, defaults);
        MergeSearchPath(lines, "GENERAL", EffectKey, effectPaths, effectAdded);
        MergeSearchPath(lines, "GENERAL", TextureKey, texturePaths, textureAdded);
        var effectKeyCreated = effectKeyWasMissing && IniLines.Get(lines, "GENERAL", EffectKey) is not null;
        var textureKeyCreated = textureKeyWasMissing && IniLines.Get(lines, "GENERAL", TextureKey) is not null;
        if (before is null || !lines.SequenceEqual(before, StringComparer.Ordinal))
            Write(path, lines, gameDirectory);

        return new(path, existed, !existed, effectKeyCreated, textureKeyCreated, effectAdded, textureAdded, FileHash.Sha256Hex(path));
    }

    /// <summary>The template's lines for a new ReShade.ini (its <c>[GENERAL] PreprocessorDefinitions</c> dropped when asked).</summary>
    internal static List<string> TemplateLines(byte[] template, bool skipPreprocessorDefinitions)
    {
        var lines = IniLines.FromBytes(template);
        if (skipPreprocessorDefinitions) IniLines.Remove(lines, "GENERAL", "PreprocessorDefinitions");
        return lines;
    }

    /// <summary>
    /// Upstream's merge: every template key overwrites the game's value (keys the template does not name stay), except
    /// the two search-path lists, whose template entries are added to the game's list (RHI owns those lists entry by
    /// entry, so removal can take back exactly what it added). A template <c>PreprocessorDefinitions</c> is left out
    /// when <paramref name="skipPreprocessorDefinitions"/> is set; the game's own line is never touched.
    /// </summary>
    internal static void MergeTemplate(List<string> lines, IReadOnlyList<string> template, bool skipPreprocessorDefinitions,
        List<string> effectAdded, List<string> textureAdded, bool recordTemplatePaths)
    {
        foreach (var (section, key, value) in IniLines.Entries(template))
        {
            if (section.Length == 0) continue; // a key outside any section has nowhere to go in ReShade.ini
            if (section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("PreprocessorDefinitions", StringComparison.OrdinalIgnoreCase) && skipPreprocessorDefinitions) continue;
                if (key.Equals(EffectKey, StringComparison.OrdinalIgnoreCase) || key.Equals(TextureKey, StringComparison.OrdinalIgnoreCase))
                {
                    var added = key.Equals(EffectKey, StringComparison.OrdinalIgnoreCase) ? effectAdded : textureAdded;
                    var scratch = new List<string>();
                    MergeSearchPath(lines, "GENERAL", key, SplitList(value), scratch);
                    if (recordTemplatePaths) added.AddRange(scratch);
                    continue;
                }
            }
            IniLines.Set(lines, section, key, value);
        }
    }

    /// <summary>Writes the Settings values <paramref name="defaults"/> carries (each only when set).</summary>
    internal static void ApplySettings(List<string> lines, ReShadeIniDefaults defaults)
    {
        if (defaults.ScreenshotSavePath is { Length: > 0 } savePath) IniLines.Set(lines, "SCREENSHOT", "SavePath", savePath);
        if (defaults.OverlayHotkey is { Length: > 0 } overlay) IniLines.Set(lines, "INPUT", "KeyOverlay", overlay);
        if (defaults.ScreenshotHotkey is { Length: > 0 } screenshot) IniLines.Set(lines, "INPUT", "KeyScreenshot", screenshot);
        if (defaults.VariableListUseTabs is { } tabs) IniLines.Set(lines, "OVERLAY", "VariableListUseTabs", tabs ? "1" : "0");
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
            AtomicFile.Delete(path);
            return;
        }

        // User modified the file (or it pre-dated RHI): remove only path tokens inserted by RHI.
        var lines = File.ReadAllLines(path).ToList();
        var changed = false;
        changed |= RemoveSearchPaths(lines, "GENERAL", EffectKey, effectPathsAdded, effectKeyCreated);
        changed |= RemoveSearchPaths(lines, "GENERAL", TextureKey, texturePathsAdded, textureKeyCreated);
        if (changed)
            Write(path, lines, Path.GetDirectoryName(path) ?? path);
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

    // One write path for ReShade.ini: atomic, a user's read-only lock kept, a symlink followed only inside the game
    // folder or the user's ReShade config folder (RenoDX's policy for the same file).
    private static void Write(string path, IReadOnlyList<string> lines, string gameDirectory)
        => AtomicFile.ReplaceUserFileLines(path, lines, new UTF8Encoding(false),
            RenoDxIniOverrideManager.LinkPolicy(gameDirectory, null), ReadOnlyUserFile.WriteKeepingLock);

    private static bool MergeSearchPath(
        List<string> lines,
        string section,
        string key,
        IReadOnlyList<string> requested,
        List<string> actuallyAdded)
    {
        if (requested.Count == 0) return false;
        var (start, end) = IniLines.FindSection(lines, section);
        if (start < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={string.Join(',', requested)}");
            actuallyAdded.AddRange(requested);
            return true;
        }

        var keyIndex = IniLines.FindKey(lines, start + 1, end, key);
        if (keyIndex < 0)
        {
            lines.Insert(end, $"{key}={string.Join(',', requested)}");
            actuallyAdded.AddRange(requested);
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
        var (start, end) = IniLines.FindSection(lines, section);
        if (start < 0) return false;
        var keyIndex = IniLines.FindKey(lines, start + 1, end, key);
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
}
