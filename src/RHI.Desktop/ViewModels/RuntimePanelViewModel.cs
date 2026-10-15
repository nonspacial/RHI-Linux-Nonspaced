using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The runtime rows of the "Execution / game" section (epic #89 unit 6): detected graphics API and its evidence, the
/// per-game API override, the OptiScaler variant preference, the Luma mode, the HDR launch profile and the neural
/// rendering recommendation. Dropdowns hold <c>{Value, Display}</c> option items (UI-12/20): the selection is the
/// stored value's item, and a language change relabels the items in place instead of rebuilding the lists.
/// </summary>
public sealed partial class RuntimePanelViewModel : PanelViewModel
{
    private readonly LinuxManagedComponentService _managedComponents;
    private readonly GraphicsApiOverrideStore _apiOverrideStore;
    private readonly OptiScalerVariantPreferenceStore _optiScalerVariantStore;
    private readonly LinuxHdrLaunchProfileService _hdrProfiles;

    internal RuntimePanelViewModel(IPanelHost host, LinuxManagedComponentService managedComponents, GraphicsApiOverrideStore apiOverrideStore,
        OptiScalerVariantPreferenceStore optiScalerVariantStore, LinuxHdrLaunchProfileService hdrProfiles) : base(host)
    {
        _managedComponents = managedComponents;
        _apiOverrideStore = apiOverrideStore;
        _optiScalerVariantStore = optiScalerVariantStore;
        _hdrProfiles = hdrProfiles;
        SelectedGraphicsApiOverride = FormatGraphicsApiOverride(null);
        SelectedOptiScalerVariant = FormatOptiScalerVariant(OptiScalerVariant.Stable);
        SelectedLumaModePreference = FormatLumaModePreference(LumaModePreference.Auto);
        SelectedHdrMode = FormatHdrMode(LinuxHdrModes.Off);
    }

    /// <summary>
    /// The OptiScaler ⚙ "Nightly build" row (sync #156): follows this panel's OptiScaler channel; one instance per shell.
    /// </summary>
    public OptiScalerNightlyBuildPresenter OptiScalerNightlyBuild
        => OptiScalerNightlyBuildPresenter.For(Shell, this, _managedComponents, Host.TryPersist);

    /// <summary>A game was selected (or none): the rows show its saved preferences.</summary>
    internal void OnSelectionChanged(DetectedGame? value)
    {
        if (value is null)
        {
            ResetGraphicsApiOverride();
            ResetOptiScalerVariant();
            ResetHdrProfile();
            return;
        }
        LoadGraphicsApiOverride(value);
        LoadOptiScalerVariant(value);
        LoadLumaModePreference(value);
        LoadHdrProfile(value);
    }

    /// <summary>The selection, its analysis or the language changed: every derived row.</summary>
    internal void NotifySection()
    {
        OnPropertyChanged(nameof(SelectedApi));
        OnPropertyChanged(nameof(SelectedApiEvidence));
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(GraphicsApiOverrideHint));
        OnPropertyChanged(nameof(CanSelectLumaMode));
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        OnPropertyChanged(nameof(SelectedEngineHint));
        NotifyHdrProfile();
    }

    internal void NotifyBusy()
    {
        NotifyHdrProfile();
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(CanSelectLumaMode));
    }

    internal void NotifyProbeBusy() => OnPropertyChanged(nameof(CanSelectLumaMode));

    internal void NotifyNeuralRecommendation() => OnPropertyChanged(nameof(NeuralRenderingRecommendation));

    /// <summary>The UI language changed: the dropdowns' labels follow, their selections (values) stay.</summary>
    internal void RefreshLocalized()
    {
        GraphicsApiOverrideOptions.Relabel();
        OptiScalerVariantOptions.Relabel();
        HdrModeOptions.Relabel();
        LumaModeOptions.Relabel();
    }

    public OptionItems<GraphicsApi?> GraphicsApiOverrideOptions { get; } = OptionLists.GraphicsApiOverride.Options();

    public OptionItems<OptiScalerVariant> OptiScalerVariantOptions { get; } = OptionLists.OptiScalerVariant.Options();

    /// <summary>
    /// What an OptiScaler channel is, in plain words (#174): the dropdown's per-channel 2 s tooltip and the line under it
    /// that names the chosen channel, so F5 DLSSNR (and the others) are visible without opening the list.
    /// </summary>
    public static string OptiScalerChannelText(OptiScalerVariant variant) => UiLocalizationService.Text(variant switch
    {
        OptiScalerVariant.Nightly => "Win.OptiScalerChannelNightly",
        OptiScalerVariant.DlssNr => "Win.OptiScalerChannelDlssNr",
        OptiScalerVariant.F5 => "Win.OptiScalerChannelF5",
        _ => "Win.OptiScalerChannelStable",
    });

    public OptionItems<LumaModePreference> LumaModeOptions { get; } = OptionLists.LumaMode.Options();

    public OptionItems<string> HdrModeOptions { get; } = OptionLists.HdrMode.Options();

    private bool _loadingGraphicsApiOverride;

    private bool _loadingOptiScalerVariant;

    private bool _loadingLumaModePreference;

    private bool _loadingHdrProfile;

    private LinuxHdrHostSnapshot? _lastHdrHost;

    /// <summary>The start-up HDR host probe, then each published analysis's (the plan is built for it).</summary>
    internal LinuxHdrHostSnapshot? LastHdrHost
    {
        get => _lastHdrHost;
        set => _lastHdrHost = value;
    }

    [ObservableProperty] private OptionItem<GraphicsApi?> selectedGraphicsApiOverride = null!;

    [ObservableProperty] private OptionItem<OptiScalerVariant> selectedOptiScalerVariant = null!;

    [ObservableProperty] private OptionItem<LumaModePreference> selectedLumaModePreference = null!;

    [ObservableProperty] private OptionItem<string> selectedHdrMode = null!;

    [ObservableProperty] private bool hdrLegacyWsi;

    // #191 resolution auto-toggle (per game, saved with the HDR profile).
    [ObservableProperty] private bool resolutionToggle;

    [ObservableProperty] private OptionItem<string> selectedResolution = OptionLists.GameResolution.Options().For(OptionLists.GameResolution.Fallback);

    public OptionItems<string> ResolutionOptions { get; } = OptionLists.GameResolution.Options();

    /// <summary>The profile the controls show (HDR mode, legacy WSI, resolution toggle and size).</summary>
    private LinuxHdrGameProfile ShownProfile() => new LinuxHdrGameProfile
    {
        Mode = ParseHdrMode(SelectedHdrMode), EnableLegacyWsi = HdrLegacyWsi,
        ResolutionToggle = ResolutionToggle, Resolution = SelectedResolution?.Value
    }.Normalize();

    /// <summary>#191: what happens to the display when Play starts the selected game.</summary>
    public ResolutionPlan SelectedResolutionPlan => SelectedGame is null
        ? ResolutionPlan.Off
        : DisplayModeSwitcher.Plan(SelectedGame, ShownProfile(), SelectedHdrPlan, HdrHost);

    /// <summary>#191: the plain line under the resolution controls (hidden while the toggle is off).</summary>
    public string ResolutionHint => ResolutionText.Hint(SelectedResolutionPlan, SelectedGame);

    public string SelectedApi
    {
        get
        {
            if (SelectedGame is null) return T("Vm.Unknown");
            if (SelectedGame.UserGraphicsApiOverride is GraphicsApi explicitApi && explicitApi != GraphicsApi.Unknown)
            {
                var detected = IsSelectionProbeBusy
                    ? T("Vm.DetectorStillRunning")
                    : SelectedGame.GraphicsApi != GraphicsApi.Unknown
                        ? TF("Vm.Detected", FormatApi(SelectedGame.GraphicsApi))
                        : T("Vm.DetectedAPIUnknown");
                return TF("Vm.ExplicitOverride", FormatApi(explicitApi), detected);
            }
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown)
            {
                var detected = IsSelectionProbeBusy
                    ? T("Vm.DetectorStillRunning")
                    : SelectedGame.GraphicsApi != GraphicsApi.Unknown
                        ? TF("Vm.Detected", FormatApi(SelectedGame.GraphicsApi))
                        : T("Vm.DetectedAPIUnknown");
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return TF("Vm.UpstreamRHICompatibilityData", manifestApis, detected);
            }
            if (IsSelectionProbeBusy) return T("Vm.Detecting");
            if (SelectedGame.GraphicsApi != GraphicsApi.Unknown)
                return $"{FormatApi(SelectedGame.GraphicsApi)} · {FormatGraphicsApiConfidence(SelectedGame.GraphicsApiConfidence)}";
            return SelectedGame.GraphicsApis.Count > 0
                ? $"{string.Join(" / ", SelectedGame.GraphicsApis.Select(FormatApi))} · {FormatGraphicsApiConfidence(SelectedGame.GraphicsApiConfidence)}"
                : T("Vm.Unknown");
        }
    }

    public string SelectedApiEvidence
    {
        get
        {
            if (SelectedGame is null) return T("Vm.NoReliableGraphicsAPISignatureWas");
            var detected = IsSelectionProbeBusy
                ? T("Vm.AnalyzingExecutableEngineConfigurationLocalRunti")
                : PresentOr(SelectedGame.GraphicsApiEvidence, T("Vm.NoReliableGraphicsAPISignatureWas"));
            if (SelectedGame.UserGraphicsApiOverride is GraphicsApi explicitApi && explicitApi != GraphicsApi.Unknown)
                return TF("Vm.ExplicitPerGameAuthorityDetectorEvidence", FormatApi(explicitApi), detected);
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown)
            {
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return TF("Vm.UpstreamRHICompatibilityLocalDetectionPreserved", manifestApis, detected);
            }
            return detected;
        }
    }

    public bool CanSelectGraphicsApiOverride => SelectedGame is not null
        && HasConfirmedPatchTarget
        && !IsBusy
        && !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    public string GraphicsApiOverrideHint
    {
        get
        {
            if (SelectedGame is null) return T("Vm.SelectWindowsGame");
            if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return T("Vm.NativeLinuxTargetsDoNotUse");
            if (SelectedGame.UserGraphicsApiOverride == GraphicsApi.DirectX11 &&
                SelectedGame.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true)
                return SelectedGame.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase)
                    ? T("Vm.ExplicitDX11AuthorityEnablesUpstreamGeneric")
                    : T("Vm.ExplicitDX11AuthorityCanEnableGeneric");
            if (SelectedGame.HasExplicitGraphicsApiOverride)
                return T("Vm.ExplicitUserOverrideTakesPriorityComponent");
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi)
            {
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return TF("Vm.AutomaticUsesUpstreamRHICompatibilityData", manifestApis);
            }
            return T("Vm.AutomaticUsesRuntimeFileDetectionUpstream");
        }
    }

    public bool CanSelectLumaMode => SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy
        && !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    public string LumaPolicyText
    {
        get
        {
            if (SelectedGame is null) return T("Vm.SelectWindowsGame");
            return Facts is { } facts ? UiLocalizationService.Present(facts.LumaPolicy.Summary) : "";
        }
    }

    public string LumaCoexistenceText
    {
        get
        {
            if (SelectedGame is null || Facts is not { } facts) return "";
            return facts.LumaPolicy.RenoDxCompatible
                ? T("Vm.UpstreamRHICompatibilityDataAllowsRenoDX")
                : T("Vm.RenoDXLumaRemainMutuallyExclusiveUnless");
        }
    }

    public bool CanEditHdrProfile => SelectedGame is not null && !IsBusy;

    public LinuxHdrLaunchPlan SelectedHdrPlan => SelectedGame is null
        ? LinuxHdrLaunchPlan.NoGame
        : _hdrProfiles.BuildPlan(SelectedGame, ShownProfile(), HdrHost);

    /// <summary>
    /// The HDR host the plan is built for: the selected game's analysis probed it off the UI thread; before the first
    /// analysis has finished, the start-up probe (<see cref="_lastHdrHost"/>). Never probed from a getter (the probe
    /// reads /proc and searches PATH): before the start-up probe lands the plan is built for
    /// <see cref="LinuxHdrHostSnapshot.NotProbed"/> and rebuilt when it lands.
    /// </summary>
    private LinuxHdrHostSnapshot HdrHost => HdrHostFor(Facts, _lastHdrHost);

    internal static LinuxHdrHostSnapshot HdrHostFor(SelectionFacts? facts, LinuxHdrHostSnapshot? startupProbe)
        => facts?.HdrHost ?? startupProbe ?? LinuxHdrHostSnapshot.NotProbed;

    public string HdrProfileHint => HdrLaunchText.Hint(SelectedHdrPlan, SelectedGame);

    public string HdrLaunchOptions => SelectedHdrPlan.LaunchOptions;

    public bool CanCopyHdrLaunchOptions => SelectedGame is not null && SelectedHdrPlan.Enabled && SelectedHdrPlan.CanApply;

    /// <summary>
    /// The launch-profile preview and its Copy button apply only with an HDR mode chosen: with HDR Off both are hidden
    /// (owner rule: hide inapplicable options; Refs #152). A blocked mode still shows them, Copy disabled, with the reason.
    /// </summary>
    public bool ShowHdrLaunchProfile => SelectedGame is not null && SelectedHdrPlan.Enabled;

    public string SelectedEngineHint
    {
        get
        {
            if (IsSelectionProbeBusy) return T("Vm.LoadingUpstreamRHICompatibilityData");
            if (SelectedGame is null) return T("Vm.NoGameSelected");

            var engine = string.IsNullOrWhiteSpace(SelectedGame.EngineHint)
                ? T("Vm.NoUpstreamEngineOverride")
                : SelectedGame.EngineHint;
            // Edit game's bitness wins over the manifest's (Refs #190), as GameArchitecture.Of reads them.
            var architecture = SelectedGame.UserArchitectureOverride switch
            {
                PeArchitecture.X64 => T("Shell.UserX64"),
                PeArchitecture.X86 => T("Shell.UserX86"),
                _ => SelectedGame.ForcedArchitecture switch
                {
                    PeArchitecture.X64 => T("Vm.ForcedX64"),
                    PeArchitecture.X86 => T("Vm.ForcedX86"),
                    _ => ""
                }
            };
            return engine + architecture;
        }
    }

    public string NeuralRenderingRecommendation
    {
        get
        {
            if (SelectedGame is null) return T("Vm.NoGameSelected");
            if (!SelectedGame.HasConfirmedPatchTarget) return T("Vm.WaitingTargetExecutableNoRecommendationWill");
            if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return T("Vm.WindowsNeuralRenderingMethodsNotAvailable");

            if (Facts is not { } facts) return T("Vm.AnalyzingGameFilesBackground");
            var recommended = LinuxManagedComponentService.RecommendedNeuralRenderingMethod(SelectedGame, facts.HasNativeDlssForNeuralRendering);
            if (facts.Architecture == PeArchitecture.X86 && recommended == ManagedComponentKind.Dlss5Feeder)
                return T("Vm.DLSS5Feeder32BitSupportedVia");

            return recommended switch
            {
                ManagedComponentKind.ShortFuse => T("Vm.DLSSToolShortFuseRecommended64Bit"),
                ManagedComponentKind.Dlss5Dx11Bridge => T("Vm.DLSS5ToolDX11BridgeRecommendedDX11"),
                ManagedComponentKind.Dlss5Feeder => T("Vm.DLSS5FeederRecommendedWhenGameHas"),
                _ => T("Vm.NoNeuralRenderingRecommendation")
            };
        }
    }

    partial void OnSelectedGraphicsApiOverrideChanged(OptionItem<GraphicsApi?> value)
    {
        if (value is null || _loadingGraphicsApiOverride || SelectedGame is null) return;
        var api = ParseGraphicsApiOverride(value);
        var game = SelectedGame;
        // The store is the authority (it is re-read on every refresh): a refused write reverts the dropdown to the
        // saved value instead of claiming the choice holds for the session.
        if (TryPersist(() => _apiOverrideStore.Set(game, api), "Ui.WhatGamePreferences", sessionOnly: false) is { } failure)
        {
            LoadGraphicsApiOverride(game);
            StatusText = failure;
            return;
        }
        game.UserGraphicsApiOverride = api;
        ReevaluateCurrentGameForApiOverride(game);
    }

    partial void OnSelectedOptiScalerVariantChanged(OptionItem<OptiScalerVariant> value)
    {
        if (value is null || _loadingOptiScalerVariant || SelectedGame is null) return;
        var installed = _managedComponents.GetInstalledOptiScalerVariant(SelectedGame);
        if (installed is not null)
        {
            _loadingOptiScalerVariant = true;
            try { SelectedOptiScalerVariant = FormatOptiScalerVariant(installed.Value); }
            finally { _loadingOptiScalerVariant = false; }
            StatusText = T("Vm.RemoveCurrentOptiScalerBeforeSwitchingStable");
            return;
        }
        var game = SelectedGame;
        if (TryPersist(() => _optiScalerVariantStore.Set(game, ParseOptiScalerVariant(value)), "Ui.WhatGamePreferences", sessionOnly: false) is { } failure)
        {
            LoadOptiScalerVariant(game);
            return;
        }
        StatusText = TF("Vm.OptiScalerVariantSelected", value, game.Name);
    }

    partial void OnSelectedLumaModePreferenceChanged(OptionItem<LumaModePreference> value)
    {
        if (value is null || _loadingLumaModePreference || SelectedGame is null) return;
        var preference = ParseLumaModePreference(value);
        var game = SelectedGame;
        LumaPolicySnapshot? policy = null;
        if (TryPersist(() => policy = _managedComponents.SetLumaModePreference(game, preference), "Ui.WhatGamePreferences", sessionOnly: false) is { } failure)
        {
            LoadLumaModePreference(game);
            StatusText = failure;
            return;
        }
        Host.ForgetFilterFacts(game); // the Luma filter follows the new preference
        ReevaluateCurrentGameForLumaPolicy(game, policy!);
    }

    internal void RaiseHdrPlanChanged()
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        OnPropertyChanged(nameof(ShowHdrLaunchProfile));
        OnPropertyChanged(nameof(SelectedResolutionPlan));
        OnPropertyChanged(nameof(ResolutionHint));
    }

    partial void OnResolutionToggleChanged(bool value)
    {
        RaiseHdrPlanChanged();
        if (_loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame, HdrControl.Resolution);
    }

    partial void OnSelectedResolutionChanged(OptionItem<string> value)
    {
        RaiseHdrPlanChanged();
        if (value is null || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame, HdrControl.Resolution);
    }

    partial void OnSelectedHdrModeChanged(OptionItem<string> value)
    {
        RaiseHdrPlanChanged();
        if (value is null || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame, HdrControl.Mode);
    }

    partial void OnHdrLegacyWsiChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        OnPropertyChanged(nameof(ShowHdrLaunchProfile));
        if (_loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame, HdrControl.LegacyWsi);
    }

    private void LoadGraphicsApiOverride(DetectedGame game)
    {
        _loadingGraphicsApiOverride = true;
        try
        {
            game.UserGraphicsApiOverride = _apiOverrideStore.Get(game);
            SelectedGraphicsApiOverride = FormatGraphicsApiOverride(game.UserGraphicsApiOverride);
        }
        finally { _loadingGraphicsApiOverride = false; }
    }

    private void LoadOptiScalerVariant(DetectedGame game)
    {
        _loadingOptiScalerVariant = true;
        try
        {
            var variant = _managedComponents.GetInstalledOptiScalerVariant(game) ?? _optiScalerVariantStore.Get(game);
            SelectedOptiScalerVariant = FormatOptiScalerVariant(variant);
        }
        finally { _loadingOptiScalerVariant = false; }
    }

    private void ResetOptiScalerVariant()
    {
        _loadingOptiScalerVariant = true;
        try { SelectedOptiScalerVariant = FormatOptiScalerVariant(OptiScalerVariant.Stable); }
        finally { _loadingOptiScalerVariant = false; }
    }

    private static OptiScalerVariant ParseOptiScalerVariant(OptionItem<OptiScalerVariant>? item) => item?.Value ?? OptionLists.OptiScalerVariant.Fallback;

    private OptionItem<OptiScalerVariant> FormatOptiScalerVariant(OptiScalerVariant variant) => OptiScalerVariantOptions.For(variant);

    private void LoadLumaModePreference(DetectedGame game)
    {
        _loadingLumaModePreference = true;
        try
        {
            SelectedLumaModePreference = FormatLumaModePreference(_managedComponents.GetLumaPolicy(game).UserPreference);
        }
        finally { _loadingLumaModePreference = false; }
    }

    private void ReevaluateCurrentGameForLumaPolicy(DetectedGame game, LumaPolicySnapshot policy)
    {
        if (!Host.Session.Recompose(game, facts => facts with { LumaPolicy = policy })) Host.NotifySelected();
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        StatusText = $"{game.Name}: {UiLocalizationService.Present(policy.Summary)}.";
    }

    private static string ParseHdrMode(OptionItem<string>? item) => item?.Value ?? OptionLists.HdrMode.Fallback;

    private OptionItem<string> FormatHdrMode(string? mode) => HdrModeOptions.For(mode ?? LinuxHdrModes.Off);

    internal void LoadHdrProfile(DetectedGame game)
    {
        _loadingHdrProfile = true;
        try
        {
            var key = LinuxGameLibraryStore.GameKey(game);
            var profile = Host.LibraryState.HdrProfiles.TryGetValue(key, out var saved)
                ? saved.Normalize()
                : new LinuxHdrGameProfile();
            SelectedHdrMode = FormatHdrMode(profile.Mode);
            HdrLegacyWsi = profile.EnableLegacyWsi;
            ResolutionToggle = profile.ResolutionToggle;
            SelectedResolution = ResolutionOptions.For(profile.Resolution ?? OptionLists.GameResolution.Fallback);
        }
        finally { _loadingHdrProfile = false; }
        NotifyHdrProfile();
    }

    private void ResetHdrProfile()
    {
        _loadingHdrProfile = true;
        try
        {
            SelectedHdrMode = FormatHdrMode(LinuxHdrModes.Off);
            HdrLegacyWsi = false;
            ResolutionToggle = false;
            SelectedResolution = ResolutionOptions.For(OptionLists.GameResolution.Fallback);
        }
        finally { _loadingHdrProfile = false; }
        NotifyHdrProfile();
    }

    private void SaveHdrProfile(DetectedGame game, HdrControl touched)
    {
        if (Host.LibraryWritesPaused())
        {
            LoadHdrProfile(game);
            return;
        }
        var key = LinuxGameLibraryStore.GameKey(game);
        var shown = ShownProfile();
        // A size is saved once the toggle has been on (the dropdown shows only then); before that, the dropdown's default
        // is not a choice and a game without any HDR or resolution setting keeps no profile.
        if (!shown.ResolutionToggle && !(Host.LibraryState.HdrProfiles.TryGetValue(key, out var before) && before.Resolution is not null))
            shown.Resolution = null;
        var adopted = Host.AdoptRestoredLibraryFile(reloadHdrControls: false);
        var profile = shown;
        if (adopted)
        {
            // The controls showed the stand-in, not the restored profile: change only the control the user touched in
            // the restored profile, then show the result (Refs #149).
            var restored = Host.LibraryState.HdrProfiles.TryGetValue(key, out var saved) ? saved.Normalize() : new LinuxHdrGameProfile();
            profile = new LinuxHdrGameProfile
            {
                Mode = touched == HdrControl.Mode ? shown.Mode : restored.Mode,
                EnableLegacyWsi = touched == HdrControl.LegacyWsi ? shown.EnableLegacyWsi : restored.EnableLegacyWsi,
                ResolutionToggle = touched == HdrControl.Resolution ? shown.ResolutionToggle : restored.ResolutionToggle,
                Resolution = touched == HdrControl.Resolution ? shown.Resolution : restored.Resolution
            }.Normalize();
        }
        if (profile.IsDefault()) Host.LibraryState.HdrProfiles.Remove(key);
        else Host.LibraryState.HdrProfiles[key] = profile;
        Host.PersistLibraryState("Ui.WhatHdrProfile");
        if (adopted) LoadHdrProfile(game);
        else NotifyHdrProfile();
    }

    /// <summary>The HDR control a save comes from (<see cref="SaveHdrProfile"/>).</summary>
    private enum HdrControl { Mode, LegacyWsi, Resolution }

    private void NotifyHdrProfile()
    {
        OnPropertyChanged(nameof(CanEditHdrProfile));
        RaiseHdrPlanChanged();
    }

    public string GetSelectedHdrLaunchOptions() => HdrLaunchOptions;

    private void ResetGraphicsApiOverride()
    {
        _loadingGraphicsApiOverride = true;
        try { SelectedGraphicsApiOverride = FormatGraphicsApiOverride(null); }
        finally { _loadingGraphicsApiOverride = false; }
    }

    private void ReevaluateCurrentGameForApiOverride(DetectedGame game)
    {
        if (!Host.Session.Recompose(game)) Host.NotifySelected();
        StatusText = game.UserGraphicsApiOverride is null
            ? game.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown
                ? TF("Vm.ExplicitGraphicsAPIOverrideRemovedUpstream", game.Name, FormatApi(manifestApi))
                : TF("Vm.ExplicitGraphicsAPIOverrideRemovedLocal", game.Name)
            : TF("Vm.ExplicitGraphicsAPIOverrideSet", game.Name, FormatApi(game.UserGraphicsApiOverride.Value));
    }

    private static GraphicsApi? ParseGraphicsApiOverride(OptionItem<GraphicsApi?>? item) => item?.Value;

    private static string FormatGraphicsApiConfidence(GraphicsApiConfidence confidence) => confidence switch
    {
        GraphicsApiConfidence.High => T("Vm.HighConfidence"),
        GraphicsApiConfidence.Medium => T("Vm.MediumConfidence"),
        GraphicsApiConfidence.Low => T("Vm.LowConfidence"),
        _ => T("Vm.UnknownConfidence")
    };

    private OptionItem<GraphicsApi?> FormatGraphicsApiOverride(GraphicsApi? api) => GraphicsApiOverrideOptions.For(api);

    private static LumaModePreference ParseLumaModePreference(OptionItem<LumaModePreference>? item) => item?.Value ?? OptionLists.LumaMode.Fallback;

    private OptionItem<LumaModePreference> FormatLumaModePreference(LumaModePreference value) => LumaModeOptions.For(value);

    private static string FormatApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX10 => "DirectX 10",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => T("Vm.Unknown")
    };
}
