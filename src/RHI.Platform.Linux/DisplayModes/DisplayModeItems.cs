using System.Globalization;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.DisplayModes;

/// <summary>
/// #311: item keys (saved in display-modes.json; upstream's keys, never renamed). Upstream's <c>RtxHdrPeak</c> and
/// <c>HdrDisplay</c> have no Linux equivalent and <c>LaunchArgs</c> waits for an owner decision; their keys are not
/// offered (a value saved for one is ignored).
/// </summary>
public static class DisplayModeItemKeys
{
    public const string RdxPeakNits = "RdxPeakNits";
    public const string RdxGameNits = "RdxGameNits";
    public const string RdxUiNits = "RdxUiNits";
    public const string UlTargetFps = "UlTargetFps";
    public const string DcFpsLimit = "DcFpsLimit";
    /// <summary>Upstream: the NVIDIA driver profile's FPS limit. Linux: the per-game FPS limit (#310, DXVK / VKD3D-Proton).</summary>
    public const string DriverFpsCap = "DriverFpsCap";
    public const string MfgMode = "MfgMode";
    public const string DlssSrPreset = "DlssSrPreset";
    public const string DlssSrScale = "DlssSrScale";
    public const string DlssRrPreset = "DlssRrPreset";
    public const string DlssRrScale = "DlssRrScale";
    public const string RsPreset = "RsPreset";
    public const string RsOverlayKey = "RsOverlayKey";
    public const string RsScreenshotKey = "RsScreenshotKey";
    public const string RsGamepadNav = "RsGamepadNav";
    public const string UlOsdKey = "UlOsdKey";
    /// <summary>Upstream: Windows HDR on launch. Linux: the game's HDR launch mode (#250/#251).</summary>
    public const string Hdr = "Hdr";
    /// <summary>Upstream: the resolution on launch. Linux: the resolution switch around the launch (#191).</summary>
    public const string Resolution = "Resolution";

    /// <summary>Items kept in ReShade.ini (kept through ReShade / RenoDX installs and the Settings "write to all" buttons).</summary>
    public static readonly IReadOnlyList<string> ReShadeIniItems = [RdxPeakNits, RdxGameNits, RdxUiNits, RsPreset, RsOverlayKey, RsScreenshotKey, RsGamepadNav];

    /// <summary>Items kept in relimiter.ini.</summary>
    public static readonly IReadOnlyList<string> ReLimiterItems = [UlTargetFps, UlOsdKey];

    /// <summary>Every item kept in a game file (ReShade.ini, relimiter.ini, DisplayCommander.ini).</summary>
    public static readonly IReadOnlyList<string> GameFileItems = [.. ReShadeIniItems, .. ReLimiterItems, DcFpsLimit];
}

/// <summary>How an item's value is edited.</summary>
public enum DisplayModeValueKind { Number, Decimal, Choice, Text, ReShadeKey, ReLimiterKey }

/// <summary>Upstream's item groups, in its order.</summary>
public enum DisplayModeGroup { HdrAndBrightness, FrameRate, Image, Controls, Launch }

/// <summary>Where an item's value lives for a game.</summary>
public enum DisplayModeTarget
{
    /// <summary>[renodx-preset*] in ReShade.ini (journaled on RenoDX's record).</summary>
    RenoDxIni,
    /// <summary>ReShade.ini (journaled on ReShade's record).</summary>
    ReShadeIni,
    /// <summary>relimiter.ini beside the add-on, and RE Framework's <c>_storage_</c> copy (ReLimiter's record).</summary>
    ReLimiterIni,
    /// <summary>DisplayCommander.ini (Display Commander's record).</summary>
    DisplayCommanderIni,
    /// <summary>RHI's own per-game launch settings in the library file (HDR mode, resolution switch, FPS limit, NVIDIA settings).</summary>
    LaunchSettings
}

/// <summary>The fixed list a Choice item offers (the desktop names the entries).</summary>
public enum DisplayModeChoices { None, OffOn01, FpsLimit, FrameGeneration, SrPreset, RrPreset, RenderScale, HdrMode, Resolution }

/// <summary>One swappable thing. Values are strings in the formats below; <see cref="Normalize"/> checks a typed one.</summary>
public sealed class DisplayModeItem
{
    public required string Key { get; init; }
    public required DisplayModeGroup Group { get; init; }
    public required DisplayModeTarget Target { get; init; }
    public DisplayModeValueKind Kind { get; init; }
    public DisplayModeChoices Choices { get; init; }
    /// <summary>Unit shown after a number ("nits", "FPS").</summary>
    public string? Unit { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = double.MaxValue;
    /// <summary>0 is accepted and means off / no limit.</summary>
    public bool AllowZero { get; init; }

    /// <summary>Checks and normalises a typed value; null when it is not valid.</summary>
    public string? Normalize(string? text)
    {
        var t = (text ?? "").Trim();
        switch (Kind)
        {
            case DisplayModeValueKind.Number:
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return null;
                if (i == 0) return AllowZero ? "0" : null;
                return i < Min || i > Max ? null : i.ToString(CultureInfo.InvariantCulture);
            case DisplayModeValueKind.Decimal:
                if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
                d = Math.Round(d, 3);
                if (d == 0) return AllowZero ? "0" : null;
                return d < Min || d > Max ? null : d.ToString("0.###", CultureInfo.InvariantCulture);
            case DisplayModeValueKind.Choice:
                return t.Length == 0 ? null : t;
            case DisplayModeValueKind.ReShadeKey:
                return ReShadeKey(t);
            case DisplayModeValueKind.ReLimiterKey:
                return RHI.Platform.Linux.Deployment.ReLimiterIniEditor.TryParseHotkey(t, out var binding)
                    ? RHI.Platform.Linux.Deployment.ReLimiterIniEditor.HotkeyName(binding) : null;
            default:
                return t;
        }
    }

    /// <summary>A ReShade key in its ini form "vk,ctrl,shift,alt" (four whole numbers, modifiers 0/1), else null.</summary>
    private static string? ReShadeKey(string t)
    {
        var parts = t.Split(',');
        if (parts.Length != 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0) return null;
        if (numbers[0] > 255 || numbers.Skip(1).Any(n => n > 1)) return null;
        return string.Join(",", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// #311 (upstream 2.9.3 <c>Services/DisplayModeItems.cs</c>): every item, in upstream's order, mapped to Linux:
/// RenoDX brightness, the ReShade preset and keys, the ReLimiter and Display Commander caps are the same files as upstream;
/// the NVIDIA driver FPS limit is the per-game FPS limit (#310); DLSS frame generation, presets and render scales are the
/// #211 launch variables; HDR and resolution on launch are RHI's HDR launch mode (#250/#251) and resolution switch (#191).
/// Not on Linux: RTX HDR peak brightness (no RTX HDR under Wine/Proton) and the HDR display (RHI does not pick a display
/// for HDR). Launch arguments wait for an owner decision.
/// </summary>
public static class DisplayModeItems
{
    public static IReadOnlyList<DisplayModeGroup> Groups { get; } =
        [DisplayModeGroup.HdrAndBrightness, DisplayModeGroup.FrameRate, DisplayModeGroup.Image, DisplayModeGroup.Controls, DisplayModeGroup.Launch];

    public static IReadOnlyList<DisplayModeItem> All { get; } =
    [
        // HDR and brightness
        Nits(DisplayModeItemKeys.RdxPeakNits),
        Nits(DisplayModeItemKeys.RdxGameNits),
        Nits(DisplayModeItemKeys.RdxUiNits),
        // Frame rate
        new() { Key = DisplayModeItemKeys.UlTargetFps, Group = DisplayModeGroup.FrameRate, Target = DisplayModeTarget.ReLimiterIni,
            Kind = DisplayModeValueKind.Number, Unit = "FPS", Min = 20, Max = 1000, AllowZero = true },
        new() { Key = DisplayModeItemKeys.DcFpsLimit, Group = DisplayModeGroup.FrameRate, Target = DisplayModeTarget.DisplayCommanderIni,
            Kind = DisplayModeValueKind.Decimal, Unit = "FPS", Min = 20, Max = 1000, AllowZero = true },
        Choice(DisplayModeItemKeys.DriverFpsCap, DisplayModeGroup.FrameRate, DisplayModeChoices.FpsLimit),
        Choice(DisplayModeItemKeys.MfgMode, DisplayModeGroup.FrameRate, DisplayModeChoices.FrameGeneration),
        // Image
        Choice(DisplayModeItemKeys.DlssSrPreset, DisplayModeGroup.Image, DisplayModeChoices.SrPreset),
        Choice(DisplayModeItemKeys.DlssSrScale, DisplayModeGroup.Image, DisplayModeChoices.RenderScale),
        Choice(DisplayModeItemKeys.DlssRrPreset, DisplayModeGroup.Image, DisplayModeChoices.RrPreset),
        Choice(DisplayModeItemKeys.DlssRrScale, DisplayModeGroup.Image, DisplayModeChoices.RenderScale),
        new() { Key = DisplayModeItemKeys.RsPreset, Group = DisplayModeGroup.Image, Target = DisplayModeTarget.ReShadeIni, Kind = DisplayModeValueKind.Text },
        // Controls
        new() { Key = DisplayModeItemKeys.RsOverlayKey, Group = DisplayModeGroup.Controls, Target = DisplayModeTarget.ReShadeIni, Kind = DisplayModeValueKind.ReShadeKey },
        new() { Key = DisplayModeItemKeys.RsScreenshotKey, Group = DisplayModeGroup.Controls, Target = DisplayModeTarget.ReShadeIni, Kind = DisplayModeValueKind.ReShadeKey },
        new() { Key = DisplayModeItemKeys.RsGamepadNav, Group = DisplayModeGroup.Controls, Target = DisplayModeTarget.ReShadeIni,
            Kind = DisplayModeValueKind.Choice, Choices = DisplayModeChoices.OffOn01 },
        new() { Key = DisplayModeItemKeys.UlOsdKey, Group = DisplayModeGroup.Controls, Target = DisplayModeTarget.ReLimiterIni, Kind = DisplayModeValueKind.ReLimiterKey },
        // When RHI launches the game
        Choice(DisplayModeItemKeys.Hdr, DisplayModeGroup.Launch, DisplayModeChoices.HdrMode),
        Choice(DisplayModeItemKeys.Resolution, DisplayModeGroup.Launch, DisplayModeChoices.Resolution),
    ];

    public static DisplayModeItem? Get(string? key)
        => key is null ? null : All.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static DisplayModeItem Nits(string key) => new()
    {
        Key = key, Group = DisplayModeGroup.HdrAndBrightness, Target = DisplayModeTarget.RenoDxIni,
        Kind = DisplayModeValueKind.Number, Unit = "nits", Min = 1, Max = 10000,
    };

    private static DisplayModeItem Choice(string key, DisplayModeGroup group, DisplayModeChoices choices) => new()
    {
        Key = key, Group = group, Target = DisplayModeTarget.LaunchSettings, Kind = DisplayModeValueKind.Choice, Choices = choices,
    };

    // ── Choice values ─────────────────────────────────────────────────────────

    /// <summary>FPS limit "Follow global" (the game has no limit of its own).</summary>
    public const string FpsGlobal = "global";

    /// <summary>Frame generation: Default (upstream "Off": no override), "fixed:N" (N = 1–5, 2×–6×), "dynamic".</summary>
    public const string FrameGenerationOff = "off";
    public const string FrameGenerationDynamic = "dynamic";
    public const string FrameGenerationFixedPrefix = "fixed:";

    /// <summary>
    /// Resolution: a mode's "Off", "On" (the size chosen on the game page), or "WIDTHxHEIGHT". A game's own value is its
    /// exact state, size included: "Off:", "Off:WIDTHxHEIGHT", "On:" (shown as Off / On) or "WIDTHxHEIGHT".
    /// </summary>
    public const string ResolutionOff = "Off";
    public const string ResolutionOn = "On";

    /// <summary>Upstream's driver FPS list (Follow global, Off, then its caps) with #310's VRR presets merged in.</summary>
    public static IReadOnlyList<string> FpsLimitValues { get; } =
        new[] { FpsGlobal, "0" }.Concat(new uint[] { 30, 40, 48, 50, 58, 60, 72, 90, 97, 100, 116, 117, 120, 138, 141, 144, 157, 160, 165, 175, 196, 200, 224, 225, 240, 280, 300, 360, 480 }
            .Concat(FrameRateLimit.Presets.Select(p => p.Fps)).Distinct().Order().Select(n => n.ToString(CultureInfo.InvariantCulture))).ToList();

    public static IReadOnlyList<string> FrameGenerationValues { get; } =
        [FrameGenerationOff, "fixed:1", "fixed:2", "fixed:3", "fixed:4", "fixed:5", FrameGenerationDynamic];

    /// <summary>Render scale in percent: 0 = the game decides, then the DLSS section's list (upstream's RenderScaleOptions).</summary>
    public static IReadOnlyList<string> RenderScaleValues { get; } = ["0", "100", "99", "88", "77", "75", "67", "58", "50", "45", "33"];
}
