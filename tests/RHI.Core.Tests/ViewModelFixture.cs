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
        IGameLauncher? launcher, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles, IGameRuntimeProbe? probe
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        )
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Settings = new LinuxSettingsStore(Path.Combine(ConfigDir, "settings.json"), Path.Combine(Root, "cache"), DataDir);
        Settings.Save(new LinuxAppSettings { UiLanguage = uiLanguage, AutoUpdateComponents = false });
        Library = new LinuxGameLibraryStore(Path.Combine(DataDir, "game-library.json"));
        seed?.Invoke(Library);
        Records = new LinuxDeploymentRecordStore(Path.Combine(DataDir, "deployments.json"));
        Vm = Build(games, Settings, Library, Records, Path.Combine(Root, "cache"), managed => Managed = managed, httpFailure, httpResponse, _hang,
            launcher, hdrProfiles, probe
            // w3/sync rows
            // w3/journal rows
            // w3/scan rows
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
            );
        // w3/scan Settings row (#90): the fixture's own "PartingX / Xbox folders" list (w3 integration).
        Vm.Settings.UseXboxFolders(new RHI.Platform.Linux.Detection.Xbox.XboxFolderStore(Path.Combine(ConfigDir, "xbox-folders.json")));
    }

    /// <param name="seed">Writes the library store before the view model loads it (e.g. a saved manual entry).</param>
    /// <param name="httpFailure">What every request throws (default: an "offline test" <see cref="HttpRequestException"/>).</param>
    /// <param name="httpResponse">A response for chosen requests (null: the request fails as <paramref name="httpFailure"/> says).</param>
    /// <param name="launcher">The game launcher (default: the real <see cref="LinuxGameLauncher"/>).</param>
    /// <param name="hdrProfiles">The HDR profile service (default: one reading this host's session and PATH).</param>
    /// <param name="probe">The runtime probe (default: one that changes nothing).</param>
    public static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, string uiLanguage = "en-US",
        Action<LinuxGameLibraryStore>? seed = null, Func<HttpRequestMessage, Exception>? httpFailure = null,
        Func<HttpRequestMessage, HttpResponseMessage?>? httpResponse = null,
        IGameLauncher? launcher = null, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles = null,
        IGameRuntimeProbe? probe = null
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        )
    {
        var fixture = new ViewModelFixture(games, uiLanguage, seed, httpFailure, httpResponse, launcher, hdrProfiles, probe
            // w3/sync rows
            // w3/journal rows
            // w3/scan rows
            // w3/parity rows
            // w3/vmsplit rows
            // w3/dllnames rows
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
        IGameLauncher? launcher, RHI.Platform.Linux.Hdr.LinuxHdrLaunchProfileService? hdrProfiles, IGameRuntimeProbe? probe
        // w3/sync rows
        // w3/journal rows
        // w3/scan rows
        // w3/parity rows
        // w3/vmsplit rows
        // w3/dllnames rows
        )
    {
        var http = new HttpClient(new OfflineHandler(httpFailure, httpResponse, hang));
        var upstreamManifest = new UpstreamRhiManifestService(http);
        var scanner = new GameLibraryScanner(new IGameDetector[] { new FixedDetector(games), new ManualGameDetector(libraryStore) });
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
        var managed = new LinuxManagedComponentService(
            records, reshade, new ReShadeAddonManagerService(http, upstreamManifest), renodx, new RenoDxUpgradeDeploymentService(http, records, upstreamManifest),
            optiscaler, relimiter, displayCommander, mfgAdaUnlock, dlssg2030, dlssEnabler, rtx40Mfg, feeder, dlss5Bridge,
            new D3DCompilerToolInstaller(wineOverrides), ual, shortFuse, new DofFixDeploymentService(http, records, upstreamManifest),
            new REFrameworkDeploymentService(http, records, wineOverrides), luma, lumaPolicy,
            new NeuralAddonVersionPreferenceStore(Path.Combine(prefs, "neural.json")), new ComponentUpdateLogStore(Path.Combine(prefs, "update-log.json")));
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
