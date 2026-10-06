using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>Upstream RHI 2.7.9 NR fixes: rc sort order, Update All coverage and in-place add-on swap.</summary>
public sealed class NeuralAddonVersionSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-swap-" + Guid.NewGuid().ToString("N"));

    public NeuralAddonVersionSwapTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void NewestFirst_PutsRc10AboveRc5AndStableAboveRc()
    {
        var sorted = ReleaseVersionOrdering.NewestFirst(
            new[] { "v1.0-rc5", "v0.9", "v1.0-rc10", "v1.0" }.Select(t => new GitHubReleaseVersion(t, t.TrimStart('v'), false)));

        Assert.Equal(["v1.0", "v1.0-rc10", "v1.0-rc5", "v0.9"], sorted.Select(v => v.Tag));
    }

    [Fact]
    public void ShortFuseLatest_PrefersRc10OverRc5RegardlessOfApiOrder()
    {
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/rc5"}] },
          { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/rc10"}] }
        ]
        """;

        Assert.Equal("8.5.0-rc10", ShortFuseDeploymentService.ParseReleaseJson(json).Version);
    }

    [Fact]
    public void UpdateAll_IncludesNeuralRenderingAddons()
    {
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Dlss5Feeder));
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Dlss5Dx11Bridge));
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task ManagedFileSwap_ReplacesOnlyTargetAndRestoresWhenPersistFails()
    {
        var target = Write("addon.addon64", [1, 2, 3]);
        var file = new LinuxDeployedFile { Path = target, Sha256 = Sha256(target) };
        var oldHash = file.Sha256;
        var source = Write("new.addon64", [9, 9, 9]);

        await Assert.ThrowsAsync<IOException>(() => ManagedFileSwap.ReplaceAsync([(file, source)], () => throw new IOException("disk full")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(oldHash, file.Sha256);

        var persisted = 0;
        Assert.Equal(1, await ManagedFileSwap.ReplaceAsync([(file, source)], () => persisted++));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(target));
        Assert.Equal(Sha256(target), file.Sha256);
        Assert.Equal(1, persisted);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "addon.addon64*"));
    }

    [Fact]
    public async Task ManagedFileSwap_RefusesFileModifiedOutsideRhi()
    {
        var target = Write("edited.addon64", [1]);
        var file = new LinuxDeployedFile { Path = target, Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagedFileSwap.ReplaceAsync([(file, Write("src.addon64", [2]))], () => { }));
        Assert.Equal([1], File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ShortFuseSwap_ReplacesAddonOnlyAndUpdatesRecord()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "sf", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName), Payload(0x10, 40 * 1024));
        var dlss = Write(Path.Combine(bin, "nvngx_dlss.dll"), Payload(0x20, 1024));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "sf-deployments.json"));
        records.Upsert(Record(game, ManagedComponentKind.ShortFuse, "8.5.0-rc5", addon, dlss));
        var dlssHash = Sha256(dlss);

        var newAddon = Payload(0x30, 40 * 1024);
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/rc5"}] },
          { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/rc10"}] }
        ]
        """;
        using var http = new HttpClient(new MapHandler(new()
        {
            [ShortFuseDeploymentService.ReleasesApiUrl] = Encoding.UTF8.GetBytes(json),
            ["https://example.invalid/rc10"] = newAddon
        }));
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var sf = new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, "sf.json")));

        var result = await sf.SwapAddonVersionAsync(game, releaseTag: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(newAddon, File.ReadAllBytes(addon));
        Assert.Equal(dlssHash, Sha256(dlss));
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal("8.5.0-rc10", record.Version);
        Assert.Equal(Sha256(addon), ManagedFileSwap.FindFile(record, ShortFuseDeploymentService.AddonFileName)!.Sha256);
        Assert.Equal(dlssHash, ManagedFileSwap.FindFile(record, "nvngx_dlss.dll")!.Sha256);
    }

    [Fact]
    public async Task FeederSwap_ReplacesFeederAddonOnlyAndKeepsRecordOnDownloadFailure()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "feeder", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, "dlss5-feed.addon64"), Pe(0x40));
        var nr = Write(Path.Combine(bin, "nvngx_dlssnr.dll"), Pe(0x50));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "feeder-deployments.json"));
        var seeded = Record(game, ManagedComponentKind.Dlss5Feeder, "1.0", addon, nr);
        seeded.Metadata["Feeder.FullStack"] = "1";
        records.Upsert(seeded);
        var nrHash = Sha256(nr);

        var newAddon = Pe(0x60);
        var responses = new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/tags/v1.1"] = Encoding.UTF8.GetBytes(
                """{"tag_name":"v1.1","assets":[{"name":"DLSS5-Feeder-1.1.zip","browser_download_url":"https://example.invalid/feeder-1.1.zip"}]}""")
        };
        using var http = new HttpClient(new MapHandler(responses));
        var feeder = Feeder(http, records);

        var failed = await feeder.SwapFeederVersionAsync(game, "v1.1");
        Assert.False(failed.Success);
        Assert.Equal("1.0", records.Find(game, ManagedComponentKind.Dlss5Feeder)!.Version);
        Assert.Equal(Pe(0x40), File.ReadAllBytes(addon));

        responses["https://example.invalid/feeder-1.1.zip"] = Zip(("DLSS5-Feeder/dlss5-feed.addon64", newAddon));
        var result = await feeder.SwapFeederVersionAsync(game, "v1.1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(newAddon, File.ReadAllBytes(addon));
        Assert.Equal(nrHash, Sha256(nr));
        var record = records.Find(game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal("1.1", record.Version);
        Assert.Equal("v1.1", record.Metadata["GitHub.ReleaseTagPin"]);
        Assert.Equal(Sha256(addon), ManagedFileSwap.FindFile(record, "dlss5-feed.addon64")!.Sha256);
    }

    private Dlss5FeederFullDeploymentService Feeder(HttpClient http, LinuxDeploymentRecordStore records)
    {
        var files = new TransactionalFileDeploymentService();
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://release.invalid/latest", 64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "cache-legacy"));
        return new Dlss5FeederFullDeploymentService(http, records,
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")), legacy, Path.Combine(_root, "cache-feeder"));
    }

    private static DetectedGame Game(string bin) => new()
    {
        Name = "NR swap " + Path.GetFileName(Path.GetDirectoryName(bin)), InstallPath = Path.GetDirectoryName(bin)!,
        Source = "Test", Runner = "Wine", BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe")
    };

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind, string version, params string[] paths) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
        BinaryDirectory = game.BinaryDirectory!, Version = version,
        Files = paths.Select(p => new LinuxDeployedFile { Path = p, Sha256 = Sha256(p) }).ToList()
    };

    private string Write(string nameOrPath, byte[] bytes)
    {
        var path = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.Combine(_root, nameOrPath);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Payload(byte marker, int size) => Enumerable.Range(0, size).Select(i => (byte)(marker + i * 7)).ToArray();

    private static byte[] Pe(byte marker)
    {
        var bytes = Payload(marker, 80 * 1024);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
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
