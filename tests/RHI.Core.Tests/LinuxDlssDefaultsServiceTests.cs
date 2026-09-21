using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class LinuxDlssDefaultsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-defaults-" + Guid.NewGuid().ToString("N"));

    public LinuxDlssDefaultsServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task EmptyDefaults_DoNotScanDownloadOrMutate()
    {
        var game = Game("empty-defaults");
        var target = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(target, Bytes(0x11, 70_000));
        var handler = new GateHandler(Manifest(), PayloadZip());
        var service = NewService(handler);

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults());

        Assert.Empty(result.Items);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(target + ".original"));
        Assert.Equal(70_000L, new FileInfo(target).Length);
    }

    [Fact]
    public async Task QuickApply_ReplacesOnlyExistingSrTargetAndPreservesOriginal()
    {
        var game = Game("quick-sr");
        var target = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(target, Bytes(0x11, 70_000));
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));

        var result = await service.ApplyDefaultsAsync(
            game,
            new LinuxDlssVersionDefaults(SuperResolution: "9.9.9", RayReconstruction: "9.9.9"),
            [target]);

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(80_000L, new FileInfo(target).Length);
        Assert.Equal(70_000L, new FileInfo(target + ".original").Length);
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "nvngx_dlssd.dll")));
    }

    [Fact]
    public async Task QuickApply_AppliesAllFiveExistingVersionFamilies()
    {
        var game = Game("quick-all-five");
        var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        var rr = Path.Combine(game.InstallPath, "nvngx_dlssd.dll");
        var fg = Path.Combine(game.InstallPath, "nvngx_dlssg.dll");
        var nr = Path.Combine(game.InstallPath, "nvngx_dlssnr.dll");
        var sl = Path.Combine(game.InstallPath, "sl.common.dll");
        foreach (var path in new[] { sr, rr, fg, nr, sl })
            await File.WriteAllBytesAsync(path, Bytes(0x19, 70_000));

        var service = NewService(new GateHandler(Manifest(), PayloadZip()));
        var result = await service.ApplyDefaultsAsync(
            game,
            new LinuxDlssVersionDefaults("9.9.9", "9.9.9", "9.9.9", "9.9.9", "2.13.0"),
            [sr, rr, fg, nr, sl]);

        Assert.Equal(5, result.ChangedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.All(new[] { sr, rr, fg, nr, sl }, path => Assert.True(File.Exists(path + ".original")));
        Assert.Equal(80_000L, new FileInfo(sr).Length);
        Assert.Equal(80_500L, new FileInfo(rr).Length);
        Assert.Equal(81_000L, new FileInfo(fg).Length);
        Assert.Equal(82_000L, new FileInfo(nr).Length);
        Assert.Equal(83_000L, new FileInfo(sl).Length);
    }

    [Fact]
    public async Task QuickApply_StreamlineReplacesOnlyExistingKnownDlls()
    {
        var game = Game("quick-streamline");
        var common = Path.Combine(game.InstallPath, "sl.common.dll");
        await File.WriteAllBytesAsync(common, Bytes(0x22, 70_000));
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));

        var result = await service.ApplyDefaultsAsync(
            game,
            new LinuxDlssVersionDefaults(Streamline: "2.13.0"),
            [common]);

        Assert.Equal(1, result.ChangedCount);
        Assert.True(File.Exists(common + ".original"));
        Assert.Equal(83_000L, new FileInfo(common).Length);
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "sl.reflex.dll")));
    }

    [Fact]
    public async Task RestoreAll_ReturnsTrueOriginalsAndConsumesBackups()
    {
        var game = Game("restore");
        var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        var common = Path.Combine(game.InstallPath, "sl.common.dll");
        await File.WriteAllBytesAsync(sr, Bytes(0x11, 70_000));
        await File.WriteAllBytesAsync(common, Bytes(0x22, 71_000));
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));
        await service.ApplyDefaultsAsync(game,
            new LinuxDlssVersionDefaults(SuperResolution: "9.9.9", Streamline: "2.13.0"), [sr, common]);

        var restored = await service.RestoreAllAsync(game, [sr, common]);

        Assert.Equal(2, restored.ChangedCount);
        Assert.Equal(70_000L, new FileInfo(sr).Length);
        Assert.Equal(71_000L, new FileInfo(common).Length);
        Assert.False(File.Exists(sr + ".original"));
        Assert.False(File.Exists(common + ".original"));
    }

    [Fact]
    public async Task ShortFuseOwnership_BlocksDirectDefaultsBeforeNetwork()
    {
        var game = Game("shortfuse");
        var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(sr, Bytes(0x11, 70_000));
        var records = NewRecords();
        Record(records, game, ManagedComponentKind.ShortFuse);
        var handler = new GateHandler(Manifest(), PayloadZip());
        var service = NewService(handler, records);

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), [sr]);

        Assert.Equal(1, result.SkippedCount);
        Assert.Contains("ShortFuse", result.Items[0].Message);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(sr + ".original"));
    }

    [Fact]
    public async Task FeederFullOwnership_BlocksButAddonOnlyFeederDoesNot()
    {
        var blockedGame = Game("feeder-full");
        var blockedSr = Path.Combine(blockedGame.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(blockedSr, Bytes(0x11, 70_000));
        var records = NewRecords();
        Record(records, blockedGame, ManagedComponentKind.Dlss5Feeder,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Feeder.FullStack"] = "1" });
        var blockedHandler = new GateHandler(Manifest(), PayloadZip());
        var blocked = NewService(blockedHandler, records);

        var blockedResult = await blocked.ApplyDefaultsAsync(blockedGame,
            new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), [blockedSr]);
        Assert.Equal(1, blockedResult.SkippedCount);
        Assert.Contains("Feeder Full", blockedResult.Items[0].Message);
        Assert.Equal(0, blockedHandler.RequestCount);

        var addonGame = Game("feeder-addon-only");
        var addonSr = Path.Combine(addonGame.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(addonSr, Bytes(0x12, 70_000));
        Record(records, addonGame, ManagedComponentKind.Dlss5Feeder,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var addonHandler = new GateHandler(Manifest(), PayloadZip());
        var addonService = NewService(addonHandler, records);

        var addonResult = await addonService.ApplyDefaultsAsync(addonGame,
            new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), [addonSr]);
        Assert.Equal(1, addonResult.ChangedCount);
        Assert.True(File.Exists(addonSr + ".original"));
    }

    [Fact]
    public async Task NativeLinuxAndManifestSkip_AreRejectedWithoutNetwork()
    {
        var native = Game("native");
        native.Runner = "Native Linux";
        var skipped = Game("manifest-skip");
        skipped.DlssScanSkippedByManifest = true;
        var handler = new GateHandler(Manifest(), PayloadZip());
        var service = NewService(handler);

        var nativeResult = await service.ApplyDefaultsAsync(native, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"));
        var skippedResult = await service.ApplyDefaultsAsync(skipped, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"));

        Assert.Equal(1, nativeResult.SkippedCount);
        Assert.Equal(1, skippedResult.SkippedCount);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RunningGame_IsDeferredWithoutMutation()
    {
        var game = Game("running");
        var sr = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(sr, Bytes(0x11, 70_000));
        var handler = new GateHandler(Manifest(), PayloadZip());
        var service = NewService(handler, running: new FixedRunningProbe(true));

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), [sr]);

        Assert.Equal(1, result.DeferredCount);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(sr + ".original"));
    }

    [Fact]
    public async Task Batch_IsolatesIneligibleGameAndUpdatesEligibleGame()
    {
        var good = Game("batch-good");
        var goodSr = Path.Combine(good.InstallPath, "nvngx_dlss.dll");
        await File.WriteAllBytesAsync(goodSr, Bytes(0x11, 70_000));
        var missing = Game("batch-missing");
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [good.Name] = [goodSr],
            [missing.Name] = []
        };

        var result = await service.ApplyBatchAsync(
            [missing, good],
            new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"),
            game => indexes[game.Name]);

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(goodSr + ".original"));
    }

    [Fact]
    public void Inspect_DoesNotInventTargetsForEmptyGame()
    {
        var game = Game("no-targets");
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));

        var candidate = service.Inspect(game, []);

        Assert.False(candidate.Eligible);
        Assert.False(candidate.Detection.HasAny);
        Assert.Contains("will not invent", candidate.Reason);
    }

    [Fact]
    public void SettingsStore_RoundTripsFiveDlssStreamlineDefaults()
    {
        var store = new LinuxSettingsStore(
            Path.Combine(_root, "settings", "settings.json"),
            Path.Combine(_root, "settings-cache"),
            Path.Combine(_root, "settings-data"));
        store.Save(new LinuxAppSettings
        {
            DefaultDlssVersion = " 9.9.9 ",
            DefaultDlssdVersion = " 9.9.8 ",
            DefaultDlssgVersion = " 9.9.7 ",
            DefaultDlssnrVersion = " 9.9.6 ",
            DefaultStreamlineVersion = " 2.13.0 "
        });

        var loaded = store.Load();

        Assert.Equal("9.9.9", loaded.DefaultDlssVersion);
        Assert.Equal("9.9.8", loaded.DefaultDlssdVersion);
        Assert.Equal("9.9.7", loaded.DefaultDlssgVersion);
        Assert.Equal("9.9.6", loaded.DefaultDlssnrVersion);
        Assert.Equal("2.13.0", loaded.DefaultStreamlineVersion);
    }

    [Fact]
    public void TryGetDllVersion_JunkFileFailsClosed()
    {
        var path = Path.Combine(_root, "junk.dll");
        File.WriteAllText(path, "not a PE");
        Assert.Null(DlssManagerService.TryGetDllVersion(path));
        Assert.Null(DlssManagerService.TryGetDllVersion(Path.Combine(_root, "missing.dll")));
    }

    private LinuxDlssDefaultsService NewService(
        GateHandler handler,
        LinuxDeploymentRecordStore? records = null,
        IGameRunningProbe? running = null)
    {
        var dlss = new DlssManagerService(
            new HttpClient(handler),
            new TransactionalFileDeploymentService(),
            Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")));
        return new LinuxDlssDefaultsService(dlss, records ?? NewRecords(), running ?? new FixedRunningProbe(false));
    }

    private LinuxDeploymentRecordStore NewRecords()
        => new(Path.Combine(_root, "records-" + Guid.NewGuid().ToString("N") + ".json"));

    private DetectedGame Game(string name)
    {
        var install = Path.Combine(_root, name);
        Directory.CreateDirectory(install);
        return new DetectedGame
        {
            Name = name,
            InstallPath = install,
            BinaryDirectory = install,
            Source = "Test",
            Runner = "Wine"
        };
    }

    private static void Record(
        LinuxDeploymentRecordStore records,
        DetectedGame game,
        ManagedComponentKind component,
        Dictionary<string, string>? metadata = null)
        => records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = component,
            BinaryDirectory = game.BinaryDirectory!,
            Version = "test",
            Metadata = metadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
        });

    private static byte[] Manifest() => Encoding.UTF8.GetBytes("""
        {
          "dlss": [{ "version": "9.9.9", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/dlss.zip" }],
          "dlssd": [{ "version": "9.9.9", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/dlssd.zip" }],
          "dlssg": [{ "version": "9.9.9", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/dlssg.zip" }],
          "dlssnr": [{ "version": "9.9.9", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/dlssnr.zip" }],
          "streamline": [{ "version": "2.13.0", "url": "https://github.com/RankFTW/rhi-repo/releases/download/test/streamline.zip" }]
        }
        """);

    private static byte[] PayloadZip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "nvngx_dlss.dll", Bytes(0x81, 80_000));
            Add(zip, "nvngx_dlssd.dll", Bytes(0x82, 80_500));
            Add(zip, "nvngx_dlssg.dll", Bytes(0x83, 81_000));
            Add(zip, "nvngx_dlssnr.dll", Bytes(0x84, 82_000));
            Add(zip, "sl.common.dll", Bytes(0x85, 83_000));
            Add(zip, "sl.reflex.dll", Bytes(0x86, 83_500));
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] Bytes(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    private sealed class GateHandler(byte[] manifest, byte[] zip) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var bytes = request.RequestUri?.AbsoluteUri == DlssManagerService.ManifestUrl ? manifest : zip;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FixedRunningProbe(bool value) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => value;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
