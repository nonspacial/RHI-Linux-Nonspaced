using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Network;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// An offline, fully wired <see cref="MainWindowViewModel"/> over a temp root: every network call fails at once, the
/// detector returns the given games, and the settings/library/record stores live under <see cref="Root"/>.
/// </summary>
internal sealed class ViewModelFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"rhi-vm-{Guid.NewGuid():N}");
    public string ConfigDir => Path.Combine(Root, "config");
    public string DataDir => Path.Combine(Root, "data");
    public LinuxSettingsStore Settings { get; }
    public LinuxGameLibraryStore Library { get; }
    public LinuxDeploymentRecordStore Records { get; }
    public MainWindowViewModel Vm { get; }

    private ViewModelFixture(IReadOnlyList<DetectedGame> games, string uiLanguage)
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Settings = new LinuxSettingsStore(Path.Combine(ConfigDir, "settings.json"), Path.Combine(Root, "cache"), DataDir);
        Settings.Save(new LinuxAppSettings { UiLanguage = uiLanguage, AutoUpdateComponents = false });
        Library = new LinuxGameLibraryStore(Path.Combine(DataDir, "game-library.json"));
        Records = new LinuxDeploymentRecordStore(Path.Combine(DataDir, "deployments.json"));
        Vm = Build(games, Settings, Library, Records, Path.Combine(Root, "cache"));
    }

    public static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, string uiLanguage = "en-US")
    {
        var fixture = new ViewModelFixture(games, uiLanguage);
        await fixture.Vm.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
        return fixture;
    }

    /// <summary>Creates a Windows game folder (MZ stub exe) under the fixture root.</summary>
    public DetectedGame Game(string name)
    {
        var dir = Path.Combine(Root, "games", name);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, Source = "Manual", Runner = "Manual Windows" };
    }

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Prepend(Root))
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* best effort before delete */ }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MainWindowViewModel Build(IReadOnlyList<DetectedGame> games, LinuxSettingsStore settingsStore,
        LinuxGameLibraryStore libraryStore, LinuxDeploymentRecordStore records, string cacheDir)
    {
        var http = new HttpClient(new OfflineHandler());
        var upstreamManifest = new UpstreamRhiManifestService(http);
        var scanner = new GameLibraryScanner(new IGameDetector[] { new FixedDetector(games), new ManualGameDetector(libraryStore) });
        var deployment = new TransactionalFileDeploymentService();
        var wineOverrides = new WineDllOverrideService();
        var reshade = new ReShadeDeploymentService(http, deployment, records, wineOverrides, new ReShadeArchiveExtractor(), upstreamManifest: upstreamManifest);
        var renodxCatalog = new RenoDxCatalogService(http, upstreamManifest);
        var dlss = new DlssManagerService(http, deployment, cacheDir);
        var renodx = new RenoDxDeploymentService(http, records, renodxCatalog, upstreamManifest: upstreamManifest,
            unityDatabase: new RenoDxUnityDatabaseService(http), controlUe: new ControlUePostInstallService(deployment, dlss));
        var relimiter = new GitHubReShadeAddonDeploymentService(http, records, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.ReLimiter, "ReLimiter", "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
            "relimiter.addon64", "relimiter.addon32", 100 * 1024, ManagedComponentKind.DisplayCommander));
        var displayCommander = new GitHubReShadeAddonDeploymentService(http, records, new GitHubReShadeAddonDefinition(
            ManagedComponentKind.DisplayCommander, "Display Commander", "https://api.github.com/repos/pmnoxx/display-commander/releases/tags/latest",
            "zzz_display_commander.addon64", "zzz_display_commander.addon32", 500 * 1024, ManagedComponentKind.ReLimiter));
        var mfgAdaUnlock = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", "https://api.github.com/repos/mavismmg/MFGAdaUnlock-RenoDx/releases/latest",
            128 * 1024, ExactAssetName64: "renodx-mfgunlock.addon64", DeployFileName64: "renodx-mfgunlock.addon64", X64Only: true));
        var prefs = Path.Combine(Path.GetDirectoryName(settingsStore.SettingsPath)!, "prefs");
        var dlssg2030 = new Dlssg2030UnlockDeploymentService(http, records, wineOverrides, new Dlssg2030PreferenceStore(Path.Combine(prefs, "dlssg2030.json")));
        var dlssEnabler = new StandaloneDlssEnablerDeploymentService(http, records, wineOverrides, new StandaloneDlssEnablerPreferenceStore(Path.Combine(prefs, "dlss-enabler.json")));
        var rtx40Mfg = new Rtx40MfgStandaloneDeploymentService(http, records, wineOverrides, new Rtx40MfgPreferenceStore(Path.Combine(prefs, "rtx40mfg.json")));
        var dlss5Bridge = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Dx11Bridge, "dlss5-dx11-bridge", "DLSS5 DX11 Bridge", "https://api.github.com/repos/NIGos/dlss5-bridge/releases/latest",
            128 * 1024, ExactAssetName64: "dlss5-bridge.addon64", DeployFileName64: "dlss5-bridge.addon64", X64Only: true));
        var feederAddon = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/latest",
            64 * 1024, AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$", AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
            DeployFileName64: "dlss5-feed.addon64", DeployFileName32: "dlss5-feed.addon32"));
        var dgVoodoo = new DgVoodooDeploymentService(http);
        var dx9ReShade = new Dx9TranslationReShadeCoordinator(records, wineOverrides);
        var feeder = new Dlss5FeederFullDeploymentService(http, records, dlss, feederAddon, overrides: wineOverrides, reShade: reshade, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
        var optiscaler = new OptiScalerDeploymentService(deployment, records, wineOverrides, new OptiScalerPackageProvider(http, new OptiScalerArchiveExtractor()), dlss, dlssEnabler);
        var ual = new UltimateAsiLoaderDeploymentService(http, records, wineOverrides);
        var shortFuse = new ShortFuseDeploymentService(http, deployment, records, wineOverrides, ual, dlss);
        var lumaCatalog = new LumaCatalogService(http, upstreamManifest);
        var lumaPolicy = new LumaPolicyService(upstreamManifest, new LumaModePreferenceStore(Path.Combine(prefs, "luma.json")));
        var luma = new LumaFrameworkDeploymentService(http, records, catalog: lumaCatalog, policy: lumaPolicy, manifest: upstreamManifest, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
        var managed = new LinuxManagedComponentService(
            records, reshade, new ReShadeAddonManagerService(http, upstreamManifest), renodx, new RenoDxUpgradeDeploymentService(http, records, upstreamManifest),
            optiscaler, relimiter, displayCommander, mfgAdaUnlock, dlssg2030, dlssEnabler, rtx40Mfg, feeder, dlss5Bridge,
            new D3DCompilerToolInstaller(wineOverrides), ual, shortFuse, new DofFixDeploymentService(http, records, upstreamManifest),
            new REFrameworkDeploymentService(http, records, wineOverrides), luma, lumaPolicy,
            new NeuralAddonVersionPreferenceStore(Path.Combine(prefs, "neural.json")), new ComponentUpdateLogStore(Path.Combine(prefs, "update-log.json")));
        return new MainWindowViewModel(scanner, new LinuxGameLauncher(), new NoProbe(), dlss, managed,
            new GraphicsApiOverrideStore(Path.Combine(prefs, "api.json")), new OptiScalerVariantPreferenceStore(Path.Combine(prefs, "opti.json")),
            settingsStore, new LinuxUpdateAllService(records, managed), new LinuxDlssDefaultsService(dlss, records), libraryStore, records);
    }

    private sealed class FixedDetector(IReadOnlyList<DetectedGame> games) : IGameDetector
    {
        public string Name => "Fixture";
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games.Select(g => g.ShallowClone()).ToList());
    }

    private sealed class NoProbe : IGameRuntimeProbe
    {
        public Task ProbeAsync(DetectedGame game, IReadOnlyList<string> files, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }
}
