using System.Net;
using System.Text;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #42 (DEP-S4): the remaining sites that took files out of downloaded ZIPs with <c>ExtractToFile</c> (archive Unix
/// modes applied, no cap, no CRC) or a raw stream copy (no cap, no CRC) now use the one checked extraction.
/// Mode bits from the archive never reach the extracted file; a corrupted member (CRC mismatch) is refused.
/// </summary>
public sealed class ExtractionSiteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-extraction-sites-" + Guid.NewGuid().ToString("N"));
    private static readonly int SetUidAndAllPermissions = Convert.ToInt32("104777", 8) << 16;
    private const UnixFileMode Dangerous = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                                           | UnixFileMode.SetUser | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite;

    public ExtractionSiteTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task DgVoodoo_D3D9_TakesNoArchiveModeBits()
    {
        var zip = Write("dg.zip", DownloadedArchiveEntryTests.Zip("MS/x64/D3D9.dll", CacheForeignFileRejectionTests.Pe(0x8664, 96 * 1024, 0x21), SetUidAndAllPermissions));
        var target = Path.Combine(_root, "dg", "D3D9.dll");

        await DgVoodooDeploymentService.ExtractD3D9Async(zip, target, RHI.Core.Models.PeArchitecture.X64, CancellationToken.None);

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(target) & Dangerous);
    }

    [Fact]
    public async Task FeederReleasePayload_TakesNoArchiveModeBits()
    {
        var zip = Write("feeder.zip", DownloadedArchiveEntryTests.Zip(@"DLSS5-Feeder\dlss5-feed.addon64", CacheForeignFileRejectionTests.Pe(0x8664, 96 * 1024, 0x22), SetUidAndAllPermissions));
        var target = Path.Combine(_root, "dlss5-feed.addon64");

        await Dlss5FeederFullDeploymentService.ExtractReleasePayloadByNameAsync(zip, "dlss5-feed.addon64", target, 64 * 1024, requirePe: true, CancellationToken.None);

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(target) & Dangerous);
    }

    [Fact]
    public async Task ShaderPackZip_TakesNoArchiveModeBits()
    {
        const string url = "https://github.com/someone/shaders/archive/refs/heads/main.zip";
        var zip = DownloadedArchiveEntryTests.Zip("shaders-main/Shaders/Test.fx", Encoding.ASCII.GetBytes(new string('x', 2048)), SetUidAndAllPermissions);
        using var http = new HttpClient(new OneBody(url, zip));
        var service = new ReShadeShaderPackService(http, Path.Combine(_root, "packs"), null, Path.Combine(_root, "custom"));

        var extracted = await service.EnsurePackAsync(
            new ReShadeShaderPackDefinition("ModeTest", "Mode test", "test", ReShadeShaderPackSourceKind.DirectUrl, url), CancellationToken.None);

        var file = Assert.Single(SafeFileEnumeration.Files(extracted));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & Dangerous);
    }

    [Fact]
    public async Task ShortFuseArchive_CorruptedMember_IsRefused()
    {
        const string url = "https://github.com/RankFTW/rhi-repo/releases/download/sf/renodx-dlss_SF_2.0.zip";
        var zip = CorruptMember(DownloadedArchiveEntryTests.Zip("payload/renodx-dlss.addon64", CacheForeignFileRejectionTests.Pe(0x8664, 80 * 1024, 0x31), 0));
        using var http = new HttpClient(new OneBody(url, zip));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "sf.json"));
        var overrides = new WineDllOverrideService();
        var service = new ShortFuseDeploymentService(http, new TransactionalFileDeploymentService(), records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "ual")),
            new DlssManagerService(http, new TransactionalFileDeploymentService(), Path.Combine(_root, "dlss")),
            Path.Combine(_root, "sf-cache"), new ShortFuseSettingsStore(Path.Combine(_root, "sf-settings.json")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureCachedAddonAsync(
            new ShortFuseReleaseInfo("2.0", "renodx-dlss-SF-2.0", url, null, true, "renodx-dlss_SF_2.0.zip"), CancellationToken.None));

        Assert.StartsWith(ReShadeArchiveExtractor.ArchiveRefusedPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "sf-cache"), "*.addon64", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UltimateAsiLoaderArchive_CorruptedMember_IsRefused()
    {
        const string url = "https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v9/Ultimate-ASI-Loader_x64.zip";
        var zip = CorruptMember(DownloadedArchiveEntryTests.Zip("dinput8.dll", CacheForeignFileRejectionTests.Pe(0x8664, 80 * 1024, 0x41), 0));
        using var http = new HttpClient(new OneBody(url, zip));
        var service = new UltimateAsiLoaderDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "ual.json")),
            new WineDllOverrideService(), Path.Combine(_root, "ual-cache"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureCachedDllAsync(
            new UalReleaseInfo("9", "Ultimate-ASI-Loader_x64.zip", url, null), RHI.Core.Models.PeArchitecture.X64, CancellationToken.None));

        Assert.Contains("CRC-32", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "ual-cache"), "*.dll", SearchOption.AllDirectories));
    }

    /// <summary>Flips one byte inside the (stored) member data, so the bytes no longer match the entry's CRC-32.</summary>
    private static byte[] CorruptMember(byte[] zip)
    {
        var copy = (byte[])zip.Clone();
        // Local header (30 bytes + name) is at 0; the PE fixture's filler starts well after it.
        copy[4096] ^= 0x5A;
        return copy;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class OneBody(string url, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsoluteUri == new Uri(url).AbsoluteUri
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
