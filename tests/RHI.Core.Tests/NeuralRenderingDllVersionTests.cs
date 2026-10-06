using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>Upstream RHI 2.7.6: NR DLL version per game, used at install and swapped in place while installed.</summary>
public sealed class NeuralRenderingDllVersionTests : IDisposable
{
    private const string OldNrUrl = "https://github.com/example/rhi/releases/download/1/nr-old.zip";
    private const string NewNrUrl = "https://github.com/example/rhi/releases/download/1/nr-new.zip";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-dll-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingDllVersionTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void PinStore_KeepsManifestLabelSeparateFromAddonPins()
    {
        var store = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "pins.json"));
        var game = Game(_root);
        store.Set(game, ManagedComponentKind.ShortFuse, "renodx-dlss-SF-1.0");

        store.SetNeuralRenderingDllVersion(game, "310.8.2 (20/30/40/50)");

        Assert.Equal("310.8.2 (20/30/40/50)", new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "pins.json")).GetNeuralRenderingDllVersion(game));
        Assert.Equal("renodx-dlss-SF-1.0", store.Get(game, ManagedComponentKind.ShortFuse));
        store.SetNeuralRenderingDllVersion(game, null);
        Assert.Null(store.GetNeuralRenderingDllVersion(game));
    }

    [Fact]
    public async Task EnsureCached_UsesRequestedVersionAndFallsBackToNewest()
    {
        using var http = new HttpClient(Handler());
        var dlss = new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));

        Assert.Equal(Pe(0x21), File.ReadAllBytes((await dlss.EnsureCachedAsync(DlssComponentKind.NeuralRendering, "310.1"))!));
        Assert.Equal(Pe(0x22), File.ReadAllBytes((await dlss.EnsureCachedAsync(DlssComponentKind.NeuralRendering, "gone"))!));
        Assert.Equal("310.2", (await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, "gone"))!.Value.Version);
        Assert.Equal("310.1", (await dlss.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, "310.1"))!.Value.Version);
    }

    [Fact]
    public async Task ShortFuseNrDllSwap_ReplacesOnlyTheNrDllAndRecordsVersion()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var game = Game(bin);
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        var addon = Path.Combine(bin, ShortFuseDeploymentService.AddonFileName);
        File.WriteAllBytes(nr, Pe(0x22));
        File.WriteAllBytes(addon, Pe(0x30));
        var addonHash = Sha256(addon);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = bin, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = nr, Sha256 = Sha256(nr) }, new LinuxDeployedFile { Path = addon, Sha256 = addonHash }]
        });
        using var http = new HttpClient(Handler());
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var sf = new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, "sf.json")));

        var result = await sf.SwapNeuralRenderingDllAsync(game, "310.1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Pe(0x21), File.ReadAllBytes(nr));
        Assert.Equal(addonHash, Sha256(addon));
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal(Sha256(nr), ManagedFileSwap.FindFile(record, "nvngx_dlssnr.dll")!.Sha256);
        Assert.Equal("310.1", record.Metadata["NeuralRendering.DllVersion"]);
        Assert.Equal("1.0", record.Version);
    }

    [Fact]
    public async Task NrDllSwap_UnpublishedPinReportsAndRecordsTheVersionActuallyInstalled()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "fallback")).FullName;
        var game = Game(bin);
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Pe(0x21));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "fallback-deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = bin, Version = "1.0", Files = [new LinuxDeployedFile { Path = nr, Sha256 = Sha256(nr) }]
        });
        using var http = new HttpClient(Handler());
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var sf = new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, "sf.json")));

        var result = await sf.SwapNeuralRenderingDllAsync(game, "309.9");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Pe(0x22), File.ReadAllBytes(nr));
        Assert.Contains("309.9 is no longer published; using 310.2", result.Message);
        Assert.Contains("swapped in place to 310.2", result.Message);
        Assert.Equal("310.2", records.Find(game, ManagedComponentKind.ShortFuse)!.Metadata["NeuralRendering.DllVersion"]);
    }

    [Fact]
    public async Task ManagedService_NrDllSwap_LogsOldToNewVersion_AndFailedSwapAsFailure()
    {
        // Ticket #112: the UI-triggered NR DLL swap goes through the update-history recorder.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "history")).FullName;
        var game = Game(bin);
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Pe(0x21));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "history-deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = bin, Version = "1.0", Files = [new LinuxDeployedFile { Path = nr, Sha256 = Sha256(nr) }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["NeuralRendering.DllVersion"] = "310.1" }
        });
        using var http = new HttpClient(Handler());
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var sf = new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, "sf.json")));
        var pins = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "history-pins.json"));
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "update-log.json"));
        // Only the services the NR DLL swap reaches are real; the rest are never touched on this path.
        var service = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: sf, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            neuralVersionPins: pins, updateLog: log);

        pins.SetNeuralRenderingDllVersion(game, "310.2");
        var swapped = await service.SwapNeuralRenderingDllAsync(game);

        Assert.True(swapped.Success, swapped.Message);
        var entry = Assert.Single(log.GetAll());
        Assert.Equal((ManagedComponentKind.ShortFuse, ComponentUpdateAction.Update, "310.1", "310.2", true),
            (entry.Component, entry.Action, entry.FromVersion, entry.ToVersion, entry.Success));
        Assert.Contains("nvngx_dlssnr.dll", entry.ComponentName);

        File.Delete(nr); // the swap now refuses ("Managed file is missing")
        pins.SetNeuralRenderingDllVersion(game, "310.1");
        var refused = await service.SwapNeuralRenderingDllAsync(game);

        Assert.False(refused.Success);
        Assert.Equal(2, log.GetAll().Count);
        var failed = log.GetAll()[0];
        Assert.Equal((false, ManagedComponentKind.ShortFuse, "310.2", (string?)null), (failed.Success, failed.Component, failed.FromVersion, failed.ToVersion));
        Assert.Equal(refused.Message, failed.Message);
    }

    private static DetectedGame Game(string bin) => new()
    {
        Name = "NR DLL test", InstallPath = bin, Source = "Test", Runner = "Wine", BinaryDirectory = bin,
        ExecutablePath = Path.Combine(bin, "Game.exe")
    };

    private static HttpMessageHandler Handler()
    {
        var manifest = $$"""
        {"dlss":[],"dlssd":[],"dlssg":[],"dlssnr":[{"version":"310.2","url":"{{NewNrUrl}}"},{"version":"310.1","url":"{{OldNrUrl}}"}],"streamline":[]}
        """;
        return new MapHandler(new()
        {
            [DlssManagerService.ManifestUrl] = Encoding.UTF8.GetBytes(manifest),
            [OldNrUrl] = Zip("nvngx_dlssnr.dll", Pe(0x21)),
            [NewNrUrl] = Zip("nvngx_dlssnr.dll", Pe(0x22))
        });
    }

    private static byte[] Pe(byte marker)
    {
        var bytes = Enumerable.Range(0, 80 * 1024).Select(i => (byte)(marker + i * 13)).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] Zip(string name, byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(name).Open())
            stream.Write(bytes);
        return ms.ToArray();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class MapHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
