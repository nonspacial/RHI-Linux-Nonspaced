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
using RHI.Platform.Linux.Launch;
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

public partial class MainWindowViewModel : ObservableObject, IDisposable, ILibraryEditHost
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private static string PresentOr(string? value, string fallback)
    {
        var presented = UiLocalizationService.Present(value);
        return string.IsNullOrWhiteSpace(presented) ? fallback : presented;
    }

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
    private LinuxAppSettings _settings;
    private LinuxGameLibraryState _libraryState;
    private readonly SelectedGameSession _session;
    private bool _libraryHasDlssBatchCandidates;
    private readonly HashSet<string> _selectedShaderPackIds = new(StringComparer.OrdinalIgnoreCase);
    // GitHub token notice (Refs #133); the settle check runs after queued UI work.
    private readonly OneShotStatusNotice _statusNotice = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));

    private readonly LibraryViewModel _library = new();

    /// <summary>The DLSS / Streamline section (unit 4 of the split).</summary>
    public DlssPanelViewModel Dlss { get; }

    /// <summary>The Extras section and the NR version pins (unit 5 of the split).</summary>
    public ExtrasViewModel Extras { get; }

    /// <summary>API override, OptiScaler variant, Luma mode and HDR launch profile rows (unit 6 of the split).</summary>
    public RuntimePanelViewModel Runtime { get; }

    /// <summary>Update All and the background update pass (unit 7 of the split).</summary>
    public UpdatesViewModel Updates { get; }

    /// <summary>Add game, Edit game and Remove manual entry (unit 7 of the split).</summary>
    public ManualGamesViewModel ManualGames { get; }

    /// <summary>The Settings dialog's snapshot, Save / Reset and Clear cache (unit 7 of the split).</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Saved or reset settings take effect: language, view mode, ReShade default, DLSS defaults, background loop.</summary>
    internal void AdoptSettings(LinuxAppSettings settings, bool restartLoop, bool runImmediate)
    {
        _settings = settings;
        UiLocalizationService.Apply(_settings.UiLanguage);
        ViewMode = _settings.ViewMode;
        ReShadeManageIni = _settings.ReShadeManageIni;
        RefreshLocalizedPresentation();
        NotifyUxSettings();
        if (restartLoop) Updates.RestartBackgroundUpdateLoop(runImmediate);
        NotifyDlssDefaults();
    }

    /// <summary>The library cards shown (unit 3 of the split; filtered in place, Refs #75, #85).</summary>
    public ObservableCollection<GameRowViewModel> Games => _library.Games;
    /// <summary>w3/parity features (Settings → Accounts &amp; updates, shortcuts, hand-off); set by App. Integrator hook.</summary>
    public ParityFeatures? Parity { get; set; }
    internal IReadOnlyList<DetectedGame> AllLibraryGames => _library.AllGames; // w3/parity hook (shortcut launch by key)
    public ObservableCollection<ComponentRowViewModel> Components { get; } = [];
    // Labels and values of every option list come from OptionLists (one mapping, shared with the dialogs; Refs #87).
    public ObservableCollection<string> ReShadeChannels { get; } = [.. OptionLists.ReShadeChannel.Items];
    public ObservableCollection<string> Dlss5FeederConsumerOptions { get; } = ["RenoDX DLSS5", "Deep Fried Chicken"];
    public string ProductVersionText => $"RHI-Linux v{RhiProductInfo.Version} · {UiLocalizationService.Text("Ui.ProductRuntime")}";

    [ObservableProperty] private DetectedGame? selectedGame;
    /// <summary>The library list's selected card; kept in step with <see cref="SelectedGame"/>.</summary>
    [ObservableProperty] private GameRowViewModel? selectedGameRow;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string libraryFilterMode = "All Games";
    [ObservableProperty] private string viewMode = "Detail";
    [ObservableProperty] private string statusText = UiLocalizationService.Text("Ui.Starting");
    [ObservableProperty] private bool isSelectionProbeBusy;

    private readonly OperationCoordinator _operations;

    /// <summary>
    /// The busy gate and its Cancel command (Refs #76, #77): every operation that changes game files or the library
    /// holds it, so <see cref="IsBusy"/> stays true until the last owner lets go.
    /// </summary>
    public OperationCoordinator Operations => _operations;
    public ReFrameworkStoragePresenter ReFrameworkStorage => new(this, _managedComponents); // w3/journal hook (#159)

    /// <summary>True while an operation holds <see cref="Operations"/>; drives the busy overlay.</summary>
    public bool IsBusy => _operations.IsBusy;

    /// <summary>The selected game's analysis (unit 1 of the split; Refs #71, #72, #74, #82, #83).</summary>
    public SelectedGameSession Session => _session;
    public DllNamePickerViewModel DllNamePicker => DllNamePickerViewModel.For(this, _managedComponents, TryPersist); // w3/dllnames (#120)
    // w5 lane rows: shell hooks (one member each; logic lives in your presenter)
    // w5/update-policy rows
    /// <summary>#177 Follow newest DLSS / Streamline; set by the app composition (and the test fixture). Integrator hook.</summary>
    public UpdatePolicyFeatures? UpdatePolicy { get; set; }
    // w5/catalogue-info rows
    // w5/ini-presets rows
    // w5/shell-launch rows
    // w5/reshade-core rows

    /// <summary>The selected game's published analysis, or null while it is being worked out.</summary>
    private GameAnalysis? CurrentAnalysis => SelectedGame is { } game && ReferenceEquals(_session.Game, game) ? _session.Analysis : null;

    /// <summary>The selected game's disk facts, or null while the analysis runs; getters read only these.</summary>
    private SelectionFacts? Facts => CurrentAnalysis?.Facts;
    [ObservableProperty] private string selectedReShadeChannel = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string reShadeLegacyVersion = "6.4.1";
    [ObservableProperty] private string reShadeCustomDllPath = "";
    [ObservableProperty] private string reShadeD3DCompiler47Path = "";
    [ObservableProperty] private string dlss5FeederConsumerPackagePath = "";
    [ObservableProperty] private string selectedDlss5FeederConsumer = "RenoDX DLSS5";
    public bool IsDeepFriedChickenFeederConsumer => SelectedDlss5FeederConsumer == "Deep Fried Chicken";
    public bool IsRenoDxDlss5FeederConsumer => !IsDeepFriedChickenFeederConsumer;
    [ObservableProperty] private bool reShadeManageIni = true;
    [ObservableProperty] private bool shaderCrosireMaster;
    [ObservableProperty] private bool shaderCrosireLegacy;
    [ObservableProperty] private bool shaderClshortfuse;
    [ObservableProperty] private bool shaderMaxG2D;
    [ObservableProperty] private string reShadeUpdateText = UiLocalizationService.Text("Ui.ReShadeApplyHint");

    private static string LibraryFilterDisplay(string value) => OptionLists.LibraryFilter.Display(value);

    public string GameCountText => $"{Games.Count} / {_library.AllGames.Count} {UiLocalizationService.Text("Ui.Games")} · {LibraryFilterDisplay(LibraryFilterMode)}";
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
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manual", ExecutablePath: not null } when Facts is { ExecutableExists: false }
            => T("Vm.ModificationTargetManualExecutableMissing"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manifest", ExecutablePath: not null } when Facts is { ExecutableExists: false }
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

    public bool HasSelectedGame => SelectedGame is not null;
    public bool CanLaunch => SelectedGame is not null && _launcher.CanLaunch(SelectedGame);
    public string? LaunchToolTip => SelectedGame switch
    {
        { ParentLauncherName: { Length: > 0 } parent } => TF("Vm.OpensStartGameFromLauncher", XboxCardText.AppName(parent)!),
        // The reason arms use CanLaunch's own test, so a disabled Play always says why (a blank command included).
        { } game when !_launcher.CanLaunch(game) && Facts is { OrphanedProtonPrefix: true }
            => TF("Vm.UnavailablePrefixWasBuiltByProton",
                  game.CompatibilityTool ?? "?"),
        // Play stays visible but disabled on a card without a launch command (the reason shows after ~2 s, like the
        // orphaned-Proton case): it becomes applicable once Edit game gives the card a command (w2-launch re-review 2).
        { } game when !_launcher.CanLaunch(game) && D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game)
            => T("Vm.LaunchRuntimeUnknownSetCommandInEdit"),
        { } game when !_launcher.CanLaunch(game) && XboxCardText.PlayUnavailable(game) is { } xbox => xbox, // w3/scan hook (Refs #90)
        { } game when !_launcher.CanLaunch(game) => T("Vm.NoLaunchCommandSetOneInEdit"),
        _ => null
    };

    private static string OrphanedProtonText(DetectedGame game)
        => TF("Vm.WinetricksUnavailablePrefixWasBuiltBy",
              game.CompatibilityTool ?? game.Runner ?? "?");
    public bool CanEditSelectedGame => SelectedGame is not null && !IsBusy;
    public bool CanInstallD3DCompilerWithProtontricks => SelectedGame?.UsesProton == true && HasConfirmedPatchTarget;
    public bool CanInstallD3DCompilerWithWinetricks
        => HasConfirmedPatchTarget && SelectedGame is not null && Facts is { CanInstallWithWinetricks: true };
    public string D3DCompilerWinetricksButtonText
        => SelectedGame is { } game && D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
            ? TF("Vm.InstallViaWinetricks", game.CompatibilityTool ?? game.Runner ?? game.Source)
            : T("Vm.InstallViaWinetricks2");
    public string D3DCompilerToolHint
        => SelectedGame switch
        {
            null => T("Vm.SelectGameDetermineCorrectD3DCompilerHelper"),
            { UsesProton: true } => T("Vm.SteamProtonTargetProtontricksUsedSteam"),
            { } game when Facts is { OrphanedProtonPrefix: true } => OrphanedProtonText(game),
            { } game when D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game) => T("Vm.WinetricksUnavailableRuntimeUnknown"),
            { SteamAppId: null } game when D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
                => TF("Vm.TargetWinetricksUsesActiveMatchingPrefix", game.Source, game.CompatibilityTool ?? game.Runner ?? T("Vm.LauncherRunner")),
            { SteamAppId: null, PrefixPath: not null } => T("Vm.StandaloneSystemWineTargetWinetricksRuns"),
            _ => T("Vm.NoInitializedWineProtonPrefixWas")
        };
    private static string ReShadeChannelDisplay(string value) => OptionLists.ReShadeChannel.Display(value);

    private static string ReShadeChannelValue(string? display) => OptionLists.ReShadeChannel.Value(display);

    public bool IsReShadeLegacy => ReShadeChannelValue(SelectedReShadeChannel).Equals("Legacy", StringComparison.OrdinalIgnoreCase);
    public bool IsReShadeCustom => ReShadeChannelValue(SelectedReShadeChannel).Equals("Custom", StringComparison.OrdinalIgnoreCase);

    public bool CanOpenDlssBatch => !IsBusy && _libraryHasDlssBatchCandidates;

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
        LinuxHdrLaunchProfileService? hdrProfiles = null,
        OperationCoordinator? operations = null,
        SelectedGameSession? session = null)
    {
        _operations = operations ?? new OperationCoordinator();
        _operations.PropertyChanged += OnOperationsChanged;
        _scanner = scanner;
        _launcher = launcher;
        _runtimeProbe = runtimeProbe;
        _runtimeProbe.LateMetadataAvailable += OnLateProbeMetadata;
        _dlss = dlss;
        _managedComponents = managedComponents;
        _apiOverrideStore = apiOverrideStore ?? new GraphicsApiOverrideStore();
        _optiScalerVariantStore = optiScalerVariantStore ?? new OptiScalerVariantPreferenceStore();
        _settingsStore = settingsStore ?? new LinuxSettingsStore();
        _updateAll = updateAllService ?? new LinuxUpdateAllService(new LinuxDeploymentRecordStore(), managedComponents);
        _dlssDefaults = dlssDefaultsService ?? new LinuxDlssDefaultsService(dlss, new LinuxDeploymentRecordStore());
        _libraryStore = libraryStore ?? new LinuxGameLibraryStore();
        _deploymentRecords = deploymentRecords ?? new LinuxDeploymentRecordStore();
        _hdrProfiles = hdrProfiles ?? new LinuxHdrLaunchProfileService();
        _session = session ?? new SelectedGameSession(runtimeProbe, managedComponents, _dlssDefaults, _hdrProfiles);
        Dlss = new DlssPanelViewModel(this, _dlss, _dlssDefaults);
        Extras = new ExtrasViewModel(this, _managedComponents, Dlss);
        Runtime = new RuntimePanelViewModel(this, _managedComponents, _apiOverrideStore, _optiScalerVariantStore, _hdrProfiles);
        Updates = new UpdatesViewModel(this, _updateAll);
        ManualGames = new ManualGamesViewModel(this);
        Settings = new SettingsViewModel(this, _settingsStore);
        _session.Changed += OnSessionChanged;
        _session.Completed += OnAnalysisCompleted;
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
        _operations.PropertyChanged -= OnOperationsChanged;
        _session.Changed -= OnSessionChanged;
        _session.Completed -= OnAnalysisCompleted;
        _runtimeProbe.LateMetadataAvailable -= OnLateProbeMetadata;
        _session.Cancel();
        Updates.StopBackgroundLoop();
        GC.SuppressFinalize(this);
    }

    /// <summary>Startup work (manifest, first scan); tests await it instead of polling busy flags.</summary>
    internal Task Initialization { get; }

    // The panels guard their own list rebuilds (Extras' version pins) or relabel in place (Runtime's option items).
    private void RefreshLocalizedPresentation() => RefreshLocalizedPresentationCore();

    private void RefreshLocalizedPresentationCore()
    {
        var channel = ReShadeChannelValue(SelectedReShadeChannel);

        OptionLists.ReShadeChannel.Fill(ReShadeChannels);

        SelectedReShadeChannel = ReShadeChannelDisplay(channel);
        Runtime.RefreshLocalized();
        Extras.RefreshLocalized();

        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
        if (SelectedGame is not null && CurrentAnalysis is { } cached)
        {
            Components.Clear();
            foreach (var item in cached.Components.Where(ComponentRowViewModel.IsShown)) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
        }

        NotifySelected();
        OnPropertyChanged(nameof(ProductVersionText));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(ViewToggleText));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        NotifyDlss();
    }

    public string ApplyGlobalReShadeSettingsToSelectedGame(LinuxAppSettings settings)
    {
        if (SelectedGame is null) return T("Vm.SelectGameFirst");
        if (IsBusy) return T("Vm.AnotherRHIOperationAlreadyRunning");
        if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return T("Vm.WindowsReShadeSettingsNotAppliedNative");
        if (!SelectedGame.HasConfirmedPatchTarget)
            return T("Vm.SetTargetGameExecutableEditGame");
        if (XboxWriteGate.Refusal(SelectedGame) is { } xbox) return UiLocalizationService.Present(xbox); // w3/scan hook (Refs #90)
        if (CurrentAnalysis is not { } probe ||
            !probe.Components.Any(component => component.Kind == ManagedComponentKind.ReShade &&
                component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
            return T("Vm.InstallDetectReShadeSelectedGameBefore");
        var path = ReShadeGlobalSettingsApplier.ApplyToGame(settings, SelectedGame);
        return TF("Vm.ReShadeCaptureHotkeyDefaultsAppliedAt", path);
    }

    private async Task InitializeAsync()
    {
        // The HDR host (NVIDIA driver, gamescope on PATH) is read once off the UI thread; each analysis refreshes it.
        var hdrHost = Task.Run(_hdrProfiles.ProbeHost);
        var neuralVersionCatalogTask = Extras.EnsureNeuralVersionCatalogAsync();
        // The library scan never waits behind the DLSS manifest: both start now (Refs #43).
        var dlssManifestTask = LoadDlssManifestAsync();
        Runtime.LastHdrHost ??= await hdrHost;
        Runtime.RaiseHdrPlanChanged();
        await RefreshAsync();
        await dlssManifestTask;
        await neuralVersionCatalogTask;
        Updates.RestartBackgroundUpdateLoop(runImmediate: _settings.AutoUpdateComponents);
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
            Dlss.ApplyManifest(manifest);
            Extras.ApplyNrDllVersions(manifest.Dlssnr.Select(x => x.Version));
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
    private void OnOperationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationCoordinator.IsBusy)) OnBusyChanged(_operations.IsBusy);
    }

    private void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        // An operation may have installed or removed managed components: re-work Update All / DLSS batch off the UI thread.
        if (!value)
        {
            _filterFacts.Clear();
            UiExceptionGuard.Forget(RefreshLibraryFactsAsync(), "RefreshLibraryFactsAsync");
        }
        _statusNotice.SetBusy(value);
        Updates.NotifyCanUpdateAll();
        ManualGames.NotifyRemove();
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSelectedGame));
        Runtime.NotifyBusy();
        Extras.NotifyUalAndShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    /// <summary>A w3/parity notice (a newer RHI-Linux release, a hand-off result), appended once like the token notice. Integrator hook.</summary>
    public void ReportParityNotice(string notice) => StatusText = _statusNotice.Raise(notice, StatusText);

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
        Runtime.NotifyProbeBusy();
        Extras.NotifyUalAndShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    /// <summary>How long the search waits after the last keystroke before it filters the list (Refs #75).</summary>
    internal static TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? _searchDebounce;

    /// <summary>Completes when the pending search has been applied (tests).</summary>
    internal Task SearchApplied { get; private set; } = Task.CompletedTask;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _searchDebounce = debounce;
        SearchApplied = ApplySearchAfterPauseAsync(debounce);
    }

    private async Task ApplySearchAfterPauseAsync(CancellationTokenSource debounce)
    {
        try { await Task.Delay(SearchDebounce, debounce.Token); }
        catch (OperationCanceledException) { return; } // another keystroke (or a filter change) came first
        if (!ReferenceEquals(_searchDebounce, debounce)) return;
        _searchDebounce = null;
        ApplyFilter();
    }

    private bool _syncingSelectedRow;

    partial void OnSelectedGameRowChanged(GameRowViewModel? value)
    {
        if (_syncingSelectedRow) return;
        SelectedGame = value?.Game;
    }

    private void SyncSelectedRow(DetectedGame? game)
    {
        _syncingSelectedRow = true;
        try { SelectedGameRow = _library.RowFor(game); }
        finally { _syncingSelectedRow = false; }
    }

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

    partial void OnSelectedReShadeChannelChanged(string value)
    {
        OnPropertyChanged(nameof(IsReShadeLegacy));
        OnPropertyChanged(nameof(IsReShadeCustom));
    }

    /// <summary>
    /// Raised when the selection moves to a different game (not when a refresh puts the same game back): the detail
    /// pane scrolls to the top so the new game's title and actions are never left half under the toolbar (Refs #152).
    /// </summary>
    public event Action? SelectedGameSwitched;

    private string? _lastSelectedKey;

    /// <summary>True while a refresh clears and restores the selection: the game it settles on decides.</summary>
    private bool _holdSelectionSwitch;

    private void RaiseSelectionSwitchIfNew(DetectedGame? value)
    {
        if (_holdSelectionSwitch || value is null || string.Equals(Key(value), _lastSelectedKey, StringComparison.Ordinal)) return;
        _lastSelectedKey = Key(value);
        SelectedGameSwitched?.Invoke();
    }

    partial void OnSelectedGameChanged(DetectedGame? value)
    {
        SyncSelectedRow(value);
        RaiseSelectionSwitchIfNew(value);
        _session.Cancel();
        Components.Clear();
        _shownAnalysis = null;
        Dlss.Detection = new();
        IsSelectionProbeBusy = false;
        CheckRepairAfterMove(force: false);

        if (value is null)
        {
            _session.Select(null);
            Runtime.OnSelectionChanged(null);
            ResetReShadeProfile();
            Extras.OnSelectionChanged(null);
            NotifySelected();
            return;
        }

        Runtime.OnSelectionChanged(value);
        LoadReShadeProfile(value);
        Extras.OnSelectionChanged(value);

        // A cached analysis is published at once (and reported through OnAnalysisCompleted); otherwise the session
        // starts one in the background and reports progress through OnSessionChanged.
        _session.Select(value);
        if (_session.IsProbeBusy) StatusText = TF("Vm.AnalyzingBackground", value.Name);
    }

    private GameAnalysis? _shownAnalysis;

    /// <summary>The session published an analysis or started/ended one: the panels follow it.</summary>
    private void OnSessionChanged()
    {
        var game = SelectedGame;
        var analysis = CurrentAnalysis;
        var published = !ReferenceEquals(analysis, _shownAnalysis);
        _shownAnalysis = analysis;
        if (published)
        {
            Components.Clear();
            if (analysis is not null)
                foreach (var item in analysis.Components.Where(ComponentRowViewModel.IsShown)) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
        }
        Dlss.Detection = analysis?.Dlss ?? new();
        if (analysis is not null && published)
        {
            Runtime.LastHdrHost = analysis.Facts.HdrHost;
            if (game is not null)
            {
                Extras.OnAnalysisPublished(game);
            }
        }
        IsSelectionProbeBusy = game is not null && ReferenceEquals(_session.Game, game) && _session.IsProbeBusy;
        NotifySelected();
    }

    /// <summary>An analysis of the selected game ended: say so in the status line.</summary>
    private void OnAnalysisCompleted(AnalysisOutcome outcome)
    {
        var game = outcome.Game;
        StatusText = outcome.Kind switch
        {
            AnalysisOutcomeKind.Cached => TF("Vm.ReadyCached", game.Name),
            AnalysisOutcomeKind.NoPatchTarget => game.GameRootNotAllowed
                ? GameRootNotAllowedText(game)
                : game.FolderMissing
                    ? FolderMissingText(game)
                    : TF("Vm.LaunchConfigurationWasPreservedButModification", game.Name),
            AnalysisOutcomeKind.Failed => TF("Vm.FailedAnalyze", game.Name, UiLocalizationService.Present(outcome.Error?.Message)),
            _ => outcome.Truncated
                ? TF("Vm.IndexingStoppedAtFilesSoSome", game.Name, outcome.FileCount)
                : TF("Vm.ReadyFilesIndexedMs", game.Name, outcome.FileCount, outcome.ElapsedMilliseconds)
        };
    }

    /// <summary>
    /// A slow PCGamingWiki answer arrived after the selection finished without it (Refs #54): drop the
    /// stale probe result and, when that game is still selected and idle, analyse it again (the answer
    /// is cached by then, so this is quick). The probe ran on a working copy, so the game is matched by its key.
    /// </summary>
    private void OnLateProbeMetadata(DetectedGame game) => Dispatcher.UIThread.Post(() =>
    {
        _session.Invalidate(game);
        if (SelectedGame is { } selected && Key(selected) == Key(game) && !IsBusy && !IsSelectionProbeBusy)
            OnSelectedGameChanged(selected);
    });

    /// <summary>
    /// Claims the busy gate for an operation the user started; null, with the status line saying another operation
    /// is running, when the gate is held. Dispose the scope (a <c>using</c>) to release it.
    /// </summary>
    private OperationScope? BeginOperation(string kind, bool cancellable = true, CancellationToken linkedToken = default)
    {
        var op = _operations.TryBegin(kind, cancellable, linkedToken);
        if (op is null) StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
        return op;
    }

    /// <summary>A gated dialog write refused while another operation runs: the status line and the result say so.</summary>
    private ComponentOperationResult? RefuseWhileBusy()
        => IsBusy ? ComponentOperationResult.Fail(ShowStatus(T("Vm.AnotherRHIOperationAlreadyRunning"))) : null;

    internal static string OperationCancelledText() => T("Vm.OperationCancelled");

    /// <summary>
    /// An operation's result line: services turn a cancelled download into a failed result, which reads as
    /// "Operation cancelled." when the user pressed Cancel; any other result is shown as it is.
    /// </summary>
    private static string OutcomeText(OperationScope op, bool success, string? message)
        => !success && op.CancelRequested ? OperationCancelledText() : UiLocalizationService.Present(message);

    internal static readonly IReadOnlySet<string> ManualOnly = new HashSet<string>(StringComparer.Ordinal) { "Manual" };
    internal static readonly IReadOnlySet<string> NoDetectors = new HashSet<string>(StringComparer.Ordinal);

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (!IsBusy) return RefreshAsync(null);
        // Clicked while another operation (Update All, the background pass, an install) holds the gate: the refresh is
        // queued, not dropped, and the status line says so; further clicks join the queued one (Refs #68).
        StatusText = T("Vm.RefreshQueued");
        return _queuedRefresh ??= RunQueuedRefreshAsync();
    }

    private Task? _queuedRefresh;

    private async Task RunQueuedRefreshAsync()
    {
        try
        {
            // Checked again after every wake-up: another operation may take the gate before this continuation runs.
            while (IsBusy) await _operations.WhenIdleAsync();
            await RefreshAsync(null);
        }
        finally { _queuedRefresh = null; }
    }

    /// <summary>
    /// Refresh that waits for the current busy operation (Update All, an install…) instead of
    /// being skipped, for changes the user just saved. True when the refresh ran and succeeded.
    /// </summary>
    private async Task<bool> RefreshWhenIdleAsync(IReadOnlySet<string>? onlyDetectors)
    {
        if (IsBusy)
            StatusText = T("Vm.WaitingCurrentOperationFinishBeforeUpdating");
        await _operations.WhenIdleAsync();
        return await RefreshAsync(onlyDetectors);
    }

    /// <summary>
    /// Full library refresh when <paramref name="onlyDetectors"/> is null; otherwise only the named
    /// detectors are re-run and the rest of the library is reused from the previous scan. False when it
    /// did not run (busy) or failed.
    /// </summary>
    private async Task<bool> RefreshAsync(IReadOnlySet<string>? onlyDetectors)
    {
        using var op = _operations.TryBegin("refresh");
        if (op is null) return false;
        StatusText = onlyDetectors is null
            ? T("Vm.ScanningSteamHeroicLutrisWinePrefixes")
            : T("Vm.UpdatingLibrary");
        var currentKey = SelectedGame is null ? null : Key(SelectedGame);
        _holdSelectionSwitch = true;
        try
        {
            _session.Cancel();
            if (onlyDetectors is null) _session.InvalidateAll();
            // Full Refresh bypasses the session NR version and latest-version caches (upstream 2.7.9).
            var neuralCatalogRefresh = onlyDetectors is null ? Extras.ReportNeuralCatalogLoadAsync(Extras.Versions.FullRefreshAsync()) : null;
            SelectedGame = null;

            var scan = await _scanner.ScanWithReportAsync(onlyDetectors, op.Token);
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
                : _library.AllGames.Where(g => _session.TryGetCached(g, out _)).ToDictionary(Key, StringComparer.Ordinal);
            var merged = new List<DetectedGame>(found.Count);
            var adoptedMergedKeys = false;
            foreach (var game in found)
            {
                game.LibraryIdentityPath ??= game.InstallPath;
                adoptedMergedKeys |= _libraryState.AdoptOlderKeys(game); // older-build and merged-duplicate keys (Refs #55, #147)
                var libraryKey = LinuxGameLibraryStore.GameKey(game);
                if (previous is not null && previous.TryGetValue(Key(game), out var kept))
                {
                    _library.SetLibraryFlags(kept, _libraryState.FavouriteGames.Contains(libraryKey), _libraryState.HiddenGames.Contains(libraryKey));
                    merged.Add(kept);
                    continue;
                }
                _session.Invalidate(game);
                _libraryState.GameEdits.TryGetValue(libraryKey, out var edit);
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
                game.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
                game.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
                merged.Add(game);
            }
            var adoptRefused = adoptedMergedKeys ? SaveAdoptedLibraryState() : null;
            _filterFacts.Clear();
            _library.SetGames(merged);
            ApplyFilter();
            SelectedGame = currentKey is null
                ? Games.FirstOrDefault()?.Game
                : (Games.FirstOrDefault(row => Key(row.Game) == currentKey) ?? Games.FirstOrDefault())?.Game;
            CheckRepairAfterMove(force: true); // the scan may have found the game in a new folder
            if (neuralCatalogRefresh is not null) await neuralCatalogRefresh;
            // Scan problems ride on the following lines (the selected game's "Ready — …") as a notice.
            var problems = string.Join(" ", new[] { LibraryScanStatusText.Problems(scan, AppLog.Current?.FilePath), adoptRefused }
                .Where(text => !string.IsNullOrEmpty(text)));
            string? scanProblems = problems.Length == 0 ? null : problems;
            var scanDone = LibraryScanStatusText.Done(_library.AllGames.Count);
            StatusText = scanProblems is null ? scanDone : _statusNotice.Raise(scanProblems, scanDone);
            await RefreshLibraryFactsAsync(); // Update All / DLSS batch availability for the new library, off the UI thread
            if (onlyDetectors is null) _ = Updates.RequestFollowNewest(); // w5/update-policy hook (#177): start-up and Full Refresh
            return true;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            // The library list is unchanged; put the selection back.
            StatusText = OperationCancelledText();
            SelectedGame = currentKey is null
                ? Games.FirstOrDefault()?.Game
                : (Games.FirstOrDefault(row => Key(row.Game) == currentKey) ?? Games.FirstOrDefault())?.Game;
            return false;
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.ScanFailed", UiLocalizationService.Present(ex.Message));
            return false;
        }
        finally
        {
            _holdSelectionSwitch = false;
            RaiseSelectionSwitchIfNew(SelectedGame);
            Updates.NotifyCanUpdateAll();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedGame is null) return;
        var plan = Runtime.SelectedHdrPlan;
        if (HdrLaunchText.LaunchRefusal(plan, _launcher is IGameLaunchOptionsLauncher) is { } refusal)
        {
            StatusText = refusal;
            return;
        }

        if (plan.AppliesAtLaunch && _launcher is IGameLaunchOptionsLauncher profiledLauncher)
        {
            var hdrGame = SelectedGame;
            StatusText = TF("Vm.LaunchingHDRProfile", hdrGame.Name, Runtime.SelectedHdrMode.Display);
            if (await TryLaunchAsync(hdrGame, () => profiledLauncher.LaunchAsync(hdrGame, new GameLaunchOptions(plan.Environment, plan.CommandPrefix))))
                StatusText = TF("Vm.LaunchingHDRProfile", hdrGame.Name, Runtime.SelectedHdrMode.Display);
            return;
        }

        var game = SelectedGame;
        var started = game.ParentLauncherName is { Length: > 0 } parent
            ? TF("Vm.OpeningStartFromLauncher", XboxCardText.AppName(parent)!, game.Name)
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
        if (RefuseWhileBusy() is { } busy) return busy;
        var result = _managedComponents.SetRenoDxCompatibilitySetting(SelectedGame, key, value);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    /// <summary>OptiScaler's DLL-name dropdown for the selected game (#172); null without a game folder to install into.</summary>
    public OptiScalerDllNameState? OptiScalerDllName()
        => SelectedGame is { HasConfirmedPatchTarget: true } game ? OptiScalerDllNamePresenter.For(_managedComponents, game, IsBusy) : null;

    /// <summary>Saves OptiScaler's DLL name for the selected game (#172; null = Automatic). False when not saved (status says why).</summary>
    public bool SaveOptiScalerDllName(string? name)
    {
        if (SelectedGame is not { } game) return false;
        // The installed name stays until removal (the dropdown is locked then; this holds for any other caller too).
        if (_managedComponents.GetInstalledOptiScalerVariant(game) is not null)
        {
            ShowStatus(TF("Names.OptiScalerDllNameTipInstalled", _managedComponents.OptiScalerProxyNameFor(game)));
            return false;
        }
        return TryPersist(() => _managedComponents.ReShadeDllNames.SetOptiScaler(game, name), "Ui.WhatGamePreferences", false, true) is null;
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
        if (RefuseWhileBusy() is { } busy) return busy;
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
        using var op = BeginOperation("optiscaler-settings", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await _managedComponents
            .SetOptiScalerAdvancedSettingAsync(game, id, value, op.Token)
            .ConfigureAwait(true);
        if (result.Success)
            ReevaluateCurrentGameComponents(game);
        StatusText = OutcomeText(op, result.Success, result.Message);
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
        if (RefuseWhileBusy() is { } busy) return busy;
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
        => (await RunComponentUpdateAsync((game, token) => _managedComponents.UpdateDlssg2030Async(game, token), cancellationToken)).Result;

    public StandaloneDlssEnablerPreferences GetStandaloneDlssEnablerPreferences()
        => SelectedGame is null ? new StandaloneDlssEnablerPreferences() : _managedComponents.GetStandaloneDlssEnablerPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetStandaloneDlssEnablerRecord()
        => SelectedGame is null ? null : _managedComponents.GetStandaloneDlssEnablerRecord(SelectedGame);

    public ComponentOperationResult SetStandaloneDlssEnablerPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        if (RefuseWhileBusy() is { } busy) return busy;
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
        => (await RunComponentUpdateAsync((game, token) => _managedComponents.UpdateStandaloneDlssEnablerAsync(game, token), cancellationToken)).Result;

    public Rtx40MfgPreferences GetRtx40MfgPreferences()
        => SelectedGame is null ? new Rtx40MfgPreferences() : _managedComponents.GetRtx40MfgPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetRtx40MfgRecord()
        => SelectedGame is null ? null : _managedComponents.GetRtx40MfgRecord(SelectedGame);

    public ComponentOperationResult SetRtx40MfgPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        if (RefuseWhileBusy() is { } busy) return busy;
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetRtx40MfgPreferences(game, proxyName));
    }

    public ComponentOperationResult SetRtx40MfgPreferences(string gpuGeneration, string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        if (RefuseWhileBusy() is { } busy) return busy;
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
        => (await RunComponentUpdateAsync((game, token) => _managedComponents.UpdateRtx40MfgAsync(game, token), cancellationToken)).Result;

    // w4/encore (#158): RTX Encore's ⚙ dialog.
    public LinuxDeploymentRecord? GetRtxEncoreRecord()
        => SelectedGame is null ? null : _managedComponents.GetRtxEncoreRecord(SelectedGame);

    public IReadOnlyList<string> RtxEncoreOfferedNames()
        => SelectedGame is { } game && _managedComponents.RtxEncore is { } encore ? encore.OfferedNames(game) : [];

    public string RtxEncoreChosenName()
        => SelectedGame is { } game && _managedComponents.RtxEncore is { } encore ? encore.ChosenName(game) : RtxEncoreDeploymentService.DefaultName;

    public bool RtxEncoreNeuralRenderingChosen()
        => SelectedGame is { } game && _managedComponents.RtxEncore is { } encore && encore.NeuralRenderingChosen(game);

    public ComponentOperationResult SetRtxEncoreName(string name)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(T("Vm.SelectGameFirst"));
        if (RefuseWhileBusy() is { } busy) return busy;
        var game = SelectedGame;
        return PersistPreference(() => _managedComponents.SetRtxEncoreName(game, name));
    }

    /// <summary>The Neural Rendering choice: saved before install; once installed the file is put in or taken out under the busy gate.</summary>
    internal async Task<(ComponentOperationResult Result, string Shown)> SetRtxEncoreNeuralRenderingAsync(bool enabled)
    {
        if (SelectedGame is not { } game || _managedComponents.RtxEncore is not { } encore)
            return (ComponentOperationResult.Fail(T("Vm.SelectGameFirst")), T("Vm.SelectGameFirst"));
        if (encore.GetRecord(game) is null)
        {
            if (RefuseWhileBusy() is { } busy) return (busy, UiLocalizationService.Present(busy.Message));
            var saved = PersistPreference(() => encore.SetNeuralRenderingPreference(game, enabled));
            return (saved, UiLocalizationService.Present(saved.Message));
        }
        return await RunComponentUpdateAsync((g, token) => _managedComponents.SetRtxEncoreNeuralRenderingAsync(g, enabled, token));
    }

    public async Task<RtxEncoreUpdateStatus> CheckRtxEncoreUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, T("Vm.SelectGameFirst"));
        var status = await _managedComponents.CheckRtxEncoreUpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    /// <summary>
    /// A component dialog's Update (FG 20/30 unlock, DLSS Enabler, Universal MFG) under the busy gate: the result and the
    /// line shown for it — "Operation cancelled." when the dialog's Cancel stopped it, otherwise the presented message.
    /// </summary>
    internal async Task<(ComponentOperationResult Result, string Shown)> RunComponentUpdateAsync(
        Func<DetectedGame, CancellationToken, Task<ComponentOperationResult>> update, CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return (ComponentOperationResult.Fail(T("Vm.SelectGameFirst")), T("Vm.SelectGameFirst"));
        var game = SelectedGame;
        using var op = BeginOperation("component-update", linkedToken: cancellationToken);
        if (op is null) return (ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning")), T("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await update(game, op.Token).ConfigureAwait(true);
        var shown = OutcomeText(op, result.Success, result.Message);
        StatusText = shown;
        if (result.Success) await ReprobeGameAsync(game);
        return (result, shown);
    }

    /// <summary>The update a component dialog of <paramref name="kind"/> runs; the shown line (see <see cref="RunComponentUpdateAsync"/>).</summary>
    internal async Task<string> UpdateProxyComponentAsync(ProxyComponentDialogKind kind)
        => (await RunComponentUpdateAsync(kind switch
        {
            ProxyComponentDialogKind.Dlssg2030 => (game, token) => _managedComponents.UpdateDlssg2030Async(game, token),
            ProxyComponentDialogKind.DlssEnabler => (game, token) => _managedComponents.UpdateStandaloneDlssEnablerAsync(game, token),
            ProxyComponentDialogKind.RtxEncore => (game, token) => _managedComponents.UpdateManagedAsync(game, ManagedComponentKind.RtxEncore, [], token),
            _ => (game, token) => _managedComponents.UpdateRtx40MfgAsync(game, token)
        })).Shown;

    /// <summary>Set by the window: shows a confirmation (title, message, accept text) and returns true to proceed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>w3/dllnames (#136): the Control UE RenoDX confirmation with its FG/HDR choices; null = plain confirmation, FG No.</summary>
    public Func<ControlUeInstallDialogModel, Task<bool>>? ConfirmControlUeAsync { get; set; }

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

    private IReadOnlyList<string>? ProbedFiles(DetectedGame game) => _session.CachedFiles(game);

    [RelayCommand]
    private async Task ManageComponentAsync(ManagedComponentKind kind)
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (CurrentAnalysis is not { } probe)
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

        using var op = BeginOperation("component");
        if (op is null) return;
        try
        {
            var removing = component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
            if (!removing && kind == ManagedComponentKind.Dlss5Feeder && IsDeepFriedChickenFeederConsumer && string.IsNullOrWhiteSpace(Dlss5FeederConsumerPackagePath))
            {
                StatusText = T("Vm.DLSS5FeederFullDeepFriedChicken");
                return;
            }

            if (!removing && !await AcceptManifestInstallWarningsAsync(game, kind, component.DisplayName, installsTarget: true, probe.Files)) return;

            // Upstream 2.7.9: Control Ultimate Edition's RenoDX mod is an RR fix with extra steps; confirm first. Since 2.8.1
            // the confirmation also offers OptiScaler FG and "Using HDR?" (w3/dllnames #136; a plain confirmation = FG No).
            ControlUeInstallDialogModel? controlUe = null;
            if (!removing && kind == ManagedComponentKind.RenoDX && ControlUePostInstallService.IsControlUltimateEdition(game))
            {
                controlUe = new ControlUeInstallDialogModel();
                var accepted = ConfirmControlUeAsync is { } ask ? await ask(controlUe)
                    : ConfirmAsync is not { } confirm || await confirm(controlUe.Title, controlUe.Message, controlUe.AcceptText);
                if (!accepted)
                {
                    StatusText = T("Vm.RenoDXInstallationCancelled");
                    return;
                }
            }

            StatusText = removing
                ? TF("Vm.RemovingRHIManaged", component.DisplayName)
                : TF("Vm.Installing", component.DisplayName);
            var result = removing
                ? await _managedComponents.RemoveAsync(game, kind, op.Token)
                : kind == ManagedComponentKind.Dlss5Feeder
                    ? await _managedComponents.InstallDlss5FeederAsync(
                        game,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumer.DeepFriedChicken : Dlss5FeederConsumer.RenoDxDlss5,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumerPackagePath : null,
                        ReShadeD3DCompiler47Path,
                        op.Token)
                    : kind == ManagedComponentKind.OptiScaler
                        ? await _managedComponents.InstallOptiScalerAsync(game, Runtime.SelectedOptiScalerVariant.Value, op.Token)
                        : await _managedComponents.InstallAsync(game, kind, probe.Files, op.Token);
            // #136: the FG step runs only after RenoDX installed; its own failure never turns the RenoDX result into one.
            var message = result.Message;
            if (result.Success && controlUe is { InstallOptiScalerFg: true })
            {
                var frameGeneration = await _managedComponents.InstallControlUeFrameGenerationAsync(game, controlUe.UseHdr, op.Token);
                message += " · " + frameGeneration.Message;
            }
            StatusText = OutcomeText(op, result.Success, message);
            if (result.Success)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.Symbol", component.DisplayName, UiLocalizationService.Present(ex.Message));
        }
    }

    /// <summary>Drops <paramref name="game"/>'s analysis and works it out again (its files changed).</summary>
    private async Task ReprobeGameAsync(DetectedGame game)
    {
        await _session.ReprobeAsync(game);
        LoadReShadeProfile(game);
    }

    /// <summary>
    /// After an operation that changed files in the selected game but reports its own result line (UAL, DLSS swap or
    /// restore, ShortFuse load order): the cached file index is dropped and rebuilt so the next install sees the new
    /// files and backups (Refs #83), and the operation's line stays on screen.
    /// </summary>
    internal Task ReprobeSelectedAsync(DetectedGame game) => ReprobeKeepingStatusAsync(game); // w3/parity hook (#96)

    private async Task ReprobeKeepingStatusAsync(DetectedGame game)
    {
        var line = StatusText;
        await ReprobeGameAsync(game);
        StatusText = line;
    }

    /// <summary>
    /// The one path for operations that may have changed files in any game (Update All, the background pass, the DLSS
    /// batch): every cached analysis is dropped and the selected game is always analysed again (Refs #72).
    /// </summary>
    private async Task InvalidateAllAndReprobeSelectedAsync()
    {
        _session.InvalidateAll();
        if (SelectedGame is { } selected) await ReprobeGameAsync(selected);
    }

    /// <summary>
    /// Re-works the library-wide facts behind Update All and DLSS batch (deployment records, game folders) off the UI
    /// thread; run after scans and after every operation (Refs #71).
    /// </summary>
    private async Task RefreshLibraryFactsAsync()
    {
        var games = _library.AllGames.ToArray();
        bool managed, batch;
        try
        {
            (managed, batch) = await Task.Run(() => (
                _updateAll.HasManagedTargets(games),
                games.Any(game => game.HasConfirmedPatchTarget &&
                                  !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) &&
                                  !game.DlssScanSkippedByManifest && Directory.Exists(game.InstallPath))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An unreadable ledger is reported by every operation that needs it; the buttons keep their last state.
            AppLog.Warn("library-facts", "Could not work out Update All / DLSS batch availability", ex);
            return;
        }
        Updates.SetLibraryHasManagedTargets(managed);
        _libraryHasDlssBatchCandidates = batch;
        Updates.NotifyCanUpdateAll();
        OnPropertyChanged(nameof(CanOpenDlssBatch));
    }

    [RelayCommand]
    private async Task ApplyReShadeProfileAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (CurrentAnalysis is not { } probe)
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

        using var op = BeginOperation("reshade");
        if (op is null) return;
        try
        {
            // Applying a profile installs ReShade when it is not there yet (Available or Unknown), and RE Framework on an
            // RE Engine game when that is missing; those are the installs the manifest warns about.
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, component.DisplayName,
                    installsTarget: ManifestInstallWarning.IsFirstInstall(component.Availability), probe.Files)) return;
            StatusText = TF("Vm.ApplyingReShadeProfile", channel);
            var result = await _managedComponents.ApplyReShadeAsync(game, options, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                _selectedShaderPackIds.Clear();
                _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
                await ReprobeGameAsync(game);
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.ReShadeProfileFailed", UiLocalizationService.Present(ex.Message)));
        }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithProtontricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithProtontricks) return;
        var game = SelectedGame;
        using var op = BeginOperation("d3dcompiler");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47Protontricks", game.Name);
            var result = await _managedComponents.InstallD3DCompilerWithProtontricksAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerProtontricks", UiLocalizationService.Present(ex.Message)));
        }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithWinetricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithWinetricks) return;
        var game = SelectedGame;
        using var op = BeginOperation("d3dcompiler");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.InstallingNativeD3DCompiler47", game.Name, game.CompatibilityTool ?? game.Runner ?? "Winetricks");
            var result = await _managedComponents.InstallD3DCompilerWithWinetricksAsync(game, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(TF("Vm.FailedInstallD3DCompilerWinetricks", UiLocalizationService.Present(ex.Message)));
        }
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
        using var op = BeginOperation("reshade-check");
        if (op is null) return;
        try
        {
            var result = await _managedComponents.CheckReShadeUpdateAsync(SelectedGame!, op.Token);
            ReShadeUpdateText = op.CancelRequested ? OperationCancelledText() : UiLocalizationService.Present(result.Message);
            StatusText = ReShadeUpdateText;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = TF("Vm.UpdateCheckFailed", UiLocalizationService.Present(ex.Message));
            StatusText = ReShadeUpdateText;
        }
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

    /// <summary>The selected game has a note from the upstream manifest (View notes, Refs #161).</summary>
    public bool SelectedGameHasNotes => !string.IsNullOrWhiteSpace(SelectedGame?.ManifestGameNote);

    /// <summary>Set by the window: shows a game's notes (title, text, optional link).</summary>
    public Func<GameNotesViewModel, Task>? ShowNotesAsync { get; set; }

    [RelayCommand]
    private async Task ViewNotesAsync()
    {
        if (SelectedGame is not { } game || !SelectedGameHasNotes || ShowNotesAsync is not { } show) return;
        var url = game.ManifestGameNoteUrl;
        await show(new GameNotesViewModel(TF("Card.NotesTitle", game.Name), game.ManifestGameNote!.Trim(),
            string.IsNullOrWhiteSpace(url) ? null : url,
            string.IsNullOrWhiteSpace(game.ManifestGameNoteUrlLabel) ? T("Card.OpenNotesLink") : game.ManifestGameNoteUrlLabel!));
    }

    /// <summary>
    /// The right-click menu of a library card (Refs #161, upstream 2.8.6): opening it selects the card, then offers the
    /// actions the detail header already has for it — Open folder, Favourite, Hide/Show — and View notes when the
    /// game has a note. Nothing here is new behaviour; the entries run the same commands as the header buttons.
    /// Every entry is listed only where it applies to this card (the owner's rule: inapplicable options are hidden,
    /// never shown disabled): Open folder only once the game folder is confirmed, View notes only with a note.
    /// </summary>
    public IReadOnlyList<CardMenuItem> CardMenuFor(GameRowViewModel row)
    {
        if (!ReferenceEquals(SelectedGame, row.Game)) SelectedGame = row.Game;
        var game = row.Game;
        var items = new List<CardMenuItem>();
        if (game.HasConfirmedPatchTarget) items.Add(new("open-folder", T("Ui.OpenFolder"), OpenGameFolderCommand));
        // w3/scan (#90): an installer-app title with a breadcrumb "show" entry point opens its page in the installer app.
        if (XboxCardActions.ShowInOwningAppLabel(game) is { } showLabel)
            items.Add(new("show-in-owning-app", showLabel, new AsyncRelayCommand(() => ShowInOwningAppAsync(game))));
        items.Add(new("favourite", FavouriteActionText, ToggleFavouriteCommand));
        items.Add(new("hide", HideActionText, ToggleHiddenCommand));
        if (SelectedGameHasNotes) items.Add(new("notes", T("Card.ViewNotes"), ViewNotesCommand, SeparatorBefore: true));
        // w3/parity card menu slot: Create shortcut (#162; only from this explicit choice, owner decision) and the game's
        // Nexus Mods page (#99, #174; only for a game Nexus has a page for). Both are left out where they do not apply.
        var separated = false;
        if (Parity is { } parity)
        {
            items.Add(new("create-shortcut", T("Parity.ShortcutCreate"), CardMenuItem.OpensSubmenu, SeparatorBefore: true,
                Children:
                [
                    new("shortcut-desktop", T("Parity.ShortcutOnDesktopItem"), new AsyncRelayCommand(async () => StatusText = await parity.Shortcuts.CreateAsync(game, ShortcutPlace.Desktop))),
                    new("shortcut-menu", T("Parity.ShortcutInMenuItem"), new AsyncRelayCommand(async () => StatusText = await parity.Shortcuts.CreateAsync(game, ShortcutPlace.ApplicationsMenu)))
                ],
                Tip: T("Parity.ShortcutTip")));
            separated = true;
        }
        // #174: every game Nexus has a page for (no account needed to open it); a list still loading is joined, not awaited.
        Parity?.NexusLinks.Games?.Prefetch();
        if (NexusLinkPresenter.PageFor(game, Parity?.NexusLinks.Games) is { } nexusUrl)
            items.Add(new("nexus-page", T("Parity.NexusPage"), new RelayCommand(() => OpenNexusPage(nexusUrl)), SeparatorBefore: !separated, Tip: nexusUrl));
        // w5/update-policy rows
        // #178: the game's update inclusion; only where it would offer a box (an RHI component Update All updates, or Follow newest on).
        if (UpdatePolicy is { } updatePolicy
            && updatePolicy.InclusionFor(game, _deploymentRecords.FindAll(game).Select(record => record.Component), _settings) is { } inclusion)
            items.Add(new("update-inclusion", T("Upd.MenuItem"), new AsyncRelayCommand(async () =>
            {
                if (await updatePolicy.EditInclusionAsync(game, inclusion, save => TryPersist(save, "Upd.WhatInclusion", sessionOnly: false, reportStatus: false)) is not { } line) return;
                StatusText = line;
                _session.Recompose(game); // the rows' held notes follow the new choice
            }), Tip: T("Upd.MenuTip")));
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        return items;
    }

    private async Task ShowInOwningAppAsync(DetectedGame game)
    {
        if (_launcher is not LinuxGameLauncher launcher) return;
        if (await XboxCardActions.ShowInOwningAppAsync(game, launcher) is { } refused) StatusText = refused;
    }

    private void OpenNexusPage(string url)
    {
        if (!LinuxDesktopOpener.TryOpenUri(url, out var error))
            StatusText = TF("Parity.LinkNotOpened", UiLocalizationService.Present(error));
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
        var game = SelectedGame;
        using var op = BeginOperation("shaders-addons", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        try
        {
            // Apply installs ReShade when RHI does not manage it yet (plus RE Framework on RE Engine): warn first.
            var installsReShade = !_managedComponents.IsReShadeManaged(game) &&
                (_session.TryGetCached(game, out var probe)
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
            var shaderResult = await _managedComponents.ApplyReShadeAsync(game, options, op.Token);
            if (!shaderResult.Success)
            {
                StatusText = OutcomeText(op, shaderResult.Success, shaderResult.Message);
                return shaderResult;
            }

            StatusText = T("Vm.ApplyingReShadeAddSelection");
            var addonResult = await _managedComponents.ApplyReShadeAddonSelectionAsync(game, addonIds, op.Token);
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
        catch (Exception ex) when (op.WasCancelled(ex) && op.CancelRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(OperationCancelledText()));
        }
        catch (Exception ex) when (!op.Token.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(TF("Vm.ShadersAddOnsFailed", UiLocalizationService.Present(ex.Message))));
        }
    }

    private static void SetPack(HashSet<string> packs, string id, bool enabled)
    {
        if (enabled) packs.Add(id);
        else packs.Remove(id);
    }

    /// <summary>
    /// Shows the cards matching the filter and search, updating the list in place (Refs #75): a selected game that
    /// still matches stays selected without passing through null, so its analysis is not restarted.
    /// </summary>
    private void ApplyFilter()
    {
        _searchDebounce?.Cancel(); // a pending search is applied now
        var selected = SelectedGame;
        var mode = ParseLibraryFilterMode(LibraryFilterMode);
        var needsManagedFacts = mode is GameLibraryFilterMode.Installed or GameLibraryFilterMode.RenoDX or GameLibraryFilterMode.Luma;
        _library.ApplyFilter(mode, SearchText, needsManagedFacts ? CachedLibraryFilterFacts : null);
        OnPropertyChanged(nameof(GameCountText));
        if (selected is not null && _library.IsShown(selected))
        {
            if (!ReferenceEquals(SelectedGameRow, _library.RowFor(selected))) SyncSelectedRow(selected);
        }
        else SelectedGame = Games.FirstOrDefault()?.Game;
    }

    // Filter facts read deployment records and folders: worked out once per game and kept until the next scan or
    // operation (RefreshLibraryFactsAsync), not on every keystroke.
    private readonly Dictionary<DetectedGame, GameLibraryFilterFacts> _filterFacts = new(ReferenceEqualityComparer.Instance);

    private GameLibraryFilterFacts CachedLibraryFilterFacts(DetectedGame game)
    {
        if (!_filterFacts.TryGetValue(game, out var facts)) _filterFacts[game] = facts = GetLibraryFilterFacts(game);
        return facts;
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
        var game = SelectedGame;
        // The target is the opposite of what the card showed: a library file restored meanwhile is adopted below, and
        // flipping its value instead would undo a restored favourite the user never saw (Refs #149).
        var favourite = !game.IsFavourite;
        AdoptRestoredLibraryFile();
        var key = LinuxGameLibraryStore.GameKey(game);
        if (favourite) _libraryState.FavouriteGames.Add(key);
        else _libraryState.FavouriteGames.Remove(key);
        _library.SetLibraryFlags(game, favourite, game.IsHidden); // the card's badge follows in place (Refs #85)
        // Kept for this session even when the save is refused; the status line says it was not saved.
        TryPersist(() => _libraryStore.Save(_libraryState), "Ui.WhatGameLibrary");
        ApplyFilter();
        NotifySelected();
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        if (SelectedGame is null || LibraryWritesPaused()) return;
        var game = SelectedGame;
        var hidden = !game.IsHidden; // what the card showed, as for favourites (Refs #149)
        AdoptRestoredLibraryFile();
        var key = LinuxGameLibraryStore.GameKey(game);
        if (hidden) _libraryState.HiddenGames.Add(key);
        else _libraryState.HiddenGames.Remove(key);
        _library.SetLibraryFlags(game, game.IsFavourite, hidden);
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
    /// (<paramref name="reloadHdrControls"/> false). True when a restored file was adopted.
    /// </summary>
    private bool AdoptRestoredLibraryFile(bool reloadHdrControls = true)
    {
        if (!_libraryStateUnreadable) return false;
        var fresh = _libraryStore.Load(out var stillUnreadable);
        if (stillUnreadable) return false;
        AdoptLibraryState(fresh, keepFilter: true);
        if (reloadHdrControls && SelectedGame is { } selected) Runtime.LoadHdrProfile(selected);
        return true;
    }

    /// <summary>Makes <paramref name="state"/> the in-memory library state and syncs the cards' favourite/hidden flags.</summary>
    private void AdoptLibraryState(LinuxGameLibraryState state, bool keepFilter)
    {
        _libraryState = state;
        _libraryStateUnreadable = false;
        if (keepFilter) _libraryState.FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(LibraryFilterMode);
        foreach (var game in _library.AllGames)
        {
            var key = LinuxGameLibraryStore.GameKey(game);
            _library.SetLibraryFlags(game, _libraryState.FavouriteGames.Contains(key), _libraryState.HiddenGames.Contains(key));
        }
    }

    private bool _libraryStateUnreadable;

    /// <summary>
    /// True while Repair after move rewrites the library file in the background: a favourite, hidden, HDR or filter
    /// save then would write the pre-repair state over the repaired file, so those changes wait (review nit 9), and
    /// Add game is refused. A filter chosen meanwhile is shown at once and saved when the repair ends, over the library
    /// state the repair left. Setting it to false ends a repair that succeeded (<see cref="EndLibraryRepair"/>).
    /// </summary>
    internal bool LibraryRepairRunning
    {
        get => _libraryRepairRunning;
        set
        {
            if (value) _libraryRepairRunning = true;
            else EndLibraryRepair(repaired: true);
        }
    }

    /// <summary>
    /// Ends a repair. A filter chosen meanwhile is saved now; after a repair that failed part-way the library file may
    /// hold part of the repair while memory holds the pre-repair state, so the file is read again first and its state
    /// adopted, and only the filter is changed on top of it (Refs #149).
    /// </summary>
    internal void EndLibraryRepair(bool repaired)
    {
        _libraryRepairRunning = false;
        if (!_filterChangedDuringRepair) return;
        _filterChangedDuringRepair = false;
        if (!repaired)
        {
            var onDisk = _libraryStore.Load(out var unreadable);
            if (!unreadable) AdoptLibraryState(onDisk, keepFilter: true);
            else _libraryStateUnreadable = true;
        }
        PersistLibraryFilter(LibraryFilterMode);
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
        using var op = BeginOperation("repair-after-move", cancellable: false);
        if (op is null) return;
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
                StatusText = OutcomeText(op, result.Success, result.Message);
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
            _library.SetLibraryFlags(game, _libraryState.FavouriteGames.Contains(libraryKey), _libraryState.HiddenGames.Contains(libraryKey));
            if (_libraryState.GameEdits.TryGetValue(libraryKey, out var edit))
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = StoreSaveFailedText(ex);
        }
        finally
        {
            EndLibraryRepair(repaired);
            NotifySelected();
        }

        if (repaired && ReferenceEquals(SelectedGame, game))
        {
            // Reload every per-game preference from the stores and probe the new folder.
            _session.Invalidate(game);
            OnSelectedGameChanged(game);
        }
        CheckRepairAfterMove(force: true);
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
        using var op = BeginOperation("luma-local", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        try
        {
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.LumaFramework, "Luma", installsTarget: true, ProbedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);
            StatusText = TF("Vm.InstallingLocalLumaPackage", game.Name);
            var result = await _managedComponents
                .InstallLocalLumaPackageAsync(game, sourcePath, op.Token)
                .ConfigureAwait(true);
            StatusText = OutcomeText(op, result.Success, result.Message);
            if (result.Success)
                await ReprobeGameAsync(game);
            return result;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            return ComponentOperationResult.Fail(ShowStatus(OperationCancelledText()));
        }
        catch (Exception ex)
        {
            var result = ComponentOperationResult.Fail(TF("Vm.LocalLuma", UiLocalizationService.Present(ex.Message)));
            StatusText = OutcomeText(op, result.Success, result.Message);
            return result;
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

    private void ReevaluateCurrentGameComponents(DetectedGame game)
    {
        if (!_session.Recompose(game)) NotifySelected();
    }

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
        Runtime.NotifySection();
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(SelectedGameHasNotes));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchToolTip));
        OnPropertyChanged(nameof(CanEditSelectedGame));
        OnPropertyChanged(nameof(FavouriteActionText));
        OnPropertyChanged(nameof(HideActionText));
        ManualGames.NotifyRemove();
        OnPropertyChanged(nameof(ShowRepairAfterMove));
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithProtontricks));
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithWinetricks));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        LaunchCommand.NotifyCanExecuteChanged();
        Updates.NotifyCanUpdateAll();
        NotifyDlss();
        Extras.NotifySection();
    }

    private void NotifyDlss()
    {
        Runtime.NotifyNeuralRecommendation();
        Extras.NotifyPins();
        Dlss.NotifyPanel();
        OnPropertyChanged(nameof(CanOpenDlssBatch));
    }

    /// <summary>The Settings DLSS defaults or the busy state changed: the section's defaults row and the DLSS batch button follow.</summary>
    private void NotifyDlssDefaults()
    {
        Dlss.NotifyDlssDefaults();
        OnPropertyChanged(nameof(CanOpenDlssBatch));
    }

    // In-session caches (probe, selection) are per card and folder; saved library state uses GameKey (Refs #147).
    private static string Key(DetectedGame g) => LinuxGameLibraryStore.SessionKey(g);

    // ---- IPanelHost: what the split-out panels read from the shell (epic #89 units 4–7) ----
    GameAnalysis? IPanelHost.CurrentAnalysis => CurrentAnalysis;
    LinuxAppSettings IPanelHost.Settings => _settings;
    IReadOnlyList<DetectedGame> IPanelHost.AllGames => _library.AllGames;
    LinuxGameLibraryState IPanelHost.LibraryState => _libraryState;
    OperationScope? IPanelHost.BeginOperation(string kind, bool cancellable, CancellationToken linkedToken) => BeginOperation(kind, cancellable, linkedToken);
    string? IPanelHost.TryPersist(Action save, string whatKey, bool sessionOnly, bool reportStatus) => TryPersist(save, whatKey, sessionOnly, reportStatus);
    Task IPanelHost.ReprobeGameAsync(DetectedGame game) => ReprobeGameAsync(game);
    Task IPanelHost.ReprobeKeepingStatusAsync(DetectedGame game) => ReprobeKeepingStatusAsync(game);
    Task IPanelHost.InvalidateAllAndReprobeSelectedAsync() => InvalidateAllAndReprobeSelectedAsync();
    Task<bool> IPanelHost.AcceptManifestInstallWarningsAsync(DetectedGame game, ManagedComponentKind target, string displayName,
        bool installsTarget, IReadOnlyList<string>? indexedFiles) => AcceptManifestInstallWarningsAsync(game, target, displayName, installsTarget, indexedFiles);
    bool IPanelHost.LibraryWritesPaused() => LibraryWritesPaused();
    bool IPanelHost.AdoptRestoredLibraryFile(bool reloadHdrControls) => AdoptRestoredLibraryFile(reloadHdrControls);
    void IPanelHost.NotifySelected() => NotifySelected();
    void IPanelHost.NotifyDlss() => NotifyDlss();
    LinuxGameLibraryState ILibraryEditHost.WorkingLibraryCopy() => WorkingLibraryCopy();
    string? ILibraryEditHost.SaveLibraryCopy(LinuxGameLibraryState state) => TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false);
    void ILibraryEditHost.AdoptLibraryCopy(LinuxGameLibraryState state) => _libraryState = state;
    Task<bool> ILibraryEditHost.RefreshWhenIdleAsync(IReadOnlySet<string>? onlyDetectors) => RefreshWhenIdleAsync(onlyDetectors);
    LinuxDeploymentRecordStore ILibraryEditHost.DeploymentRecords => _deploymentRecords;
    bool ILibraryEditHost.LibraryRepairRunning => LibraryRepairRunning;
    void ILibraryEditHost.RaiseNotice(string message) => StatusText = _statusNotice.Raise(message);
    void ILibraryEditHost.ShowAllGames() => LibraryFilterMode = "All Games";
    void ILibraryEditHost.Select(DetectedGame game) => SelectedGame = game;
    string? IPanelHost.PersistLibraryState(string whatKey) => TryPersist(() => _libraryStore.Save(_libraryState), whatKey);
    void IPanelHost.ForgetFilterFacts(DetectedGame game) => _filterFacts.Remove(game);
}

/// <summary>One component's manifest warning in the install-warning dialog; <see cref="Heading"/> names the component when several are shown.</summary>
public sealed record InstallWarningSection(string? Heading, ManifestInstallWarning Warning);
