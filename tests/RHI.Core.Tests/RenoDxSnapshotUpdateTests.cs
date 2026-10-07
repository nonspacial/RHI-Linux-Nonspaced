using System.Net;
using System.Net.Http.Headers;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #129/#107: RenoDX snapshots are rebuilt in place under the same URL and name, often at the same size.
public sealed class RenoDxSnapshotUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-snapshot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SnapshotHandler _handler = new();
    private readonly HttpClient _http;
    private readonly LinuxDeploymentRecordStore _store;
    private readonly ComponentUpdateLogStore _log;
    private readonly RenoDxDeploymentService _service;
    private readonly DetectedGame _game;
    private readonly string[] _files;
    private readonly string _cacheDir;

    public RenoDxSnapshotUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_handler);
        _store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        _log = new ComponentUpdateLogStore(Path.Combine(_root, "update-log.json"));
        _cacheDir = Path.Combine(_root, "cache");
        _service = new RenoDxDeploymentService(_http, _store, new RenoDxCatalogService(_http), _cacheDir);
        (_game, _files) = CreateUnityGame();
    }

    private string Deployed => Path.Combine(_game.BinaryDirectory!, "renodx-unityengine.addon64");

    [Fact]
    public async Task UnchangedSnapshot_ManualUpdateAll_DoesNotDownloadWriteOrLog()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        var writtenAt = File.GetLastWriteTimeUtc(Deployed);

        var result = await UpdateAll().RunAsync([_game], _ => _files, LinuxUpdateRunMode.Manual);

        Assert.Equal(1, result.CurrentCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(1, _handler.Gets);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(Deployed));
        Assert.Empty(_log.GetAll());
    }

    [Fact]
    public async Task RebuiltSnapshot_SameUrlNameAndSize_IsFlaggedAndUpdatedInBackground()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        _handler.Rebuild();

        var check = await _service.CheckUpdateAsync(_game, _files);
        var result = await UpdateAll().RunAsync([_game], _ => _files, LinuxUpdateRunMode.Background);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(2, _handler.Gets);
        Assert.Equal(_handler.Payload, await File.ReadAllBytesAsync(Deployed));
        Assert.False((await _service.CheckUpdateAsync(_game, _files)).UpdateAvailable);
    }

    [Fact]
    public async Task FailedHead_IsNoUpdateAndReportedAsCouldNotCheck()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        _handler.Rebuild();
        _handler.FailHead = true;

        var result = await UpdateAll().RunAsync([_game], _ => _files, LinuxUpdateRunMode.Manual);

        Assert.Equal(1, result.UncheckedCount);
        Assert.Equal(0, result.CurrentCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(1, _handler.Gets);
        Assert.Contains("could not be checked", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebuiltSnapshot_OverUserModifiedAddon_IsRefusedAndFileKept()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        var userBytes = await File.ReadAllBytesAsync(Deployed);
        userBytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(Deployed, userBytes);
        _handler.Rebuild();

        var result = await UpdateAll().RunAsync([_game], _ => _files, LinuxUpdateRunMode.Manual);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(Deployed));
    }

    [Fact]
    public async Task LegacyRecordWithoutStamp_JudgedByLastModifiedAgainstInstallTime()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        var record = _store.Find(_game, ManagedComponentKind.RenoDX)!;
        record.Metadata.Remove(RenoDxDeploymentService.RemoteStampMetadataKey);
        record.InstalledAtUtc = _handler.LastModified.UtcDateTime.AddHours(1);
        _store.Upsert(record);

        Assert.False((await _service.CheckUpdateAsync(_game, _files)).UpdateAvailable);
        _handler.Rebuild(lastModified: record.InstalledAtUtc.AddHours(1));
        Assert.True((await _service.CheckUpdateAsync(_game, _files)).UpdateAvailable);
    }

    [Fact]
    public async Task Cache_SameSizeRebuildOldSidecarOrOtherUrl_DownloadsAgain()
    {
        var sidecar = Path.Combine(_cacheDir, "renodx-unityengine.addon64.source");
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        Assert.Equal(1, _handler.Gets);

        _handler.Rebuild();
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        Assert.Equal(2, _handler.Gets);
        Assert.Equal(_handler.Payload, await File.ReadAllBytesAsync(Deployed));

        await File.WriteAllTextAsync(sidecar, RenoDxCatalogService.GenericUnityUrl64); // pre-#107 URL-only sidecar
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        Assert.Equal(3, _handler.Gets);

        await File.WriteAllTextAsync(sidecar, "https://github.com/other/repo/releases/download/snapshot/renodx-unityengine.addon64");
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        Assert.Equal(4, _handler.Gets);
    }

    [Fact]
    public async Task Cache_FailedHead_KeepsCachedAddon()
    {
        Assert.True((await _service.InstallAsync(_game, _files)).Success);
        _handler.FailHead = true;

        var reinstall = await _service.InstallAsync(_game, _files);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.Equal(1, _handler.Gets);
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LinuxUpdateAllService UpdateAll()
        => new(_store, new RenoDxBackend(_service, new ComponentUpdateRecorder(_store, _log)), new NeverRunning());

    private (DetectedGame Game, string[] Files) CreateUnityGame()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "OuterWilds.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        File.WriteAllText(Path.Combine(bin, "UnityPlayer.dll"), "MZ");
        var game = new DetectedGame
        {
            Name = "Outer Wilds", InstallPath = _root, BinaryDirectory = bin,
            ExecutablePath = exe, Runner = "Proton", SteamAppId = 753640
        };
        return (game, [exe, Path.Combine(bin, "ReShade64.dll"), Path.Combine(bin, "UnityPlayer.dll")]);
    }

    private sealed class RenoDxBackend(RenoDxDeploymentService service, ComponentUpdateRecorder history) : ILinuxManagedUpdateBackend
    {
        public Task<ManagedUpdateCheck> CheckManagedUpdateAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => service.CheckUpdateAsync(game, indexedFiles, cancellationToken);

        public Task<ComponentOperationResult> UpdateManagedAsync(DetectedGame game, ManagedComponentKind component, IReadOnlyList<string> indexedFiles, CancellationToken cancellationToken = default)
            => history.TrackAsync(game, component, ComponentUpdateAction.Update, () => service.UpdateAsync(game, indexedFiles, cancellationToken));
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    internal sealed class SnapshotHandler : HttpMessageHandler
    {
        private int _build;
        public byte[] Payload { get; private set; } = Build(0);
        public string ETag { get; private set; } = "\"0x8DF0000000000000\"";
        public DateTimeOffset LastModified { get; private set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public bool FailHead { get; set; }
        public int Gets { get; private set; }

        // Same URL, same size, new bytes: what a RenoDX CI snapshot rebuild looks like.
        public void Rebuild(DateTimeOffset? lastModified = null)
        {
            _build++;
            Payload = Build(_build);
            ETag = $"\"0x8DF000000000000{_build}\"";
            LastModified = lastModified ?? LastModified.AddDays(1);
        }

        private static byte[] Build(int build)
        {
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            bytes[^1] = (byte)build;
            return bytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsoluteUri.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head && FailHead)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.Method == HttpMethod.Get) Gets++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
            response.Content.Headers.LastModified = LastModified;
            return Task.FromResult(response);
        }
    }
}
