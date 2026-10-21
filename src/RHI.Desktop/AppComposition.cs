using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
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
    /// <summary>The "Xbox / Game Pass folders" setting the Xbox detector reads (Settings hosts it through <see cref="ViewModels.XboxFoldersPresenter"/>).</summary>
    public RHI.Platform.Linux.Detection.Xbox.XboxFolderStore XboxFolders { get; init; } = new();
    // w3/parity rows
    public ParityFeatures? Parity { get; init; }
    // w3/vmsplit rows
    // w3/dllnames rows
    public required DllNamePreferenceStore DllNames { get; init; }
    // w5/update-policy rows
    /// <summary>#177 Follow newest DLSS / Streamline; handed to the view model by <see cref="CreateViewModel"/>.</summary>
    public UpdatePolicyFeatures? UpdatePolicy { get; init; }
    // w5/catalogue-info rows
    /// <summary>#184/#197: RHI's UE-Extended database (ⓘ notes, Available HDR mods rows); one instance, one 1 h cache.</summary>
    public RenoDxUnrealDatabaseService? RenoDxUnrealDatabase { get; init; }
    /// <summary>#197: the Available HDR mods list (RenoDX database incl. Nexus/Discord-only mods, UE-Extended, Unity, Luma).</summary>
    public AvailableHdrModsCatalog? HdrModsCatalog { get; init; }
    /// <summary>#185: the lists the "New mods" notice compares with the seen sets.</summary>
    public NewModsSources? NewModsSources { get; init; }
    // w5/ini-presets rows
    // w5/shell-launch rows
    /// <summary>
    /// #191: switches the display around a direct launch (kscreen-doctor / wlr-randr). Only the app passes one to
    /// <see cref="Create"/>; without it (tests, the harness) the view model never switches, queries or recovers the display.
    /// </summary>
    public RHI.Platform.Linux.Hdr.DisplayModeSwitcher? DisplayModes { get; init; }
    // w5/reshade-core rows
    // w7/smalls rows
    // w7/reshade rows
    // w7/shader-sets rows
    // w7/nvapi-launch rows
    // w7/dxvk-hdr rows
    // w8/reshade-ini rows
    // w8/renodx rows
    // w8/commit-point rows

    public MainWindowViewModel CreateViewModel()
    {
        var vm = new MainWindowViewModel(Scanner, Launcher, RuntimeProbe, Dlss, ManagedComponents, ApiOverrideStore, OptiScalerVariantStore, SettingsStore,
            UpdateAll, DlssDefaults, LibraryStore, Records, operations: new OperationCoordinator());
        vm.Settings.UseXboxFolders(XboxFolders); // w3/scan Settings row (#90): the one store the detectors read
        vm.Resolution.Switcher = DisplayModes; // #191: also puts back a display switch an earlier run left behind
        vm.UpdatePolicy = UpdatePolicy; // w5/update-policy hook (#177)
        return vm;
    }

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

    /// <param name="displayModes">#191: the display switcher the app runs with; null (tests) = the display is never touched.</param>
    public static AppComposition Create(HttpClient http, LinuxDeploymentRecordStore records, WineDllOverrideService wineOverrides, DeploymentJournal journal,
        RHI.Platform.Linux.Hdr.DisplayModeSwitcher? displayModes = null)
    {
        var libraryStore = new LinuxGameLibraryStore();
        // w3/scan hook: one "Xbox / Game Pass folders" store for Settings, the Xbox detector and the Wine detector (Refs #90).
        var xboxFolders = new RHI.Platform.Linux.Detection.Xbox.XboxFolderStore();
        var detectors = new IGameDetector[]
        {
            new SteamGameDetector(),
            new HeroicGameDetector(),
            new LutrisGameDetector(),
            new WinePrefixDetector(xboxFolders), // w3/scan hook: installer-app instances' prefixes are the installer app's
            new ItchGameDetector(), // the native itch app's games (Refs #90)
            new ManualGameDetector(libraryStore),
            // w3/scan rows (new store detectors)
            new RHI.Platform.Linux.Detection.Xbox.XboxGameDetector(xboxFolders),
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
        // w3/parity hook (#93): the one shader-pack service, shared with the shader-profile export (same cache dir as before).
        var shaderPacks = new ReShadeShaderPackService(http, Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "reshade", "shader-packs"), upstreamManifest);
        var reshade = new ReShadeDeploymentService(http, deployment, records, wineOverrides, reshadeExtractor, shaderPacks: shaderPacks, upstreamManifest: upstreamManifest, journal: journal);
        var addonManager = new ReShadeAddonManagerService(http, upstreamManifest, records: records, journal: journal); // #168: state in the ledger
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
        var optiscaler = new OptiScalerDeploymentService(deployment, records, wineOverrides, optiPackages, dlss, dlssEnablerStandalone, journal,
            optiPatcher: new OptiPatcherProvider(http, Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "optipatcher"))); // w3/dllnames hook (#98)
        var ultimateAsiLoader = new UltimateAsiLoaderDeploymentService(http, records, wineOverrides, journal: journal);
        var shortFuse = new ShortFuseDeploymentService(http, deployment, records, wineOverrides, ultimateAsiLoader, dlss, journal: journal);
        var dofFix = new DofFixDeploymentService(http, records, upstreamManifest, journal: journal);
        var reFramework = new REFrameworkDeploymentService(http, records, wineOverrides, journal: journal);
        var lumaCatalog = new LumaCatalogService(http, upstreamManifest);
        var apiOverrideStore = new GraphicsApiOverrideStore();
        var optiScalerVariantStore = new OptiScalerVariantPreferenceStore();
        var neuralAddonVersionStore = new NeuralAddonVersionPreferenceStore();
        var settingsStore = new LinuxSettingsStore();
        optiscaler.IniTemplates = new OptiScalerIniTemplates(Path.Combine(settingsStore.ConfigRoot, "inis"), // w3/dllnames hook (#97)
            () => OptiScalerIniTemplates.ChoiceFrom(settingsStore.Load()));
        var lumaModeStore = new LumaModePreferenceStore();
        var lumaPolicy = new LumaPolicyService(upstreamManifest, lumaModeStore);
        var lumaFramework = new LumaFrameworkDeploymentService(http, records, catalog: lumaCatalog, policy: lumaPolicy, manifest: upstreamManifest, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade, journal: journal); // #169
        var updateLog = new ComponentUpdateLogStore();
        var nrCostScaler = new NrCostScalerService(http, records, journal); // w3/parity hook (#96)
        // w5 lane rows: services LinuxManagedComponentService takes (named args below)
        // w5/update-policy rows
        var updateInclusion = new UpdateInclusionStore(globalSkips: () => UpdateInclusionStore.GlobalSkipsFrom(settingsStore.Load()), // #178
            followChoice: () => DlssFollowChoice.From(settingsStore.Load()));
        var dlssFollowStore = new DlssAutoFollowStore(); // #177: per-game holds move with the game
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // #181: the user's INI templates (beside the OptiScaler ones) feed ReShade.ini, relimiter.ini and DisplayCommander.ini at install.
        var userIniTemplates = new UserIniTemplates(Path.Combine(settingsStore.ConfigRoot, "inis"));
        reshade.IniDefaults = (game, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(settingsStore.Load(), game, userIniTemplates, vulkan);
        relimiter.IniTemplates = userIniTemplates;
        displayCommander.IniTemplates = userIniTemplates;
        renodx.PeakNits = () => PeakNitsSettings.From(settingsStore.Load()); // #182: peak brightness into RenoDX presets at install
        // w5/shell-launch rows
        // w5/reshade-core rows
        // w7 lane rows: services LinuxManagedComponentService takes (named args below)
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        var shaderSets = new GlobalShaderAddonStore(records); // #179: global shader/add-on sets, per-game Global / Select / Off
        shaderPacks.Sets = shaderSets; // ReShade's install and update read the game's effective packs from it
        try { shaderSets.AdoptFromLedger(addonManager); } // #179 migration: saved per-game selections become Select, unchanged (the Addon Manager's older copy reconciled first)
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("shader-sets", "Could not save the shader and add-on choices taken from the deployment records", ex); }
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // #200 DX9 HDR: DXVK HDR beside DirectX 9 games (the view model points its row note at the in-memory library).
        var dxvkHdr = new DxvkHdrDeploymentService(http, records, wineOverrides, reshade, journal);
        // w8 lane rows: services LinuxManagedComponentService takes (named args below)
        // w8/reshade-ini rows
        // w8/renodx rows
        // w8/commit-point rows
        var managedComponents = new LinuxManagedComponentService(
            records, reshade, addonManager, renodx, renodxUpgrade, optiscaler, relimiter, displayCommander,
            mfgAdaUnlock, dlssg2030, dlssEnablerStandalone, rtx40Mfg, dlss5Feeder, dlss5Bridge, d3dCompilerTools, ultimateAsiLoader, shortFuse, dofFix, reFramework, lumaFramework, lumaPolicy, neuralAddonVersionStore,
            updateLog, nrCostScaler: nrCostScaler, // w3/parity hook (#96): named argument
            rtxEncore: new RtxEncoreDeploymentService(http, records, wineOverrides, reshade.DllNames, dlss, journal: journal) // w4/encore (#158)
            // w5 lane rows: named arguments, each line starting with a comma
            // w5/update-policy rows
            , updateInclusion: updateInclusion
            , dlssFollow: dlssFollowStore
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            // w7 lane rows: named arguments, each line starting with a comma
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            , dxvkHdr: dxvkHdr // #200 DX9 HDR
            // w8 lane rows: named arguments, each line starting with a comma
            // w8/reshade-ini rows
            // w8/renodx rows
            // w8/commit-point rows
            );
        // w3/sync rows
        var renodxUnityDatabase = new RenoDxUnityDatabaseService(http);
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        var accounts = new AccountsPreferenceStore();
        var nexusApi = NexusApiClient.CreateDefault(); // its own client: never follows a redirect with the key
        var nexusAccount = new NexusAccountPresenter(new SecretFileStore(NexusAccountPresenter.DefaultKeyPath, "nexus"), nexusApi, accounts,
            new NxmHandlerRegistration(), ssoSlug: NexusApplication.SsoSlug);
        var parity = new ParityFeatures
        {
            NexusAccount = nexusAccount,
            ShaderProfiles = new ShaderProfilePresenter(new ShaderSelectionProfileStore(), shaderPacks),
            NrCostScaler = new NrCostScalerPresenter(managedComponents),
            GameLinks = new GameLinksPresenter(nexusAccount, nexusApi, new NexusDownloadService(http, nexusApi), new NexusGameCatalogue(http)),
            AppUpdate = new AppUpdatePresenter(new AppUpdateCheckService(http), accounts),
            Shortcuts = new GameShortcutPresenter(new DesktopShortcutService()),
            GitHubToken = new GitHubTokenPresenter(new GitHubTokenSettingsService(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new SecretFileStore(GitHubTokenSettingsService.DefaultTokenPath, "github-token"))),
        };
        // w3/vmsplit rows
        // w3/dllnames rows
        // w5/update-policy rows
        var updatePolicy = new UpdatePolicyFeatures( // #177: the same history store the components write; #178 the same inclusion store
            new DlssAutoFollowService(dlss, new LinuxDlssDefaultsService(dlss, records), records, dlssFollowStore, updateLog,
                updateInclusion: updateInclusion), updateInclusion);
        // w5/catalogue-info rows
        // #183: the card menu's web pages (PCGamingWiki shares the graphics-API lookup's request gate).
        parity.GameLinks.Links = new GameLinkCatalogue(http, pcgwData: pcgwData, pcgwSearch: pcgwGraphics,
            policy: async ct => await upstreamManifest.FetchLibraryPolicyAsync(ct).ConfigureAwait(false));
        parity.GameLinks.RenoDx = renodxCatalog;
        // #184: the component rows' ⓘ dialogs.
        var renodxUnrealDatabase = new RenoDxUnrealDatabaseService(http);
        parity.ComponentInfo = new ComponentInfoPresenter(new ComponentInfoCatalogue(http, upstreamManifest, renodxCatalog, lumaCatalog,
            new OptiScalerWikiCatalogue(http), renodxUnrealDatabase, renodxUnityDatabase, parity.GameLinks.Links));
        // w5/ini-presets rows
        managedComponents.UserIni = new UserIniService(userIniTemplates, records, managedComponents.IniWriteRefusal, journal); // #181: deploys journaled
        managedComponents.ReShadePresets = new ReShadePresetService(records, journal, managedComponents.IniWriteRefusal); // #180
        managedComponents.LumaSettings = new LumaSettingsService(records, journal, managedComponents.IniWriteRefusal); // #189
        managedComponents.RenoDxPeakNits = new RenoDxPeakNits(records, journal, managedComponents.IniWriteRefusal); // #182
        // w5/shell-launch rows
        // w5/reshade-core rows
        // w7/smalls rows
        // w7/reshade rows
        // w7/shader-sets rows
        managedComponents.ShaderSets = shaderSets; // #179 (the Addon Manager reads it through the managed service)
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        // w8/reshade-ini rows
        reshade.IniDefaults = ReShadePresetService.KeepingSelection(reshade.IniDefaults, records); // #206: a regenerated ReShade.ini keeps RHI's preset
        // w8/renodx rows
        // w8/commit-point rows
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
            XboxFolders = xboxFolders,
            // w3/parity rows
            Parity = parity,
            // w3/vmsplit rows
            // w3/dllnames rows
            DllNames = reshade.DllNames,
            // w5/update-policy rows
            UpdatePolicy = updatePolicy,
            // w5/catalogue-info rows
            RenoDxUnrealDatabase = renodxUnrealDatabase,
            HdrModsCatalog = new AvailableHdrModsCatalog(renodxCatalog, lumaCatalog, renodxUnityDatabase, renodxUnrealDatabase),
            NewModsSources = new NewModsSources(new SeenModsStore(),
                async ct => (await renodxCatalog.FetchListingAsync(ct).ConfigureAwait(false))
                    .Where(mod => mod.SnapshotUrl64 is not null || mod.SnapshotUrl32 is not null || mod.NexusUrl is not null).Select(mod => mod.Name).ToArray(),
                async ct => (await lumaCatalog.FetchAsync(ct).ConfigureAwait(false)).Select(mod => mod.Name).ToArray(),
                async _ =>
                {
                    await parity.GameLinks.Links!.LoadAsync().ConfigureAwait(false);
                    return parity.GameLinks.Links.UltraPlusGames.Select(game => game.Name).ToArray();
                }),
            // w5/ini-presets rows
            // w5/shell-launch rows
            DisplayModes = displayModes,
            // w5/reshade-core rows
            // w7/smalls rows
            // w7/reshade rows
            // w7/shader-sets rows
            // w7/nvapi-launch rows
            // w7/dxvk-hdr rows
            // w8/reshade-ini rows
            // w8/renodx rows
            // w8/commit-point rows
        };
    }
}
