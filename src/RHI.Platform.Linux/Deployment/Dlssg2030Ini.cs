namespace RHI.Platform.Linux.Deployment;

/// <summary>One 20/30 FG Unlock setting in <c>dlssg_sm86.ini</c>: the values the ⚙ may write, in its dropdown's order.</summary>
public sealed record Dlssg2030Setting(string Section, string Key, IReadOnlyList<string> Values, string Default)
{
    public int DefaultIndex => Values.ToList().FindIndex(value => value.Equals(Default, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// #281 (upstream 2.8.9 <c>Dlssg2030Settings</c>): the mod's main settings as the ⚙ offers them — each a fixed list of
/// valid values, so no value can switch frame generation off by mistake (the mod treats an invalid value as off). Values
/// as upstream writes them; the order is the dropdown's. Pinned against the 0.4.2 package's <c>dlssg_sm86.ini</c>.
/// </summary>
public static class Dlssg2030Ini
{
    public static readonly Dlssg2030Setting Enabled = new("General", "Enabled", ["1", "0"], "1");
    public static readonly Dlssg2030Setting Engine = new("FrameGeneration", "Engine", ["DLSS", "XeSS"], "DLSS");
    /// <summary>2X…6X are 1…5 generated frames; "Game decides" is 0.</summary>
    public static readonly Dlssg2030Setting MaxFrames = new("FrameGeneration", "MaxGeneratedFrames", ["1", "2", "3", "4", "5", "0"], "3");
    public static readonly Dlssg2030Setting Preset = new("DLSSG", "Preset", ["A", "B", "C"], "A");
    /// <summary>Linux deviation: a missing key reads as 1, the mod's documented default (upstream's fallback reads 0).</summary>
    public static readonly Dlssg2030Setting SpeedTier = new("DLSSG", "Optimized", ["0", "1", "2", "3"], "1");
    public static readonly Dlssg2030Setting SuperResolution = new("SuperResolution", "Mode", ["Off", "M-INT8", "L-INT8", "M-FP16", "L-FP16"], "Off");

    /// <summary>The ⚙'s settings in upstream's order (also the keys an update carries over).</summary>
    public static readonly IReadOnlyList<Dlssg2030Setting> All = [Enabled, Engine, MaxFrames, Preset, SpeedTier, SuperResolution];

    /// <summary>The log the mod writes (<c>[Logging] Directory=dlssg_sm86\logs</c>), relative to the game folder.</summary>
    public const string LogRelativePath = "dlssg_sm86/logs/dlssg_sm86.log";

    // Fixed messages (PT-BR presentation pairs in UiLocalizationService).
    public const string XeSsMaxFourMessage = "XeSS goes up to 4X. Switch Engine to DLSS for 5X or 6X.";
    public const string NotInstalledMessage = "Install 20/30 FG Unlock on this game to change these settings.";

    /// <summary>The setting's index in <paramref name="lines"/>; a missing or invalid value shows the default.</summary>
    public static int IndexOf(Dlssg2030Setting setting, IReadOnlyList<string> lines)
    {
        var value = IniLines.Get(lines, setting.Section, setting.Key)?.Trim();
        var index = value is null ? -1 : setting.Values.ToList().FindIndex(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : setting.DefaultIndex;
    }

    /// <summary>
    /// Applies a choice to <paramref name="lines"/> with upstream's rule between Engine and Max Frames: XeSS reaches 4X, so
    /// picking XeSS at 5X/6X moves Max Frames to 4X, and 5X/6X are refused under XeSS. Returns the refusal, or null.
    /// </summary>
    public static string? Apply(List<string> lines, Dlssg2030Setting setting, int index)
    {
        var value = setting.Values[index];
        var xess = setting == Engine ? value == "XeSS" : IndexOf(Engine, lines) == 1;
        if (setting == MaxFrames && xess && value is "4" or "5") return XeSsMaxFourMessage;
        IniLines.Set(lines, setting.Section, setting.Key, value);
        if (setting == Engine && xess && MaxFrames.Values[IndexOf(MaxFrames, lines)] is "4" or "5")
            IniLines.Set(lines, MaxFrames.Section, MaxFrames.Key, "3");
        return null;
    }

    /// <summary>The ⚙'s settings as set in <paramref name="lines"/>, valid values only (what an update carries into the new INI).</summary>
    public static IReadOnlyList<(Dlssg2030Setting Setting, string Value)> Kept(IReadOnlyList<string> lines)
        => All.Select(setting => (setting, IniLines.Get(lines, setting.Section, setting.Key)?.Trim()))
            .Where(pair => pair.Item2 is not null && pair.setting.Values.Contains(pair.Item2, StringComparer.OrdinalIgnoreCase))
            .Select(pair => (pair.setting, pair.setting.Values.First(v => v.Equals(pair.Item2, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
}
