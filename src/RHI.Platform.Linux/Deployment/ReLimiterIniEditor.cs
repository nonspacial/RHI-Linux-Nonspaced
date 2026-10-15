using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>The four ReLimiter settings RHI applies to every game (#188; upstream 2.8.7 Settings → ReLimiter).</summary>
/// <param name="OsdHotkey">ReLimiter's key name: <c>[Ctrl+][Alt+][Shift+]Key</c>, e.g. <c>F12</c>, <c>Ctrl+PageUp</c>.</param>
/// <param name="TargetFps">0 = no cap (the key is removed), otherwise 20–1000.</param>
public sealed record ReLimiterGlobals(string OsdHotkey, bool SharedPresets, bool DlssHooks, int TargetFps)
{
    public static ReLimiterGlobals From(LinuxAppSettings settings)
        => new(settings.ReLimiterOsdHotkey, settings.ReLimiterSharedPresets, settings.ReLimiterDlssHooks, settings.ReLimiterTargetFps);
}

/// <summary>
/// relimiter.ini edits for the ReLimiter globals (#188; upstream <c>AuxInstallService.ApplyUl*</c>): only
/// <c>[FrameLimiter] osd_toggle_key</c>, <c>shared_presets</c>, <c>dlss_info_hooks</c> and <c>target_fps</c> change;
/// every other line of the file stays. The OSD key is stored in ReLimiter's own key-name form; the Settings editor
/// works in ReShade's virtual-key form, so this class converts between the two (upstream <c>HotkeyManager.VkNames</c>).
/// </summary>
public static class ReLimiterIniEditor
{
    public const string Section = "FrameLimiter";
    public const string DefaultOsdHotkey = "F12";
    public const int MinTargetFps = 20;
    public const int MaxTargetFps = 1000;

    /// <summary>Upstream's VRR cap presets: the frame rate and the refresh rate it caps for.</summary>
    public static IReadOnlyList<(int Fps, int Hz)> TargetFpsPresets { get; } =
    [
        (59, 60), (73, 75), (97, 100), (116, 120), (138, 144), (157, 165), (171, 180),
        (189, 200), (224, 240), (258, 280), (275, 300), (324, 360), (416, 480), (431, 500)
    ];

    /// <summary>Writes the four keys into <paramref name="lines"/>. True when a line changed.</summary>
    public static bool Apply(List<string> lines, ReLimiterGlobals globals)
    {
        var changed = IniLines.Set(lines, Section, "osd_toggle_key", NormalizeHotkey(globals.OsdHotkey));
        changed |= IniLines.Set(lines, Section, "shared_presets", globals.SharedPresets ? "true" : "false");
        changed |= IniLines.Set(lines, Section, "dlss_info_hooks", globals.DlssHooks ? "true" : "false");
        var fps = NormalizeTargetFps(globals.TargetFps);
        changed |= fps > 0
            ? IniLines.Set(lines, Section, "target_fps", fps.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : IniLines.Remove(lines, Section, "target_fps");
        return changed;
    }

    /// <summary>0, or a cap between 20 and 1000 FPS (anything else reads as no cap).</summary>
    public static int NormalizeTargetFps(int fps) => fps is >= MinTargetFps and <= MaxTargetFps ? fps : 0;

    /// <summary>The OSD key in ReLimiter's form, or <see cref="DefaultOsdHotkey"/> when it is not one RHI can name.</summary>
    public static string NormalizeHotkey(string? value)
        => TryParseHotkey(value, out var binding) ? HotkeyName(binding) : DefaultOsdHotkey;

    /// <summary>ReLimiter's name for a key (upstream <c>BuildUlHotkeyString</c>: Ctrl, Alt, Shift, then the key, no spaces).</summary>
    public static string HotkeyName(ReShadeHotkeyCodec.Binding binding)
    {
        var parts = new List<string>(4);
        if (binding.Ctrl) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        parts.Add(KeyNames.TryGetValue(binding.KeyCode, out var name) ? name : $"0x{binding.KeyCode:X2}");
        return string.Join('+', parts);
    }

    /// <summary>
    /// Reads a ReLimiter key name back as a key and modifiers (case-insensitive). The key is a known name the text ends
    /// with right after a '+' (or the whole text), so key names that contain '+' (<c>Num+</c>, <c>Ctrl+Num+</c>) read back.
    /// </summary>
    public static bool TryParseHotkey(string? value, out ReShadeHotkeyCodec.Binding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        foreach (var (code, name) in KeyNames)
        {
            if (!text.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = text[..^name.Length];
            if (prefix.Length > 0 && !prefix.EndsWith('+')) continue;
            if (!TryModifiers(prefix, out var ctrl, out var alt, out var shift)) return false;
            binding = new ReShadeHotkeyCodec.Binding(code, ctrl, shift, alt);
            return true;
        }
        var plus = text.LastIndexOf('+');
        var key = plus >= 0 ? text[(plus + 1)..] : text;
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var raw) && raw is > 0 and <= 255
            && TryModifiers(plus >= 0 ? text[..(plus + 1)] : "", out var c, out var a, out var s))
        {
            binding = new ReShadeHotkeyCodec.Binding(raw, c, s, a);
            return true;
        }
        return false;
    }

    // "Ctrl+Alt+" → the three flags; any other word refuses the whole name.
    private static bool TryModifiers(string prefix, out bool ctrl, out bool alt, out bool shift)
    {
        ctrl = alt = shift = false;
        foreach (var modifier in prefix.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else return false;
        }
        return true;
    }

    /// <summary>The OSD key in the ReShade virtual-key form the Settings key editor uses.</summary>
    public static string ToEditorValue(string? osdHotkey)
        => (TryParseHotkey(osdHotkey, out var binding) ? binding : new ReShadeHotkeyCodec.Binding(123, false, false, false)).ToConfigValue();

    /// <summary>The Settings key editor's value as a ReLimiter key name.</summary>
    public static string FromEditorValue(string editorValue)
        => HotkeyName(ReShadeHotkeyCodec.ParseOrDefault(editorValue, "123,0,0,0"));

    // Upstream HotkeyManager.VkNames with the spaces removed (ReLimiter expects "PageUp", not "Page Up").
    private static readonly IReadOnlyDictionary<int, string> KeyNames = BuildKeyNames();

    private static Dictionary<int, string> BuildKeyNames()
    {
        var names = new Dictionary<int, string>
        {
            [8] = "Backspace", [9] = "Tab", [13] = "Enter", [19] = "Pause", [20] = "CapsLock",
            [27] = "Escape", [32] = "Space", [33] = "PageUp", [34] = "PageDown",
            [35] = "End", [36] = "Home", [37] = "Left", [38] = "Up", [39] = "Right", [40] = "Down",
            [44] = "PrintScreen", [45] = "Insert", [46] = "Delete",
            [106] = "Num*", [107] = "Num+", [109] = "Num-", [110] = "Num.", [111] = "Num/",
            [144] = "NumLock", [145] = "ScrollLock",
            [186] = ";", [187] = "=", [188] = ",", [189] = "-", [190] = ".", [191] = "/",
            [192] = "`", [219] = "[", [220] = "\\", [221] = "]", [222] = "'"
        };
        for (var digit = 0; digit <= 9; digit++)
        {
            names[48 + digit] = digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names[96 + digit] = "Num" + digit;
        }
        for (var letter = 'A'; letter <= 'Z'; letter++) names[letter] = letter.ToString();
        for (var f = 1; f <= 24; f++) names[111 + f] = "F" + f;
        return names;
    }
}
