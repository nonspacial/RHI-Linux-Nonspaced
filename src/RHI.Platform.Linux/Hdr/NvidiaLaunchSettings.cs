using System.Text.Json.Serialization;
using RHI.Core.Models;
using RHI.Platform.Linux.Launch;

namespace RHI.Platform.Linux.Hdr;

/// <summary>#211: upstream's MFG dialog "FG Mode" (Default = the game decides).</summary>
public static class NvidiaFrameGenerationModes
{
    public const string Default = "Default";
    public const string Fixed = "Fixed";
    public const string Dynamic = "Dynamic";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        Fixed => Fixed,
        Dynamic => Dynamic,
        _ => Default
    };
}

/// <summary>
/// #211: one game's NVIDIA driver settings, handed to the game at launch through Proton's NVAPI layer (dxvk-nvapi
/// reads <c>DXVK_NVAPI_DRS_*</c> variables). Every member defaults to "off" and is not written while off, so a profile
/// with nothing chosen keeps the shape older builds wrote. Stored as <see cref="LinuxHdrGameProfile.Nvidia"/> (the
/// per-game launch profile in the library state: moves and key adoption already carry it).
/// </summary>
public sealed class NvidiaGameSettings
{
    /// <summary>Lowest and highest render scale (percent) the driver takes (<c>NGX_DLSS_*_OVERRIDE_SCALING_RATIO</c>).</summary>
    public const uint MinRenderScale = 33;
    public const uint MaxRenderScale = 100;

    /// <summary>Upstream's custom target frame rate bounds (MFG dialog: 20–1000).</summary>
    public const uint MinTargetFrameRate = 20;
    public const uint MaxTargetFrameRate = 1000;

    /// <summary>"Max refresh rate" (<c>NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE_AUTO</c>).</summary>
    public const uint TargetFrameRateMaxRefresh = 0x01000000;

    /// <summary>Highest frame count setting (6×; upstream offers 2×–6× = 1–5).</summary>
    public const uint MaxFrameCount = 5;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint SuperResolutionPreset { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint RayReconstructionPreset { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint FrameGenerationPreset { get; set; }

    /// <summary>0 = the game decides; else 33–100 percent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint SuperResolutionRenderScale { get; set; }

    /// <summary>0 = the game decides; else 33–100 percent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint RayReconstructionRenderScale { get; set; }

    /// <summary><see cref="NvidiaFrameGenerationModes"/>; null = Default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrameGenerationMode { get; set; }

    /// <summary>Fixed mode: 1–5 (2×–6×); 0 = the game decides.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint FrameCount { get; set; }

    /// <summary>Dynamic mode: up to 1–5 (2×–6×); 0 = the driver's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DynamicMaxFrameCount { get; set; }

    /// <summary>Dynamic mode: 0 = off, 20–1000 FPS, or <see cref="TargetFrameRateMaxRefresh"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DynamicTargetFrameRate { get; set; }

    /// <summary>
    /// Upstream's "NVIDIA Override" (newest DLSS from the driver): the driver's NGX updater for Proton fetches the newest
    /// DLSS files into the game's prefix and the DLSS overrides tell the game to use them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NewestDlss { get; set; }

    /// <summary>
    /// Values the driver would not take become "the game decides"; a frame-generation mode keeps only its own values
    /// (upstream clears the others when the mode changes).
    /// </summary>
    public NvidiaGameSettings Normalize()
    {
        SuperResolutionPreset = Preset(SuperResolutionPreset);
        RayReconstructionPreset = Preset(RayReconstructionPreset);
        FrameGenerationPreset = Preset(FrameGenerationPreset);
        SuperResolutionRenderScale = Scale(SuperResolutionRenderScale);
        RayReconstructionRenderScale = Scale(RayReconstructionRenderScale);
        var mode = NvidiaFrameGenerationModes.Normalize(FrameGenerationMode);
        FrameGenerationMode = mode == NvidiaFrameGenerationModes.Default ? null : mode;
        FrameCount = mode == NvidiaFrameGenerationModes.Fixed && FrameCount <= MaxFrameCount ? FrameCount : 0;
        DynamicMaxFrameCount = mode == NvidiaFrameGenerationModes.Dynamic && DynamicMaxFrameCount <= MaxFrameCount ? DynamicMaxFrameCount : 0;
        DynamicTargetFrameRate = mode == NvidiaFrameGenerationModes.Dynamic && IsTargetFrameRate(DynamicTargetFrameRate) ? DynamicTargetFrameRate : 0;
        return this;
    }

    /// <summary>True when nothing is chosen (the member is then dropped from the profile).</summary>
    public bool IsDefault()
        => SuperResolutionPreset == 0 && RayReconstructionPreset == 0 && FrameGenerationPreset == 0
           && SuperResolutionRenderScale == 0 && RayReconstructionRenderScale == 0
           && FrameGenerationMode is null && FrameCount == 0 && DynamicMaxFrameCount == 0 && DynamicTargetFrameRate == 0
           && !NewestDlss;

    public NvidiaGameSettings Copy() => (NvidiaGameSettings)MemberwiseClone();

    /// <summary>A render scale the driver takes (33–100), else 0.</summary>
    public static uint Scale(uint percent) => percent is >= MinRenderScale and <= MaxRenderScale ? percent : 0;

    public static bool IsTargetFrameRate(uint value)
        => value is >= MinTargetFrameRate and <= MaxTargetFrameRate || value == TargetFrameRateMaxRefresh;

    private static uint Preset(uint value) => value <= DlssPresetCatalogue.MaxValue ? value : 0;

    /// <summary>
    /// #215: a preset value as the driver takes it (0 = Default for one it would not take). The global default presets
    /// normalise with the same rule as the game's own, so a default is never a value the game's settings would drop.
    /// </summary>
    public static uint NormalizePreset(uint value) => Preset(value);
}

/// <summary>#215: one of the three DLSS preset rows of the NVIDIA settings.</summary>
public enum NvidiaPresetSlot { SuperResolution, RayReconstruction, FrameGeneration }

/// <summary>#215: whether a game's NVIDIA settings take a preset row (the dialog shows it), and why not.</summary>
public enum NvidiaPresetRowState
{
    Shown,
    /// <summary>The game's NVIDIA settings do not show the row (no GPU for it, no feature file, launcher takes no variables…).</summary>
    NotShown,
    /// <summary>The row shows, but the game's DLSS file is 1.x (upstream never sets a preset there).</summary>
    Dlss1x
}

/// <summary>#215: the state of each preset row for one game (<see cref="NvidiaLaunchPlan.PresetRows"/>).</summary>
public sealed record NvidiaPresetRows(NvidiaPresetRowState SuperResolution, NvidiaPresetRowState RayReconstruction, NvidiaPresetRowState FrameGeneration)
{
    public static NvidiaPresetRows None { get; } = new(NvidiaPresetRowState.NotShown, NvidiaPresetRowState.NotShown, NvidiaPresetRowState.NotShown);

    public NvidiaPresetRowState this[NvidiaPresetSlot slot] => slot switch
    {
        NvidiaPresetSlot.SuperResolution => SuperResolution,
        NvidiaPresetSlot.RayReconstruction => RayReconstruction,
        _ => FrameGeneration
    };
}

/// <summary>
/// #215: preset values to copy into a game's NVIDIA settings; null = leave that row as it is. Quick Apply copies the
/// global defaults (<see cref="FromDefaults"/>: a Default default copies nothing); the DLSS batch copies what its three
/// dropdowns show ("Do not change" = null, Default = set the row back to Default).
/// </summary>
public sealed record NvidiaPresetChoice(uint? SuperResolution, uint? RayReconstruction, uint? FrameGeneration)
{
    public static NvidiaPresetChoice None { get; } = new(null, null, null);

    /// <summary>The Settings defaults: Default (0) leaves the game's own choice.</summary>
    public static NvidiaPresetChoice FromDefaults(uint superResolution, uint rayReconstruction, uint frameGeneration)
        => new(Chosen(superResolution), Chosen(rayReconstruction), Chosen(frameGeneration));

    public bool HasAny => SuperResolution is not null || RayReconstruction is not null || FrameGeneration is not null;

    public uint? this[NvidiaPresetSlot slot] => slot switch
    {
        NvidiaPresetSlot.SuperResolution => SuperResolution,
        NvidiaPresetSlot.RayReconstruction => RayReconstruction,
        _ => FrameGeneration
    };

    private static uint? Chosen(uint value) => NvidiaGameSettings.NormalizePreset(value) is var preset && preset != DlssPresetCatalogue.Default ? preset : null;
}

/// <summary>#215: what <see cref="NvidiaPresetCopy.Apply"/> did to one game's NVIDIA settings.</summary>
/// <param name="Settings">The settings after the copy (null when nothing is chosen any more).</param>
public sealed record NvidiaPresetCopyResult(
    NvidiaGameSettings? Settings,
    IReadOnlyList<NvidiaPresetSlot> Set,
    IReadOnlyList<(NvidiaPresetSlot Slot, NvidiaPresetRowState Why)> Skipped)
{
    public bool AnySet => Set.Count > 0;
    public bool AnySkipped => Skipped.Count > 0;
}

/// <summary>
/// #215 (upstream's Apply defaults and mass deploy): copies chosen preset values into one game's NVIDIA settings, only
/// into the rows that game's NVIDIA settings… dialog shows (<see cref="NvidiaPresetRows"/>). A chosen value replaces the
/// game's own as upstream does; every other setting of the game stays as it was.
/// </summary>
public static class NvidiaPresetCopy
{
    private static readonly NvidiaPresetSlot[] Slots = [NvidiaPresetSlot.SuperResolution, NvidiaPresetSlot.RayReconstruction, NvidiaPresetSlot.FrameGeneration];

    public static NvidiaPresetCopyResult Apply(NvidiaGameSettings? current, NvidiaPresetChoice choice, NvidiaPresetRows rows)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(rows);
        var next = (current?.Copy() ?? new NvidiaGameSettings()).Normalize();
        var set = new List<NvidiaPresetSlot>();
        var skipped = new List<(NvidiaPresetSlot, NvidiaPresetRowState)>();
        foreach (var slot in Slots)
        {
            if (choice[slot] is not { } value) continue;
            if (rows[slot] != NvidiaPresetRowState.Shown) { skipped.Add((slot, rows[slot])); continue; }
            var preset = NvidiaGameSettings.NormalizePreset(value);
            switch (slot)
            {
                case NvidiaPresetSlot.SuperResolution: next.SuperResolutionPreset = preset; break;
                case NvidiaPresetSlot.RayReconstruction: next.RayReconstructionPreset = preset; break;
                default: next.FrameGenerationPreset = preset; break;
            }
            set.Add(slot);
        }
        return new NvidiaPresetCopyResult(next.IsDefault() ? null : next, set, skipped);
    }
}

/// <summary>#211: which DLSS features the game ships (its <c>nvngx_dlss</c>/<c>nvngx_dlssd</c>/<c>nvngx_dlssg</c> files).</summary>
public sealed record NvidiaDlssFamilies(bool SuperResolution, bool RayReconstruction, bool FrameGeneration)
{
    public bool Any => SuperResolution || RayReconstruction || FrameGeneration;

    /// <summary>Used while the game's files are not known yet: a saved choice is kept as it is.</summary>
    public static NvidiaDlssFamilies All { get; } = new(true, true, true);
}

/// <summary>#211: why the NVIDIA settings show (or not) for a game. Codes only; the desktop words them.</summary>
public enum NvidiaPlanReason
{
    /// <summary>The settings apply: RHI sets them at launch, or the store's launch options take them.</summary>
    Ready,
    NoGame,
    /// <summary>The host has not been probed yet (nothing is known about the GPU).</summary>
    HostNotProbed,
    NoNvidiaGpu,
    /// <summary>A native Linux game: Proton's NVAPI layer is not there.</summary>
    NativeLinux,
    /// <summary>
    /// Started with the runner's own <c>wine</c>, not the <c>proton</c> script: Proton does not set up its NVAPI layer
    /// (no <c>DXVK_ENABLE_NVAPI</c>, no NVAPI DLL override), so the variables would claim something that does not happen.
    /// </summary>
    DirectWineLaunch,
    /// <summary>The launcher that starts it takes no variables (store clients, the Xbox app that installed it).</summary>
    LauncherTakesNoVariables,
    /// <summary>The game ships no DLSS file.</summary>
    NoDlss,
    /// <summary>The game's files are not known yet: not offered, but a saved choice still applies at launch.</summary>
    FilesNotKnownYet
}

/// <summary>
/// #211: the NVIDIA part of a launch plan. <see cref="Environment"/> holds the variables RHI adds (empty when nothing
/// is chosen or the settings cannot apply); <see cref="Shown"/> says whether the settings are offered at all.
/// </summary>
public sealed record NvidiaLaunchPlan(
    NvidiaPlanReason Reason,
    IReadOnlyDictionary<string, string> Environment,
    NvidiaDlssFamilies Families,
    bool FrameGenerationOffered)
{
    /// <summary>The Frame Generation preset: a frame-generation file and a GPU that runs DLSS Frame Generation (RTX 40 or newer).</summary>
    public bool FrameGenerationPresetOffered { get; init; }

    public static NvidiaLaunchPlan Hidden(NvidiaPlanReason reason)
        => new(reason, new Dictionary<string, string>(StringComparer.Ordinal), new NvidiaDlssFamilies(false, false, false), false);

    public bool Shown => Reason == NvidiaPlanReason.Ready;

    /// <summary>
    /// #215: the preset rows the game's NVIDIA settings… dialog shows — the one rule the dialog, Quick Apply and the DLSS
    /// batch share: Super Resolution and Ray Reconstruction with their file, Frame Generation with its file on an RTX 40 or
    /// newer; none while the settings are hidden.
    /// </summary>
    public NvidiaPresetRows PresetRows => !Shown
        ? NvidiaPresetRows.None
        : new NvidiaPresetRows(Row(Families.SuperResolution), Row(Families.RayReconstruction), Row(FrameGenerationPresetOffered));

    private static NvidiaPresetRowState Row(bool shown) => shown ? NvidiaPresetRowState.Shown : NvidiaPresetRowState.NotShown;

    /// <summary>Something is chosen and applies.</summary>
    public bool HasVariables => Environment.Count > 0;
}

/// <summary>
/// #211: per-game NVIDIA settings → dxvk-nvapi launch variables (the mapping is in the lane's progress file and
/// UPSTREAM.md). Nothing is written to disk: no driver profile, no prefix registry, no game file; the variables only
/// live in the game's environment (or in the launch options the user pastes into his launcher).
/// </summary>
public static class NvidiaLaunchSettings
{
    public const string DrsPrefix = "DXVK_NVAPI_DRS_";
    public const string NgxUpdater = "PROTON_ENABLE_NGX_UPDATER";

    /// <summary>dxvk-nvapi's numeric form (<c>0x&lt;id&gt;=&lt;value&gt;,…</c>), read by every build that reads DRS variables.</summary>
    public const string DrsSettings = "DXVK_NVAPI_DRS_SETTINGS";

    /// <summary>
    /// Settings whose <c>DXVK_NVAPI_DRS_&lt;NAME&gt;</c> variable older dxvk-nvapi builds do not know (their
    /// <c>src/util/util_drs.h</c> table lacks the name, and <c>enrichwithenv</c> reads only names in that table): v0.9.0,
    /// the first build with DRS variables, lacks the two scaling ratios; Proton 10's build (0ea2317) lacks the Frame
    /// Generation preset and the frame-generation mode / dynamic settings. These ids are also written in the numeric
    /// <see cref="DrsSettings"/> form (same value), which every build since v0.9.0 reads. Ids per NVIDIA's
    /// <c>NvApiDriverSettings.h</c>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, uint> NumericIds = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["NGX_DLSS_SR_OVERRIDE_SCALING_RATIO"] = 0x10E41DF5,
        ["NGX_DLSS_RR_OVERRIDE_SCALING_RATIO"] = 0x10C7D4A2,
        ["NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION"] = 0x10E41DF1,
        ["NGX_DLSSG_MODE"] = 0x10308298,
        ["NGX_DLSSG_DYNAMIC_MULTI_FRAME_COUNT_MAX"] = 0x10562D0F,
        ["NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE"] = 0x10CF4125,
    };

    // NGX custom performance mode (NGX_DLSS_SR_MODE / NGX_DLSS_RR_MODE = CUSTOM) that makes the scaling ratio apply.
    private const uint CustomMode = 6;
    // NGX_DLSSG_MODE: ON (fixed multiplier) and DYNAMIC.
    private const uint FrameGenerationOn = 2;
    private const uint FrameGenerationDynamic = 4;

    /// <summary>
    /// The plan for <paramref name="game"/> on <paramref name="host"/>. <paramref name="families"/> null = the game's
    /// files are not known yet: a saved choice is applied as it is, but the settings are not offered until they are.
    /// </summary>
    public static NvidiaLaunchPlan Plan(DetectedGame game, NvidiaGameSettings? settings, LinuxHdrHostSnapshot host, NvidiaDlssFamilies? families)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Probed) return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.HostNotProbed);
        if (!host.Gpu.Present) return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.NoNvidiaGpu);
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.NativeLinux);
        var handoff = LaunchHandoffClassifier.Classify(game);
        if (handoff is LaunchHandoff.StoreClient or LaunchHandoff.Xbox)
            return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.LauncherTakesNoVariables);
        if (handoff == LaunchHandoff.None && LinuxHdrLaunchProfileService.IsDirectWineLaunch(game))
            return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.DirectWineLaunch);
        if (families is { Any: false }) return NvidiaLaunchPlan.Hidden(NvidiaPlanReason.NoDlss);

        var known = families ?? NvidiaDlssFamilies.All;
        var frameGeneration = host.Gpu.Rtx50 && known.FrameGeneration;
        var frameGenerationPreset = host.Gpu.FrameGeneration && known.FrameGeneration;
        var env = Environment((settings ?? new NvidiaGameSettings()).Copy().Normalize(), known, frameGeneration, frameGenerationPreset);
        // Not offered while the files are unknown (the section would flash in and out); a saved choice still applies.
        return new NvidiaLaunchPlan(families is null ? NvidiaPlanReason.FilesNotKnownYet : NvidiaPlanReason.Ready, env, known, frameGeneration)
        {
            FrameGenerationPresetOffered = frameGenerationPreset
        };
    }

    /// <summary>
    /// The variables for <paramref name="settings"/> (normalised) in a game shipping <paramref name="families"/>;
    /// multi frame generation only with <paramref name="frameGeneration"/> (an RTX 50 GPU and a frame-generation file),
    /// the Frame Generation preset only with <paramref name="frameGenerationPreset"/> (a GPU that runs DLSS Frame
    /// Generation). "Default" adds nothing. Ids older dxvk-nvapi builds know by number only are also written in the
    /// numeric <see cref="DrsSettings"/> form (<see cref="NumericIds"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(NvidiaGameSettings settings, NvidiaDlssFamilies families, bool frameGeneration,
        bool frameGenerationPreset = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(families);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string name, uint value) => env[DrsPrefix + name] = Format(value);

        if (families.SuperResolution)
        {
            if (settings.SuperResolutionPreset != 0) Set("NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION", settings.SuperResolutionPreset);
            if (settings.SuperResolutionRenderScale != 0)
            {
                Set("NGX_DLSS_SR_MODE", CustomMode);
                Set("NGX_DLSS_SR_OVERRIDE_SCALING_RATIO", settings.SuperResolutionRenderScale);
            }
        }
        if (families.RayReconstruction)
        {
            if (settings.RayReconstructionPreset != 0) Set("NGX_DLSS_RR_OVERRIDE_RENDER_PRESET_SELECTION", settings.RayReconstructionPreset);
            if (settings.RayReconstructionRenderScale != 0)
            {
                Set("NGX_DLSS_RR_MODE", CustomMode);
                Set("NGX_DLSS_RR_OVERRIDE_SCALING_RATIO", settings.RayReconstructionRenderScale);
            }
        }
        if (families.FrameGeneration)
        {
            if (frameGenerationPreset && settings.FrameGenerationPreset != 0) Set("NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION", settings.FrameGenerationPreset);
            if (frameGeneration)
            {
                switch (NvidiaFrameGenerationModes.Normalize(settings.FrameGenerationMode))
                {
                    case NvidiaFrameGenerationModes.Fixed:
                        Set("NGX_DLSSG_MODE", FrameGenerationOn);
                        if (settings.FrameCount != 0) Set("NGX_DLSSG_MULTI_FRAME_COUNT", settings.FrameCount);
                        break;
                    case NvidiaFrameGenerationModes.Dynamic:
                        Set("NGX_DLSSG_MODE", FrameGenerationDynamic);
                        if (settings.DynamicMaxFrameCount != 0) Set("NGX_DLSSG_DYNAMIC_MULTI_FRAME_COUNT_MAX", settings.DynamicMaxFrameCount);
                        if (settings.DynamicTargetFrameRate != 0) Set("NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE", settings.DynamicTargetFrameRate);
                        break;
                }
            }
        }
        if (settings.NewestDlss && families.Any)
        {
            env[NgxUpdater] = "1";
            if (families.SuperResolution) Set("NGX_DLSS_SR_OVERRIDE", 1);
            if (families.RayReconstruction) Set("NGX_DLSS_RR_OVERRIDE", 1);
            if (families.FrameGeneration) Set("NGX_DLSS_FG_OVERRIDE", 1);
        }
        var numeric = NumericIds
            .Where(pair => env.ContainsKey(DrsPrefix + pair.Key))
            .OrderBy(pair => pair.Value)
            .Select(pair => $"0x{pair.Value:x}={env[DrsPrefix + pair.Key]}")
            .ToList();
        if (numeric.Count > 0) env[DrsSettings] = string.Join(",", numeric);
        return env;
    }

    /// <summary>
    /// A direct launch keeps the user's own <see cref="DrsSettings"/> (from RHI's environment): RHI's entries are added
    /// after it, so for the same id RHI's value wins (dxvk-nvapi keeps the last one).
    /// </summary>
    public static string MergeDrsSettings(string? inherited, string ours)
        => string.IsNullOrWhiteSpace(inherited) ? ours : inherited.Trim().TrimEnd(',') + "," + ours;

    /// <summary>Small values in decimal, the driver's sentinel values (0xFFFFFF, 0x1000000) in hex; dxvk-nvapi reads both.</summary>
    public static string Format(uint value) => value >= 0x100000 ? $"0x{value:x}" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
