using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

public sealed record ShaderAddonManagerUiSnapshot(
    IReadOnlyList<ReShadeShaderPackInfo> ShaderPacks,
    IReadOnlyList<string> SelectedShaderPackIds,
    IReadOnlyList<ReShadeAddonCatalogItem> Addons,
    IReadOnlyList<string> SelectedAddonIds,
    string CustomShaderRoot,
    string CustomAddonRoot);


public sealed record LinuxSettingsUiSnapshot(
    LinuxAppSettings Settings,
    string SettingsPath,
    string ConfigRoot,
    string CacheRoot,
    string DataRoot);

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private static string PresentOr(string? value, string fallback)
    {
        var presented = UiLocalizationService.Present(value);
        return string.IsNullOrWhiteSpace(presented) ? fallback : presented;
    }

    private sealed record ProbeCacheEntry(
        IReadOnlyList<string> Files,
        IReadOnlyList<ManagedComponent> Components,
        DlssDetectionResult Dlss);

    private readonly GameLibraryScanner _scanner;
    private readonly IGameLauncher _launcher;
    private readonly IGameRuntimeProbe _runtimeProbe;
    private readonly DlssManagerService _dlss;
    private readonly LinuxManagedComponentService _managedComponents;
    private readonly GraphicsApiOverrideStore _apiOverrideStore;
    private readonly OptiScalerVariantPreferenceStore _optiScalerVariantStore;
    private readonly LinuxSettingsStore _settingsStore;
    private readonly LinuxUpdateAllService _updateAll;
    private readonly LinuxDlssDefaultsService _dlssDefaults;
    private readonly LinuxGameLibraryStore _libraryStore;
    private readonly LinuxDeploymentRecordStore _deploymentRecords;
    private readonly LinuxHdrLaunchProfileService _hdrProfiles;
    private CancellationTokenSource? _backgroundUpdateCts;
    private LinuxAppSettings _settings;
    private LinuxGameLibraryState _libraryState;
    private bool _loadingGraphicsApiOverride;
    /// <summary>
    /// Raised while an option list bound as ItemsSource is being rebuilt. Avalonia writes null
    /// back to the TwoWay SelectedItem when the list is cleared; every persisting handler below
    /// must ignore that transient write or per-game preferences get deleted on a Settings save.
    /// </summary>
    private bool _rebuildingOptionLists;
    private bool _loadingOptiScalerVariant;
    private bool _loadingLumaModePreference;
    private bool _loadingHdrProfile;
    private readonly List<DetectedGame> _allGames = [];
    private readonly Dictionary<string, ProbeCacheEntry> _probeCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _selectionProbeCts;
    private int _selectionGeneration;
    private DlssDetectionResult _dlssDetection = new();
    private readonly HashSet<string> _selectedShaderPackIds = new(StringComparer.OrdinalIgnoreCase);
    // NR version lists and UAL/ShortFuse latest versions (TTL; Full Refresh reloads them) (Refs #86, #117).
    private readonly UpstreamVersionCatalog _versions;
    // GitHub token notice (Refs #133); the settle check runs after queued UI work.
    private readonly OneShotStatusNotice _statusNotice = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));

    public ObservableCollection<DetectedGame> Games { get; } = [];
    public ObservableCollection<ComponentRowViewModel> Components { get; } = [];
    public ObservableCollection<string> DlssSrVersions { get; } = [];
    public ObservableCollection<string> DlssRrVersions { get; } = [];
    public ObservableCollection<string> DlssFgVersions { get; } = [];
    public ObservableCollection<string> DlssNrVersions { get; } = [];
    public ObservableCollection<string> StreamlineVersions { get; } = [];
    // Labels and values of every option list come from OptionLists (one mapping, shared with the dialogs; Refs #87).
    public ObservableCollection<string> ReShadeChannels { get; } = [.. OptionLists.ReShadeChannel.Items];
    public ObservableCollection<string> GraphicsApiOverrideOptions { get; } = [.. OptionLists.GraphicsApiOverride.Items];
    public ObservableCollection<string> OptiScalerVariantOptions { get; } = [.. OptionLists.OptiScalerVariant.Items];
    public ObservableCollection<string> LumaModeOptions { get; } = [.. OptionLists.LumaMode.Items];
    public ObservableCollection<string> HdrModeOptions { get; } = [.. OptionLists.HdrMode.Items];
    public ObservableCollection<string> Dlss5FeederConsumerOptions { get; } = ["RenoDX DLSS5", "Deep Fried Chicken"];
    public ObservableCollection<string> Dlss5FeederVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> Dlss5BridgeVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> ShortFuseVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> NeuralRenderingDllVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> UalProxyNames { get; } = [];
    public string ProductVersionText => $"RHI-Linux v{RhiProductInfo.Version} · {UiLocalizationService.Text("Ui.ProductRuntime")}";

    [ObservableProperty] private DetectedGame? selectedGame;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string libraryFilterMode = "All Games";
    [ObservableProperty] private string viewMode = "Detail";
    [ObservableProperty] private string statusText = UiLocalizationService.Text("Ui.Starting");
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isSelectionProbeBusy;
    [ObservableProperty] private string? selectedDlssSrVersion;
    [ObservableProperty] private string? selectedDlssRrVersion;
    [ObservableProperty] private string? selectedDlssFgVersion;
    [ObservableProperty] private string? selectedDlssNrVersion;
    [ObservableProperty] private string? selectedStreamlineVersion;
    [ObservableProperty] private string selectedReShadeChannel = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string selectedGraphicsApiOverride = UiLocalizationService.Text("Ui.OptionAutomatic");
    [ObservableProperty] private string selectedOptiScalerVariant = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string selectedLumaModePreference = UiLocalizationService.Text("Ui.OptionAutomatic");
    [ObservableProperty] private string selectedHdrMode = UiLocalizationService.Text("Ui.HdrOff");
    [ObservableProperty] private bool hdrLegacyWsi;
    [ObservableProperty] private string reShadeLegacyVersion = "6.4.1";
    [ObservableProperty] private string reShadeCustomDllPath = "";
    [ObservableProperty] private string reShadeD3DCompiler47Path = "";
    [ObservableProperty] private string dlss5FeederConsumerPackagePath = "";
    [ObservableProperty] private string selectedDlss5FeederConsumer = "RenoDX DLSS5";
    [ObservableProperty] private string selectedDlss5FeederVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedDlss5BridgeVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedShortFuseVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedNeuralRenderingDllVersion = UiLocalizationService.Text("Ui.OptionLatest");
    public bool IsDeepFriedChickenFeederConsumer => SelectedDlss5FeederConsumer == "Deep Fried Chicken";
    public bool IsRenoDxDlss5FeederConsumer => !IsDeepFriedChickenFeederConsumer;
    [ObservableProperty] private bool reShadeManageIni = true;
    [ObservableProperty] private bool shaderCrosireMaster;
    [ObservableProperty] private bool shaderCrosireLegacy;
    [ObservableProperty] private bool shaderClshortfuse;
    [ObservableProperty] private bool shaderMaxG2D;
    [ObservableProperty] private string reShadeUpdateText = UiLocalizationService.Text("Ui.ReShadeApplyHint");
    [ObservableProperty] private string selectedUalProxyName = "";
    [ObservableProperty] private string ualLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
    [ObservableProperty] private bool shortFuseAutoConfigEnabled = false;
    [ObservableProperty] private bool shortFuseZzzLoadOrder;
    private bool _loadingShortFuseZzzLoadOrder;
    [ObservableProperty] private string shortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");

    private static string LibraryFilterDisplay(string value) => OptionLists.LibraryFilter.Display(value);

    public string GameCountText => $"{Games.Count} / {_allGames.Count} {UiLocalizationService.Text("Ui.Games")} · {LibraryFilterDisplay(LibraryFilterMode)}";
    public bool IsCompactView => string.Equals(ViewMode, "Compact", StringComparison.OrdinalIgnoreCase);
    public string ViewToggleText => IsCompactView ? UiLocalizationService.Text("Ui.ViewDetail") : UiLocalizationService.Text("Ui.ViewCompact");
    public bool TrayIconEnabled => _settings.TrayIconEnabled;
    public bool CloseToTray => _settings.CloseToTray;
    public bool AddonWatcherEnabled => _settings.AddonWatcherEnabled;
    public string AddonWatchFolder => _settings.AddonWatchFolder;
    public bool ShowMotdOnStartup => _settings.ShowMotdOnStartup;
    public string FavouriteActionText => SelectedGame?.IsFavourite == true
        ? UiLocalizationService.Text("Ui.FavouriteOn")
        : UiLocalizationService.Text("Ui.FavouriteOff");
    public string HideActionText => SelectedGame?.IsHidden == true
        ? UiLocalizationService.Text("Ui.Show")
        : UiLocalizationService.Text("Ui.Hide");
    public bool CanRemoveManualGame => SelectedGame?.IsManuallyAdded == true && !IsBusy;

    /// <summary>
    /// The selected game was moved and RHI's records still point to its old folder (Refs #38).
    /// Cached per selection and refreshed after scans and repairs: the check reads the ledger and
    /// resolves paths, so it runs off the UI thread (<see cref="CheckRepairAfterMove"/>).
    /// </summary>
    public bool ShowRepairAfterMove => SelectedGame is { } game && ReferenceEquals(_repairCheckedFor, game) && _repairNeeded;

    private DetectedGame? _repairCheckStartedFor;
    private DetectedGame? _repairCheckedFor;
    private bool _repairNeeded;
    private int _repairCheckGeneration;

    /// <summary>Re-checks, in the background, whether the selected game needs "Repair after move".</summary>
    private void CheckRepairAfterMove(bool force)
    {
        var game = SelectedGame;
        if (!force && ReferenceEquals(game, _repairCheckStartedFor)) return;
        _repairCheckStartedFor = game;
        var generation = ++_repairCheckGeneration;
        _repairCheckedFor = null;
        _repairNeeded = false;
        OnPropertyChanged(nameof(ShowRepairAfterMove));
        if (game is null) return;
        _ = CheckAsync();

        async Task CheckAsync()
        {
            bool needed;
            try { needed = await Task.Run(() => _managedComponents.NeedsRelocationRepair(game, RepairUiStores)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                needed = false; // an unreadable ledger is reported by every operation that needs it
            }
            if (generation != _repairCheckGeneration) return;
            _repairCheckedFor = game;
            _repairNeeded = needed;
            OnPropertyChanged(nameof(ShowRepairAfterMove));
        }
    }

    private bool CanRepairAfterMove() => !IsBusy && SelectedGame is not null;
    public bool CanUpdateAll => !IsBusy && _updateAll.HasManagedTargets(_allGames);
    public string SelectedTitle => SelectedGame?.Name ?? UiLocalizationService.Text("Ui.SelectGame");
    public string SelectedSource => SelectedGame is null ? "" : $"{SelectedGame.Source}  •  {SelectedGame.Runner ?? UiLocalizationService.Text("Ui.UnknownRunner")}";

    public string SelectedLibraryProvider => SelectedGame?.LibraryProvider ?? "";
    public string SelectedLaunchProvider => SelectedGame?.LaunchProvider ?? "";
    public Bitmap? SelectedLibraryProviderIcon => ProviderIconCatalog.Get(SelectedLibraryProvider);
    public Bitmap? SelectedLaunchProviderIcon => ProviderIconCatalog.Get(SelectedLaunchProvider);
    public bool ShowLibraryProviderBadge => SelectedGame is not null && SelectedLibraryProviderIcon is not null;
    public bool ShowLaunchProviderBadge => SelectedGame is not null &&
        SelectedLaunchProviderIcon is not null &&
        !string.Equals(SelectedLaunchProvider, SelectedLibraryProvider, StringComparison.OrdinalIgnoreCase);
    public string LibraryProviderToolTip => string.IsNullOrWhiteSpace(SelectedLibraryProvider)
        ? T("Vm.LibrarySourceNotIdentified")
        : TF("Vm.LibrarySource", SelectedLibraryProvider);
    public string LaunchProviderToolTip => string.IsNullOrWhiteSpace(SelectedLaunchProvider)
        ? T("Vm.LauncherPlatformNotIdentified")
        : TF("Vm.LauncherPlatform", SelectedLaunchProvider);

    public bool HasConfirmedPatchTarget => SelectedGame?.HasConfirmedPatchTarget == true;
    public string PatchTargetStatusText => SelectedGame switch
    {
        null => T("Vm.NoGameSelected"),
        { GameRootNotAllowed: true } game => GameRootNotAllowedText(game),
        { FolderMissing: true } game => FolderMissingText(game),
        { PatchTargetFolderMissing: true } => T("Vm.ModificationTargetFolderGoneUseRepair"), // Refs #147
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manual", ExecutablePath: { } manualExe } when !File.Exists(manualExe)
            => T("Vm.ModificationTargetManualExecutableMissing"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manifest", ExecutablePath: { } manifestExe } when !File.Exists(manifestExe)
            => T("Vm.ModificationTargetManifestExecutableMissing"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manual" } => T("Vm.ModificationTargetManual"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manifest" } => T("Vm.ModificationTargetManifest"),
        { HasConfirmedPatchTarget: true } => T("Vm.ModificationTargetAutomatic"),
        _ => T("Vm.ModificationTargetNotDefinedUseEdit")
    };
    public string SelectedLaunchExecutable => SelectedGame?.LaunchExecutablePath ??
        (SelectedGame is null ? "" : T("Vm.LauncherManagesStartupWithoutExposingLocal"));
    public string SelectedInstallPath => SelectedGame switch
    {
        null => "",
        { GameRootNotAllowed: true } game => TF("Vm.FolderNotAllowed", game.InstallPath),
        { FolderMissing: true } game => TF("Vm.FolderMissing", game.InstallPath),
        { HasConfirmedPatchTarget: false } => T("Vm.NotDefinedSelectRealGameExecutable"),
        _ => SelectedGame.InstallPath
    };
    public string SelectedPrefix => SelectedGame is null
        ? T("Vm.NoWineProtonPrefixDetected")
        : string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
            ? T("Vm.NativeLinuxGameNoWineProton")
            : SelectedGame.PrefixPath ?? T("Vm.WineProtonPrefixNotCreatedDetected");
    public string SelectedExecutable => SelectedGame switch
    {
        null => T("Vm.ExecutableNotDetectedYet"),
        { HasConfirmedPatchTarget: false } => T("Vm.NotDefinedLauncherExecutableWillNot"),
        _ => SelectedGame.ExecutablePath ?? T("Vm.ExecutableNotDetectedYet")
    };
    public string SelectedBinaryDirectory => SelectedGame switch
    {
        null => "",
        { HasConfirmedPatchTarget: false } => T("Vm.NotDefinedPatchesAddOnsBlocked"),
        _ => SelectedGame.BinaryDirectory ?? SelectedGame.InstallPath
    };
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
            var policy = _managedComponents.GetLumaPolicy(SelectedGame);
            return UiLocalizationService.Present(policy.Summary);
        }
    }
    public string LumaCoexistenceText
    {
        get
        {
            if (SelectedGame is null) return "";
            var policy = _managedComponents.GetLumaPolicy(SelectedGame);
            return policy.RenoDxCompatible
                ? T("Vm.UpstreamRHICompatibilityDataAllowsRenoDX")
                : T("Vm.RenoDXLumaRemainMutuallyExclusiveUnless");
        }
    }

    public bool CanEditHdrProfile => SelectedGame is not null && !IsBusy;
    public LinuxHdrLaunchPlan SelectedHdrPlan => SelectedGame is null
        ? LinuxHdrLaunchPlan.NoGame
        : _hdrProfiles.BuildPlan(SelectedGame, new LinuxHdrGameProfile { Mode = ParseHdrMode(SelectedHdrMode), EnableLegacyWsi = HdrLegacyWsi });
    public string HdrProfileHint => HdrLaunchText.Hint(SelectedHdrPlan, SelectedGame);
    public string HdrLaunchOptions => SelectedHdrPlan.LaunchOptions;
    public bool CanCopyHdrLaunchOptions => SelectedGame is not null && SelectedHdrPlan.Enabled && SelectedHdrPlan.CanApply;

    public string SelectedEngineHint
    {
        get
        {
            if (IsSelectionProbeBusy) return T("Vm.LoadingUpstreamRHICompatibilityData");
            if (SelectedGame is null) return T("Vm.NoGameSelected");

            var engine = string.IsNullOrWhiteSpace(SelectedGame.EngineHint)
                ? T("Vm.NoUpstreamEngineOverride")
                : SelectedGame.EngineHint;
            var architecture = SelectedGame.ForcedArchitecture switch
            {
                PeArchitecture.X64 => T("Vm.ForcedX64"),
                PeArchitecture.X86 => T("Vm.ForcedX86"),
                _ => ""
            };
            return engine + architecture;
        }
    }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool CanLaunch => SelectedGame is not null && _launcher.CanLaunch(SelectedGame);
    public string? LaunchToolTip => SelectedGame switch
    {
        { ParentLauncherName: { Length: > 0 } parent } => TF("Vm.OpensStartGameFromLauncher", parent),
        { LaunchCommand: null or "" } game when D3DCompilerToolInstaller.IsOrphanedProtonPrefix(game)
            => TF("Vm.UnavailablePrefixWasBuiltByProton",
                  game.CompatibilityTool ?? "?"),
        _ => null
    };

    private static string OrphanedProtonText(DetectedGame game)
        => TF("Vm.WinetricksUnavailablePrefixWasBuiltBy",
              game.CompatibilityTool ?? game.Runner ?? "?");
    public bool CanEditSelectedGame => SelectedGame is not null && !IsBusy;
    public bool CanInstallD3DCompilerWithProtontricks => SelectedGame?.UsesProton == true && HasConfirmedPatchTarget;
    public bool CanInstallD3DCompilerWithWinetricks
        => HasConfirmedPatchTarget && SelectedGame is { } game
           && D3DCompilerToolInstaller.CanInstallWithWinetricks(game);
    public string D3DCompilerWinetricksButtonText
        => SelectedGame is { } game && D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
            ? TF("Vm.InstallViaWinetricks", game.CompatibilityTool ?? game.Runner ?? game.Source)
            : T("Vm.InstallViaWinetricks2");
    public string D3DCompilerToolHint
        => SelectedGame switch
        {
            null => T("Vm.SelectGameDetermineCorrectD3DCompilerHelper"),
            { UsesProton: true } => T("Vm.SteamProtonTargetProtontricksUsedSteam"),
            { } game when D3DCompilerToolInstaller.IsOrphanedProtonPrefix(game) => OrphanedProtonText(game),
            { SteamAppId: null } game when D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
                => TF("Vm.TargetWinetricksUsesActiveMatchingPrefix", game.Source, game.CompatibilityTool ?? game.Runner ?? T("Vm.LauncherRunner")),
            { SteamAppId: null, PrefixPath: not null } => T("Vm.StandaloneSystemWineTargetWinetricksRuns"),
            _ => T("Vm.NoInitializedWineProtonPrefixWas")
        };
    private static string ReShadeChannelDisplay(string value) => OptionLists.ReShadeChannel.Display(value);

    private static string ReShadeChannelValue(string? display) => OptionLists.ReShadeChannel.Value(display);

    public bool IsReShadeLegacy => ReShadeChannelValue(SelectedReShadeChannel).Equals("Legacy", StringComparison.OrdinalIgnoreCase);
    public bool IsReShadeCustom => ReShadeChannelValue(SelectedReShadeChannel).Equals("Custom", StringComparison.OrdinalIgnoreCase);

    public bool UalInstalled => SelectedGame is not null && _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame) is not null;
    public string UalInstalledProxy => SelectedGame is null
        ? ""
        : _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame)?.Metadata.GetValueOrDefault("UAL.ProxyName") ?? "";
    public string UalInstalledVersion => SelectedGame is null
        ? ""
        : _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame)?.Version ?? "";
    public string UalStatusText => SelectedGame switch
    {
        null => T("Vm.NoGameSelected"),
        { Runner: "Native Linux" } => T("Vm.UnsupportedNativeLinuxTargets"),
        _ when UalInstalled => TF("Vm.InstalledAs", UalInstalledVersion, UalInstalledProxy),
        _ when UalProxyNames.Count == 0 => T("Vm.RequiresDetectedX86X64WindowsExecutable"),
        _ => T("Vm.Ready")
    };
    public bool CanInstallUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled
                                 && UalProxyNames.Count > 0 && !string.IsNullOrWhiteSpace(SelectedUalProxyName);
    public bool CanUpdateUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && UalInstalled;
    public bool CanInstallOrUpdateUal => UalInstalled ? CanUpdateUal : CanInstallUal;
    public bool CanRemoveUal => CanUpdateUal && !ShortFuseInstalled;
    public bool CanSelectUalProxy => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled && UalProxyNames.Count > 0;
    public string UalActionText => UalInstalled ? T("Vm.Update") : T("Vm.Install");
    public string UalProxyHint
    {
        get
        {
            if (SelectedGame is null) return T("Vm.SelectWindowsGame");
            if (UalInstalled) return TF("Vm.RHIManagesRemoveFirstChooseAnother", UalInstalledProxy);
            if (string.IsNullOrWhiteSpace(SelectedUalProxyName)) return T("Vm.ChooseProxyDLLNameASILoader");
            var notes = new List<string>();
            if (UltimateAsiLoaderDeploymentService.RecommendedNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(T("Vm.RecommendedByRHI"));
            if (UltimateAsiLoaderDeploymentService.RhiConflictNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(T("Vm.MayConflictReShadeOptiScaler"));
            var dir = SelectedGame.BinaryDirectory ?? Path.GetDirectoryName(SelectedGame.ExecutablePath ?? "");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var target = Path.Combine(dir, SelectedUalProxyName);
                if (File.Exists(target))
                    notes.Add(TF("Vm.ExistingWillPreservedAsUnlessManaged", SelectedUalProxyName, UltimateAsiLoaderDeploymentService.HookedName(SelectedUalProxyName)));
            }
            return notes.Count == 0
                ? T("Vm.WineOverrideScopedSelectedGameNative")
                : string.Join(" · ", notes) + ".";
        }
    }

    public bool ShortFuseInstalled => SelectedGame is not null && _managedComponents.IsShortFuseManaged(SelectedGame);
    public string ShortFuseInstalledVersion => SelectedGame is null ? "" : _managedComponents.GetShortFuseRecord(SelectedGame)?.Version ?? "";
    private ManagedComponent? CurrentShortFuseComponent
        => SelectedGame is not null && _probeCache.TryGetValue(Key(SelectedGame), out var probe)
            ? probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ShortFuse)
            : null;

    public string ShortFuseStatusText => SelectedGame switch
    {
        null => T("Vm.NoGameSelected"),
        { Runner: "Native Linux" } => T("Vm.UnsupportedNativeLinuxTargets"),
        _ when ShortFuseInstalled => TF("Vm.ShortFuseEnabledSummary", ShortFuseInstalledVersion, ShortFuseAutoConfigEnabled ? T("Vm.AutomaticASIConfiguration") : T("Vm.ManualASIConfiguration")),
        _ => TF("Vm.ShortFuseDisabledSummary", ShortFuseAutoConfigEnabled ? T("Vm.Enabled") : T("Vm.OptionalDisabled"))
    };
    public string ShortFuseActionText => ShortFuseInstalled ? T("Vm.Remove") : T("Vm.Install");
    public bool ShortFuseNotInstalled => !ShortFuseInstalled;
    public bool CanInstallOrRemoveShortFuse
        => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy
           && (ShortFuseInstalled || (CurrentShortFuseComponent?.Availability == ComponentAvailability.Available
                                      && CurrentShortFuseComponent.ActionAllowed));
    public string ShortFuseActionHint
        => ShortFuseInstalled
            ? T("Vm.RemovesShortFuseTransactionallyRestoresPreviousD")
            : CurrentShortFuseComponent?.ActionAllowed == true
                ? PresentOr(CurrentShortFuseComponent.Note, T("Vm.ShortFuseAvailableGame"))
                : PresentOr(CurrentShortFuseComponent?.Note, T("Vm.ShortFuseWillBecomeAvailableAfterGame"));
    public bool CanConfigureShortFuse => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !ShortFuseInstalled;
    // Pins stay editable while installed: a change swaps the add-on in place (upstream 2.7.6).
    public bool CanConfigureFeederVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureBridgeVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureShortFuseVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureNeuralRenderingDllVersion => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanLoadNeuralVersionHistory => !IsBusy;
    public bool OrchestratedDlssStackManaged => Dlss5FeederFullManaged || ShortFuseInstalled;

    public bool Dlss5FeederFullManaged => SelectedGame is not null && _managedComponents.IsDlss5FeederFullManaged(SelectedGame);
    public bool CanSwapDlssSr => HasConfirmedPatchTarget && DlssSrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssRr => HasConfirmedPatchTarget && DlssRrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssFg => HasConfirmedPatchTarget && DlssFgDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssNr => HasConfirmedPatchTarget && DlssNrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapStreamline => HasConfirmedPatchTarget && StreamlineDetected && !OrchestratedDlssStackManaged;

    public string NeuralRenderingRecommendation
    {
        get
        {
            if (SelectedGame is null) return T("Vm.NoGameSelected");
            if (!SelectedGame.HasConfirmedPatchTarget) return T("Vm.WaitingTargetExecutableNoRecommendationWill");
            if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return T("Vm.WindowsNeuralRenderingMethodsNotAvailable");

            var hasNativeDlss = _managedComponents.HasNativeDlssForNeuralRendering(
                SelectedGame, _dlssDetection.DlssPaths.Count > 0);
            var recommended = LinuxManagedComponentService.RecommendedNeuralRenderingMethod(SelectedGame, hasNativeDlss);
            var architecture = SelectedGame.ForcedArchitecture ?? PeArchitectureDetector.Detect(SelectedGame.ExecutablePath);
            if (architecture == PeArchitecture.X86 && recommended == ManagedComponentKind.Dlss5Feeder)
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

    public bool DlssSrDetected => _dlssDetection.DlssPaths.Count > 0;
    public bool DlssRrDetected => _dlssDetection.DlssdPaths.Count > 0;
    public bool DlssFgDetected => _dlssDetection.DlssgPaths.Count > 0;
    public bool DlssNrDetected => _dlssDetection.DlssnrPaths.Count > 0;
    public bool StreamlineDetected => _dlssDetection.StreamlineFolders.Count > 0;

    // A restore button is meaningful only after RHI actually created an .original backup.
    // This also doubles as a persistent visual signal that survives switching games/restarting RHI.
    public bool DlssSrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssPaths);
    public bool DlssRrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssdPaths);
    public bool DlssFgRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssgPaths);
    public bool DlssNrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssnrPaths);
    public bool StreamlineRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && CountStreamlineBackups() > 0;

    public string DlssSrDeploymentState => ShortFuseInstalled && DlssSrDetected
        ? T("Vm.ShortFuseManaged")
        : Dlss5FeederFullManaged && DlssSrDetected ? T("Vm.FeederFullPrivate") : DlssDeploymentState(_dlssDetection.DlssPaths);
    public string DlssRrDeploymentState => ShortFuseInstalled && DlssRrDetected ? T("Vm.ShortFuseManaged") : DlssDeploymentState(_dlssDetection.DlssdPaths);
    public string DlssFgDeploymentState => ShortFuseInstalled && DlssFgDetected ? T("Vm.ShortFuseManaged") : DlssDeploymentState(_dlssDetection.DlssgPaths);
    public string DlssNrDeploymentState => ShortFuseInstalled && DlssNrDetected
        ? T("Vm.ShortFuseManaged")
        : Dlss5FeederFullManaged && DlssNrDetected ? T("Vm.FeederFullPrivate") : DlssDeploymentState(_dlssDetection.DlssnrPaths);
    public string StreamlineDeploymentState
    {
        get
        {
            if (_dlssDetection.StreamlineFolders.Count == 0) return T("Vm.Missing");
            if (ShortFuseInstalled) return T("Vm.ShortFuseManaged");
            var backups = CountStreamlineBackups();
            return backups > 0 ? TF("Vm.RHIAppliedDLLS", backups) : T("Vm.GameDLLs");
        }
    }

    public string DlssSrPath => FirstOrNotFound(_dlssDetection.DlssPaths, "nvngx_dlss.dll");
    public string DlssRrPath => FirstOrNotFound(_dlssDetection.DlssdPaths, "nvngx_dlssd.dll");
    public string DlssFgPath => FirstOrNotFound(_dlssDetection.DlssgPaths, "nvngx_dlssg.dll");
    public string DlssNrPath => FirstOrNotFound(_dlssDetection.DlssnrPaths, "nvngx_dlssnr.dll");
    public string StreamlinePath => _dlssDetection.StreamlineFolders.FirstOrDefault()
        ?? T("Vm.StreamlineNotDetectedUpdateUnavailable");
    public string DlssStatusSummary => IsSelectionProbeBusy
        ? T("Vm.AnalyzingGameFilesBackground")
        : ShortFuseInstalled
            ? T("Vm.ManagedShortFuseDLSSStreamlineStackTransactional")
        : Dlss5FeederFullManaged
            ? T("Vm.ManagedDLSS5FeederFullPrivateSuper")
            : _dlssDetection.HasAny
                ? TF("Vm.DetectedSuperResolutionRayReconstructionFrame", _dlssDetection.DlssPaths.Count, _dlssDetection.DlssdPaths.Count, _dlssDetection.DlssgPaths.Count, _dlssDetection.DlssnrPaths.Count, _dlssDetection.StreamlineFolders.Count)
                : T("Vm.NoLocalDLSSStreamlineDLLDetected");

    public LinuxDlssVersionDefaults DlssVersionDefaults => new(
        _settings.DefaultDlssVersion,
        _settings.DefaultDlssdVersion,
        _settings.DefaultDlssgVersion,
        _settings.DefaultDlssnrVersion,
        _settings.DefaultStreamlineVersion);

    public bool HasDlssVersionDefaults => DlssVersionDefaults.HasAny;
    public string DlssDefaultsSummary
    {
        get
        {
            static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
            var d = DlssVersionDefaults;
            return TF("Vm.DefaultsSRRRFGNRSL", Show(d.SuperResolution), Show(d.RayReconstruction), Show(d.FrameGeneration), Show(d.NeuralRendering), Show(d.Streamline));
        }
    }

    public bool CanQuickApplyDlssDefaults =>
        SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy && HasDlssVersionDefaults &&
        _dlssDetection.HasAny && !OrchestratedDlssStackManaged &&
        !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    public bool CanRestoreAllDlss =>
        SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy && !OrchestratedDlssStackManaged &&
        (DlssSrRestoreAvailable || DlssRrRestoreAvailable || DlssFgRestoreAvailable || DlssNrRestoreAvailable || StreamlineRestoreAvailable);

    public bool CanOpenDlssBatch => !IsBusy && _allGames.Any(game =>
        game.HasConfirmedPatchTarget &&
        !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) &&
        !game.DlssScanSkippedByManifest && Directory.Exists(game.InstallPath));

    public MainWindowViewModel(
        GameLibraryScanner scanner,
        IGameLauncher launcher,
        IGameRuntimeProbe runtimeProbe,
        DlssManagerService dlss,
        LinuxManagedComponentService managedComponents,
        GraphicsApiOverrideStore? apiOverrideStore = null,
        OptiScalerVariantPreferenceStore? optiScalerVariantStore = null,
        LinuxSettingsStore? settingsStore = null,
        LinuxUpdateAllService? updateAllService = null,
        LinuxDlssDefaultsService? dlssDefaultsService = null,
        LinuxGameLibraryStore? libraryStore = null,
        LinuxDeploymentRecordStore? deploymentRecords = null,
        LinuxHdrLaunchProfileService? hdrProfiles = null)
    {
        _scanner = scanner;
        _launcher = launcher;
        _runtimeProbe = runtimeProbe;
        _runtimeProbe.LateMetadataAvailable += OnLateProbeMetadata;
        _dlss = dlss;
        _managedComponents = managedComponents;
        _versions = new UpstreamVersionCatalog(kind => _managedComponents.ListNeuralAddonVersionsAsync(kind), ApplyNeuralVersionLists);
        _apiOverrideStore = apiOverrideStore ?? new GraphicsApiOverrideStore();
        _optiScalerVariantStore = optiScalerVariantStore ?? new OptiScalerVariantPreferenceStore();
        _settingsStore = settingsStore ?? new LinuxSettingsStore();
        _updateAll = updateAllService ?? new LinuxUpdateAllService(new LinuxDeploymentRecordStore(), managedComponents);
        _dlssDefaults = dlssDefaultsService ?? new LinuxDlssDefaultsService(dlss, new LinuxDeploymentRecordStore());
        _libraryStore = libraryStore ?? new LinuxGameLibraryStore();
        _deploymentRecords = deploymentRecords ?? new LinuxDeploymentRecordStore();
        _hdrProfiles = hdrProfiles ?? new LinuxHdrLaunchProfileService();
        // Attached before the first load and detached by Dispose; it also receives any store set
        // aside before a listener existed (e.g. settings read at startup).
        _quarantineSubscription = JsonStoreQuarantine.Listen(notice => Dispatcher.UIThread.Post(() => ReportStoreQuarantined(notice)));
        _settings = _settingsStore.Load();
        _libraryState = _libraryStore.Load(out _libraryStateUnreadable);
        LibraryFilterMode = _libraryState.FilterMode;
        ViewMode = _settings.ViewMode;
        SelectedReShadeChannel = ReShadeChannelDisplay(_settings.ReShadeChannel);
        ReShadeManageIni = _settings.ReShadeManageIni;
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
        Initialization = InitializeAsync();
        UiExceptionGuard.Forget(Initialization, "initialize");
    }

    private readonly IDisposable _quarantineSubscription;
    private bool _disposed;

    /// <summary>Detaches the process-wide store notices and stops the background update loop.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quarantineSubscription.Dispose();
        try { _backgroundUpdateCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already stopped */ }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Startup work (manifest, first scan); tests await it instead of polling busy flags.</summary>
    internal Task Initialization { get; }

    public LinuxSettingsUiSnapshot GetLinuxSettingsSnapshot()
    {
        var copy = new LinuxAppSettings
        {
            UiLanguage = _settings.UiLanguage,
            ReShadeChannel = _settings.ReShadeChannel,
            ReShadeManageIni = _settings.ReShadeManageIni,
            ScreenshotPath = _settings.ScreenshotPath,
            OverlayHotkey = _settings.OverlayHotkey,
            ScreenshotHotkey = _settings.ScreenshotHotkey,
            PerGameScreenshotFolders = _settings.PerGameScreenshotFolders,
            AddonWatchFolder = _settings.AddonWatchFolder,
            AddonWatcherEnabled = _settings.AddonWatcherEnabled,
            ViewMode = _settings.ViewMode,
            TrayIconEnabled = _settings.TrayIconEnabled,
            CloseToTray = _settings.CloseToTray,
            ShowMotdOnStartup = _settings.ShowMotdOnStartup,
            CacheAllShaders = _settings.CacheAllShaders,
            VerboseLogging = _settings.VerboseLogging,
            AutoUpdateComponents = _settings.AutoUpdateComponents,
            UpdateCheckIntervalHours = _settings.UpdateCheckIntervalHours,
            DefaultDlssVersion = _settings.DefaultDlssVersion,
            DefaultDlssdVersion = _settings.DefaultDlssdVersion,
            DefaultDlssgVersion = _settings.DefaultDlssgVersion,
            DefaultDlssnrVersion = _settings.DefaultDlssnrVersion,
            DefaultStreamlineVersion = _settings.DefaultStreamlineVersion
        };
        return new(copy, _settingsStore.SettingsPath, _settingsStore.ConfigRoot, _settingsStore.CacheRoot, _settingsStore.DataRoot);
    }

    public string SaveLinuxSettings(LinuxAppSettings settings)
    {
        var previousAutoUpdate = _settings.AutoUpdateComponents;
        var previousInterval = _settings.UpdateCheckIntervalHours;
        if (TryPersist(() => _settingsStore.Save(settings), "Ui.WhatSettings", sessionOnly: false) is { } notSaved) return notSaved;
        _settings = _settingsStore.Load();
        UiLocalizationService.Apply(_settings.UiLanguage);
        ViewMode = _settings.ViewMode;
        ReShadeManageIni = _settings.ReShadeManageIni;
        RefreshLocalizedPresentation();
        NotifyUxSettings();
        if (previousAutoUpdate != _settings.AutoUpdateComponents || previousInterval != _settings.UpdateCheckIntervalHours)
            RestartBackgroundUpdateLoop(runImmediate: _settings.AutoUpdateComponents);
        NotifyDlssDefaults();
        return UiLocalizationService.Format("Vm.SettingsSaved", _settingsStore.SettingsPath);
    }

    public string ResetLinuxSettings()
    {
        LinuxAppSettings? reset = null;
        if (TryPersist(() => reset = _settingsStore.Reset(), "Ui.WhatSettings", sessionOnly: false) is { } notReset) return notReset;
        _settings = reset!;
        UiLocalizationService.Apply(_settings.UiLanguage);
        ViewMode = _settings.ViewMode;
        ReShadeManageIni = _settings.ReShadeManageIni;
        RefreshLocalizedPresentation();
        NotifyUxSettings();
        RestartBackgroundUpdateLoop(runImmediate: false);
        NotifyDlssDefaults();
        return UiLocalizationService.Text("Vm.SettingsRestoredLinuxDefaults");
    }

    public string PurgeLinuxCache()
    {
        int removed;
        try
        {
            removed = _settingsStore.PurgeCache();
        }
        catch (Exception ex)
        {
            UiExceptionGuard.Log.TryWrite("purge cache", ex);
            return CacheNotClearedText(ex);
        }
        return removed == 0 ? UiLocalizationService.Text("Vm.RHILinuxCacheAlreadyEmpty") : UiLocalizationService.Format("Vm.FileSRemovedFromCache", removed);
    }

    /// <summary>Clear-cache failure line; the exception's text in the UI language's presentation.</summary>
    internal static string CacheNotClearedText(Exception ex)
        => UiLocalizationService.Format("Ui.CacheNotCleared", UiLocalizationService.Present(ex.Message));

    private void RefreshLocalizedPresentation()
    {
        _rebuildingOptionLists = true;
        try { RefreshLocalizedPresentationCore(); }
        finally { _rebuildingOptionLists = false; }
    }

    private void RefreshLocalizedPresentationCore()
    {
        var channel = ReShadeChannelValue(SelectedReShadeChannel);
        var apiOverride = ParseGraphicsApiOverride(SelectedGraphicsApiOverride);
        var optiScalerVariant = ParseOptiScalerVariant(SelectedOptiScalerVariant);
        var lumaPreference = ParseLumaModePreference(SelectedLumaModePreference);
        var hdrMode = ParseHdrMode(SelectedHdrMode);
        var feederPin = IsLatestSelection(SelectedDlss5FeederVersion) ? null : SelectedDlss5FeederVersion;
        var bridgePin = IsLatestSelection(SelectedDlss5BridgeVersion) ? null : SelectedDlss5BridgeVersion;
        var shortFusePin = IsLatestSelection(SelectedShortFuseVersion) ? null : SelectedShortFuseVersion;
        var nrDllPin = IsLatestSelection(SelectedNeuralRenderingDllVersion) ? null : SelectedNeuralRenderingDllVersion;

        OptionLists.ReShadeChannel.Fill(ReShadeChannels);
        GraphicsApiOverrideOptions[0] = OptionLists.GraphicsApiOverride.Display(null); // only "Automatic" is translated
        OptionLists.OptiScalerVariant.Fill(OptiScalerVariantOptions);
        OptionLists.HdrMode.Fill(HdrModeOptions);
        OptionLists.LumaMode.Fill(LumaModeOptions);

        SelectedReShadeChannel = ReShadeChannelDisplay(channel);
        _loadingGraphicsApiOverride = true;
        _loadingOptiScalerVariant = true;
        _loadingLumaModePreference = true;
        _loadingHdrProfile = true;
        try
        {
            SelectedGraphicsApiOverride = FormatGraphicsApiOverride(apiOverride);
            SelectedOptiScalerVariant = FormatOptiScalerVariant(optiScalerVariant);
            SelectedLumaModePreference = FormatLumaModePreference(lumaPreference);
            SelectedHdrMode = FormatHdrMode(hdrMode);
        }
        finally
        {
            _loadingGraphicsApiOverride = false;
            _loadingOptiScalerVariant = false;
            _loadingLumaModePreference = false;
            _loadingHdrProfile = false;
        }
        // Rebuild the lists (new "Latest" label, pin kept as an option) before selecting (UI-12, Refs #80).
        ReplaceLatestLabel(Dlss5FeederVersionOptions, feederPin ?? LatestDisplay());
        ReplaceLatestLabel(Dlss5BridgeVersionOptions, bridgePin ?? LatestDisplay());
        ReplaceLatestLabel(ShortFuseVersionOptions, shortFusePin ?? LatestDisplay());
        ReplaceLatestLabel(NeuralRenderingDllVersionOptions, nrDllPin ?? LatestDisplay());
        SelectedDlss5FeederVersion = feederPin ?? LatestDisplay();
        SelectedDlss5BridgeVersion = bridgePin ?? LatestDisplay();
        SelectedShortFuseVersion = shortFusePin ?? LatestDisplay();
        SelectedNeuralRenderingDllVersion = nrDllPin ?? LatestDisplay();

        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
        if (SelectedGame is { } game)
        {
            RefreshUalState(game);
            RefreshShortFuseState(game);
            if (_probeCache.TryGetValue(Key(game), out var cached))
            {
                Components.Clear();
                foreach (var item in cached.Components) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
            }
        }
        else
        {
            UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        }

        NotifySelected();
        OnPropertyChanged(nameof(ProductVersionText));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(ViewToggleText));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        NotifyDlss();
    }

    private static void ReplaceLatestLabel(ObservableCollection<string> options, string selected)
    {
        var tags = options.Where(x => !IsLatestSelection(x)).ToArray();
        options.Clear();
        options.Add(LatestDisplay());
        foreach (var tag in tags)
            if (!options.Contains(tag, StringComparer.OrdinalIgnoreCase)) options.Add(tag);
        EnsureOption(options, selected);
    }

    public string ApplyGlobalReShadeSettingsToSelectedGame(LinuxAppSettings settings)
    {
        if (SelectedGame is null) return T("Vm.SelectGameFirst");
        if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return T("Vm.WindowsReShadeSettingsNotAppliedNative");
        if (!SelectedGame.HasConfirmedPatchTarget)
            return T("Vm.SetTargetGameExecutableEditGame");
        if (!_probeCache.TryGetValue(Key(SelectedGame), out var probe) ||
            !probe.Components.Any(component => component.Kind == ManagedComponentKind.ReShade &&
                component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
            return T("Vm.InstallDetectReShadeSelectedGameBefore");
        var path = ReShadeGlobalSettingsApplier.ApplyToGame(settings, SelectedGame);
        return TF("Vm.ReShadeCaptureHotkeyDefaultsAppliedAt", path);
    }

    private async Task InitializeAsync()
    {
        var neuralVersionCatalogTask = EnsureNeuralVersionCatalogAsync();
        // The library scan never waits behind the DLSS manifest: both start now (Refs #43).
        var dlssManifestTask = LoadDlssManifestAsync();
        await RefreshAsync();
        await dlssManifestTask;
        await neuralVersionCatalogTask;
        RestartBackgroundUpdateLoop(runImmediate: _settings.AutoUpdateComponents);
    }

    /// <summary>
    /// A merged card took over a duplicate's favourite/hidden/edit state; persist the move through the one
    /// store-write helper. Automatic, so the status line is left alone (<c>reportStatus: false</c>) and a refusal is
    /// returned for the scan's own status line instead: the card already shows the adopted state, and the next
    /// library-state save persists it (it is part of <c>_libraryState</c>, which add/edit/reset/remove copy). The
    /// move is one-way: if the duplicates later stop merging, the adopted keys stay on the card's key.
    /// </summary>
    private string? SaveAdoptedLibraryState()
        => TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatGameLibrary", reportStatus: false);

    private async Task LoadDlssManifestAsync()
    {
        try
        {
            var manifest = await _dlss.FetchManifestAsync();
            FillVersions(DlssSrVersions, manifest.Dlss.Select(x => x.Version));
            FillVersions(DlssRrVersions, manifest.Dlssd.Select(x => x.Version));
            FillVersions(DlssFgVersions, manifest.Dlssg.Select(x => x.Version));
            FillVersions(DlssNrVersions, manifest.Dlssnr.Select(x => x.Version));
            var nrDllSelected = SelectedNeuralRenderingDllVersion;
            _rebuildingOptionLists = true;
            try
            {
                FillVersions(NeuralRenderingDllVersionOptions, manifest.Dlssnr.Select(x => x.Version).Prepend(LatestDisplay()));
                EnsureOption(NeuralRenderingDllVersionOptions, nrDllSelected);
                SelectedNeuralRenderingDllVersion = nrDllSelected ?? LatestDisplay();
            }
            finally { _rebuildingOptionLists = false; }
            FillVersions(StreamlineVersions, manifest.Streamline.Select(x => x.Version));
            SelectedDlssSrVersion = DlssSrVersions.FirstOrDefault();
            SelectedDlssRrVersion = DlssRrVersions.FirstOrDefault();
            SelectedDlssFgVersion = DlssFgVersions.FirstOrDefault();
            SelectedDlssNrVersion = DlssNrVersions.FirstOrDefault();
            SelectedStreamlineVersion = StreamlineVersions.FirstOrDefault();
            NotifyDlss();
        }
        catch (Exception ex)
        {
            // Appended as a notice: this can land after the scan, whose problems notice or the selected
            // game's Ready line must stay on screen (Refs #43).
            StatusText = _statusNotice.Raise(TF("Vm.DLSSManifestUnavailable", UiLocalizationService.Present(ex.Message)), StatusText);
        }
    }

    // Computed action properties in Extras depend on IsBusy, but the initial full-library
    // Refresh can finish the per-game probe before the outer scan clears IsBusy. The managed
    // picker rows already have their decorated ActionAllowed state at that point, while UAL /
    // ShortFuse can otherwise remain visually disabled until another manual Refresh. Re-notify
    // those computed states on every busy transition so both surfaces converge automatically.
    partial void OnIsBusyChanged(bool value)
    {
        _busyGate.SetBusy(value);
        _statusNotice.SetBusy(value);
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveManualGame));
        RemoveManualGameCommand.NotifyCanExecuteChanged();
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSelectedGame));
        NotifyHdrProfile();
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(CanSelectLumaMode));
        NotifyUal();
        NotifyShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    /// <summary>
    /// GitHub answered 401 to the saved API token (Refs #133): say so once, appended to the
    /// current status line (an operation's result stays visible); the token handler already
    /// continues anonymously. Must run on the UI thread.
    /// </summary>
    public void ReportGitHubTokenRejected() => StatusText = _statusNotice.Raise(T("Vm.GitHubRejectedTokenSavedGithubApi"),
        StatusText);

    /// <summary>
    /// Start-up deployment recovery (#23) undid, could not undo, or deferred interrupted installs, or
    /// moved leftover copies aside: say so once in the status line. Must run on the UI thread.
    /// </summary>
    public void ReportDeploymentRecovery(DeploymentRecoverySummary summary)
    {
        var parts = new List<string>();
        if (summary.RolledBack > 0)
            parts.Add(TF("Vm.RHIUndidInstallSInterruptedBy", summary.RolledBack));
        if (summary.KeptLaterChanges > 0)
            parts.Add(TF("Vm.RHIUndidInterruptedInstallSBut", summary.KeptLaterChanges, summary.LogPath));
        if (summary.RollbackFailed > 0)
            parts.Add(TF("Vm.RHICouldNotUndoInterruptedInstall", summary.RollbackFailed, summary.LogPath));
        if (summary.Deferred > 0)
            parts.Add(TF("Vm.InterruptedInstallSWillFinishedAt", summary.Deferred, summary.LogPath));
        if (summary.Quarantined > 0)
            parts.Add(TF("Vm.RHISetAsideOldCopyIes", summary.Quarantined, summary.LogPath));
        if (parts.Count > 0)
            StatusText = _statusNotice.Raise(string.Join(" ", parts), StatusText);
    }


    /// <summary>
    /// A JSON store file could not be read and was set aside (Refs #57): say once which file and
    /// where its copy is, appended to the current status line. Must run on the UI thread.
    /// </summary>
    public void ReportStoreQuarantined(JsonStoreQuarantineNotice notice)
        => StatusText = _statusNotice.Raise(StoreQuarantinedText(notice), StatusText);

    public static string StoreQuarantinedText(JsonStoreQuarantineNotice notice)
    {
        var name = Path.GetFileName(notice.FilePath);
        if (!notice.WritesRefused)
            return notice.QuarantinePath is null
                ? TF("Vm.CacheCouldNotReadWillRebuilt", name)
                : TF("Vm.CacheCouldNotReadCopyWas", name, notice.QuarantinePath);
        return notice.QuarantinePath is null
            ? TF("Vm.CouldNotReadCopiedRHIWill", name, notice.FilePath)
            : TF("Vm.CouldNotReadItsContentsWere", name, notice.QuarantinePath);
    }

    /// <summary>Localised status for a store save that failed (refused unreadable file, or an I/O error).</summary>
    public static string StoreSaveFailedText(Exception error) => error switch
    {
        JsonStoreUnavailableException refused when refused.QuarantinePath is not null => TF("Vm.NotSavedCouldNotReadCopy",
            Path.GetFileName(refused.FilePath), refused.QuarantinePath),
        JsonStoreUnavailableException refused => TF("Vm.NotSavedCouldNotReadFix", refused.FilePath),
        _ => TF("Win.CouldNotSave", UiLocalizationService.Present(error.Message))
    };

    /// <summary>
    /// github_api.txt could be read by other users (Refs #36): say once whether RHI restricted it
    /// to 0600 or is ignoring the token. Must run on the UI thread.
    /// </summary>
    public void ReportGitHubTokenFileExposed(bool restricted) => StatusText = _statusNotice.Raise(GitHubTokenFileExposedText(restricted), StatusText);

    public static string GitHubTokenFileExposedText(bool restricted) => restricted
        ? T("Vm.GithubApiTxtCouldReadBy")
        : T("Vm.GithubApiTxtCanReadBy");

    partial void OnStatusTextChanged(string value)
    {
        var decorated = _statusNotice.Decorate(value);
        if (!ReferenceEquals(decorated, value))
            StatusText = decorated;
    }

    /// <summary>
    /// Shows <paramref name="message"/> in the status bar and returns it undecorated, for callers
    /// that also hand it to a panel or dialog (the status bar may append a pending notice).
    /// </summary>
    private string ShowStatus(string message)
    {
        StatusText = message;
        return message;
    }

    partial void OnIsSelectionProbeBusyChanged(bool value)
    {
        _statusNotice.SetSelectionProbeBusy(value);
        OnPropertyChanged(nameof(CanSelectLumaMode));
        NotifyUal();
        NotifyShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnLibraryFilterModeChanged(string value)
    {
        if (_libraryState is null) return;
        if (LibraryRepairRunning)
        {
            // Shown now, saved once the repair has finished rewriting the library file (LibraryRepairRunning).
            _filterChangedDuringRepair = true;
            ApplyFilter();
            return;
        }
        PersistLibraryFilter(value);
        ApplyFilter();
    }

    private void PersistLibraryFilter(string value)
    {
        AdoptRestoredLibraryFile();
        _libraryState.FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(value);
        if (!string.Equals(value, _libraryState.FilterMode, StringComparison.Ordinal))
            LibraryFilterMode = _libraryState.FilterMode;
        TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatGameLibrary");
    }

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(ViewToggleText));
    }

    partial void OnSelectedDlss5FeederConsumerChanged(string value)
    {
        OnPropertyChanged(nameof(IsDeepFriedChickenFeederConsumer));
        OnPropertyChanged(nameof(IsRenoDxDlss5FeederConsumer));
        if (!IsDeepFriedChickenFeederConsumer)
            StatusText = UiLocalizationService.Text("Vm.DLSS5FeederRenoDXDLSS5SelectedAs");
    }

    partial void OnSelectedDlss5FeederVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Feeder, value);

    partial void OnSelectedDlss5BridgeVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Dx11Bridge, value);

    partial void OnSelectedShortFuseVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.ShortFuse, value);

    partial void OnSelectedNeuralRenderingDllVersionChanged(string value)
        => SaveNeuralRenderingDllPin(value);

    partial void OnSelectedReShadeChannelChanged(string value)
    {
        OnPropertyChanged(nameof(IsReShadeLegacy));
        OnPropertyChanged(nameof(IsReShadeCustom));
    }

    partial void OnSelectedGraphicsApiOverrideChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingGraphicsApiOverride || SelectedGame is null) return;
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

    partial void OnSelectedOptiScalerVariantChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingOptiScalerVariant || SelectedGame is null) return;
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

    partial void OnSelectedLumaModePreferenceChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingLumaModePreference || SelectedGame is null) return;
        var preference = ParseLumaModePreference(value);
        var game = SelectedGame;
        if (TryPersist(() => _managedComponents.SetLumaModePreference(game, preference), "Ui.WhatGamePreferences", sessionOnly: false) is { } failure)
        {
            LoadLumaModePreference(game);
            StatusText = failure;
            return;
        }
        ReevaluateCurrentGameForLumaPolicy(game);
    }

    partial void OnSelectedHdrModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        if (_rebuildingOptionLists || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame);
    }

    partial void OnHdrLegacyWsiChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        if (_rebuildingOptionLists || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame);
    }

    partial void OnSelectedGameChanged(DetectedGame? value)
    {
        CancelSelectionProbe();
        Components.Clear();
        _dlssDetection = new();
        IsSelectionProbeBusy = false;
        CheckRepairAfterMove(force: false);

        if (value is null)
        {
            ResetGraphicsApiOverride();
            ResetOptiScalerVariant();
            ResetHdrProfile();
            ResetReShadeProfile();
            ResetUalState();
            ResetShortFuseState();
            ResetNeuralVersionPins();
            NotifySelected();
            return;
        }

        LoadGraphicsApiOverride(value);
        LoadOptiScalerVariant(value);
        LoadLumaModePreference(value);
        LoadHdrProfile(value);
        LoadReShadeProfile(value);
        RefreshUalState(value);
        RefreshShortFuseState(value);
        LoadNeuralVersionPins(value);
        UiExceptionGuard.Forget(RefreshSelectedLatestVersionsAsync(value), "RefreshSelectedLatestVersionsAsync");

        if (_probeCache.TryGetValue(Key(value), out var cached))
        {
            ApplyProbeResult(cached);
            StatusText = TF("Vm.ReadyCached", value.Name);
            NotifySelected();
            return;
        }

        IsSelectionProbeBusy = true;
        NotifySelected();
        var generation = ++_selectionGeneration;
        _selectionProbeCts = new CancellationTokenSource();
        UiExceptionGuard.Forget(AnalyzeSelectedGameAsync(value, generation, _selectionProbeCts.Token), "AnalyzeSelectedGameAsync");
    }

    private async Task AnalyzeSelectedGameAsync(DetectedGame game, int generation, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            StatusText = TF("Vm.AnalyzingBackground", game.Name);

            if (!game.HasConfirmedPatchTarget)
            {
                // No patch target: nothing is indexed (a refused manual folder may be $HOME or /).
                var emptyFiles = Array.Empty<string>();
                var components = ComponentProbe.Probe(game, emptyFiles, cancellationToken);
                _managedComponents.Decorate(game, components);
                var pending = new ProbeCacheEntry(emptyFiles, components, new DlssDetectionResult());

                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _selectionGeneration || !ReferenceEquals(SelectedGame, game)) return;

                _probeCache[Key(game)] = pending;
                ApplyProbeResult(pending);
                StatusText = game.GameRootNotAllowed
                    ? GameRootNotAllowedText(game)
                    : game.FolderMissing
                        ? FolderMissingText(game)
                        : TF("Vm.LaunchConfigurationWasPreservedButModification", game.Name);
                return;
            }

            var files = await Task.Run(
                () => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken),
                cancellationToken);

            await _runtimeProbe.ProbeAsync(game, files, cancellationToken);
            await _managedComponents.PrepareGameAsync(game, files, cancellationToken);

            var derived = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var components = ComponentProbe.Probe(game, files, cancellationToken);
                _managedComponents.Decorate(game, components);
                var dlss = game.DlssScanSkippedByManifest
                    ? new DlssDetectionResult()
                    : _dlssDefaults.DetectTargets(game, files, cancellationToken);
                return new ProbeCacheEntry(files, components, dlss);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _selectionGeneration || !ReferenceEquals(SelectedGame, game)) return;

            _probeCache[Key(game)] = derived;
            ApplyProbeResult(derived);
            timer.Stop();
            StatusText = GameFileIndexer.IsTruncated(files)
                ? TF("Vm.IndexingStoppedAtFilesSoSome",
                     game.Name, files.Count)
                : TF("Vm.ReadyFilesIndexedMs", game.Name, files.Count, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Switching games is expected to cancel the previous probe. Do not surface it as an error.
        }
        catch (Exception ex)
        {
            if (generation == _selectionGeneration && ReferenceEquals(SelectedGame, game))
                StatusText = TF("Vm.FailedAnalyze", game.Name, UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            if (generation == _selectionGeneration && ReferenceEquals(SelectedGame, game))
            {
                IsSelectionProbeBusy = false;
                NotifySelected();
            }
        }
    }

    /// <summary>
    /// A slow PCGamingWiki answer arrived after the selection finished without it (Refs #54): drop the
    /// stale probe result and, when that game is still selected and idle, analyse it again (the answer
    /// is cached by then, so this is quick).
    /// </summary>
    private void OnLateProbeMetadata(DetectedGame game) => Dispatcher.UIThread.Post(() =>
    {
        _probeCache.Remove(Key(game));
        if (ReferenceEquals(SelectedGame, game) && !IsBusy && !IsSelectionProbeBusy)
            OnSelectedGameChanged(game);
    });

    private void ApplyProbeResult(ProbeCacheEntry result)
    {
        Components.Clear();
        foreach (var item in result.Components) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
        _dlssDetection = result.Dlss;
        if (SelectedGame is { } game)
        {
            RefreshUalState(game);
            RefreshShortFuseState(game);
        }
        NotifySelected();
    }

    private readonly BusyGate _busyGate = new();
    private static readonly IReadOnlySet<string> ManualOnly = new HashSet<string>(StringComparer.Ordinal) { "Manual" };
    private static readonly IReadOnlySet<string> NoDetectors = new HashSet<string>(StringComparer.Ordinal);

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(null);

    /// <summary>
    /// Refresh that waits for the current busy operation (Update All, an install…) instead of
    /// being skipped, for changes the user just saved. True when the refresh ran and succeeded.
    /// </summary>
    private async Task<bool> RefreshWhenIdleAsync(IReadOnlySet<string>? onlyDetectors)
    {
        if (IsBusy)
            StatusText = T("Vm.WaitingCurrentOperationFinishBeforeUpdating");
        await _busyGate.WhenIdleAsync();
        return await RefreshAsync(onlyDetectors);
    }

    /// <summary>
    /// Full library refresh when <paramref name="onlyDetectors"/> is null; otherwise only the named
    /// detectors are re-run and the rest of the library is reused from the previous scan. False when it
    /// did not run (busy) or failed.
    /// </summary>
    private async Task<bool> RefreshAsync(IReadOnlySet<string>? onlyDetectors)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusText = onlyDetectors is null
            ? T("Vm.ScanningSteamHeroicLutrisWinePrefixes")
            : T("Vm.UpdatingLibrary");
        try
        {
            var currentKey = SelectedGame is null ? null : Key(SelectedGame);
            CancelSelectionProbe();
            if (onlyDetectors is null) _probeCache.Clear();
            // Full Refresh bypasses the session NR version and latest-version caches (upstream 2.7.9).
            var neuralCatalogRefresh = onlyDetectors is null ? ReportNeuralCatalogLoadAsync(_versions.FullRefreshAsync()) : null;
            SelectedGame = null;

            var scan = await _scanner.ScanWithReportAsync(onlyDetectors);
            var found = scan.Games;
            // Re-key records older builds stored under a Heroic game's folder while the game is still
            // there, so they keep matching after it moves (Refs #38).
            TryPersist(() => _deploymentRecords.MigrateGameKeys(found), "Ui.WhatDeploymentRecords", sessionOnly: false);
            _libraryState = _libraryStore.Load(out _libraryStateUnreadable);
            LibraryFilterMode = _libraryState.FilterMode;

            // Partial refresh: the scanner hands back fresh detector-level instances, but the
            // runtime probe (binary directory, graphics API, engine hint) only ever ran on the old
            // instances. Keep any old instance that still has a valid probe cache entry so a cache
            // hit is never applied to an un-probed game. Everything else takes the normal path.
            var previous = onlyDetectors is null
                ? null
                : _allGames.Where(g => _probeCache.ContainsKey(Key(g))).ToDictionary(Key, StringComparer.Ordinal);
            var merged = new List<DetectedGame>(found.Count);
            var adoptedMergedKeys = false;
            foreach (var game in found)
            {
                game.LibraryIdentityPath ??= game.InstallPath;
                adoptedMergedKeys |= _libraryState.AdoptOlderKeys(game); // older-build and merged-duplicate keys (Refs #55, #147)
                var libraryKey = LinuxGameLibraryStore.GameKey(game);
                if (previous is not null && previous.TryGetValue(Key(game), out var kept))
                {
                    kept.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
                    kept.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
                    merged.Add(kept);
                    continue;
                }
                _probeCache.Remove(Key(game));
                _libraryState.GameEdits.TryGetValue(libraryKey, out var edit);
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
                game.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
                game.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
                merged.Add(game);
            }
            var adoptRefused = adoptedMergedKeys ? SaveAdoptedLibraryState() : null;
            _allGames.Clear();
            _allGames.AddRange(merged);
            ApplyFilter();
            SelectedGame = currentKey is null
                ? Games.FirstOrDefault()
                : Games.FirstOrDefault(g => Key(g) == currentKey) ?? Games.FirstOrDefault();
            CheckRepairAfterMove(force: true); // the scan may have found the game in a new folder
            if (neuralCatalogRefresh is not null) await neuralCatalogRefresh;
            // Scan problems ride on the following lines (the selected game's "Ready — …") as a notice.
            var problems = string.Join(" ", new[] { LibraryScanStatusText.Problems(scan, AppLog.Current?.FilePath), adoptRefused }
                .Where(text => !string.IsNullOrEmpty(text)));
            string? scanProblems = problems.Length == 0 ? null : problems;
            var scanDone = LibraryScanStatusText.Done(_allGames.Count);
            StatusText = scanProblems is null ? scanDone : _statusNotice.Raise(scanProblems, scanDone);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.ScanFailed", UiLocalizationService.Present(ex.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll) return;
        var games = _allGames.ToArray();
        IsBusy = true;
        try
        {
            StatusText = T("Vm.UpdateAllPreparingRHIManagedGames");
            var indexes = await PrepareUpdateIndexesAsync(games, CancellationToken.None);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Manual);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.UpdateAllSummary(result);
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.UpdateAllFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareUpdateIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget || !_updateAll.HasManagedTargets([game])) continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
            {
                indexes[key] = cached.Files;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    private void RestartBackgroundUpdateLoop(bool runImmediate)
    {
        try { _backgroundUpdateCts?.Cancel(); } catch { }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        if (!_settings.AutoUpdateComponents) return;
        _backgroundUpdateCts = new CancellationTokenSource();
        UiExceptionGuard.Forget(BackgroundUpdateLoopAsync(_backgroundUpdateCts.Token, runImmediate), "BackgroundUpdateLoopAsync");
    }

    // A failed pass is logged, reported and retried with back-off; the loop ends only when cancelled (#81).
    private Task BackgroundUpdateLoopAsync(CancellationToken cancellationToken, bool runImmediate)
        => BackgroundUpdateLoop.RunAsync(RunBackgroundUpdatePassAsync, TimeSpan.FromHours(_settings.UpdateCheckIntervalHours), runImmediate,
            (ex, retry) => StatusText = BackgroundUpdateLoop.FailedStatus(ex, retry), cancellationToken);

    private async Task RunBackgroundUpdatePassAsync(CancellationToken cancellationToken)
    {
        if (!_settings.AutoUpdateComponents || IsBusy) return;
        var games = _allGames.ToArray();
        if (!_updateAll.HasManagedTargets(games)) return;
        IsBusy = true;
        try
        {
            var indexes = await PrepareUpdateIndexesAsync(games, cancellationToken);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Background,
                cancellationToken);
            if (result.ChangedCount > 0)
            {
                _probeCache.Clear();
                StatusText = UiLocalizationService.UpdateAllSummary(result) + T("Vm.Background");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedGame is null) return;
        var plan = SelectedHdrPlan;
        if (HdrLaunchText.LaunchRefusal(plan, _launcher is IGameLaunchOptionsLauncher) is { } refusal)
        {
            StatusText = refusal;
            return;
        }

        if (plan.AppliesAtLaunch && _launcher is IGameLaunchOptionsLauncher profiledLauncher)
        {
            var hdrGame = SelectedGame;
            StatusText = TF("Vm.LaunchingHDRProfile", hdrGame.Name, SelectedHdrMode);
            if (await TryLaunchAsync(hdrGame, () => profiledLauncher.LaunchAsync(hdrGame, new GameLaunchOptions(plan.Environment, plan.CommandPrefix))))
                StatusText = TF("Vm.LaunchingHDRProfile", hdrGame.Name, SelectedHdrMode);
            return;
        }

        var game = SelectedGame;
        var started = game.ParentLauncherName is { Length: > 0 } parent
            ? TF("Vm.OpeningStartFromLauncher", parent, game.Name)
            : TF("Vm.Launching", game.Name);
        // A store hand-off starts unwrapped; its launcher applies the HDR profile, so say where to enter it (#46).
        if (HdrLaunchText.HandoffNotice(plan, game) is { } handoff) started += " " + handoff;
        StatusText = started;
        if (await TryLaunchAsync(game, () => _launcher.LaunchAsync(game)))
            StatusText = started;
    }

    /// <summary>Runs a launch and reports a failure in the status line; true when the launch went ahead.</summary>
    private async Task<bool> TryLaunchAsync(DetectedGame game, Func<Task> launch)
    {
        try
        {
            await launch();
            return true;
        }
        catch (GameLaunchFailedException ex)
        {
            StatusText = string.IsNullOrWhiteSpace(ex.Detail)
                ? TF("Vm.CouldNotLaunchLaunchCommandExited", game.Name, ex.ExitCode)
                : TF("Vm.CouldNotLaunchLaunchCommandExited2", game.Name, ex.ExitCode, ex.Detail);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            StatusText = TF("Vm.CouldNotLaunch", game.Name, UiLocalizationService.Present(ex.Message));
        }
        return false;
    }


    public async Task<RenoDxCompatibilitySnapshot> GetRenoDxCompatibilitySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), T("Vm.SelectGameFirst"));
        if (!SelectedGame.HasConfirmedPatchTarget)
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), T("Vm.SetTargetExecutableEditGameFirst"));

        try
        {
            var snapshot = await _managedComponents
                .GetRenoDxCompatibilitySettingsAsync(SelectedGame, cancellationToken)
                .ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(snapshot.Warning))
                StatusText = UiLocalizationService.Present(snapshot.Warning);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), ShowStatus(TF("Vm.RenoDXSettings", UiLocalizationService.Present(ex.Message))));
        }
    }

    public ComponentOperationResult SetRenoDxCompatibilitySetting(string key, string value)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var result = _managedComponents.SetRenoDxCompatibilitySetting(SelectedGame, key, value);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public OptiScalerAdvancedSnapshot GetOptiScalerAdvancedSettings()
    {
        if (SelectedGame is null)
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), T("Vm.SelectGameFirst"));
        if (!SelectedGame.HasConfirmedPatchTarget)
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), T("Vm.SetTargetExecutableEditGameFirst"));
        try
        {
            var snapshot = _managedComponents.GetOptiScalerAdvancedSettings(SelectedGame);
            if (!string.IsNullOrWhiteSpace(snapshot.Warning))
                StatusText = UiLocalizationService.Present(snapshot.Warning);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), ShowStatus(TF("Vm.OptiScalerSettings", UiLocalizationService.Present(ex.Message))));
        }
    }

    public ComponentOperationResult SetOptiScalerAdvancedSetting(string id, string value)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var result = _managedComponents.SetOptiScalerAdvancedSetting(SelectedGame, id, value);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public async Task<ComponentOperationResult> SetOptiScalerAdvancedSettingAsync(
        string id,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        var result = await _managedComponents
            .SetOptiScalerAdvancedSettingAsync(game, id, value, cancellationToken)
            .ConfigureAwait(true);
        if (result.Success)
            ReevaluateCurrentGameComponents(game);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public Dlssg2030Preferences GetDlssg2030Preferences()
        => SelectedGame is null ? new Dlssg2030Preferences() : _managedComponents.GetDlssg2030Preferences(SelectedGame);

    public LinuxDeploymentRecord? GetDlssg2030Record()
        => SelectedGame is null ? null : _managedComponents.GetDlssg2030Record(SelectedGame);

    public ComponentOperationResult SetDlssg2030Preferences(string gpuGeneration, string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetDlssg2030Preferences(game, gpuGeneration, proxyName));
    }

    /// <summary>
    /// A dialog preference save (DLSS Enabler, RTX 20 FG, Universal MFG) through <see cref="TryPersist"/>: any
    /// store failure, not only a refused unreadable file, comes back as a failed result saying nothing changed, and
    /// the dialog puts its dropdowns back to the stored preferences.
    /// </summary>
    private ComponentOperationResult PersistPreference(Func<ComponentOperationResult> save)
    {
        ComponentOperationResult? result = null;
        if (TryPersist(() => result = save(), "Ui.WhatGamePreferences", sessionOnly: false) is { } failure)
            return ComponentOperationResult.Fail(failure);
        StatusText = UiLocalizationService.Present(result!.Message);
        return result;
    }

    public async Task<Dlssg2030UpdateStatus> CheckDlssg2030UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, T("Vm.SelectGameFirst"));
        var status = await _managedComponents.CheckDlssg2030UpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateDlssg2030Async(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateDlssg2030Async(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    public StandaloneDlssEnablerPreferences GetStandaloneDlssEnablerPreferences()
        => SelectedGame is null ? new StandaloneDlssEnablerPreferences() : _managedComponents.GetStandaloneDlssEnablerPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetStandaloneDlssEnablerRecord()
        => SelectedGame is null ? null : _managedComponents.GetStandaloneDlssEnablerRecord(SelectedGame);

    public ComponentOperationResult SetStandaloneDlssEnablerPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetStandaloneDlssEnablerPreferences(game, proxyName));
    }

    public async Task<DlssEnablerUpdateStatus> CheckStandaloneDlssEnablerUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, T("Vm.SelectGameFirst"));
        var status = await _managedComponents.CheckStandaloneDlssEnablerUpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateStandaloneDlssEnablerAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateStandaloneDlssEnablerAsync(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    public Rtx40MfgPreferences GetRtx40MfgPreferences()
        => SelectedGame is null ? new Rtx40MfgPreferences() : _managedComponents.GetRtx40MfgPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetRtx40MfgRecord()
        => SelectedGame is null ? null : _managedComponents.GetRtx40MfgRecord(SelectedGame);

    public ComponentOperationResult SetRtx40MfgPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetRtx40MfgPreferences(game, proxyName));
    }

    public ComponentOperationResult SetRtx40MfgPreferences(string gpuGeneration, string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetRtx40MfgPreferences(game, gpuGeneration, proxyName));
    }

    public async Task<Rtx40MfgUpdateStatus> CheckRtx40MfgUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, T("Vm.SelectGameFirst"));
        var status = await _managedComponents.CheckRtx40MfgUpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateRtx40MfgAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateRtx40MfgAsync(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    /// <summary>Set by the window: shows a confirmation (title, message, accept text) and returns true to proceed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Set by the window: shows the manifest install warnings (title, one section per component with its links, accept
    /// text) in one dialog; true to proceed.
    /// </summary>
    public Func<string, IReadOnlyList<InstallWarningSection>, string, Task<bool>>? ConfirmInstallWarningAsync { get; set; }

    /// <summary>Dialog sections for <paramref name="warnings"/>; a lone warning for the requested component has no heading, as upstream.</summary>
    public static IReadOnlyList<InstallWarningSection> InstallWarningSections(IReadOnlyList<ComponentInstallWarning> warnings, string targetName)
    {
        if (warnings is [{ AddedAutomatically: false } only]) return [new InstallWarningSection(null, only.Warning)];
        return warnings.Select(warning => new InstallWarningSection(
            warning.AddedAutomatically
                ? TF("Vm.InstalledAutomatically",
                    LinuxManagedComponentService.DisplayDependencyName(warning.Kind), targetName)
                : targetName,
            warning.Warning)).ToList();
    }

    // Upstream 2.8.0: user-initiated installs show the manifest's per-game warning first. On Linux an install can also add
    // ReShade (add-ons, Luma, ShortFuse) or RE Framework (ReShade on RE Engine) automatically; their warnings are shown in
    // the same dialog. Update All and the background pass go through LinuxUpdateAllService and never reach this prompt.
    private async Task<bool> AcceptManifestInstallWarningsAsync(
        DetectedGame game,
        ManagedComponentKind target,
        string displayName,
        bool installsTarget,
        IReadOnlyList<string>? indexedFiles)
    {
        if (ConfirmInstallWarningAsync is not { } confirm) return true;
        // The dependency plan may index the game folder (RE Framework check on RE Engine games): keep it off the UI thread.
        var pending = await Task.Run(() => _managedComponents.PendingAutoDependencies(game, target, indexedFiles));
        var warnings = ManifestInstallWarning.ForInstall(game, target, installsTarget, pending);
        if (warnings.Count == 0) return true;
        if (await confirm(TF("Vm.InstallNote", game.Name), InstallWarningSections(warnings, displayName), T("Vm.Continue")))
            return true;
        StatusText = TF("Vm.InstallationCancelled", displayName);
        return false;
    }

    private IReadOnlyList<string>? ProbedFiles(DetectedGame game)
        => _probeCache.TryGetValue(Key(game), out var probe) ? probe.Files : null;

    [RelayCommand]
    private async Task ManageComponentAsync(ManagedComponentKind kind)
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (!_probeCache.TryGetValue(Key(game), out var probe))
        {
            StatusText = T("Vm.WaitGameAnalysisFinishBeforeChanging");
            return;
        }

        var component = probe.Components.FirstOrDefault(c => c.Kind == kind);
        if (component is null || !component.ActionAllowed)
        {
            StatusText = TF("Vm.ActionBlockedProtectInstallation", kind);
            return;
        }

        IsBusy = true;
        try
        {
            var removing = component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
            if (!removing && kind == ManagedComponentKind.Dlss5Feeder && IsDeepFriedChickenFeederConsumer && string.IsNullOrWhiteSpace(Dlss5FeederConsumerPackagePath))
            {
                StatusText = T("Vm.DLSS5FeederFullDeepFriedChicken");
                return;
            }

            if (!removing && !await AcceptManifestInstallWarningsAsync(game, kind, component.DisplayName, installsTarget: true, probe.Files)) return;

            // Upstream 2.7.9: Control Ultimate Edition's RenoDX mod is an RR fix with extra steps; confirm first.
            if (!removing && kind == ManagedComponentKind.RenoDX && ControlUePostInstallService.IsControlUltimateEdition(game) &&
                ConfirmAsync is { } confirm &&
                !await confirm(T("Vm.ControlUltimateEditionRenoDXMod"), T("Vm.NOTHDRModFixesRTNoise"),
                    T("Vm.Install")))
            {
                StatusText = T("Vm.RenoDXInstallationCancelled");
                return;
            }

            StatusText = removing
                ? TF("Vm.RemovingRHIManaged", component.DisplayName)
                : TF("Vm.Installing", component.DisplayName);
            var result = removing
                ? await _managedComponents.RemoveAsync(game, kind)
                : kind == ManagedComponentKind.Dlss5Feeder
                    ? await _managedComponents.InstallDlss5FeederAsync(
                        game,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumer.DeepFriedChicken : Dlss5FeederConsumer.RenoDxDlss5,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumerPackagePath : null,
                        ReShadeD3DCompiler47Path)
                    : kind == ManagedComponentKind.OptiScaler
                        ? await _managedComponents.InstallOptiScalerAsync(game, ParseOptiScalerVariant(SelectedOptiScalerVariant))
                        : await _managedComponents.InstallAsync(game, kind, probe.Files);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.Symbol", component.DisplayName, UiLocalizationService.Present(ex.Message));
        }
        finally { IsBusy = false; }
    }

    private async Task ReprobeGameAsync(DetectedGame game)
    {
        _probeCache.Remove(Key(game));
        CancelSelectionProbe();
        Components.Clear();
        _dlssDetection = new();
        IsSelectionProbeBusy = true;
        NotifySelected();
        var generation = ++_selectionGeneration;
        _selectionProbeCts = new CancellationTokenSource();
        await AnalyzeSelectedGameAsync(game, generation, _selectionProbeCts.Token);
        LoadReShadeProfile(game);
    }

    [RelayCommand]
    private async Task CheckUalUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore");
            return;
        }
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            StatusText = T("Vm.RefreshingLatestUltimateASILoaderVersion");
            var game = SelectedGame;
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game);
            if (status.Success && GetUalArchitecture(game) is { } architecture)
                _versions.UalLatest.Set(architecture, status.LatestVersion);
            UalLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, status.LatestVersion)
                : TF("Vm.LatestVersionUnavailable", UiLocalizationService.Present(status.Message));
            StatusText = UiLocalizationService.Present(status.Message);
        }
        catch (Exception ex)
        {
            UalLatestVersionText = TF("Vm.UpdateCheckFailed", UiLocalizationService.Present(ex.Message));
            StatusText = UalLatestVersionText;
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task InstallOrUpdateUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            ComponentOperationResult result;
            if (_managedComponents.GetUltimateAsiLoaderRecord(game) is not null)
            {
                StatusText = T("Vm.UpdatingUltimateASILoader");
                result = await _managedComponents.UpdateUltimateAsiLoaderAsync(game);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SelectedUalProxyName))
                {
                    StatusText = T("Vm.ChooseProxyNameUltimateASILoader");
                    return;
                }
                StatusText = TF("Vm.InstallingUltimateASILoaderAs", SelectedUalProxyName);
                result = await _managedComponents.InstallUltimateAsiLoaderAsync(game, SelectedUalProxyName);
            }

            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                RefreshUalState(game);
                var update = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game);
                if (update.Success && GetUalArchitecture(game) is { } architecture)
                    _versions.UalLatest.Set(architecture, update.LatestVersion);
                UalLatestVersionText = update.Success
                    ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, update.LatestVersion)
                    : UalLatestVersionText;
            }
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.UltimateASILoader", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task RemoveUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            StatusText = T("Vm.RemovingUltimateASILoaderRestoringChained");
            var result = await _managedComponents.RemoveUltimateAsiLoaderAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                RefreshUalState(game);
                UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            }
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.UltimateASILoaderRemovalFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task CheckShortFuseUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore2");
            return;
        }
        IsBusy = true;
        try
        {
            var game = SelectedGame;
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game);
            if (status.Success)
                _versions.ShortFuseLatest.Set(ManagedComponentKind.ShortFuse, status.LatestVersion);
            ShortFuseLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, status.LatestVersion)
                : TF("Vm.LatestVersionUnavailable", UiLocalizationService.Present(status.Message));
            StatusText = UiLocalizationService.Present(status.Message);
        }
        catch (Exception ex) { ShortFuseLatestVersionText = TF("Vm.ShortFuseUpdateCheckFailed", UiLocalizationService.Present(ex.Message)); StatusText = ShortFuseLatestVersionText; }
        finally { IsBusy = false; NotifyShortFuse(); }
    }

    [RelayCommand]
    private async Task InstallOrRemoveShortFuseAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            var removing = _managedComponents.IsShortFuseManaged(game);
            // ShortFuse has no manifest key of its own, but installing it adds RHI-owned ReShade when absent.
            if (!removing && !await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ShortFuse, "ShortFuse", installsTarget: true, ProbedFiles(game))) return;
            StatusText = removing
                ? T("Vm.RemovingShortFuseRestoringPreviousStack")
                : T("Vm.InstallingShortFuseDLSSFrameGenerationOrchestrat");
            var result = removing
                ? await _managedComponents.RemoveShortFuseAsync(game)
                : await _managedComponents.InstallShortFuseAsync(game, ShortFuseAutoConfigEnabled);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
            }
        }
        catch (Exception ex) { StatusText = TF("Vm.ShortFuse", UiLocalizationService.Present(ex.Message)); }
        finally { IsBusy = false; NotifyShortFuse(); NotifyUal(); NotifyDlss(); }
    }

    [RelayCommand]
    private async Task ApplyReShadeProfileAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (!_probeCache.TryGetValue(Key(game), out var probe))
        {
            StatusText = T("Vm.WaitGameAnalysisFinishBeforeChanging2");
            return;
        }

        var component = probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade);
        if (component is null)
        {
            StatusText = T("Vm.ReShadeComponentStateUnavailableGame");
            return;
        }
        if ((component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable) && !component.ManagedByRhi)
        {
            StatusText = T("Vm.ExternalReShadeDetectedRHIWillNot");
            return;
        }
        if (component.Availability == ComponentAvailability.Unsupported)
        {
            var note = UiLocalizationService.Present(component.Note);
            StatusText = string.IsNullOrWhiteSpace(note)
                ? T("Vm.ReShadeNotCompatibleTarget")
                : note;
            return;
        }

        if (!Enum.TryParse<ReShadeChannel>(ReShadeChannelValue(SelectedReShadeChannel), true, out var channel))
        {
            StatusText = T("Vm.UnknownReShadeChannel");
            return;
        }

        var packSet = new HashSet<string>(_selectedShaderPackIds, StringComparer.OrdinalIgnoreCase);
        SetPack(packSet, "CrosireMaster", ShaderCrosireMaster);
        SetPack(packSet, "CrosireLegacy", ShaderCrosireLegacy);
        SetPack(packSet, "ClshortfuseShaders", ShaderClshortfuse);
        SetPack(packSet, "MaxG2DSimpleHDR", ShaderMaxG2D);
        var packs = packSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var options = new ReShadeInstallOptions
        {
            Channel = channel,
            LegacyVersion = ReShadeLegacyVersion,
            CustomDllPath = ReShadeCustomDllPath,
            D3DCompiler47Path = ReShadeD3DCompiler47Path,
            ManageIni = ReShadeManageIni,
            ShaderPackIds = packs
        };

        IsBusy = true;
        try
        {
            // Applying a profile installs ReShade when it is not there yet (Available or Unknown), and RE Framework on an
            // RE Engine game when that is missing; those are the installs the manifest warns about.
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, component.DisplayName,
                    installsTarget: ManifestInstallWarning.IsFirstInstall(component.Availability), probe.Files)) return;
            StatusText = TF("Vm.ApplyingReShadeProfile", channel);
            var result = await _managedComponents.ApplyReShadeAsync(game, options);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                _selectedShaderPackIds.Clear();
                _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
                await ReprobeGameAsync(game);
            }
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.ReShadeProfileFailed", UiLocalizationService.Present(ex.Message)));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithProtontricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithProtontricks) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47Protontricks", game.Name);
            var result = await _managedComponents.InstallD3DCompilerWithProtontricksAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerProtontricks", UiLocalizationService.Present(ex.Message)));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithWinetricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithWinetricks) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47", game.Name, game.CompatibilityTool ?? game.Runner ?? "Winetricks");
            var result = await _managedComponents.InstallD3DCompilerWithWinetricksAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerWinetricks", UiLocalizationService.Present(ex.Message)));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CheckReShadeUpdateAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            ReShadeUpdateText = T("Vm.SetTargetExecutableEditGameBefore3");
            StatusText = ReShadeUpdateText;
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _managedComponents.CheckReShadeUpdateAsync(SelectedGame);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            StatusText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = TF("Vm.UpdateCheckFailed", UiLocalizationService.Present(ex.Message));
            StatusText = ReShadeUpdateText;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanQuickApplyDlssDefaults))]
    private async Task QuickApplyDlssDefaultsAsync()
    {
        if (SelectedGame is null || !CanQuickApplyDlssDefaults) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.QuickApplyApplyingDLSSStreamlineDefaults", game.Name);
            IReadOnlyList<string>? files = _probeCache.TryGetValue(Key(game), out var cached)
                ? cached.Files
                : await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8));
            var result = await _dlssDefaults.ApplyDefaultsAsync(game, DlssVersionDefaults, files);
            StatusText = UiLocalizationService.Present(result.Summary);
            if (result.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.QuickApplyFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreAllDlss))]
    private async Task RestoreAllDlssAsync()
    {
        if (SelectedGame is null || !CanRestoreAllDlss) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.RestoringAllRHIDLSSStreamlineOriginals", game.Name);
            IReadOnlyList<string>? files = _probeCache.TryGetValue(Key(game), out var cached)
                ? cached.Files
                : await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8));
            var result = await _dlssDefaults.RestoreAllAsync(game, files);
            StatusText = UiLocalizationService.Present(result.Summary);
            if (result.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.RestoreAllFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    public async Task<IReadOnlyList<LinuxDlssBatchCandidate>> GetDlssBatchCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var games = _allGames.ToArray();
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget ||
                string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) ||
                game.DlssScanSkippedByManifest || string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
                continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
                indexes[key] = cached.Files;
            else
                indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }

        return _dlssDefaults.InspectBatch(
            games,
            game => indexes.TryGetValue(Key(game), out var files) ? files : null,
            cancellationToken);
    }

    public async Task<LinuxDlssBatchResult> ApplyDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        LinuxDlssVersionDefaults defaults,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new LinuxDlssBatchResult([]);
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.DLSSStreamlineBatchPreparingGameS", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, cancellationToken);
            var result = await _dlssDefaults.ApplyBatchAsync(
                games, defaults,
                game => indexes.TryGetValue(Key(game), out var files) ? files : null,
                cancellationToken);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.Present(result.Summary);
            return result;
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    public async Task<LinuxDlssBatchResult> RestoreDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new LinuxDlssBatchResult([]);
        IsBusy = true;
        try
        {
            StatusText = TF("Vm.DLSSStreamlineBatchRestorePreparingGame", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, cancellationToken);
            var result = await _dlssDefaults.RestoreBatchAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : null,
                cancellationToken);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.Present(result.Summary);
            return result;
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareDlssBatchIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget) continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
            {
                indexes[key] = cached.Files;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    [RelayCommand]
    private async Task SwapDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out var version, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore4");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectDLSSSwappingBlockedWhileShortFuse")
                : T("Vm.DirectDLSSSwappingBlockedWhileRHI");
            return;
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusText = T("Vm.ChooseDLSSVersionFirst");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = TF("Vm.NoExistingTargetDLLWasFound", DlssComponentDisplayName(kind));
            return;
        }

        IsBusy = true;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            StatusText = TF("Vm.DownloadingApplying", displayName, version);
            var result = await _dlss.SwapAsync(kind, paths, version);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? TF("Vm.AlreadyMatchesGameFileNothingChanged", displayName, version)
                    : TF("Vm.AppliedFileSOriginalPreservedAs", displayName, version, changed);
                NotifyDlss();
            }
            else
            {
                StatusText = TF("Vm.FailedSwap", displayName, UiLocalizationService.Present(result.Error));
            }
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.FailedSwap", DlssComponentDisplayName(kind), UiLocalizationService.Present(ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out _, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore5");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectDLSSRestoreBlockedWhileShortFuse")
                : T("Vm.DirectDLSSRestoreBlockedWhileRHI");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = TF("Vm.NoExistingTargetDLLWasFound2", DlssComponentDisplayName(kind));
            return;
        }

        IsBusy = true;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            var result = await _dlss.RestoreAsync(kind, paths);
            StatusText = result.Success
                ? TF("Vm.OriginalDLLRestored", displayName)
                : TF("Vm.RestoreUnavailable", displayName, UiLocalizationService.Present(result.Error));
            if (result.Success) NotifyDlss();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.RestoreFailed", DlssComponentDisplayName(kind), UiLocalizationService.Present(ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SwapStreamlineAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectStreamlineSwappingBlockedWhileShortFuse")
                : T("Vm.DirectStreamlineSwappingBlockedWhileDLSS5");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedStreamlineVersion))
        {
            StatusText = T("Vm.ChooseStreamlineVersionFirst");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = T("Vm.StreamlineNoLocalStreamlineFolderWas");
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = TF("Vm.DownloadingApplyingStreamline", SelectedStreamlineVersion);
            var result = await _dlss.SwapStreamlineAsync(streamlineFolders, SelectedStreamlineVersion);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? TF("Vm.StreamlineAlreadyMatchesAllDetectedDLLs", SelectedStreamlineVersion)
                    : TF("Vm.StreamlineAppliedExistingDLLSOriginals", SelectedStreamlineVersion, changed);
                NotifyDlss();
            }
            else
            {
                StatusText = TF("Vm.FailedSwapStreamline", UiLocalizationService.Present(result.Error));
            }
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.FailedSwapStreamline", UiLocalizationService.Present(ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreStreamlineAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectStreamlineRestoreBlockedWhileShortFuse")
                : T("Vm.DirectStreamlineRestoreBlockedWhileDLSS5");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = T("Vm.StreamlineNoLocalStreamlineFolderWas2");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _dlss.RestoreStreamlineAsync(streamlineFolders);
            StatusText = result.Success
                ? TF("Vm.StreamlineOriginalDLLSRestored", result.Results.Count)
                : TF("Vm.StreamlineRestoreUnavailable", UiLocalizationService.Present(result.Error));
            if (result.Success) NotifyDlss();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.StreamlineRestoreFailed", UiLocalizationService.Present(ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (SelectedGame is null) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.RealGameRootNotDefinedYet");
            return;
        }
        if (!LinuxDesktopOpener.TryOpenDirectory(SelectedGame.InstallPath, out var error))
            StatusText = TF("Vm.CouldNotOpenGameFolder", UiLocalizationService.Present(error));
    }

    [RelayCommand]
    private void OpenPrefix()
    {
        if (SelectedGame?.PrefixPath is not { } prefix || !Directory.Exists(prefix)) return;
        if (!LinuxDesktopOpener.TryOpenDirectory(prefix, out var error))
            StatusText = TF("Vm.CouldNotOpenWineProtonPrefix", UiLocalizationService.Present(error));
    }

    private void LoadReShadeProfile(DetectedGame game)
    {
        var managed = _managedComponents.IsReShadeManaged(game);
        var profile = _managedComponents.GetReShadeOptions(game);
        SelectedReShadeChannel = ReShadeChannelDisplay(managed ? profile.Channel.ToString() : _settings.ReShadeChannel);
        ReShadeLegacyVersion = string.IsNullOrWhiteSpace(profile.LegacyVersion) ? "6.4.1" : profile.LegacyVersion;
        ReShadeCustomDllPath = profile.CustomDllPath ?? "";
        ReShadeD3DCompiler47Path = profile.D3DCompiler47Path ?? "";
        ReShadeManageIni = managed ? profile.ManageIni : _settings.ReShadeManageIni;
        var packs = profile.ShaderPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedShaderPackIds.Clear();
        _selectedShaderPackIds.UnionWith(packs);
        ShaderCrosireMaster = packs.Contains("CrosireMaster");
        ShaderCrosireLegacy = packs.Contains("CrosireLegacy");
        ShaderClshortfuse = packs.Contains("ClshortfuseShaders");
        ShaderMaxG2D = packs.Contains("MaxG2DSimpleHDR");
        ReShadeUpdateText = TF("Vm.Profile", ReShadeChannelDisplay(profile.Channel.ToString()), string.IsNullOrWhiteSpace(profile.LegacyVersion) ? "" : $" {profile.LegacyVersion}");
    }

    private void ResetReShadeProfile()
    {
        SelectedReShadeChannel = ReShadeChannelDisplay(_settings.ReShadeChannel);
        ReShadeLegacyVersion = "6.4.1";
        ReShadeCustomDllPath = "";
        ReShadeD3DCompiler47Path = "";
        ReShadeManageIni = _settings.ReShadeManageIni;
        ShaderCrosireMaster = false;
        ShaderCrosireLegacy = false;
        ShaderClshortfuse = false;
        ShaderMaxG2D = false;
        _selectedShaderPackIds.Clear();
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
    }

    public async Task<ShaderAddonManagerUiSnapshot?> GetShaderAddonManagerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null) return null;
        var game = SelectedGame;
        var shaderCatalog = await _managedComponents.RefreshReShadeShaderPackCatalogAsync(cancellationToken);
        var addonSnapshot = await _managedComponents.GetReShadeAddonManagerSnapshotAsync(game, cancellationToken);
        var selectedShaders = _managedComponents.GetReShadeOptions(game).ShaderPackIds.ToArray();
        return new ShaderAddonManagerUiSnapshot(
            shaderCatalog,
            selectedShaders,
            addonSnapshot.Catalog,
            addonSnapshot.SelectedIds,
            _managedComponents.CustomShaderRoot,
            addonSnapshot.CustomAddonRoot);
    }

    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> GetShaderPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
        => _managedComponents.GetReShadeShaderPackFilesAsync(packId, cancellationToken);

    /// <summary>Saves the pack's file selection; returns the localised failure, or null when saved.</summary>
    public string? SetShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => TryPersist(() => _managedComponents.SetReShadeShaderPackExcludedFiles(packId, excluded), "Ui.WhatShaderPackSelection", sessionOnly: false);

    public async Task<ComponentOperationResult> ApplyShaderAddonManagerAsync(
        IReadOnlyList<string> shaderIds,
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null) return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        if (IsBusy) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            // Apply installs ReShade when RHI does not manage it yet (plus RE Framework on RE Engine): warn first.
            var installsReShade = !_managedComponents.IsReShadeManaged(game) &&
                (_probeCache.TryGetValue(Key(game), out var probe)
                    ? probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade) is not { } row || ManifestInstallWarning.IsFirstInstall(row.Availability)
                    : true);
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, "ReShade", installsReShade, ProbedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);

            var options = _managedComponents.GetReShadeOptions(game);
            options.ShaderPackIds = shaderIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            StatusText = T("Vm.ApplyingShaderPackSelection");
            var shaderResult = await _managedComponents.ApplyReShadeAsync(game, options, cancellationToken);
            if (!shaderResult.Success)
            {
                StatusText = UiLocalizationService.Present(shaderResult.Message);
                return shaderResult;
            }

            StatusText = T("Vm.ApplyingReShadeAddSelection");
            var addonResult = await _managedComponents.ApplyReShadeAddonSelectionAsync(game, addonIds, cancellationToken);
            if (!addonResult.Success)
            {
                return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.Present(addonResult.Message))));
            }

            _selectedShaderPackIds.Clear();
            _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
            ShaderCrosireMaster = _selectedShaderPackIds.Contains("CrosireMaster");
            ShaderCrosireLegacy = _selectedShaderPackIds.Contains("CrosireLegacy");
            ShaderClshortfuse = _selectedShaderPackIds.Contains("ClshortfuseShaders");
            ShaderMaxG2D = _selectedShaderPackIds.Contains("MaxG2DSimpleHDR");
            await ReprobeGameAsync(game);
            return ComponentOperationResult.Ok(ShowStatus(UiLocalizationService.ShaderAddonAppliedFor(
                UiLocalizationService.CurrentLanguage, options.ShaderPackIds.Count, addonIds.Count, shaderResult.FromCachedSource)));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAddOnsFailed", UiLocalizationService.Present(ex.Message))));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void SetPack(HashSet<string> packs, string id, bool enabled)
    {
        if (enabled) packs.Add(id);
        else packs.Remove(id);
    }

    // Swap/restore targets come from LinuxDlssDefaultsService (ownership exclusion re-applied at action time).
    private bool TryResolveComponent(string? component, out DlssComponentKind kind, out string? version, out IReadOnlyList<string> paths)
    {
        if (!_dlssDefaults.TryPanelTargets(SelectedGame, _dlssDetection, component, out kind, out paths))
        {
            version = null;
            return false;
        }
        version = kind switch
        {
            DlssComponentKind.SuperResolution => SelectedDlssSrVersion,
            DlssComponentKind.RayReconstruction => SelectedDlssRrVersion,
            DlssComponentKind.FrameGeneration => SelectedDlssFgVersion,
            _ => SelectedDlssNrVersion
        };
        return true;
    }

    private static string DlssComponentDisplayName(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => UiLocalizationService.Text("Ui.SuperResolution"),
        DlssComponentKind.RayReconstruction => UiLocalizationService.Text("Ui.RayReconstruction"),
        DlssComponentKind.FrameGeneration => UiLocalizationService.Text("Ui.FrameGeneration"),
        DlssComponentKind.NeuralRendering => UiLocalizationService.Text("Ui.NeuralRendering"),
        _ => "DLSS"
    };

    private void ApplyFilter()
    {
        var selected = SelectedGame;
        Games.Clear();
        var mode = ParseLibraryFilterMode(LibraryFilterMode);
        var needsManagedFacts = mode is GameLibraryFilterMode.Installed or GameLibraryFilterMode.RenoDX or GameLibraryFilterMode.Luma;
        foreach (var game in _allGames
                     .Where(game => GameLibraryFilter.Matches(
                         game,
                         mode,
                         SearchText,
                         needsManagedFacts ? GetLibraryFilterFacts(game) : default))
                     .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(game);
        OnPropertyChanged(nameof(GameCountText));
        if (selected is not null && Games.Contains(selected)) SelectedGame = selected;
        else SelectedGame = Games.FirstOrDefault();
    }

    private GameLibraryFilterFacts GetLibraryFilterFacts(DetectedGame game)
    {
        var records = _deploymentRecords.FindAll(game);
        var hasManagedReShade = records.Any(record => record.Component == ManagedComponentKind.ReShade);
        if (!game.HasConfirmedPatchTarget)
        {
            var hasManagedRenoDx = records.Any(record => record.Component == ManagedComponentKind.RenoDX);
            var hasManagedLuma = records.Any(record => record.Component == ManagedComponentKind.LumaFramework);
            return new(hasManagedReShade, hasManagedRenoDx, hasManagedLuma);
        }
        var hasReShadeIni = HasFileIgnoreCase(game.BinaryDirectory ?? game.InstallPath, "ReShade.ini")
                           || HasFileIgnoreCase(game.InstallPath, "ReShade.ini");
        var isUnrealOrUnity = game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true
                             || game.EngineHint?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true;
        var hasRenoDx = records.Any(record => record.Component == ManagedComponentKind.RenoDX) || isUnrealOrUnity;
        var hasLuma = records.Any(record => record.Component == ManagedComponentKind.LumaFramework)
                      || _managedComponents.GetLumaPolicy(game).EffectiveModeEnabled;
        return new(hasManagedReShade || hasReShadeIni, hasRenoDx, hasLuma);
    }

    private static bool HasFileIgnoreCase(string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static GameLibraryFilterMode ParseLibraryFilterMode(string? value) => value switch
    {
        "Favourites" => GameLibraryFilterMode.Favourites,
        "Installed" => GameLibraryFilterMode.Installed,
        "Unreal" => GameLibraryFilterMode.Unreal,
        "Unity" => GameLibraryFilterMode.Unity,
        "Other" => GameLibraryFilterMode.Other,
        "RenoDX" => GameLibraryFilterMode.RenoDX,
        "Luma" => GameLibraryFilterMode.Luma,
        "Hidden" => GameLibraryFilterMode.Hidden,
        _ => GameLibraryFilterMode.AllGames
    };

    [RelayCommand]
    private void ToggleViewMode()
    {
        ViewMode = IsCompactView ? "Detail" : "Compact";
        _settings.ViewMode = ViewMode;
        TryPersist(() => _settingsStore.Save(_settings), "Ui.WhatSettings");
        NotifyUxSettings();
    }

    [RelayCommand]
    private void SetLibraryFilter(string? mode)
    {
        LibraryFilterMode = LinuxGameLibraryStore.NormalizeFilterMode(mode);
    }

    [RelayCommand]
    private void ToggleFavourite()
    {
        if (SelectedGame is null || LibraryWritesPaused()) return;
        AdoptRestoredLibraryFile();
        var game = SelectedGame;
        var key = LinuxGameLibraryStore.GameKey(game);
        if (_libraryState.FavouriteGames.Contains(key))
        {
            _libraryState.FavouriteGames.Remove(key);
            game.IsFavourite = false;
        }
        else
        {
            _libraryState.FavouriteGames.Add(key);
            game.IsFavourite = true;
        }
        // Kept for this session even when the save is refused; the status line says it was not saved.
        TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatGameLibrary");
        ApplyFilter();
        NotifySelected();
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        if (SelectedGame is null || LibraryWritesPaused()) return;
        AdoptRestoredLibraryFile();
        var game = SelectedGame;
        var key = LinuxGameLibraryStore.GameKey(game);
        if (_libraryState.HiddenGames.Contains(key))
        {
            _libraryState.HiddenGames.Remove(key);
            game.IsHidden = false;
        }
        else
        {
            _libraryState.HiddenGames.Add(key);
            game.IsHidden = true;
        }
        // Kept for this session even when the save is refused; the status line says it was not saved.
        TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatGameLibrary");
        ApplyFilter();
        NotifySelected();
    }

    /// <summary>
    /// The library state add/edit/reset/remove mutate: a copy of the in-memory state, so changes kept only for this
    /// session (a favourite, hidden flag or HDR profile whose save was refused, a merged-duplicate adoption) are kept
    /// and written with it. A library file that was unreadable is read again first (<see cref="AdoptRestoredLibraryFile"/>).
    /// </summary>
    private LinuxGameLibraryState WorkingLibraryCopy()
    {
        AdoptRestoredLibraryFile();
        return LinuxGameLibraryStore.Copy(_libraryState);
    }

    /// <summary>
    /// Every library write calls this first. While the in-memory state is only the empty stand-in for an unreadable
    /// library file, the file is read again; once it reads (the user restored it), it becomes the in-memory state at
    /// once, whatever the calling operation then does, and the cards' favourite/hidden flags follow it. Saving the
    /// stand-in instead would overwrite what the user restored. The filter shown keeps its value (it is saved with the
    /// state), and the selected game's HDR controls are reloaded unless the caller is saving them
    /// (<paramref name="reloadHdrControls"/> false).
    /// </summary>
    private void AdoptRestoredLibraryFile(bool reloadHdrControls = true)
    {
        if (!_libraryStateUnreadable) return;
        var fresh = _libraryStore.Load(out var stillUnreadable);
        if (stillUnreadable) return;
        _libraryState = fresh;
        _libraryStateUnreadable = false;
        _libraryState.FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(LibraryFilterMode);
        foreach (var game in _allGames)
        {
            var key = LinuxGameLibraryStore.GameKey(game);
            game.IsFavourite = _libraryState.FavouriteGames.Contains(key);
            game.IsHidden = _libraryState.HiddenGames.Contains(key);
        }
        if (reloadHdrControls && SelectedGame is { } selected) LoadHdrProfile(selected);
    }

    private bool _libraryStateUnreadable;

    /// <summary>
    /// True while Repair after move rewrites the library file in the background: a favourite, hidden, HDR or filter
    /// save then would write the pre-repair state over the repaired file, so those changes wait (review nit 9), and
    /// Add game is refused. A filter chosen meanwhile is shown at once and saved when the repair ends, over the library
    /// state the repair left.
    /// </summary>
    internal bool LibraryRepairRunning
    {
        get => _libraryRepairRunning;
        set
        {
            _libraryRepairRunning = value;
            if (value || !_filterChangedDuringRepair) return;
            _filterChangedDuringRepair = false;
            PersistLibraryFilter(LibraryFilterMode);
        }
    }

    private bool _libraryRepairRunning;
    private bool _filterChangedDuringRepair;

    /// <summary>Says so and returns true while <see cref="LibraryRepairRunning"/>.</summary>
    private bool LibraryWritesPaused()
    {
        if (!LibraryRepairRunning) return false;
        StatusText = T("Vm.WaitForRepairAfterMove");
        return true;
    }

    /// <summary>
    /// The UI's one store-write helper: runs <paramref name="save"/>; on failure writes the exception (with its stack)
    /// to the app log, shows the localised reason on the status line and returns it. Null means saved.
    /// A store refusing to overwrite an unreadable file (<see cref="JsonStoreUnavailableException"/>) is reported with
    /// the file and its set-aside copy. <paramref name="sessionOnly"/> says the in-memory change stays for this session;
    /// pass false where the store is the authority and the caller reverts (preference dropdowns go back to the saved
    /// value) or aborts (add/edit/remove/reset of library entries, which mutate a copy and adopt it only once saved).
    /// <paramref name="reportStatus"/> false is for background saves the user did not ask for (merged-duplicate
    /// adoption): the failure is logged and returned, the status line is left alone.
    /// Every exception is caught on purpose: this is the UI boundary, so even a programming error in a store reads as
    /// "Could not save …" rather than ending the session — the full stack is in the log.
    /// </summary>
    private string? TryPersist(Action save, string whatKey, bool sessionOnly = true, bool reportStatus = true)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex)
        {
            UiExceptionGuard.Log.TryWrite("save " + whatKey, ex);
            var message = SaveFailedText(ex, whatKey, sessionOnly);
            if (reportStatus) StatusText = message;
            return message;
        }
    }

    /// <summary>
    /// <see cref="TryPersist"/>'s status text. The exception's own text goes through the UI language's presentation:
    /// store and file-write messages have PT-BR pairs, and unmapped English becomes the localised pointer to the log.
    /// </summary>
    internal static string SaveFailedText(Exception ex, string whatKey, bool sessionOnly)
        => ex is JsonStoreUnavailableException
            ? StoreSaveFailedText(ex)
            : UiLocalizationService.Format(sessionOnly ? "Ui.SaveFailedSessionOnly" : "Ui.SaveFailedNothingChanged",
                UiLocalizationService.Text(whatKey), UiLocalizationService.Present(ex.Message));

    /// <summary>
    /// Saves the Edit game target; <c>Saved</c> is the structural success flag (the message is localised).
    /// <paramref name="saved"/> runs as soon as the edit is on disk, before the library refresh waits for a
    /// running operation, so the dialog can close instead of sitting disabled with no message.
    /// </summary>
    public async Task<(bool Saved, string Message)> SaveSelectedGameEditAsync(
        string patchExecutablePath,
        string gameRootPath,
        string binaryDirectory,
        string? launchProvider,
        Action? saved = null)
    {
        if (SelectedGame is null) return (false, T("Vm.SelectGameFirst"));

        if (string.IsNullOrWhiteSpace(patchExecutablePath) || !File.Exists(patchExecutablePath))
            return (false, T("Vm.SelectRealGameExecutableShouldReceive"));
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
            return (false, T("Vm.SelectedGameRootDoesNotExist"));
        if (GameRootRefusal(gameRootPath) is { } rootRefusal) return (false, rootRefusal);
        if (string.IsNullOrWhiteSpace(binaryDirectory) || !Directory.Exists(binaryDirectory))
            return (false, T("Vm.SelectedBinariesPatchesDirectoryDoesNot"));

        var executable = Path.GetFullPath(patchExecutablePath);
        var root = Path.GetFullPath(gameRootPath);
        var binaries = Path.GetFullPath(binaryDirectory);

        var executableDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return (false, T("Vm.CouldNotDetermineTargetExecutableDirectory"));
        if (!IsPathWithin(root, executable))
            return (false, T("Vm.TargetExecutableMustInsideSelectedGame"));
        if (!IsPathWithin(root, binaries))
            return (false, T("Vm.BinariesPatchesDirectoryMustInsideSelected"));

        var managedRecords = _deploymentRecords.FindAll(SelectedGame);
        if (managedRecords.Count > 0)
        {
            var targetChanged = !PathEquals(SelectedGame.ExecutablePath, executable)
                                || !PathEquals(SelectedGame.InstallPath, root)
                                || !PathEquals(SelectedGame.BinaryDirectory, binaries);
            if (targetChanged)
                return (false, T("Vm.RemoveRHIManagedComponentsBeforeChanging"));
        }

        // Mutate a copy and adopt it only once it is on disk: a refused save must not stay in
        // _libraryState, or the next unrelated library save would persist it.
        var state = WorkingLibraryCopy();
        var key = LinuxGameLibraryStore.GameKey(SelectedGame);
        state.GameEdits[key] = new LinuxGameEditOverride
        {
            LaunchProvider = launchProvider,
            PatchExecutablePath = executable,
            GameRootPath = root,
            BinaryDirectory = binaries
        }.Normalize();
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved) return (false, notSaved);
        _libraryState = state;
        saved?.Invoke();

        var name = SelectedGame.Name;
        _probeCache.Remove(Key(SelectedGame));
        var message = await RefreshWhenIdleAsync(NoDetectors)
            ? TF("Vm.ModificationTargetSavedLauncherStartupWas", name)
            : TF("Vm.ModificationTargetSavedButLibraryWas", name);
        StatusText = message;
        return (true, message);
    }

    /// <summary>Drops the selected game's manual overrides; <c>Reset</c> is false only when the reset was refused.</summary>
    /// <param name="saved">Runs once the change is on disk, before the refresh waits for a running operation.</param>
    public async Task<(bool Reset, string Message)> ResetSelectedGameEditAsync(Action? saved = null)
    {
        if (SelectedGame is null) return (false, T("Vm.SelectGameFirst"));
        if (_deploymentRecords.FindAll(SelectedGame).Count > 0)
            return (false, T("Vm.RemoveRHIManagedComponentsBeforeRestoring"));
        var state = WorkingLibraryCopy();
        var key = LinuxGameLibraryStore.GameKey(SelectedGame);
        var removed = state.GameEdits.Remove(key);
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved) return (false, notSaved);
        _libraryState = state;
        saved?.Invoke();
        var name = SelectedGame.Name;
        _probeCache.Remove(Key(SelectedGame));
        var message = !await RefreshWhenIdleAsync(NoDetectors)
            ? TF("Vm.ChangesSavedButLibraryWasNot", name)
            : removed
                ? TF("Vm.ManualOverridesRemovedAutomaticDetectionWas", name)
                : TF("Vm.NoManualOverridesWereSaved", name);
        StatusText = message;
        return (true, message);
    }

    /// <summary>Why a folder cannot be a game root (shared rule with every detector), or null when it can.</summary>
    public static string? GameRootRefusal(string path, string? home = null) => GameRootGuard.Check(path, home) switch
    {
        GameRootProblem.None => null,
        GameRootProblem.Missing => T("Vm.SelectedGameRootDoesNotExist"),
        GameRootProblem.FilesystemRoot => T("Vm.FilesystemRootCannotGameFolderChoose"),
        GameRootProblem.HomeFolder => T("Vm.YourHomeFolderFolderAboveCannot"),
        GameRootProblem.SystemFolder => T("Vm.SystemFoldersDriveRootsCannotGame"),
        GameRootProblem.SharedFolder => T("Vm.FolderHoldsManyProgramsGamesChoose"),
        GameRootProblem.WinePrefix => T("Vm.WholeWinePrefixOneItsSystem"),
        GameRootProblem.SteamLibraryFolder => T("Vm.SteamLibraryFolderChooseGameS"),
        _ => T("Vm.FolderCannotGameFolder")
    };

    /// <summary>Plain state of a saved manual entry whose folder the guard refuses (listed, never patched).</summary>
    private static string GameRootNotAllowedText(DetectedGame game)
        => TF("Vm.FolderNotAllowedRemoveManualEntry",
            game.Name, GameRootRefusal(game.LibraryIdentityPath ?? game.InstallPath) ?? string.Empty);

    /// <summary>Plain state of a saved manual entry whose folder no longer exists (listed, never probed; Refs #145).</summary>
    private static string FolderMissingText(DetectedGame game)
        => TF("Vm.FolderMissingRemoveOrEdit", game.Name, game.LibraryIdentityPath ?? game.InstallPath);

    public async Task<string> AddManualGameAsync(LinuxManualGameEntry entry)
        => (await TryAddManualGameAsync(entry)).Message;

    /// <summary>
    /// Adds a manual game; <c>Added</c> is the structural success flag (the message is localised).
    /// <paramref name="saved"/> runs as soon as the entry is on disk, before the library refresh waits for
    /// a running operation (Update All…), so the dialog can close instead of sitting disabled with no message;
    /// the outcome then goes to the main status line.
    /// </summary>
    public async Task<(bool Added, string Message)> TryAddManualGameAsync(LinuxManualGameEntry entry, Action? saved = null)
    {
        // The Add game button and drag-and-drop are not busy-gated: a save between the repair's commit and its
        // reload would write the pre-repair library over the repaired file.
        if (LibraryRepairRunning) return (false, T("Vm.WaitForRepairAfterMove"));
        if (string.IsNullOrWhiteSpace(entry.Name)) return (false, T("Vm.EnterNameGame"));
        if (string.IsNullOrWhiteSpace(entry.InstallPath) || !Directory.Exists(entry.InstallPath))
            return (false, T("Vm.GameDirectoryDoesNotExist"));
        if (GameRootRefusal(entry.InstallPath) is { } rootRefusal) return (false, rootRefusal);
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath) && !File.Exists(entry.ExecutablePath))
            return (false, T("Vm.SelectedExecutableDoesNotExist"));
        if (!string.IsNullOrWhiteSpace(entry.PrefixPath) && !Directory.Exists(entry.PrefixPath))
            return (false, T("Vm.SelectedWineProtonPrefixDoesNot"));

        // Duplicates are judged by the library's own identity (PathIdentity: symlinks resolved, ordinal), so a
        // symlink spelling of a listed folder is refused here instead of being saved and merged away by the scan.
        var identity = PathIdentity.Canonicalize(entry.InstallPath);
        bool SameFolder(string? path) => !string.IsNullOrWhiteSpace(path) && PathIdentity.Canonicalize(path).Equals(identity, StringComparison.Ordinal);
        if (_allGames.Any(game => SameFolder(game.InstallPath) || SameFolder(game.LibraryIdentityPath)))
            return (false, T("Vm.GameDirectoryAlreadyLibrary"));

        // Mutate a copy; the live state changes only once the save succeeded.
        var state = WorkingLibraryCopy();
        if (state.ManualGames.Any(existing => SameFolder(existing.InstallPath)))
            return (false, T("Vm.ManualGameAlreadySaved"));

        state.ManualGames.Add(entry);
        state.FilterMode = "All Games";
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved) return (false, notSaved);
        _libraryState = state;
        LibraryFilterMode = "All Games";
        saved?.Invoke();
        string message;
        if (!await RefreshWhenIdleAsync(ManualOnly))
            message = TF("Vm.WasSavedManualLibraryButLibrary", entry.Name);
        else if (_allGames.FirstOrDefault(game => game.IsManuallyAdded && SameFolder(game.LibraryIdentityPath ?? game.InstallPath)) is { } added)
        {
            SelectedGame = added;
            message = TF("Vm.AddedManualLibrary", entry.Name);
        }
        else
            message = TF("Vm.WasSavedManualLibraryButDid", entry.Name);
        StatusText = message;
        return (true, message);
    }

    /// <summary>The per-game stores the UI owns, handed to "Repair after move" with the service's own.</summary>
    internal IReadOnlyList<IGameStateStore> RepairUiStores => [_libraryStore, _apiOverrideStore, _optiScalerVariantStore];

    /// <summary>
    /// Explicit repair after a move: checks every file RHI owns at the game's new folder and only
    /// then re-points its records there, together with the game's preferences, favourite/hidden
    /// flags, edits, HDR settings and update history. Nothing changes when a check fails. Records
    /// found by name and folder (a game without a store id) are adopted only after the user
    /// confirms the old and new folders.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepairAfterMove))]
    private async Task RepairAfterMoveAsync()
    {
        if (SelectedGame is not { } game) return;
        IsBusy = true;
        var repaired = false;
        try
        {
            var preview = await Task.Run(() => _managedComponents.RelocationPreview(game));
            if (preview.FirstOrDefault(r => r.Adopted) is { } adopted && ConfirmAsync is { } confirm
                && !await confirm(
                    T("Vm.RepairAfterMove"),
                    TF("Vm.RHIFoundComponentsInstalledWhichNo",
                        game.Name, adopted.Record.GameKey, game.InstallPath),
                    T("Vm.UseTheseRecords")))
            {
                StatusText = T("Vm.RepairAfterMoveCancelledNothingWas");
                return;
            }

            StatusText = TF("Vm.CheckingRHISFilesNewFolder", game.Name);
            LibraryRepairRunning = true;
            var result = await Task.Run(() => _managedComponents.RepairAfterMove(game, RepairUiStores));
            if (!result.Success)
            {
                StatusText = UiLocalizationService.Present(result.Message);
                return;
            }

            repaired = true;
            // Raised as a notice: the new folder's probe starts right after the repair and its "Ready" line would replace a
            // plain status line within milliseconds; the notice rides on that line instead (review w2-stores #3).
            StatusText = _statusNotice.Raise(result.Repaired == 0
                ? TF("Vm.GameSettingsNowPointNewFolder", game.Name)
                : TF("Vm.RHISRecordsSettingsNowPoint", game.Name, result.Repaired));
            // The library file now holds the game's favourite/hidden/edits/HDR under its new key.
            _libraryState = _libraryStore.Load(out _libraryStateUnreadable);
            var libraryKey = LinuxGameLibraryStore.GameKey(game);
            game.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
            game.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
            if (_libraryState.GameEdits.TryGetValue(libraryKey, out var edit))
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = StoreSaveFailedText(ex);
        }
        finally
        {
            LibraryRepairRunning = false;
            IsBusy = false;
            NotifySelected();
        }

        if (repaired && ReferenceEquals(SelectedGame, game))
        {
            // Reload every per-game preference from the stores and probe the new folder.
            _probeCache.Remove(Key(game));
            OnSelectedGameChanged(game);
        }
        CheckRepairAfterMove(force: true);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualGame))]
    private async Task RemoveManualGameAsync()
    {
        if (SelectedGame is not { IsManuallyAdded: true } selected) return;
        // Mutate a copy; the live state changes only once the save succeeded. Matched by every folder the
        // entry may have been saved with (an edit or the manifest may have moved InstallPath).
        var state = WorkingLibraryCopy();
        if (state.RemoveManualEntriesFor(selected) == 0)
        {
            StatusText = TF("Vm.SavedManualEntryWasNotFound", selected.Name);
            return;
        }
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is not null) return;
        _libraryState = state;
        StatusText = await RefreshWhenIdleAsync(ManualOnly)
            ? TF("Vm.RemovedFromManualLibraryGameFiles", selected.Name)
            : TF("Vm.RemovedFromManualLibraryButList", selected.Name);
    }

    public async Task<ComponentOperationResult> InstallLocalLumaPackageAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameWillReceiveLumaAdd"));
        if (IsBusy || IsSelectionProbeBusy)
            return ComponentOperationResult.Fail(T("Vm.WaitCurrentOperationAnalysisFinishBefore"));

        var game = SelectedGame;
        IsBusy = true;
        try
        {
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.LumaFramework, "Luma", installsTarget: true, ProbedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);
            StatusText = TF("Vm.InstallingLocalLumaPackage", game.Name);
            var result = await _managedComponents
                .InstallLocalLumaPackageAsync(game, sourcePath, cancellationToken)
                .ConfigureAwait(true);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
                await ReprobeGameAsync(game);
            return result;
        }
        catch (Exception ex)
        {
            var result = ComponentOperationResult.Fail(TF("Vm.LocalLuma", UiLocalizationService.Present(ex.Message)));
            StatusText = UiLocalizationService.Present(result.Message);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string ImportCustomAddonFile(string sourcePath)
    {
        var result = _managedComponents.ImportCustomAddonFile(sourcePath);
        return ShowStatus(UiLocalizationService.Present(result.Message));
    }

    private void NotifyUxSettings()
    {
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(ViewToggleText));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(FavouriteActionText));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(TrayIconEnabled));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(AddonWatcherEnabled));
        OnPropertyChanged(nameof(AddonWatchFolder));
        OnPropertyChanged(nameof(ShowMotdOnStartup));
    }

    private void CancelSelectionProbe()
    {
        _selectionProbeCts?.Cancel();
        _selectionProbeCts?.Dispose();
        _selectionProbeCts = null;
        _selectionGeneration++;
    }

    private void ResetNeuralVersionPins()
    {
        ReplaceLatestLabel(Dlss5FeederVersionOptions, LatestDisplay());
        ReplaceLatestLabel(Dlss5BridgeVersionOptions, LatestDisplay());
        ReplaceLatestLabel(ShortFuseVersionOptions, LatestDisplay());
        ReplaceLatestLabel(NeuralRenderingDllVersionOptions, LatestDisplay());
        SelectedDlss5FeederVersion = LatestDisplay();
        SelectedDlss5BridgeVersion = LatestDisplay();
        SelectedShortFuseVersion = LatestDisplay();
        SelectedNeuralRenderingDllVersion = LatestDisplay();
    }

    private void LoadNeuralVersionPins(DetectedGame game)
    {
        // Option first, then the selection: a ComboBox drops a SelectedItem missing from its items (UI-12, Refs #80).
        var feeder = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Feeder) ?? LatestDisplay();
        var bridge = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Dx11Bridge) ?? LatestDisplay();
        var shortFuse = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.ShortFuse) ?? LatestDisplay();
        var nrDll = _managedComponents.GetNeuralRenderingDllVersionPin(game) ?? LatestDisplay();
        EnsureOption(Dlss5FeederVersionOptions, feeder);
        EnsureOption(Dlss5BridgeVersionOptions, bridge);
        EnsureOption(ShortFuseVersionOptions, shortFuse);
        EnsureOption(NeuralRenderingDllVersionOptions, nrDll);
        SelectedDlss5FeederVersion = feeder;
        SelectedDlss5BridgeVersion = bridge;
        SelectedShortFuseVersion = shortFuse;
        SelectedNeuralRenderingDllVersion = nrDll;
    }

    private void SaveNeuralVersionPin(ManagedComponentKind kind, string value)
    {
        // A null/empty value only ever arrives from an ItemsSource rebuild, never from the user.
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralAddonVersionPin(game, kind);
        // Loading a game's pins re-sets the same value; only a real change persists or swaps.
        if (string.Equals(previous, pin, StringComparison.OrdinalIgnoreCase)) return;
        if (TryPersist(() => _managedComponents.SetNeuralAddonVersionPin(game, kind, pin), "Ui.WhatGamePreferences", sessionOnly: false) is not null)
        {
            LoadNeuralVersionPins(game); // the dropdown goes back to what is saved
            return;
        }
        // A legacy add-on-only DLSS5 Feeder keeps the pin for its reinstall instead of a swap that must fail (#115).
        var change = _managedComponents.PlanNeuralPinChange(game, kind);
        if (change == NeuralPinChange.SwapInPlace)
            UiExceptionGuard.Forget(SwapNeuralVersionInPlaceAsync(game, kind, previous), "SwapNeuralVersionInPlaceAsync");
        else if (change == NeuralPinChange.ReinstallNeeded)
            StatusText = T("Vm.Dlss5FeederLegacyPinSaved");
    }

    private void SaveNeuralRenderingDllPin(string value)
    {
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralRenderingDllVersionPin(game);
        if (string.Equals(previous, pin, StringComparison.Ordinal)) return;
        if (TryPersist(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, pin), "Ui.WhatGamePreferences", sessionOnly: false) is not null)
        {
            LoadNeuralVersionPins(game);
            return;
        }
        if (OrchestratedDlssStackManaged)
            UiExceptionGuard.Forget(SwapNeuralVersionInPlaceAsync(game, kind: null, previous), "SwapNeuralVersionInPlaceAsync");
    }

    /// <summary>
    /// Applies a changed pin to an installed NR method by swapping only the add-on (kind set) or
    /// the orchestrated nvngx_dlssnr.dll (kind null). On failure the previous pin and selection
    /// are restored so the dropdown keeps describing what is installed.
    /// </summary>
    private async Task SwapNeuralVersionInPlaceAsync(DetectedGame game, ManagedComponentKind? kind, string? previousPin)
    {
        void RevertPin()
        {
            if (kind is { } addon) TryPersist(() => _managedComponents.SetNeuralAddonVersionPin(game, addon, previousPin), "Ui.WhatGamePreferences", sessionOnly: false);
            else TryPersist(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, previousPin), "Ui.WhatGamePreferences", sessionOnly: false);
            if (ReferenceEquals(SelectedGame, game)) LoadNeuralVersionPins(game);
        }

        IsBusy = true;
        try
        {
            StatusText = T("Vm.SwappingNRVersionPlace");
            var result = kind is { } addon
                ? await _managedComponents.SwapNeuralAddonVersionAsync(game, addon)
                : await _managedComponents.SwapNeuralRenderingDllAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
                return;
            }
            RevertPin();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.FailedSwapNRVersion", UiLocalizationService.Present(ex.Message));
            try { RevertPin(); } catch { /* the status line already reports the failure */ }
        }
        finally
        {
            IsBusy = false;
            NotifyDlss();
        }
    }

    [RelayCommand]
    private async Task LoadNeuralVersionHistoryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = T("Vm.RefreshingNeuralRenderingAddVersions");
            await EnsureNeuralVersionCatalogAsync(forceRefresh: true, reportStatus: true);
        }
        finally
        {
            IsBusy = false;
            NotifyDlss();
        }
    }

    private Task EnsureNeuralVersionCatalogAsync(bool forceRefresh = false, bool reportStatus = false)
        => ReportNeuralCatalogLoadAsync(_versions.NeuralVersions.EnsureAsync(forceRefresh), reportStatus);

    private async Task ReportNeuralCatalogLoadAsync(Task<NeuralCatalogOutcome> load, bool reportStatus = false)
    {
        try
        {
            var outcome = await load;
            if (!reportStatus) return;
            if (outcome == NeuralCatalogOutcome.Loaded) StatusText = T("Vm.NRVersionsRefreshedLatestRemainsDefault");
            else if (outcome == NeuralCatalogOutcome.AlreadyLoaded) StatusText = T("Vm.NRVersionsUpDate");
        }
        catch (Exception ex)
        {
            if (reportStatus)
                StatusText = TF("Vm.FailedRefreshNRVersions", UiLocalizationService.Present(ex.Message));
            else
                AppLog.Warn("nr-versions", "Automatic NR version catalog refresh failed", ex);
        }
    }

    private void ApplyNeuralVersionLists(NeuralVersionLists lists)
    {
        var feederSelected = SelectedDlss5FeederVersion;
        var bridgeSelected = SelectedDlss5BridgeVersion;
        var shortFuseSelected = SelectedShortFuseVersion;
        _rebuildingOptionLists = true;
        try
        {
            ReplaceVersionOptions(Dlss5FeederVersionOptions, lists.Dlss5Feeder, feederSelected);
            ReplaceVersionOptions(Dlss5BridgeVersionOptions, lists.Dlss5Bridge, bridgeSelected);
            ReplaceVersionOptions(ShortFuseVersionOptions, lists.ShortFuse, shortFuseSelected);
            // Clearing the list nulled the bound selection; restore it now that the option exists again.
            SelectedDlss5FeederVersion = feederSelected ?? LatestDisplay();
            SelectedDlss5BridgeVersion = bridgeSelected ?? LatestDisplay();
            SelectedShortFuseVersion = shortFuseSelected ?? LatestDisplay();
        }
        finally { _rebuildingOptionLists = false; }
    }

    private static void ReplaceVersionOptions(ObservableCollection<string> target, IReadOnlyList<GitHubReleaseVersion> versions, string selected)
    {
        target.Clear();
        target.Add(LatestDisplay());
        foreach (var version in versions)
            if (!target.Contains(version.Tag, StringComparer.OrdinalIgnoreCase))
                target.Add(version.Tag);
        EnsureOption(target, selected);
    }

    private static void EnsureOption(ObservableCollection<string> target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            target.Add(value);
    }

    private PeArchitecture? GetUalArchitecture(DetectedGame game)
    {
        var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
        return architecture is PeArchitecture.X86 or PeArchitecture.X64 ? architecture : null;
    }

    private string FormatLatestVersionSummary(string? installedVersion, string? latestVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion) && !string.IsNullOrWhiteSpace(latestVersion))
        {
            if (string.Equals(installedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                return TF("Vm.InstalledAlreadyLatest", installedVersion);
            return TF("Vm.InstalledLatest", installedVersion, latestVersion);
        }
        if (!string.IsNullOrWhiteSpace(latestVersion))
            return TF("Vm.LatestVersion", latestVersion);
        if (!string.IsNullOrWhiteSpace(installedVersion))
            return TF("Vm.InstalledLoadingLatestVersion", installedVersion);
        return T("Vm.LoadingLatestVersion");
    }

    private string? CachedUalLatestVersion(DetectedGame game)
    {
        var architecture = GetUalArchitecture(game);
        return architecture is null ? null : _versions.UalLatest.TryGetFresh(architecture.Value);
    }

    private async Task<string?> GetUalLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var architecture = GetUalArchitecture(game);
        if (architecture is null) return null;
        return await _versions.UalLatest.GetAsync(architecture.Value, async () =>
        {
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }, forceRefresh);
    }

    private async Task<string?> GetShortFuseLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        return await _versions.ShortFuseLatest.GetAsync(ManagedComponentKind.ShortFuse, async () =>
        {
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }, forceRefresh);
    }

    private async Task RefreshSelectedLatestVersionsAsync(DetectedGame game)
    {
        try
        {
            var ualTask = GetUalLatestVersionAsync(game);
            var shortFuseTask = GetShortFuseLatestVersionAsync(game);
            await Task.WhenAll(ualTask, shortFuseTask);
            if (!ReferenceEquals(SelectedGame, game)) return;

            UalLatestVersionText = !string.IsNullOrWhiteSpace(ualTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, ualTask.Result)
                : GetUalArchitecture(game) is null
                    ? T("Vm.LatestVersionUnavailableNoX86X64")
                    : T("Vm.LatestVersionCurrentlyUnavailable");
            ShortFuseLatestVersionText = !string.IsNullOrWhiteSpace(shortFuseTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, shortFuseTask.Result)
                : T("Vm.LatestVersionCurrentlyUnavailable");
            NotifyUal();
            NotifyShortFuse();
        }
        catch (Exception ex)
        {
            AppLog.Warn("latest-version", "Automatic latest-version refresh failed", ex);
        }
    }

    private void ResetShortFuseState()
    {
        ShortFuseAutoConfigEnabled = false;
        SetShortFuseZzzLoadOrderSilently(false);
        ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyShortFuse();
    }

    private void RefreshShortFuseState(DetectedGame game)
    {
        ShortFuseAutoConfigEnabled = _managedComponents.GetShortFuseAutoConfigEnabled(game);
        SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
        var record = _managedComponents.GetShortFuseRecord(game);
        ShortFuseLatestVersionText = FormatLatestVersionSummary(record?.Version, _versions.ShortFuseLatest.TryGetFresh(ManagedComponentKind.ShortFuse));
        NotifyShortFuse();
    }

    private void NotifyShortFuse()
    {
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(ShortFuseInstalledVersion));
        OnPropertyChanged(nameof(ShortFuseStatusText));
        OnPropertyChanged(nameof(ShortFuseActionText));
        OnPropertyChanged(nameof(ShortFuseNotInstalled));
        OnPropertyChanged(nameof(CanInstallOrRemoveShortFuse));
        OnPropertyChanged(nameof(ShortFuseActionHint));
        OnPropertyChanged(nameof(CanConfigureShortFuse));
        OnPropertyChanged(nameof(CanConfigureShortFuseZzzLoadOrder));
        OnPropertyChanged(nameof(OrchestratedDlssStackManaged));
        CheckShortFuseUpdateCommand.NotifyCanExecuteChanged();
        InstallOrRemoveShortFuseCommand.NotifyCanExecuteChanged();
    }

    // Upstream 2.7.9 ZZZ load order: unlike Auto Config it stays live while ShortFuse is
    // installed, because toggling renames the owned add-on in place.
    public bool CanConfigureShortFuseZzzLoadOrder => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    private void SetShortFuseZzzLoadOrderSilently(bool value)
    {
        _loadingShortFuseZzzLoadOrder = true;
        try { ShortFuseZzzLoadOrder = value; }
        finally { _loadingShortFuseZzzLoadOrder = false; }
    }

    partial void OnShortFuseZzzLoadOrderChanged(bool value)
    {
        if (_loadingShortFuseZzzLoadOrder || SelectedGame is null) return;
        UiExceptionGuard.Forget(ApplyShortFuseZzzLoadOrderAsync(SelectedGame, value), "ApplyShortFuseZzzLoadOrderAsync");
    }

    private async Task ApplyShortFuseZzzLoadOrderAsync(DetectedGame game, bool enabled)
    {
        IsBusy = true;
        try
        {
            var result = await _managedComponents.SetShortFuseZzzLoadOrderAsync(game, enabled);
            StatusText = !result.Success
                ? UiLocalizationService.Present(result.Message)
                : enabled
                    ? T("Vm.ShortFuseWillLoadLastZzzRenodx")
                    : T("Vm.ShortFuseUsesDefaultNameRenodxDlss");
        }
        catch (Exception ex) { StatusText = TF("Vm.ShortFuse", UiLocalizationService.Present(ex.Message)); }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(SelectedGame, game))
                SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
        }
    }

    private void ResetUalState()
    {
        UalProxyNames.Clear();
        SelectedUalProxyName = "";
        UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyUal();
    }

    private void RefreshUalState(DetectedGame game)
    {
        var previous = SelectedUalProxyName;
        UalProxyNames.Clear();
        foreach (var name in _managedComponents.GetUltimateAsiLoaderProxyNames(game))
            UalProxyNames.Add(name);

        var record = _managedComponents.GetUltimateAsiLoaderRecord(game);
        var installed = record?.Metadata.GetValueOrDefault("UAL.ProxyName");
        if (!string.IsNullOrWhiteSpace(installed))
            SelectedUalProxyName = installed;
        else if (!string.IsNullOrWhiteSpace(previous) &&
                 UalProxyNames.Contains(previous, StringComparer.OrdinalIgnoreCase))
            SelectedUalProxyName = UalProxyNames.First(x => x.Equals(previous, StringComparison.OrdinalIgnoreCase));
        else
            SelectedUalProxyName = _managedComponents.GetUltimateAsiLoaderRecommendedProxy(game);

        UalLatestVersionText = FormatLatestVersionSummary(record?.Version, CachedUalLatestVersion(game));
        NotifyUal();
    }

    private void NotifyUal()
    {
        OnPropertyChanged(nameof(UalInstalled));
        OnPropertyChanged(nameof(UalInstalledProxy));
        OnPropertyChanged(nameof(UalInstalledVersion));
        OnPropertyChanged(nameof(UalStatusText));
        OnPropertyChanged(nameof(CanInstallUal));
        OnPropertyChanged(nameof(CanUpdateUal));
        OnPropertyChanged(nameof(CanInstallOrUpdateUal));
        OnPropertyChanged(nameof(CanRemoveUal));
        OnPropertyChanged(nameof(CanSelectUalProxy));
        OnPropertyChanged(nameof(UalActionText));
        OnPropertyChanged(nameof(UalProxyHint));
        NotifyUalCommands();
    }

    private void NotifyUalCommands()
    {
        CheckUalUpdateCommand.NotifyCanExecuteChanged();
        InstallOrUpdateUalCommand.NotifyCanExecuteChanged();
        RemoveUalCommand.NotifyCanExecuteChanged();
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
        try { SelectedOptiScalerVariant = UiLocalizationService.Text("Ui.OptionStable"); }
        finally { _loadingOptiScalerVariant = false; }
    }

    private static OptiScalerVariant ParseOptiScalerVariant(string? value) => OptionLists.OptiScalerVariant.Value(value);

    private static string FormatOptiScalerVariant(OptiScalerVariant variant) => OptionLists.OptiScalerVariant.Display(variant);

    private void LoadLumaModePreference(DetectedGame game)
    {
        _loadingLumaModePreference = true;
        try
        {
            SelectedLumaModePreference = FormatLumaModePreference(_managedComponents.GetLumaPolicy(game).UserPreference);
        }
        finally { _loadingLumaModePreference = false; }
    }

    private void ReevaluateCurrentGameComponents(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else
        {
            NotifySelected();
        }
    }

    private void ReevaluateCurrentGameForLumaPolicy(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else NotifySelected();
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        StatusText = $"{game.Name}: {UiLocalizationService.Present(_managedComponents.GetLumaPolicy(game).Summary)}.";
    }

    private static string ParseHdrMode(string? value) => OptionLists.HdrMode.Value(value);

    private static string FormatHdrMode(string? mode) => OptionLists.HdrMode.Display(mode ?? LinuxHdrModes.Off);

    private void LoadHdrProfile(DetectedGame game)
    {
        _loadingHdrProfile = true;
        try
        {
            var key = LinuxGameLibraryStore.GameKey(game);
            var profile = _libraryState.HdrProfiles.TryGetValue(key, out var saved)
                ? saved.Normalize()
                : new LinuxHdrGameProfile();
            SelectedHdrMode = FormatHdrMode(profile.Mode);
            HdrLegacyWsi = profile.EnableLegacyWsi;
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
        }
        finally { _loadingHdrProfile = false; }
        NotifyHdrProfile();
    }

    private void SaveHdrProfile(DetectedGame game)
    {
        if (LibraryWritesPaused())
        {
            LoadHdrProfile(game);
            return;
        }
        var key = LinuxGameLibraryStore.GameKey(game);
        var profile = new LinuxHdrGameProfile { Mode = ParseHdrMode(SelectedHdrMode), EnableLegacyWsi = HdrLegacyWsi }.Normalize();
        AdoptRestoredLibraryFile(reloadHdrControls: false);
        if (profile.Mode == LinuxHdrModes.Off && !profile.EnableLegacyWsi) _libraryState.HdrProfiles.Remove(key);
        else _libraryState.HdrProfiles[key] = profile;
        TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatHdrProfile");
        NotifyHdrProfile();
    }

    private void NotifyHdrProfile()
    {
        OnPropertyChanged(nameof(CanEditHdrProfile));
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
    }

    public string GetSelectedHdrLaunchOptions() => HdrLaunchOptions;

    private void ResetGraphicsApiOverride()
    {
        _loadingGraphicsApiOverride = true;
        try { SelectedGraphicsApiOverride = UiLocalizationService.Text("Ui.OptionAutomatic"); }
        finally { _loadingGraphicsApiOverride = false; }
    }

    private void ReevaluateCurrentGameForApiOverride(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else
        {
            NotifySelected();
        }
        StatusText = game.UserGraphicsApiOverride is null
            ? game.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown
                ? TF("Vm.ExplicitGraphicsAPIOverrideRemovedUpstream", game.Name, FormatApi(manifestApi))
                : TF("Vm.ExplicitGraphicsAPIOverrideRemovedLocal", game.Name)
            : TF("Vm.ExplicitGraphicsAPIOverrideSet", game.Name, FormatApi(game.UserGraphicsApiOverride.Value));
    }

    private static GraphicsApi? ParseGraphicsApiOverride(string? value) => OptionLists.GraphicsApiOverride.Value(value);

    private static string FormatGraphicsApiConfidence(GraphicsApiConfidence confidence) => confidence switch
    {
        GraphicsApiConfidence.High => T("Vm.HighConfidence"),
        GraphicsApiConfidence.Medium => T("Vm.MediumConfidence"),
        GraphicsApiConfidence.Low => T("Vm.LowConfidence"),
        _ => T("Vm.UnknownConfidence")
    };

    private static string FormatGraphicsApiOverride(GraphicsApi? api) => OptionLists.GraphicsApiOverride.Display(api);

    private static LumaModePreference ParseLumaModePreference(string? value) => OptionLists.LumaMode.Value(value);

    private static string FormatLumaModePreference(LumaModePreference value) => OptionLists.LumaMode.Display(value);

    private static bool IsLatestSelection(string? value) => OptionLists.IsLatest(value);

    private static string LatestDisplay() => OptionLists.LatestDisplay();

    private void NotifySelected()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedLibraryProvider));
        OnPropertyChanged(nameof(SelectedLaunchProvider));
        OnPropertyChanged(nameof(SelectedLibraryProviderIcon));
        OnPropertyChanged(nameof(SelectedLaunchProviderIcon));
        OnPropertyChanged(nameof(ShowLibraryProviderBadge));
        OnPropertyChanged(nameof(ShowLaunchProviderBadge));
        OnPropertyChanged(nameof(LibraryProviderToolTip));
        OnPropertyChanged(nameof(LaunchProviderToolTip));
        OnPropertyChanged(nameof(HasConfirmedPatchTarget));
        OnPropertyChanged(nameof(PatchTargetStatusText));
        OnPropertyChanged(nameof(SelectedLaunchExecutable));
        OnPropertyChanged(nameof(SelectedInstallPath));
        OnPropertyChanged(nameof(SelectedPrefix));
        OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(SelectedBinaryDirectory));
        OnPropertyChanged(nameof(SelectedApi));
        OnPropertyChanged(nameof(SelectedApiEvidence));
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(GraphicsApiOverrideHint));
        OnPropertyChanged(nameof(CanSelectLumaMode));
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        OnPropertyChanged(nameof(SelectedEngineHint));
        NotifyHdrProfile();
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchToolTip));
        OnPropertyChanged(nameof(CanEditSelectedGame));
        OnPropertyChanged(nameof(FavouriteActionText));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(CanRemoveManualGame));
        RemoveManualGameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRepairAfterMove));
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithProtontricks));
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithWinetricks));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
        NotifyDlss();
        NotifyUal();
        NotifyShortFuse();
    }

    private void NotifyDlss()
    {
        OnPropertyChanged(nameof(NeuralRenderingRecommendation));
        OnPropertyChanged(nameof(Dlss5FeederFullManaged));
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(CanConfigureFeederVersionPin));
        OnPropertyChanged(nameof(CanConfigureBridgeVersionPin));
        OnPropertyChanged(nameof(CanConfigureShortFuseVersionPin));
        OnPropertyChanged(nameof(CanConfigureNeuralRenderingDllVersion));
        OnPropertyChanged(nameof(CanLoadNeuralVersionHistory));
        OnPropertyChanged(nameof(OrchestratedDlssStackManaged));
        OnPropertyChanged(nameof(CanSwapDlssSr));
        OnPropertyChanged(nameof(CanSwapDlssRr));
        OnPropertyChanged(nameof(CanSwapDlssFg));
        OnPropertyChanged(nameof(CanSwapDlssNr));
        OnPropertyChanged(nameof(CanSwapStreamline));
        OnPropertyChanged(nameof(DlssSrDetected));
        OnPropertyChanged(nameof(DlssRrDetected));
        OnPropertyChanged(nameof(DlssFgDetected));
        OnPropertyChanged(nameof(DlssNrDetected));
        OnPropertyChanged(nameof(StreamlineDetected));
        OnPropertyChanged(nameof(DlssSrRestoreAvailable));
        OnPropertyChanged(nameof(DlssRrRestoreAvailable));
        OnPropertyChanged(nameof(DlssFgRestoreAvailable));
        OnPropertyChanged(nameof(DlssNrRestoreAvailable));
        OnPropertyChanged(nameof(StreamlineRestoreAvailable));
        OnPropertyChanged(nameof(DlssSrDeploymentState));
        OnPropertyChanged(nameof(DlssRrDeploymentState));
        OnPropertyChanged(nameof(DlssFgDeploymentState));
        OnPropertyChanged(nameof(DlssNrDeploymentState));
        OnPropertyChanged(nameof(StreamlineDeploymentState));
        OnPropertyChanged(nameof(DlssSrPath));
        OnPropertyChanged(nameof(DlssRrPath));
        OnPropertyChanged(nameof(DlssFgPath));
        OnPropertyChanged(nameof(DlssNrPath));
        OnPropertyChanged(nameof(StreamlinePath));
        OnPropertyChanged(nameof(DlssStatusSummary));
        NotifyDlssDefaults();
    }

    private void NotifyDlssDefaults()
    {
        OnPropertyChanged(nameof(DlssVersionDefaults));
        OnPropertyChanged(nameof(HasDlssVersionDefaults));
        OnPropertyChanged(nameof(DlssDefaultsSummary));
        OnPropertyChanged(nameof(CanQuickApplyDlssDefaults));
        OnPropertyChanged(nameof(CanRestoreAllDlss));
        OnPropertyChanged(nameof(CanOpenDlssBatch));
        QuickApplyDlssDefaultsCommand.NotifyCanExecuteChanged();
        RestoreAllDlssCommand.NotifyCanExecuteChanged();
    }

    private static bool HasRhiBackup(IEnumerable<string> targets)
        => targets.Any(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));

    private static string DlssDeploymentState(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return T("Vm.Missing");
        var backups = targets.Count(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
        return backups > 0
            ? TF("Vm.RHIApplied", backups, targets.Count)
            : targets.Count == 1 ? T("Vm.GameDLL") : TF("Vm.GameDLLs2", targets.Count);
    }

    private int CountStreamlineBackups()
    {
        var ext = TransactionalFileDeploymentService.BackupExtension;
        return _dlssDetection.StreamlineFolders
            .SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name)))
            .Distinct(StringComparer.Ordinal)
            .Count(target => File.Exists(target + ext));
    }

    private static void FillVersions(ObservableCollection<string> target, IEnumerable<string> versions)
    {
        target.Clear();
        foreach (var version in versions) target.Add(version);
    }

    private static string FirstOrNotFound(IReadOnlyList<string> paths, string dll)
        => paths.FirstOrDefault() ?? TF("Vm.MissingDirectSwapBlocked", dll);

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

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.GetFullPath(candidate);
            if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // In-session caches (probe, selection) are per card and folder; saved library state uses GameKey (Refs #147).
    private static string Key(DetectedGame g) => LinuxGameLibraryStore.SessionKey(g);
}

/// <summary>One component's manifest warning in the install-warning dialog; <see cref="Heading"/> names the component when several are shown.</summary>
public sealed record InstallWarningSection(string? Heading, ManifestInstallWarning Warning);
