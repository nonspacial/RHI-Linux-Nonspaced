using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var libraryStore = new LinuxGameLibraryStore();
            var detectors = new IGameDetector[]
            {
                new SteamGameDetector(),
                new HeroicGameDetector(),
                new LutrisGameDetector(),
                new WinePrefixDetector(),
                new ManualGameDetector(libraryStore)
            };
            var githubTokenPath = Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "github_api.txt");
            var http = new HttpClient(new GitHubApiTokenHandler(githubTokenPath)) { Timeout = TimeSpan.FromMinutes(5) };
            var upstreamManifest = new UpstreamRhiManifestService(http);
            var libraryPolicy = new UpstreamGameLibraryPolicyService(upstreamManifest);
            var scanner = new GameLibraryScanner(detectors, [libraryPolicy]);
            var launcher = new LinuxGameLauncher();
            var deployment = new TransactionalFileDeploymentService();
            var pcgwGraphics = new PcGamingWikiGraphicsApiService(http);
            var pcgwData = new PcgwCentralDataService(http);
            var runtimeProbe = new LinuxGameRuntimeProbe(upstreamManifest, pcgwGraphics, pcgwData);
            var records = new LinuxDeploymentRecordStore();
            var wineOverrides = new WineDllOverrideService();
            var reshadeExtractor = new ReShadeArchiveExtractor();
            var reshade = new ReShadeDeploymentService(http, deployment, records, wineOverrides, reshadeExtractor, upstreamManifest: upstreamManifest);
            var addonManager = new ReShadeAddonManagerService(http, upstreamManifest);
            var renodxCatalog = new RenoDxCatalogService(http, upstreamManifest);
            var dlss = new DlssManagerService(http, deployment, Path.Combine(LinuxPaths.CacheHome, "rhi-linux"));
            var renodx = new RenoDxDeploymentService(http, records, renodxCatalog, upstreamManifest: upstreamManifest,
                controlUe: new ControlUePostInstallService(deployment, dlss));
            var renodxUpgrade = new RenoDxUpgradeDeploymentService(http, records, upstreamManifest);
            var relimiter = new GitHubReShadeAddonDeploymentService(http, records, new GitHubReShadeAddonDefinition(
                ManagedComponentKind.ReLimiter,
                "ReLimiter",
                "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
                "relimiter.addon64",
                "relimiter.addon32",
                100 * 1024,
                ManagedComponentKind.DisplayCommander));
            var displayCommander = new GitHubReShadeAddonDeploymentService(http, records, new GitHubReShadeAddonDefinition(
                ManagedComponentKind.DisplayCommander,
                "Display Commander",
                "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
                "zzz_display_commander.addon64",
                "zzz_display_commander.addon32",
                500 * 1024,
                ManagedComponentKind.ReLimiter,
                @"Version in binaries\*\*:\s*([0-9.]+)"));
            var mfgAdaUnlock = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
                ManagedComponentKind.MfgAdaUnlock,
                "mfgunlock",
                "MFG Ada Unlock",
                "https://api.github.com/repos/mavismmg/MFGAdaUnlock-RenoDx/releases/latest",
                128 * 1024,
                ExactAssetName64: "renodx-mfgunlock.addon64",
                DeployFileName64: "renodx-mfgunlock.addon64",
                X64Only: true));
            var dlssg2030Preferences = new Dlssg2030PreferenceStore();
            var dlssg2030 = new Dlssg2030UnlockDeploymentService(http, records, wineOverrides, dlssg2030Preferences);
            var dlssEnablerPreferences = new StandaloneDlssEnablerPreferenceStore();
            var dlssEnablerStandalone = new StandaloneDlssEnablerDeploymentService(http, records, wineOverrides, dlssEnablerPreferences);
            var rtx40MfgPreferences = new Rtx40MfgPreferenceStore();
            var rtx40Mfg = new Rtx40MfgStandaloneDeploymentService(http, records, wineOverrides, rtx40MfgPreferences);
            var dlss5Bridge = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
                ManagedComponentKind.Dlss5Dx11Bridge,
                "dlss5-dx11-bridge",
                "DLSS5 DX11 Bridge",
                "https://api.github.com/repos/NIGos/dlss5-bridge/releases/latest",
                128 * 1024,
                ExactAssetName64: "dlss5-bridge.addon64",
                DeployFileName64: "dlss5-bridge.addon64",
                X64Only: true));
            var dlss5FeederAddonOnly = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
                ManagedComponentKind.Dlss5Feeder,
                "dlss5-feed",
                "DLSS5 Feeder",
                "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest",
                64 * 1024,
                AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$",
                AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
                DeployFileName64: "dlss5-feed.addon64",
                DeployFileName32: "dlss5-feed.addon32"));
            var d3dCompilerTools = new D3DCompilerToolInstaller(wineOverrides);
            var dgVoodoo = new DgVoodooDeploymentService(http);
            var dx9ReShade = new Dx9TranslationReShadeCoordinator(records, wineOverrides);
            var dlss5Feeder = new Dlss5FeederFullDeploymentService(http, records, dlss, dlss5FeederAddonOnly, overrides: wineOverrides, reShade: reshade, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
            var optiExtractor = new OptiScalerArchiveExtractor();
            var optiPackages = new OptiScalerPackageProvider(http, optiExtractor);
            var optiscaler = new OptiScalerDeploymentService(deployment, records, wineOverrides, optiPackages, dlss, dlssEnablerStandalone);
            var ultimateAsiLoader = new UltimateAsiLoaderDeploymentService(http, records, wineOverrides);
            var shortFuse = new ShortFuseDeploymentService(http, deployment, records, wineOverrides, ultimateAsiLoader, dlss);
            var dofFix = new DofFixDeploymentService(http, records, upstreamManifest);
            var reFramework = new REFrameworkDeploymentService(http, records, wineOverrides);
            var lumaCatalog = new LumaCatalogService(http, upstreamManifest);
            var apiOverrideStore = new GraphicsApiOverrideStore();
            var optiScalerVariantStore = new OptiScalerVariantPreferenceStore();
            var neuralAddonVersionStore = new NeuralAddonVersionPreferenceStore();
            var settingsStore = new LinuxSettingsStore();
            UiLocalizationService.Apply(settingsStore.Load().UiLanguage);
            var lumaModeStore = new LumaModePreferenceStore();
            var lumaPolicy = new LumaPolicyService(upstreamManifest, lumaModeStore);
            var lumaFramework = new LumaFrameworkDeploymentService(http, records, catalog: lumaCatalog, policy: lumaPolicy, manifest: upstreamManifest, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
            var updateLog = new ComponentUpdateLogStore();
            var managedComponents = new LinuxManagedComponentService(
                records, reshade, addonManager, renodx, renodxUpgrade, optiscaler, relimiter, displayCommander,
                mfgAdaUnlock, dlssg2030, dlssEnablerStandalone, rtx40Mfg, dlss5Feeder, dlss5Bridge, d3dCompilerTools, ultimateAsiLoader, shortFuse, dofFix, reFramework, lumaFramework, lumaPolicy, neuralAddonVersionStore,
                updateLog);
            var updateAll = new LinuxUpdateAllService(records, managedComponents);
            var dlssDefaults = new LinuxDlssDefaultsService(dlss, records);
            var vm = new MainWindowViewModel(scanner, launcher, runtimeProbe, dlss, managedComponents, apiOverrideStore, optiScalerVariantStore, settingsStore, updateAll, dlssDefaults, libraryStore, records);
            desktop.MainWindow = new MainWindow { DataContext = vm, UpdateLogStore = updateLog };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
