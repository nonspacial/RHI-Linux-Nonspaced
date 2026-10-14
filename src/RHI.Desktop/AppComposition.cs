using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop;

/// <summary>
/// The app's service graph: every store, detector and deployment service, and the view model over them. Built by
/// <see cref="App"/> once start-up recovery has run (it owns the ledger, the Wine override service and the journal it
/// hands in). Kept apart from Avalonia start-up so tests walk the real composition, not a copy of it
/// (<c>RepairStoreWiringTests</c>: every per-game store the app constructs must be in Repair after move's set).
/// </summary>
internal sealed class AppComposition
{
    public required GameLibraryScanner Scanner { get; init; }
    public required LinuxGameLauncher Launcher { get; init; }
    public required LinuxGameRuntimeProbe RuntimeProbe { get; init; }
    public required DlssManagerService Dlss { get; init; }
    public required LinuxManagedComponentService ManagedComponents { get; init; }
    public required GraphicsApiOverrideStore ApiOverrideStore { get; init; }
    public required OptiScalerVariantPreferenceStore OptiScalerVariantStore { get; init; }
    public required LinuxSettingsStore SettingsStore { get; init; }
    public required LinuxUpdateAllService UpdateAll { get; init; }
    public required LinuxDlssDefaultsService DlssDefaults { get; init; }
    public required LinuxGameLibraryStore LibraryStore { get; init; }
    public required LinuxDeploymentRecordStore Records { get; init; }
    public required ComponentUpdateLogStore UpdateLog { get; init; }
    public required RenoDxCatalogService RenoDxCatalog { get; init; }
    public required LumaCatalogService LumaCatalog { get; init; }
    public required UpstreamRhiManifestService UpstreamManifest { get; init; }
    // w3/sync rows
    /// <summary>RHI Unity database for the Available HDR mods list (#126); the RenoDX installer keeps its own instance (same disk cache, ETag revalidated).</summary>
    public RenoDxUnityDatabaseService? RenoDxUnityDatabase { get; init; }
    // w3/journal rows
    // w3/scan rows
    // w3/parity rows
    // w3/vmsplit rows
    // w3/dllnames rows

    public MainWindowViewModel CreateViewModel()
        => new(Scanner, Launcher, RuntimeProbe, Dlss, ManagedComponents, ApiOverrideStore, OptiScalerVariantStore, SettingsStore,
            UpdateAll, DlssDefaults, LibraryStore, Records, operations: new OperationCoordinator());

    /// <summary>The Wine override service every registry edit goes through, and the deployment journal over it.</summary>
    internal sealed record WineRecovery(WineDllOverrideService Overrides, DeploymentJournal Journal);

    /// <summary>
    /// The app's Wine override service and deployment journal (built before start-up recovery, which uses both). A
    /// registry edit started on the UI thread never waits long for another RHI's lock (w2 wine review, nit 9), and the
    /// journal sees the ledger, so a deferred override undo can tell a later install that depends on it (major 1).
    /// </summary>
    /// <param name="isUiThread">Whether the calling thread is the UI thread (default: Avalonia's dispatcher).</param>
    /// <param name="journalDirectory">Test seam (default: the app's journal folder).</param>
    /// <param name="isPrefixInUse">Test seam (default: a wineserver runs for the prefix).</param>
    /// <param name="lockTimeout">Test seam (default: the editor's lock wait).</param>
    public static WineRecovery CreateWineRecovery(
        LinuxDeploymentRecordStore records, Func<bool>? isUiThread = null, string? journalDirectory = null,
        Func<string, bool>? isPrefixInUse = null, TimeSpan? lockTimeout = null)
    {
        isUiThread ??= () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        var overrides = new WineDllOverrideService(new WineRegistryEditor(isPrefixInUse, lockTimeout, mayBlock: () => !isUiThread()));
        return new WineRecovery(overrides, LinuxDeploymentRecovery.CreateJournal(overrides, journalDirectory, isPrefixInUse, records));
    }

    public static AppComposition Create(HttpClient http, LinuxDeploymentRecordStore records, WineDllOverrideService wineOverrides, DeploymentJournal journal)
    {
        var libraryStore = new LinuxGameLibraryStore();
        var detectors = new IGameDetector[]
        {
            new SteamGameDetector(),
            new HeroicGameDetector(),
            new LutrisGameDetector(),
            new WinePrefixDetector(),
            new ManualGameDetector(libraryStore),
            // w3/scan rows (new store detectors)
        };
        var upstreamManifest = new UpstreamRhiManifestService(http);
        var pcgwData = new PcgwCentralDataService(http);
        var libraryPolicy = new UpstreamGameLibraryPolicyService(upstreamManifest, pcgwData);
        var scanner = new GameLibraryScanner(detectors, [libraryPolicy]);
        var launcher = new LinuxGameLauncher();
        var deployment = new TransactionalFileDeploymentService(journal);
        var pcgwGraphics = new PcGamingWikiGraphicsApiService(http);
        var runtimeProbe = new LinuxGameRuntimeProbe(upstreamManifest, pcgwGraphics, pcgwData);
        var reshadeExtractor = new ReShadeArchiveExtractor();
        var reshade = new ReShadeDeploymentService(http, deployment, records, wineOverrides, reshadeExtractor, upstreamManifest: upstreamManifest, journal: journal);
        var addonManager = new ReShadeAddonManagerService(http, upstreamManifest);
        var renodxCatalog = new RenoDxCatalogService(http, upstreamManifest);
        var dlss = new DlssManagerService(http, deployment, Path.Combine(LinuxPaths.CacheHome, "rhi-linux"));
        var renodx = new RenoDxDeploymentService(http, records, renodxCatalog, upstreamManifest: upstreamManifest,
            unityDatabase: new RenoDxUnityDatabaseService(http),
            controlUe: new ControlUePostInstallService(deployment, dlss));
        var renodxUpgrade = new RenoDxUpgradeDeploymentService(http, records, upstreamManifest, journal: journal);
        var relimiter = new GitHubReShadeAddonDeploymentService(http, records, journal: journal, definition: new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter,
            "ReLimiter",
            "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
            "relimiter.addon64",
            "relimiter.addon32",
            100 * 1024,
            ManagedComponentKind.DisplayCommander));
        var displayCommander = new GitHubReShadeAddonDeploymentService(http, records, journal: journal, definition: new GitHubReShadeAddonDefinition(
            ManagedComponentKind.DisplayCommander,
            "Display Commander",
            "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
            "zzz_display_commander.addon64",
            "zzz_display_commander.addon32",
            500 * 1024,
            ManagedComponentKind.ReLimiter,
            @"Version in binaries\*\*:\s*([0-9.]+)"));
        var mfgAdaUnlock = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, journal: journal, options: new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.MfgAdaUnlock,
            "mfgunlock",
            "MFG Ada Unlock",
            "https://api.github.com/repos/mavismmg/MFGAdaUnlock-RenoDx/releases?per_page=1",
            128 * 1024,
            ExactAssetName64: "renodx-mfgunlock.addon64",
            DeployFileName64: "renodx-mfgunlock.addon64",
            X64Only: true));
        var dlssg2030Preferences = new Dlssg2030PreferenceStore();
        var dlssg2030 = new Dlssg2030UnlockDeploymentService(http, records, wineOverrides, dlssg2030Preferences, journal: journal);
        var dlssEnablerPreferences = new StandaloneDlssEnablerPreferenceStore();
        var dlssEnablerStandalone = new StandaloneDlssEnablerDeploymentService(http, records, wineOverrides, dlssEnablerPreferences, journal: journal);
        var rtx40MfgPreferences = new Rtx40MfgPreferenceStore();
        var rtx40Mfg = new Rtx40MfgStandaloneDeploymentService(http, records, wineOverrides, rtx40MfgPreferences, journal: journal);
        var dlss5Bridge = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, journal: journal, options: new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Dx11Bridge,
            "dlss5-dx11-bridge",
            "DLSS5 DX11 Bridge",
            "https://api.github.com/repos/NIGos/dlss5-bridge/releases?per_page=1",
            128 * 1024,
            ExactAssetName64: "dlss5-bridge.addon64",
            DeployFileName64: "dlss5-bridge.addon64",
            X64Only: true));
        var dlss5FeederAddonOnly = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, journal: journal, options: new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder,
            "dlss5-feed",
            "DLSS5 Feeder",
            "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1",
            64 * 1024,
            AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$",
            AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
            DeployFileName64: "dlss5-feed.addon64",
            DeployFileName32: "dlss5-feed.addon32"));
        var d3dCompilerTools = new D3DCompilerToolInstaller(wineOverrides);
        var dgVoodoo = new DgVoodooDeploymentService(http);
        var dx9ReShade = new Dx9TranslationReShadeCoordinator(records, wineOverrides, journal);
        var dlss5Feeder = new Dlss5FeederFullDeploymentService(http, records, dlss, dlss5FeederAddonOnly, overrides: wineOverrides, reShade: reshade, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade, journal: journal);
        var optiExtractor = new OptiScalerArchiveExtractor();
        var optiPackages = new OptiScalerPackageProvider(http, optiExtractor);
        var optiscaler = new OptiScalerDeploymentService(deployment, records, wineOverrides, optiPackages, dlss, dlssEnablerStandalone, journal);
        var ultimateAsiLoader = new UltimateAsiLoaderDeploymentService(http, records, wineOverrides, journal: journal);
        var shortFuse = new ShortFuseDeploymentService(http, deployment, records, wineOverrides, ultimateAsiLoader, dlss, journal: journal);
        var dofFix = new DofFixDeploymentService(http, records, upstreamManifest, journal: journal);
        var reFramework = new REFrameworkDeploymentService(http, records, wineOverrides, journal: journal);
        var lumaCatalog = new LumaCatalogService(http, upstreamManifest);
        var apiOverrideStore = new GraphicsApiOverrideStore();
        var optiScalerVariantStore = new OptiScalerVariantPreferenceStore();
        var neuralAddonVersionStore = new NeuralAddonVersionPreferenceStore();
        var settingsStore = new LinuxSettingsStore();
        var lumaModeStore = new LumaModePreferenceStore();
        var lumaPolicy = new LumaPolicyService(upstreamManifest, lumaModeStore);
        var lumaFramework = new LumaFrameworkDeploymentService(http, records, catalog: lumaCatalog, policy: lumaPolicy, manifest: upstreamManifest, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
        var updateLog = new ComponentUpdateLogStore();
        var managedComponents = new LinuxManagedComponentService(
            records, reshade, addonManager, renodx, renodxUpgrade, optiscaler, relimiter, displayCommander,
            mfgAdaUnlock, dlssg2030, dlssEnablerStandalone, rtx40Mfg, dlss5Feeder, dlss5Bridge, d3dCompilerTools, ultimateAsiLoader, shortFuse, dofFix, reFramework, lumaFramework, lumaPolicy, neuralAddonVersionStore,
            updateLog);
        // w3/sync rows
        var renodxUnityDatabase = new RenoDxUnityDatabaseService(http);
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        return new AppComposition
        {
            Scanner = scanner,
            Launcher = launcher,
            RuntimeProbe = runtimeProbe,
            Dlss = dlss,
            ManagedComponents = managedComponents,
            ApiOverrideStore = apiOverrideStore,
            OptiScalerVariantStore = optiScalerVariantStore,
            SettingsStore = settingsStore,
            UpdateAll = new LinuxUpdateAllService(records, managedComponents),
            DlssDefaults = new LinuxDlssDefaultsService(dlss, records),
            LibraryStore = libraryStore,
            Records = records,
            UpdateLog = updateLog,
            RenoDxCatalog = renodxCatalog,
            LumaCatalog = lumaCatalog,
            UpstreamManifest = upstreamManifest,
            // w3/sync rows
            RenoDxUnityDatabase = renodxUnityDatabase,
            // w3/journal rows
            // w3/scan rows
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
        };
    }
}
