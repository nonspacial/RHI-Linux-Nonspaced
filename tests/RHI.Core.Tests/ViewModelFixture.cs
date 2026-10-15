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
    public LinuxManagedComponentService Managed { get; private set; } = null!;

    /// <summary>Requests this matches never answer until cancelled (a stalled download; Refs #77).</summary>
    public Func<HttpRequestMessage, bool>? HangWhen { get => _hang.When; set => _hang.When = value; }

    private readonly HttpHang _hang = new();

    private ViewModelFixture(IReadOnlyList<DetectedGame> games, string uiLanguage, Action<LinuxGameLibraryStore>? seed,
        Func<HttpRequestMessage, Exception>? httpFailure, Func<HttpRequestMessage, HttpResponseMessage?>? httpResponse,
        IGameLauncher? launcher, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles, IGameRuntimeProbe? probe,
        IReadOnlyList<IGameDetector>? detectors
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        // w6/crash rows
        , bool cacheAllShaders = false
        )
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Settings = new LinuxSettingsStore(Path.Combine(ConfigDir, "settings.json"), Path.Combine(Root, "cache"), DataDir);
        // #194: no start-up shader-pack prefetch unless a test asks for it (it would add requests to every VM test).
        Settings.Save(new LinuxAppSettings { UiLanguage = uiLanguage, AutoUpdateComponents = false, CacheAllShaders = cacheAllShaders });
        Library = new LinuxGameLibraryStore(Path.Combine(DataDir, "game-library.json"));
        seed?.Invoke(Library);
        Records = new LinuxDeploymentRecordStore(Path.Combine(DataDir, "deployments.json"));
        Vm = Build(games, Settings, Library, Records, Path.Combine(Root, "cache"), managed => Managed = managed, httpFailure, httpResponse, _hang,
            launcher, hdrProfiles, probe, detectors
            // w3/sync rows
            // w3/journal rows
            // w3/scan rows
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
            // w5/update-policy rows
            , out var updatePolicy
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            );
        // w3/scan Settings row (#90): the fixture's own "Xbox / Game Pass folders" list (w3 integration).
        Vm.Settings.UseXboxFolders(new RHI.Platform.Linux.Detection.Xbox.XboxFolderStore(Path.Combine(ConfigDir, "xbox-folders.json")));
        // w5 lane rows: hand the view model what the fixture builds
        // w5/update-policy rows
        Vm.UpdatePolicy = updatePolicy; // #177
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
    }

    /// <param name="seed">Writes the library store before the view model loads it (e.g. a saved manual entry).</param>
    /// <param name="httpFailure">What every request throws (default: an "offline test" <see cref="HttpRequestException"/>).</param>
    /// <param name="httpResponse">A response for chosen requests (null: the request fails as <paramref name="httpFailure"/> says).</param>
    /// <param name="launcher">The game launcher (default: the real <see cref="LinuxGameLauncher"/>).</param>
    /// <param name="hdrProfiles">The HDR profile service (default: one reading this host's session and PATH).</param>
    /// <param name="probe">The runtime probe (default: one that changes nothing).</param>
    /// <param name="detectors">Extra detectors the scanner runs beside the fixture's own (e.g. one counting its scans).</param>
    public static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, string uiLanguage = "en-US",
        Action<LinuxGameLibraryStore>? seed = null, Func<HttpRequestMessage, Exception>? httpFailure = null,
        Func<HttpRequestMessage, HttpResponseMessage?>? httpResponse = null,
        IGameLauncher? launcher = null, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles = null,
        IGameRuntimeProbe? probe = null, IReadOnlyList<IGameDetector>? detectors = null
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        // w5/update-policy rows
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        // w6/crash rows
        , bool cacheAllShaders = false
        )
    {
        var fixture = new ViewModelFixture(games, uiLanguage, seed, httpFailure, httpResponse, launcher, hdrProfiles, probe, detectors
            // w3/sync rows
            // w3/journal rows
            // w3/scan rows
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
            // w5/update-policy rows
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            // w6/crash rows
            , cacheAllShaders
            );
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
        // Detaches the view model from process-wide hooks (store quarantine notices) before its files go.
        Vm.Dispose();
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Prepend(Root))
            try { if (OperatingSystem.IsLinux()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* best effort before delete */ }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MainWindowViewModel Build(IReadOnlyList<DetectedGame> games, LinuxSettingsStore settingsStore,
        LinuxGameLibraryStore libraryStore, LinuxDeploymentRecordStore records, string cacheDir, Action<LinuxManagedComponentService> built,
        Func<HttpRequestMessage, Exception>? httpFailure, Func<HttpRequestMessage, HttpResponseMessage?>? httpResponse, HttpHang hang,
        IGameLauncher? launcher, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles, IGameRuntimeProbe? probe,
        IReadOnlyList<IGameDetector>? detectors
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        // w5/update-policy rows
        , out UpdatePolicyFeatures updatePolicy
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        )
    {
        var http = new HttpClient(new OfflineHandler(httpFailure, httpResponse, hang));
        var upstreamManifest = new UpstreamRhiManifestService(http);
        var scanner = new GameLibraryScanner(new IGameDetector[] { new FixedDetector(games), new ManualGameDetector(libraryStore) }.Concat(detectors ?? []).ToArray());
        var deployment = new TransactionalFileDeploymentService();
        var wineOverrides = new WineDllOverrideService();
        var reshade = new ReShadeDeploymentService(http, deployment, records, wineOverrides, new ReShadeArchiveExtractor(), upstreamManifest: upstreamManifest,
            dllNames: new DllNamePreferenceStore(Path.Combine(Path.GetDirectoryName(cacheDir)!, "config", "dll-names.json"))); // w3/dllnames: per fixture
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
            ManagedComponentKind.MfgAdaUnlock, "mfgunlock", "MFG Ada Unlock", "https://api.github.com/repos/mavismmg/MFGAdaUnlock-RenoDx/releases?per_page=1",
            128 * 1024, ExactAssetName64: "renodx-mfgunlock.addon64", DeployFileName64: "renodx-mfgunlock.addon64", X64Only: true));
        var prefs = Path.Combine(Path.GetDirectoryName(settingsStore.SettingsPath)!, "prefs");
        var dlssg2030 = new Dlssg2030UnlockDeploymentService(http, records, wineOverrides, new Dlssg2030PreferenceStore(Path.Combine(prefs, "dlssg2030.json")));
        var dlssEnabler = new StandaloneDlssEnablerDeploymentService(http, records, wineOverrides, new StandaloneDlssEnablerPreferenceStore(Path.Combine(prefs, "dlss-enabler.json")));
        var rtx40Mfg = new Rtx40MfgStandaloneDeploymentService(http, records, wineOverrides, new Rtx40MfgPreferenceStore(Path.Combine(prefs, "rtx40mfg.json")));
        var dlss5Bridge = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Dx11Bridge, "dlss5-dx11-bridge", "DLSS5 DX11 Bridge", "https://api.github.com/repos/NIGos/dlss5-bridge/releases?per_page=1",
            128 * 1024, ExactAssetName64: "dlss5-bridge.addon64", DeployFileName64: "dlss5-bridge.addon64", X64Only: true));
        var feederAddon = new UpstreamRhiAddonDeploymentService(http, records, upstreamManifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1",
            64 * 1024, AssetNamePattern64: @"^DLSS5-Feeder-.*\.zip$", AssetNamePattern32: @"^DLSS5-Feeder-.*\.zip$",
            DeployFileName64: "dlss5-feed.addon64", DeployFileName32: "dlss5-feed.addon32"));
        var dgVoodoo = new DgVoodooDeploymentService(http);
        var dx9ReShade = new Dx9TranslationReShadeCoordinator(records, wineOverrides);
        var feeder = new Dlss5FeederFullDeploymentService(http, records, dlss, feederAddon, overrides: wineOverrides, reShade: reshade, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
        var optiscaler = new OptiScalerDeploymentService(deployment, records, wineOverrides, new OptiScalerPackageProvider(http, new OptiScalerArchiveExtractor()), dlss, dlssEnabler,
            optiPatcher: new OptiPatcherProvider(http, Path.Combine(cacheDir, "optipatcher"))); // w3/dllnames hook (#98)
        optiscaler.IniTemplates = new OptiScalerIniTemplates(Path.Combine(settingsStore.ConfigRoot, "inis"), // w3/dllnames hook (#97)
            () => OptiScalerIniTemplates.ChoiceFrom(settingsStore.Load()));
        var ual = new UltimateAsiLoaderDeploymentService(http, records, wineOverrides);
        var shortFuse = new ShortFuseDeploymentService(http, deployment, records, wineOverrides, ual, dlss);
        var lumaCatalog = new LumaCatalogService(http, upstreamManifest);
        var lumaPolicy = new LumaPolicyService(upstreamManifest, new LumaModePreferenceStore(Path.Combine(prefs, "luma.json")));
        var luma = new LumaFrameworkDeploymentService(http, records, catalog: lumaCatalog, policy: lumaPolicy, manifest: upstreamManifest, dgVoodoo: dgVoodoo, dx9ReShade: dx9ReShade);
        // w5 lane rows: services LinuxManagedComponentService takes (named args below)
        // w5/update-policy rows
        var updateInclusion = new UpdateInclusionStore(Path.Combine(prefs, "update-inclusion.json"),
            () => UpdateInclusionStore.GlobalSkipsFrom(settingsStore.Load()), () => DlssFollowChoice.From(settingsStore.Load())); // #178
        var dlssFollowStore = new DlssAutoFollowStore(Path.Combine(prefs, "dlss-follow.json"));
        updatePolicy = new UpdatePolicyFeatures(new DlssAutoFollowService(dlss, new LinuxDlssDefaultsService(dlss, records), records,
            dlssFollowStore, new ComponentUpdateLogStore(Path.Combine(prefs, "update-log.json")), // #177: the components' history file
            updateInclusion: updateInclusion), updateInclusion);
        // w5/catalogue-info rows
        // w5/ini-presets rows
        // w5/shell-launch rows
        // w5/reshade-core rows
        var managed = new LinuxManagedComponentService(
            records, reshade, new ReShadeAddonManagerService(http, upstreamManifest, Path.Combine(cacheDir, "addons"), Path.Combine(prefs, "custom-addons"), Path.Combine(prefs, "addon-manager.json"), records), renodx, new RenoDxUpgradeDeploymentService(http, records, upstreamManifest),
            optiscaler, relimiter, displayCommander, mfgAdaUnlock, dlssg2030, dlssEnabler, rtx40Mfg, feeder, dlss5Bridge,
            new D3DCompilerToolInstaller(wineOverrides), ual, shortFuse, new DofFixDeploymentService(http, records, upstreamManifest),
            new REFrameworkDeploymentService(http, records, wineOverrides), luma, lumaPolicy,
            new NeuralAddonVersionPreferenceStore(Path.Combine(prefs, "neural.json")), new ComponentUpdateLogStore(Path.Combine(prefs, "update-log.json")),
            rtxEncore: new RtxEncoreDeploymentService(http, records, wineOverrides, reshade.DllNames, dlss, Path.Combine(cacheDir, "rtx-encore")) // w4/encore (#158)
            // w5 lane rows: named arguments, each line starting with a comma
            // w5/update-policy rows
            , updateInclusion: updateInclusion
            , dlssFollow: dlssFollowStore
            // w5/catalogue-info rows
            // w5/ini-presets rows
            // w5/shell-launch rows
            // w5/reshade-core rows
            );
        built(managed);
        return new MainWindowViewModel(scanner, launcher ?? new LinuxGameLauncher(), probe ?? new NoProbe(), dlss, managed,
            new GraphicsApiOverrideStore(Path.Combine(prefs, "api.json")), new OptiScalerVariantPreferenceStore(Path.Combine(prefs, "opti.json")),
            settingsStore, new LinuxUpdateAllService(records, managed), new LinuxDlssDefaultsService(dlss, records), libraryStore, records, hdrProfiles);
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

    private sealed class HttpHang
    {
        public Func<HttpRequestMessage, bool>? When;
    }

    private sealed class OfflineHandler(Func<HttpRequestMessage, Exception>? failure, Func<HttpRequestMessage, HttpResponseMessage?>? response,
        HttpHang hang) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (hang.When?.Invoke(request) == true) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (response?.Invoke(request) is { } answer) return answer;
            throw failure?.Invoke(request) ?? new HttpRequestException("offline test");
        }
    }
}
