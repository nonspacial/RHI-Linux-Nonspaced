using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #211: the per-game NVIDIA settings (DLSS presets, render scale, RTX 50 frame generation, newest DLSS) shown in the
/// DLSS section. The choice is saved with the game's launch profile (<see cref="LinuxHdrGameProfile.Nvidia"/>) through
/// <see cref="RuntimePanelViewModel"/>, which owns the profile and merges the variables into the launch plan; this
/// presenter holds the controls and reads what to show from that plan (<see cref="NvidiaLaunchPlan"/>): hidden without
/// an NVIDIA GPU, for native Linux games, for games without DLSS files and where the launcher takes no variables;
/// frame generation only on an RTX 50 GPU with a frame-generation file.
/// </summary>
public sealed partial class NvidiaSettingsPresenter : ObservableObject
{
    private readonly RuntimePanelViewModel _runtime;
    private readonly Func<DlssPresetLists> _presets;
    private DlssPresetLists? _listsBuiltFrom;
    private NvidiaGameSettings _shown = new();
    private bool _loading;

    internal NvidiaSettingsPresenter(RuntimePanelViewModel runtime, Func<DlssPresetLists>? presets = null)
    {
        _runtime = runtime;
        _presets = presets ?? (() => DlssPresetCatalogue.Current);
        RebuildPresetLists();
        _loading = true;
        try { ShowControls(_shown); }
        finally { _loading = false; }
    }

    /// <summary>Test seam: the preset lists come from <paramref name="presets"/> (default: the manifest's, <see cref="DlssPresetCatalogue.Current"/>).</summary>
    internal Func<DlssPresetLists> PresetSource => _presets;

    public OptionItems<uint> SuperResolutionPresetOptions { get; private set; } = null!;
    public OptionItems<uint> RayReconstructionPresetOptions { get; private set; } = null!;
    public OptionItems<uint> FrameGenerationPresetOptions { get; private set; } = null!;
    public OptionItems<uint> SuperResolutionScaleOptions { get; } = OptionLists.NvidiaRenderScale.Options();
    public OptionItems<uint> RayReconstructionScaleOptions { get; } = OptionLists.NvidiaRenderScale.Options();
    public OptionItems<string> FrameGenerationModeOptions { get; } = OptionLists.NvidiaFrameGenerationMode.Options();
    public OptionItems<uint> FixedFrameCountOptions { get; } = OptionLists.NvidiaFixedFrameCount.Options();
    public OptionItems<uint> DynamicFrameCountOptions { get; } = OptionLists.NvidiaDynamicFrameCount.Options();
    public OptionItems<uint> TargetFrameRateOptions { get; } = OptionLists.NvidiaTargetFrameRate.Options();

    [ObservableProperty] private OptionItem<uint> selectedSuperResolutionPreset = null!;
    [ObservableProperty] private OptionItem<uint> selectedRayReconstructionPreset = null!;
    [ObservableProperty] private OptionItem<uint> selectedFrameGenerationPreset = null!;
    [ObservableProperty] private OptionItem<uint> selectedSuperResolutionScale = null!;
    [ObservableProperty] private OptionItem<uint> selectedRayReconstructionScale = null!;
    /// <summary>The Custom render scale (33–100) while "Custom" is chosen.</summary>
    [ObservableProperty] private decimal? superResolutionCustomScale;
    [ObservableProperty] private decimal? rayReconstructionCustomScale;
    [ObservableProperty] private OptionItem<string> selectedFrameGenerationMode = null!;
    [ObservableProperty] private OptionItem<uint> selectedFixedFrameCount = null!;
    [ObservableProperty] private OptionItem<uint> selectedDynamicFrameCount = null!;
    [ObservableProperty] private OptionItem<uint> selectedTargetFrameRate = null!;
    /// <summary>The Custom target frame rate (20–1000) while "Custom" is chosen.</summary>
    [ObservableProperty] private decimal? customTargetFrameRate;
    [ObservableProperty] private bool newestDlss;

    /// <summary>What the controls show (the runtime panel saves it with the launch profile).</summary>
    internal NvidiaGameSettings Shown => _shown.Copy();

    private LinuxHdrLaunchPlan HdrPlan => _runtime.SelectedHdrPlan;
    private NvidiaLaunchPlan Plan => HdrPlan.Nvidia;

    /// <summary>The settings apply to this game (owner rule: hidden where they cannot).</summary>
    public bool IsShown => Plan.Shown;

    public bool ShowSuperResolution => IsShown && Plan.Families.SuperResolution;
    public bool ShowRayReconstruction => IsShown && Plan.Families.RayReconstruction;
    public bool ShowFrameGenerationPreset => IsShown && Plan.Families.FrameGeneration;

    /// <summary>Multi frame generation: an RTX 50 GPU and a frame-generation file.</summary>
    public bool ShowFrameGeneration => IsShown && Plan.FrameGenerationOffered;

    public bool ShowSuperResolutionCustomScale => ShowSuperResolution && SelectedSuperResolutionScale?.Value == OptionLists.NvidiaCustom;
    public bool ShowRayReconstructionCustomScale => ShowRayReconstruction && SelectedRayReconstructionScale?.Value == OptionLists.NvidiaCustom;

    public bool ShowFixedFrameCount => NvidiaFrameGenerationModes.Normalize(SelectedFrameGenerationMode?.Value) == NvidiaFrameGenerationModes.Fixed;
    public bool ShowDynamicFrameCount => NvidiaFrameGenerationModes.Normalize(SelectedFrameGenerationMode?.Value) == NvidiaFrameGenerationModes.Dynamic;
    public bool ShowTargetFrameRate => ShowDynamicFrameCount;
    public bool ShowCustomTargetFrameRate => ShowTargetFrameRate && SelectedTargetFrameRate?.Value == OptionLists.NvidiaCustom;

    /// <summary>Disabled with the HDR controls (no game, an operation running).</summary>
    public bool CanEdit => _runtime.CanEditHdrProfile;

    /// <summary>The line under the controls: what happens at launch, or where to paste the launch options.</summary>
    public string Hint => NvidiaLaunchText.Hint(HdrPlan, _runtime.SelectedGameForNvidia);

    /// <summary>A store hand-off with something chosen: the line to paste into that launcher, and its Copy button.</summary>
    public bool ShowLaunchOptions => IsShown && Plan.HasVariables && HdrPlan.Handoff != LaunchHandoff.None;

    public string LaunchOptions => HdrPlan.LaunchOptions;

    /// <summary>The frame-generation button's summary ("Default", "Fixed · 3×", "Dynamic · up to 4× · 138 FPS · 144 Hz VRR").</summary>
    public string FrameGenerationSummary
    {
        get
        {
            var mode = NvidiaFrameGenerationModes.Normalize(_shown.FrameGenerationMode);
            var parts = new List<string> { OptionLists.NvidiaFrameGenerationMode.Display(mode) };
            if (mode == NvidiaFrameGenerationModes.Fixed && _shown.FrameCount != 0) parts.Add(OptionLists.NvidiaFixedFrameCount.Display(_shown.FrameCount));
            if (mode == NvidiaFrameGenerationModes.Dynamic)
            {
                if (_shown.DynamicMaxFrameCount != 0) parts.Add(OptionLists.NvidiaDynamicFrameCount.Display(_shown.DynamicMaxFrameCount));
                if (_shown.DynamicTargetFrameRate != 0)
                    parts.Add(OptionLists.NvidiaTargetFrameRate.Values.Contains(_shown.DynamicTargetFrameRate)
                        ? OptionLists.NvidiaTargetFrameRate.Display(_shown.DynamicTargetFrameRate)
                        : $"{_shown.DynamicTargetFrameRate} FPS");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The selected game's saved settings (null = none): the controls show them; nothing is saved.</summary>
    internal void Load(NvidiaGameSettings? saved)
    {
        _shown = (saved?.Copy() ?? new NvidiaGameSettings()).Normalize();
        _loading = true;
        try { ShowControls(_shown); }
        finally { _loading = false; }
        Notify();
    }

    /// <summary>The plan, selection, busy state or manifest changed: every derived property.</summary>
    internal void Notify()
    {
        if (!ReferenceEquals(_listsBuiltFrom, _presets())) RebuildPresetLists();
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(ShowSuperResolution));
        OnPropertyChanged(nameof(ShowRayReconstruction));
        OnPropertyChanged(nameof(ShowFrameGenerationPreset));
        OnPropertyChanged(nameof(ShowFrameGeneration));
        OnPropertyChanged(nameof(ShowSuperResolutionCustomScale));
        OnPropertyChanged(nameof(ShowRayReconstructionCustomScale));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(ShowLaunchOptions));
        OnPropertyChanged(nameof(LaunchOptions));
        OnPropertyChanged(nameof(FrameGenerationSummary));
    }

    /// <summary>The UI language changed: labels follow, selections stay.</summary>
    internal void RefreshLocalized()
    {
        SuperResolutionPresetOptions.Relabel();
        RayReconstructionPresetOptions.Relabel();
        FrameGenerationPresetOptions.Relabel();
        SuperResolutionScaleOptions.Relabel();
        RayReconstructionScaleOptions.Relabel();
        FrameGenerationModeOptions.Relabel();
        FixedFrameCountOptions.Relabel();
        DynamicFrameCountOptions.Relabel();
        TargetFrameRateOptions.Relabel();
        Notify();
    }

    /// <summary>The Copy button: the status line says where to paste the line (or that the session has no clipboard).</summary>
    internal void ReportCopy(string? copied)
        => _runtime.ReportNvidiaStatus(copied is null
            ? UiLocalizationService.Text("Win.ClipboardUnavailableSession")
            : NvidiaLaunchText.Copied(HdrPlan, _runtime.SelectedGameForNvidia));

    private void RebuildPresetLists()
    {
        var lists = _presets();
        _listsBuiltFrom = lists;
        var loading = _loading;
        _loading = true;
        try
        {
            SuperResolutionPresetOptions = WithSaved(lists.SuperResolution, _shown.SuperResolutionPreset).Options();
            RayReconstructionPresetOptions = WithSaved(lists.RayReconstruction, _shown.RayReconstructionPreset).Options();
            FrameGenerationPresetOptions = WithSaved(lists.FrameGeneration, _shown.FrameGenerationPreset).Options();
            OnPropertyChanged(nameof(SuperResolutionPresetOptions));
            OnPropertyChanged(nameof(RayReconstructionPresetOptions));
            OnPropertyChanged(nameof(FrameGenerationPresetOptions));
            SelectedSuperResolutionPreset = SuperResolutionPresetOptions.For(_shown.SuperResolutionPreset);
            SelectedRayReconstructionPreset = RayReconstructionPresetOptions.For(_shown.RayReconstructionPreset);
            SelectedFrameGenerationPreset = FrameGenerationPresetOptions.For(_shown.FrameGenerationPreset);
        }
        finally { _loading = loading; }
    }

    /// <summary>A saved preset the manifest no longer lists stays visible and selected (never silently dropped).</summary>
    private static OptionList<uint> WithSaved(IReadOnlyList<DlssPreset> presets, uint saved)
    {
        if (saved == DlssPresetCatalogue.Default || presets.Any(p => p.Value == saved)) return OptionLists.DlssPresets(presets);
        var list = presets.ToList();
        list.Insert(Math.Max(1, list.Count - 1), new DlssPreset(Format(saved), saved));
        return OptionLists.DlssPresets(list);
    }

    private static string Format(uint value) => NvidiaLaunchSettings.Format(value);

    private void ShowControls(NvidiaGameSettings settings)
    {
        if (!SuperResolutionPresetOptions.Any(i => i.Value == settings.SuperResolutionPreset)
            || !RayReconstructionPresetOptions.Any(i => i.Value == settings.RayReconstructionPreset)
            || !FrameGenerationPresetOptions.Any(i => i.Value == settings.FrameGenerationPreset))
            RebuildPresetLists();
        SelectedSuperResolutionPreset = SuperResolutionPresetOptions.For(settings.SuperResolutionPreset);
        SelectedRayReconstructionPreset = RayReconstructionPresetOptions.For(settings.RayReconstructionPreset);
        SelectedFrameGenerationPreset = FrameGenerationPresetOptions.For(settings.FrameGenerationPreset);
        SelectedSuperResolutionScale = ScaleItem(SuperResolutionScaleOptions, settings.SuperResolutionRenderScale);
        SuperResolutionCustomScale = settings.SuperResolutionRenderScale == 0 ? null : settings.SuperResolutionRenderScale;
        SelectedRayReconstructionScale = ScaleItem(RayReconstructionScaleOptions, settings.RayReconstructionRenderScale);
        RayReconstructionCustomScale = settings.RayReconstructionRenderScale == 0 ? null : settings.RayReconstructionRenderScale;
        SelectedFrameGenerationMode = FrameGenerationModeOptions.For(NvidiaFrameGenerationModes.Normalize(settings.FrameGenerationMode));
        SelectedFixedFrameCount = FixedFrameCountOptions.For(settings.FrameCount);
        SelectedDynamicFrameCount = DynamicFrameCountOptions.For(settings.DynamicMaxFrameCount);
        SelectedTargetFrameRate = settings.DynamicTargetFrameRate == 0 || OptionLists.NvidiaTargetFrameRate.Values.Contains(settings.DynamicTargetFrameRate)
            ? TargetFrameRateOptions.For(settings.DynamicTargetFrameRate)
            : TargetFrameRateOptions.For(OptionLists.NvidiaCustom);
        CustomTargetFrameRate = settings.DynamicTargetFrameRate is >= NvidiaGameSettings.MinTargetFrameRate and <= NvidiaGameSettings.MaxTargetFrameRate
            ? settings.DynamicTargetFrameRate : null;
        NewestDlss = settings.NewestDlss;
    }

    private static OptionItem<uint> ScaleItem(OptionItems<uint> options, uint percent)
        => percent == 0 || OptionLists.NvidiaRenderScale.Values.Contains(percent) ? options.For(percent) : options.For(OptionLists.NvidiaCustom);

    /// <summary>A control changed: apply it to the shown settings, save, and raise what depends on it.</summary>
    private void Change(Action<NvidiaGameSettings> apply)
    {
        if (_loading) return;
        var next = _shown.Copy();
        apply(next);
        next.Normalize();
        _shown = next;
        _runtime.SaveNvidiaSettings();
        OnPropertyChanged(nameof(ShowSuperResolutionCustomScale));
        OnPropertyChanged(nameof(ShowRayReconstructionCustomScale));
        OnPropertyChanged(nameof(ShowFixedFrameCount));
        OnPropertyChanged(nameof(ShowDynamicFrameCount));
        OnPropertyChanged(nameof(ShowTargetFrameRate));
        OnPropertyChanged(nameof(ShowCustomTargetFrameRate));
        OnPropertyChanged(nameof(FrameGenerationSummary));
    }

    partial void OnSelectedSuperResolutionPresetChanged(OptionItem<uint> value)
    {
        if (value is not null) Change(s => s.SuperResolutionPreset = value.Value);
    }

    partial void OnSelectedRayReconstructionPresetChanged(OptionItem<uint> value)
    {
        if (value is not null) Change(s => s.RayReconstructionPreset = value.Value);
    }

    partial void OnSelectedFrameGenerationPresetChanged(OptionItem<uint> value)
    {
        if (value is not null) Change(s => s.FrameGenerationPreset = value.Value);
    }

    // "Custom" keeps the saved scale until a number is entered (upstream: the box replaces the list until Enter).
    partial void OnSelectedSuperResolutionScaleChanged(OptionItem<uint> value)
    {
        if (value is null) return;
        if (value.Value == OptionLists.NvidiaCustom) { OnPropertyChanged(nameof(ShowSuperResolutionCustomScale)); return; }
        Change(s => s.SuperResolutionRenderScale = value.Value);
    }

    partial void OnSelectedRayReconstructionScaleChanged(OptionItem<uint> value)
    {
        if (value is null) return;
        if (value.Value == OptionLists.NvidiaCustom) { OnPropertyChanged(nameof(ShowRayReconstructionCustomScale)); return; }
        Change(s => s.RayReconstructionRenderScale = value.Value);
    }

    partial void OnSuperResolutionCustomScaleChanged(decimal? value)
    {
        if (SelectedSuperResolutionScale?.Value == OptionLists.NvidiaCustom && Whole(value) is { } percent && NvidiaGameSettings.Scale(percent) != 0)
            Change(s => s.SuperResolutionRenderScale = percent);
    }

    partial void OnRayReconstructionCustomScaleChanged(decimal? value)
    {
        if (SelectedRayReconstructionScale?.Value == OptionLists.NvidiaCustom && Whole(value) is { } percent && NvidiaGameSettings.Scale(percent) != 0)
            Change(s => s.RayReconstructionRenderScale = percent);
    }

    partial void OnSelectedFrameGenerationModeChanged(OptionItem<string> value)
    {
        if (value is null) return;
        Change(s => s.FrameGenerationMode = value.Value);
        if (_loading) return;
        // The mode keeps only its own values (upstream clears the others): show what was kept.
        _loading = true;
        try
        {
            SelectedFixedFrameCount = FixedFrameCountOptions.For(_shown.FrameCount);
            SelectedDynamicFrameCount = DynamicFrameCountOptions.For(_shown.DynamicMaxFrameCount);
            SelectedTargetFrameRate = TargetFrameRateOptions.For(_shown.DynamicTargetFrameRate);
            CustomTargetFrameRate = null;
        }
        finally { _loading = false; }
    }

    partial void OnSelectedFixedFrameCountChanged(OptionItem<uint> value)
    {
        if (value is not null) Change(s => s.FrameCount = value.Value);
    }

    partial void OnSelectedDynamicFrameCountChanged(OptionItem<uint> value)
    {
        if (value is not null) Change(s => s.DynamicMaxFrameCount = value.Value);
    }

    partial void OnSelectedTargetFrameRateChanged(OptionItem<uint> value)
    {
        if (value is null) return;
        if (value.Value == OptionLists.NvidiaCustom) { OnPropertyChanged(nameof(ShowCustomTargetFrameRate)); return; }
        Change(s => s.DynamicTargetFrameRate = value.Value);
    }

    partial void OnCustomTargetFrameRateChanged(decimal? value)
    {
        if (SelectedTargetFrameRate?.Value == OptionLists.NvidiaCustom && Whole(value) is { } fps
            && fps is >= NvidiaGameSettings.MinTargetFrameRate and <= NvidiaGameSettings.MaxTargetFrameRate)
            Change(s => s.DynamicTargetFrameRate = fps);
    }

    partial void OnNewestDlssChanged(bool value) => Change(s => s.NewestDlss = value);

    private static uint? Whole(decimal? value) => value is { } v && v >= 0 && v <= uint.MaxValue && decimal.Truncate(v) == v ? (uint)v : null;
}

/// <summary>#211: the NVIDIA settings' wording, in the UI language (the plan carries reason codes only).</summary>
public static class NvidiaLaunchText
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>The line under the NVIDIA settings (empty while they are hidden).</summary>
    public static string Hint(LinuxHdrLaunchPlan plan, DetectedGame? game)
    {
        if (game is null || !plan.Nvidia.Shown) return "";
        if (!plan.Nvidia.HasVariables) return T("Nv.NothingChosen");
        return plan.Handoff == LaunchHandoff.None ? T("Nv.AppliedAtLaunch") : ApplyInLauncher(plan, game);
    }

    /// <summary>The store launcher that starts the game, and where its launch options go.</summary>
    public static string ApplyInLauncher(LinuxHdrLaunchPlan plan, DetectedGame game)
    {
        var entry = string.IsNullOrWhiteSpace(game.ParentLauncherName) ? game.Name : XboxCardText.AppName(game.ParentLauncherName)!;
        var variables = string.Join(", ", plan.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return plan.Handoff switch
        {
            LaunchHandoff.Steam => TF("Nv.ApplyInSteam", plan.LaunchOptions),
            LaunchHandoff.Heroic => TF("Nv.ApplyInHeroic", entry, variables),
            LaunchHandoff.Lutris => TF("Nv.ApplyInLutris", entry, variables),
            _ => TF("Nv.ApplyInOtherLauncher", variables)
        };
    }

    /// <summary>Status after Copy: where to paste the line.</summary>
    public static string Copied(LinuxHdrLaunchPlan plan, DetectedGame? game)
        => game is not null && plan.Handoff != LaunchHandoff.None ? ApplyInLauncher(plan, game) : T("Nv.Copied");

    /// <summary>The status line of a direct launch with NVIDIA settings and HDR Off.</summary>
    public static string Launching(DetectedGame game) => TF("Nv.LaunchingWithSettings", game.Name);
}
