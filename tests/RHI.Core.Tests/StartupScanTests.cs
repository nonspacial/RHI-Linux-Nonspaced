using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>Start-up: the library scan never waits behind the DLSS manifest (Refs #43).</summary>
public sealed class StartupScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-startup-" + Guid.NewGuid().ToString("N"));

    public StartupScanTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task LibraryScanStarts_WhileTheDlssManifestRequestHangs()
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dlss = new DlssManagerService(new HttpClient(new HangingHandler()), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache"));
        var settings = new LinuxSettingsStore(Path.Combine(_root, "settings.json"), Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        var current = settings.Load();
        current.AutoUpdateComponents = false;
        settings.Save(current);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

        var timer = Stopwatch.StartNew();
        _ = new MainWindowViewModel(
            new GameLibraryScanner([new SignallingDetector(scanStarted)]),
            new LinuxGameLauncher(),
            new NoProbe(),
            dlss,
            managed,
            new GraphicsApiOverrideStore(Path.Combine(_root, "api.json")),
            new OptiScalerVariantPreferenceStore(Path.Combine(_root, "opti.json")),
            settings,
            new LinuxUpdateAllService(records, managed),
            new LinuxDlssDefaultsService(dlss, records),
            new LinuxGameLibraryStore(Path.Combine(_root, "library.json")),
            records);

        await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(timer.ElapsedMilliseconds < 2000, $"scan started after {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task HangingManifestRequest_FallsBackToTheCachedCopyWithinItsDeadline()
    {
        var cache = Directory.CreateDirectory(Path.Combine(_root, "dlss-cache")).FullName;
        await File.WriteAllTextAsync(Path.Combine(cache, "dlss_manifest.json"), """
            { "dlss": [ { "version": "310.2.1", "url": "https://github.com/example/dlss/releases/download/v1/dlss.zip" } ],
              "dlssd": [], "dlssg": [], "dlssnr": [], "streamline": [] }
            """);
        var service = new DlssManagerService(new HttpClient(new HangingHandler()), new TransactionalFileDeploymentService(), cache)
        {
            ManifestFetchTimeout = TimeSpan.FromMilliseconds(200)
        };

        var timer = Stopwatch.StartNew();
        var manifest = await service.FetchManifestAsync();

        Assert.True(timer.ElapsedMilliseconds < 3000, $"took {timer.ElapsedMilliseconds} ms");
        Assert.Equal("310.2.1", Assert.Single(manifest.Dlss).Version);
    }

    [Fact]
    public async Task HangingManifestRequest_WithoutACache_FailsWithATimeout()
    {
        var service = new DlssManagerService(new HttpClient(new HangingHandler()), new TransactionalFileDeploymentService(), Path.Combine(_root, "empty"))
        {
            ManifestFetchTimeout = TimeSpan.FromMilliseconds(200)
        };

        await Assert.ThrowsAsync<TimeoutException>(() => service.FetchManifestAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class SignallingDetector(TaskCompletionSource started) : IGameDetector
    {
        public string Name => "Manual";
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return Task.FromResult<IReadOnlyList<DetectedGame>>([]);
        }
    }

    private sealed class NoProbe : IGameRuntimeProbe
    {
        public Task ProbeAsync(DetectedGame game, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
