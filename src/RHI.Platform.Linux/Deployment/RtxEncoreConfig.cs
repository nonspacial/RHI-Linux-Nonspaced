using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #280 (upstream 2.8.9 <c>RtxEncoreConfig</c>): reads and edits single values in RTX Encore's <c>rtx-encore.jsonc</c>
/// without rewriting the file. The mod writes a <c>//</c> comment after almost every key, so a JSON round-trip would lose
/// them: an edit replaces only the value token on the key's line (trailing comma and comment kept); a missing key goes at
/// the top of its section, a missing section at the top of its deepest existing parent (or of the file). Nested sections
/// are written <c>parent/child</c>. A missing file is created holding only the chosen setting; RTX Encore fills in the rest
/// with its defaults on the next start. The file's newline style and trailing newline are kept.
/// </summary>
public static class RtxEncoreConfig
{
    public const string FileName = "rtx-encore.jsonc";

    /// <summary>The configVersion of a file RHI creates (what RTX Encore 1.5.3 writes; upstream's value).</summary>
    public const int CreatedConfigVersion = 4;

    public const string CreatedHeader = "// Created by RHI with only the settings chosen in RHI. RTX Encore fills in the rest with its defaults when the game starts.";
    public const string NoOpeningBraceMessage = "rtx-encore.jsonc has no opening brace, so RHI did not change it.";

    /// <summary>The raw value of <c>section.key</c> (strings without quotes), or null when absent.</summary>
    public static string? Get(string? text, string section, string key)
    {
        if (text is null) return null;
        var lines = Split(text).Lines;
        var index = FindKeyLine(lines, Parts(section), key);
        if (index < 0) return null;
        var match = KeyLine(key).Match(lines[index]);
        if (!match.Success) return null;
        var token = match.Groups[2].Value;
        return token.StartsWith('"') ? Unquote(token) : token;
    }

    /// <summary>The JSON token for a string, a bool or a number value (upstream: true/false and integers bare, the rest quoted).</summary>
    public static string Token(string value)
        => value is "true" or "false" || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? value : Quote(value);

    /// <summary>
    /// <paramref name="text"/> (null: no file yet) with <c>section.key</c> set to <paramref name="token"/>. Throws
    /// <see cref="InvalidDataException"/> for a file without an opening brace (nothing to insert into).
    /// </summary>
    public static string Set(string? text, string section, string key, string token)
    {
        var parts = Parts(section);
        if (text is null)
        {
            var created = new List<string> { CreatedHeader, "{", $"  \"configVersion\": {CreatedConfigVersion}," };
            created.AddRange(SectionBlock(parts, 0, key, token, trailingComma: false));
            created.Add("}");
            return string.Join("\r\n", created);
        }

        var (lines, newline, trailingNewline) = Split(text);
        var keyLine = FindKeyLine(lines, parts, key);
        if (keyLine >= 0)
        {
            lines[keyLine] = KeyLine(key).Replace(lines[keyLine], m => m.Groups[1].Value + token + m.Groups[3].Value, 1);
        }
        else
        {
            var sectionLine = FindSectionLine(lines, parts);
            if (sectionLine >= 0)
            {
                // Top of the section, with a comma: the section already holds at least one key after it.
                var indent = Regex.Match(lines[sectionLine], @"^\s*").Value + "  ";
                lines.Insert(sectionLine + 1, $"{indent}\"{key}\": {token},");
            }
            else
            {
                // A missing section (and any missing parents) at the top of the deepest parent that exists, or of the file,
                // with a comma: that parent already holds other entries after it.
                int depth = parts.Length - 1, anchor = -1;
                for (; depth > 0; depth--)
                    if ((anchor = FindSectionLine(lines, parts[..depth])) >= 0) break;
                if (depth == 0)
                {
                    anchor = lines.FindIndex(line => StripComment(line).Trim().StartsWith('{'));
                    if (anchor < 0) throw new InvalidDataException(NoOpeningBraceMessage);
                }
                lines.InsertRange(anchor + 1, SectionBlock(parts, depth, key, token, trailingComma: true));
            }
        }
        if (trailingNewline) lines.Add("");
        return string.Join(newline, lines);
    }

    /// <summary>Reads the file, or null when it does not exist.</summary>
    public static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>Sets one value in the file at <paramref name="path"/> (created when missing), written atomically (UTF-8, no BOM).</summary>
    public static void SetInFile(string path, string section, string key, string value)
    {
        var folder = Path.GetDirectoryName(path)!;
        var text = Set(Read(path), section, key, Token(value));
        IniLines.WriteBytes(path, new UTF8Encoding(false).GetBytes(text), [folder]);
    }

    private static (List<string> Lines, string Newline, bool TrailingNewline) Split(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var trailing = lines.Count > 0 && lines[^1].Length == 0;
        if (trailing) lines.RemoveAt(lines.Count - 1);
        return (lines, newline, trailing);
    }

    private static Regex KeyLine(string key) => new($@"^(\s*""{Regex.Escape(key)}""\s*:\s*)(""(?:[^""\\]|\\.)*""|[^,\s/}}]+)(.*)$");

    private static string[] Parts(string section) => section.Split('/');

    private static List<string> SectionBlock(string[] parts, int from, string key, string token, bool trailingComma)
    {
        static string Indent(int level) => new(' ', level * 2);
        var block = new List<string>();
        for (var j = from; j < parts.Length; j++) block.Add($"{Indent(j + 1)}\"{parts[j]}\": {{");
        block.Add($"{Indent(parts.Length + 1)}\"{key}\": {token}");
        for (var j = parts.Length - 1; j >= from; j--) block.Add(Indent(j + 1) + "}" + (j == from && trailingComma ? "," : ""));
        return block;
    }

    private static int FindKeyLine(IList<string> lines, string[] parts, string key)
    {
        var keyLine = KeyLine(key);
        return Walk(lines, (i, path, opens) => !opens && path.SequenceEqual(parts) && keyLine.IsMatch(lines[i]));
    }

    private static int FindSectionLine(IList<string> lines, string[] parts) => Walk(lines, (_, path, opens) => opens && path.SequenceEqual(parts));

    /// <summary>Walks the file tracking the section path; returns the first line <paramref name="match"/> accepts, or -1.</summary>
    private static int Walk(IList<string> lines, Func<int, List<string>, bool, bool> match)
    {
        var path = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var code = StripComment(lines[i]);
            var open = Regex.Match(code, @"^\s*""([^""]+)""\s*:\s*\{");
            if (open.Success)
            {
                if (code.IndexOf('}', open.Length) >= 0) continue; // a one-line section
                path.Add(open.Groups[1].Value);
                if (match(i, path, true)) return i;
                continue;
            }
            if (match(i, path, false)) return i;
            var closes = code.Count(c => c == '}');
            for (var c = 0; c < closes && path.Count > 0; c++) path.RemoveAt(path.Count - 1);
        }
        return -1;
    }

    /// <summary>The line without a trailing <c>//</c> comment (a <c>//</c> inside a string is kept).</summary>
    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && inString) { i++; continue; }
            if (c == '"') inString = !inString;
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string Unquote(string token) => token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}

/// <summary>One RTX Encore setting in <c>rtx-encore.jsonc</c> (upstream's ⚙): its values in dropdown order, or a number range.</summary>
public sealed record RtxEncoreSetting(string Section, string Key, IReadOnlyList<string> Values, string Default, int Min = 0, int Max = 0)
{
    public bool IsNumber => Values.Count == 0;
    public bool IsBool => Values is ["false", "true"];
    public int IndexOf(string? value)
    {
        var index = value is null ? -1 : Values.ToList().FindIndex(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Values.ToList().FindIndex(v => v.Equals(Default, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// #280: the settings upstream's RTX Encore ⚙ offers (2.9.3, RTX Encore 1.6.0), keys and values as upstream writes them.
/// Linux deviation: Smooth Motion is left out (it needs NVIDIA's Windows driver and does not run under Wine/Proton).
/// </summary>
public static class RtxEncoreSettings
{
    private static readonly string[] Bool = ["false", "true"];
    private static RtxEncoreSetting B(string section, string key, bool on) => new(section, key, Bool, on ? "true" : "false");

    public static readonly RtxEncoreSetting FgEngine = new("renderingEngines", "frameGenerationEngine", ["game", "dlss-fg", "xefg"], "game");
    public static readonly RtxEncoreSetting Mode = new("frameGeneration", "mode", ["game", "fixed", "dynamic"], "game");
    public static readonly RtxEncoreSetting Multiplier = new("frameGeneration", "multiplier", ["2", "3", "4", "5", "6"], "4");
    public static readonly RtxEncoreSetting FgPreset = new("dlssPresets", "dlssFgPreset", ["game", "a", "b", "c"], "game");
    public static readonly RtxEncoreSetting DynamicTarget = new("frameGeneration", "dynamicTargetFrameRate", [], "0", 0, 1000);
    public static readonly RtxEncoreSetting Dynamic56 = B("frameGeneration", "dynamicExperimental56", false);
    public static readonly RtxEncoreSetting FlipMetering = B("frameGeneration", "flipMeteringBypass", false);
    public static readonly RtxEncoreSetting FrameLimit = new("nativeMenuFeatures", "reflexFrameLimit", [], "0", 0, 1000);

    public static readonly RtxEncoreSetting XefgTensorCores = B("xefg", "xefgTensorCores", false);
    public static readonly RtxEncoreSetting XefgPacing = B("xefg", "xefgMfg4VariablePacing", true);
    public static readonly RtxEncoreSetting XefgUi = new("xefg", "xefgUiHandling", ["interpolate", "game"], "interpolate");
    public static readonly RtxEncoreSetting XefgAlpha = new("xefg", "xefgUiAlpha", ["unknown", "straight", "premultiplied"], "unknown");

    public static readonly RtxEncoreSetting HighMultiplierQuality = B("imageQuality", "highMultiplierQuality", true);
    public static readonly RtxEncoreSetting FrameProtection = B("imageQuality", "frameProtection", true);
    public static readonly RtxEncoreSetting FastFrameGeneration = B("imageQuality", "fastFrameGeneration", true);
    public static readonly RtxEncoreSetting ProtectionTuning = new("imageQuality", "protectionTuning", ["refined", "classic"], "refined");

    public static readonly RtxEncoreSetting AutoUi = B("hudUi", "autoUiRecomposition", true);
    public static readonly RtxEncoreSetting UiAssist = B("hudUi", "uiAssist", true);
    public static readonly RtxEncoreSetting ForceUi = B("hudUi", "forceUiRecomposition", false);

    public static readonly RtxEncoreSetting Upscaler = new("renderingEngines", "reconstructionEngine", ["game", "xess"], "game");
    public static readonly RtxEncoreSetting XessTensorCores = B("xess", "xessTensorCores", false);
    public static readonly RtxEncoreSetting RenderScale = new("dlssSuperResolution", "dlssRenderScale", ["game", "dlaa", "quality", "balanced", "performance", "ultra-performance", "custom"], "game");
    public static readonly RtxEncoreSetting CustomScale = new("dlssSuperResolution", "dlssCustomScale", [], "67", 50, 100);
    public static readonly RtxEncoreSetting FastSr = B("dlssSuperResolution", "fastSuperResolution", false);
    public static readonly RtxEncoreSetting SrPreset = new("dlssPresets", "dlssSrPreset", ["game", "default", "a", "b", "c", "d", "e", "f", "j", "k", "l", "m"], "game");
    public static readonly RtxEncoreSetting RrPreset = new("dlssPresets", "dlssRrPreset", ["game", "default", "a", "b", "c", "d", "e", "f"], "game");
    public static readonly RtxEncoreSetting SrHdr = new("superResolutionInputs", "srHdr", ["game", "on", "off"], "game");
    public static readonly RtxEncoreSetting SrAutoExposure = new("superResolutionInputs", "srAutoExposure", ["game", "on", "off"], "game");

    public static readonly RtxEncoreSetting NrEnabled = B("neuralRendering/core", "nrEnabled", false);
    public static readonly RtxEncoreSetting NrModel = new("dlssPresets", "dlssNrPreset", ["b", "a", "auto"], "b");
    public static readonly RtxEncoreSetting NrPrecision = new("neuralRendering/optimizations", "nrPrecision", ["exact", "fast"], "exact");
    public static readonly RtxEncoreSetting NrFast = B("neuralRendering/optimizations", "nrFastProcessing", true);

    public static readonly RtxEncoreSetting GpuSeries = new("general", "gpuSeries", ["auto", "rtx40", "rtx30", "rtx20"], "auto");
    public static readonly RtxEncoreSetting ForceOta = B("compatibility", "forceOTA", false);
    public static readonly RtxEncoreSetting DisableMenu = B("compatibility", "disableInGameMenu", false);
    public static readonly RtxEncoreSetting FgInMenus = B("compatibility", "disableMenuDetection", false);
    public static readonly RtxEncoreSetting Hair = B("nativeMenuFeatures", "hairEnabled", true);

    /// <summary>The value the file holds for <paramref name="setting"/>, or its default.</summary>
    public static string ValueOf(string? text, RtxEncoreSetting setting) => RtxEncoreConfig.Get(text, setting.Section, setting.Key) ?? setting.Default;
}
