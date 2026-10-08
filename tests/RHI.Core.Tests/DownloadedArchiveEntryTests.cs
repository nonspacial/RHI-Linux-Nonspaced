using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Files taken out of downloaded ZIPs (ReShade nightly artifact, Addon Manager add-on archives) go through the one
/// verified extraction (<see cref="ReShadeArchiveExtractor.ExtractEntryAsync"/>): a declared size over the cap is
/// refused before anything is written, and the archive's Unix mode bits are never applied (integration w1 deploy
/// review 4; formerly <c>ExtractToFile</c>).
/// </summary>
public sealed class DownloadedArchiveEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-archive-entry-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly int SetUidAndAllPermissions = Convert.ToInt32("104777", 8) << 16;
    private const UnixFileMode Dangerous = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                                           | UnixFileMode.SetUser | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite;

    public DownloadedArchiveEntryTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task NightlyArtifact_ArchiveModeBitsAreNotApplied()
    {
        var (svc, _) = Nightly(Zip("ReShade64.dll", ReShadeDll(), SetUidAndAllPermissions));

        var staged = await svc.EnsureNightlyStagedAsync(PeArchitecture.X64);

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(staged.DllPath) & Dangerous);
    }

    [Fact]
    public async Task NightlyArtifact_DeclaringMoreThanTheCap_IsRefusedBeforeWriting()
    {
        var (svc, cache) = Nightly(Zip("ReShade64.dll", ReShadeDll(), 0, declaredLength: ReShadeArchiveExtractor.MaxEntryBytes + 1));

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => svc.EnsureNightlyStagedAsync(PeArchitecture.X64));

        Assert.StartsWith(ReShadeArchiveExtractor.ArchiveRefusedPrefix, refused.Message);
        Assert.Contains($": declared size over the limit ({ReShadeArchiveExtractor.MaxEntryBytes + 1} > {ReShadeArchiveExtractor.MaxEntryBytes} bytes)", refused.Message);
        Assert.StartsWith("Arquivo baixado recusado: ", RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", refused.Message));
        Assert.Empty(Directory.EnumerateFiles(cache, "*.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AddonManagerArchive_ArchiveModeBitsAreNotApplied()
    {
        var (manager, game, id, cache) = await AddonManager(Zip("Thing.addon64", CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 0x42), SetUidAndAllPermissions));

        var result = await manager.ApplySelectionAsync(game, [id]);

        Assert.True(result.Success, result.Message);
        var cached = Assert.Single(Directory.EnumerateFiles(cache, "*.addon64"));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(cached) & Dangerous);
    }

    [Fact]
    public async Task AddonManagerArchive_DeclaringMoreThanTheCap_IsRefusedBeforeWriting()
    {
        var (manager, game, id, cache) = await AddonManager(Zip("Thing.addon64", CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 0x42), 0,
            declaredLength: TrustedDownloadPolicy.AddonMaxBytes + 1));

        var result = await manager.ApplySelectionAsync(game, [id]);

        Assert.False(result.Success);
        Assert.Contains($": declared size over the limit ({TrustedDownloadPolicy.AddonMaxBytes + 1} > {TrustedDownloadPolicy.AddonMaxBytes} bytes)", result.Message);
        Assert.Empty(Directory.EnumerateFiles(cache, "*.addon64"));
        Assert.Empty(Directory.EnumerateFiles(game.BinaryDirectory!, "*.addon64", SearchOption.AllDirectories));
    }

    private (ReShadeDeploymentService Service, string Cache) Nightly(byte[] zip)
    {
        var http = new HttpClient(new Routes(new() { [ReShadeDeploymentService.Nightly64Url] = zip }));
        var cache = Path.Combine(_root, "reshade-cache");
        var svc = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(),
            new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json")), new WineDllOverrideService(), new ReShadeArchiveExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(_root, "shader-packs"), null, Path.Combine(_root, "custom-shaders")));
        return (svc, cache);
    }

    private async Task<(ReShadeAddonManagerService Manager, DetectedGame Game, string Id, string Cache)> AddonManager(byte[] zip)
    {
        const string url = "https://github.com/someone/thing/releases/download/v1/Thing.zip";
        var http = new HttpClient(new Routes(new()
        {
            [ReShadeAddonManagerService.AddonsIniUrl] = Encoding.UTF8.GetBytes($"[01]\nPackageName=Thing Addon\nDownloadUrl64={url}\n"),
            ["https://manifest.test/manifest.json"] = Encoding.UTF8.GetBytes("""{"version":2}"""),
            [url] = zip
        }));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(_root, "manifest.json"));
        var cache = Path.Combine(_root, "addon-cache");
        var manager = new ReShadeAddonManagerService(http, manifest, cache, Path.Combine(_root, "custom-addons"), Path.Combine(_root, "addon-state.json"));
        var id = (await manager.RefreshCatalogItemsAsync()).Single(x => x.DisplayName.Contains("Thing", StringComparison.Ordinal)).Id;

        var bin = Directory.CreateDirectory(Path.Combine(_root, "Game", "bin")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade64.dll"), "MZ ReShade crosire");
        var game = new DetectedGame
        {
            Name = "Game", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Steam"
        };
        Directory.CreateDirectory(cache);
        return (manager, game, id, cache);
    }

    private static byte[] ReShadeDll()
    {
        var bytes = CacheForeignFileRejectionTests.Pe(0x8664, 1_100_000, 0x11);
        Encoding.ASCII.GetBytes("ReShade crosire reshade.me").CopyTo(bytes, 512);
        return bytes;
    }

    /// <summary>One stored entry; optional Unix mode in the external attributes and a forged declared (uncompressed) size.</summary>
    private static byte[] Zip(string name, byte[] content, int externalAttributes, long? declaredLength = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.ExternalAttributes = externalAttributes;
            using var stream = entry.Open();
            stream.Write(content);
        }
        var bytes = ms.ToArray();
        if (declaredLength is long declared)
        {
            // Uncompressed size: local header +22, central directory header +24.
            for (var i = 0; i + 4 <= bytes.Length; i++)
            {
                var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
                if (signature == 0x04034b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), (uint)declared);
                else if (signature == 0x02014b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), (uint)declared);
            }
        }
        return bytes;
    }

    /// <summary>Serves fixed bodies (GET and HEAD with Content-Length); anything else is 404.</summary>
    private sealed class Routes(Dictionary<string, byte[]> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!bodies.TryGetValue(request.RequestUri!.AbsoluteUri, out var body) && !bodies.TryGetValue(request.RequestUri.OriginalString, out body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
