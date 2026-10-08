using System.IO.Compression;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class DgVoodooDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dgvoodoo-tests-" + Guid.NewGuid().ToString("N"));

    public DgVoodooDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BuildConfiguration_ContainsRequiredDx9ToDx11Settings()
    {
        var text = DgVoodooDeploymentService.BuildConfiguration();

        Assert.Contains("OutputAPI = d3d11_fl11_0", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DisableAndPassThru = false", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VideoCard = geforce_9800_gt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VRAM = 1024", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dgVoodooWatermark = false", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PeArchitecture.X86, @"MS\x86\D3D9.dll")]
    [InlineData(PeArchitecture.X64, @"MS\x64\D3D9.dll")]
    public async Task ExtractD3D9_SelectsMatchingArchitectureAndNormalizesWindowsPaths(PeArchitecture architecture, string expectedEntry)
    {
        var zip = Path.Combine(_root, $"dgvoodoo-{architecture}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var item in new[] { @"MS\x86\D3D9.dll", @"MS\x64\D3D9.dll" })
            {
                var entry = archive.CreateEntry(item);
                using var output = entry.Open();
                var payload = new byte[96 * 1024];
                payload[0] = (byte)'M';
                payload[1] = (byte)'Z';
                payload[80] = (byte)(item == expectedEntry ? 0x5A : 0x33);
                output.Write(payload);
            }
        }

        var target = Path.Combine(_root, $"D3D9-{architecture}.dll");
        var extracted = await DgVoodooDeploymentService.ExtractD3D9Async(zip, target, architecture, CancellationToken.None);

        Assert.Equal(target, extracted);
        Assert.Equal(0x5A, File.ReadAllBytes(target)[80]);
    }

    [Fact]
    public async Task Prepare_CarriesTheVerifiedArchiveForTheDeployingRecord()
    {
        // Review w2-trust minor 6: Luma and the Feeder record dgVoodoo2's download as part "dgVoodoo".
        const string url = "https://github.com/dege-diosg/dgVoodoo2/releases/download/v2.86/dgVoodoo2_86.zip";
        byte[] zip;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("MS/x64/D3D9.dll", CompressionLevel.NoCompression);
                using var output = entry.Open();
                var payload = new byte[96 * 1024];
                payload[0] = (byte)'M';
                payload[1] = (byte)'Z';
                output.Write(payload);
            }
            zip = memory.ToArray();
        }
        using var http = new HttpClient(new ZipHandler(url, zip));
        var service = new DgVoodooDeploymentService(http, Path.Combine(_root, "cache"));

        var prepared = await service.PrepareManifestVersionAsync("2.86", url, PeArchitecture.X64);

        Assert.NotNull(prepared.Download);
        Assert.Equal(DownloadRecordAssert.Sha256Of(zip), prepared.Download!.Sha256, ignoreCase: true);
        Assert.False(prepared.Download.DigestVerified); // a manifest link publishes no digest
        Assert.Equal("dgVoodoo", DgVoodooPreparedPayload.DownloadPart);
    }

    private sealed class ZipHandler(string url, byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsoluteUri == url
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(zip), RequestMessage = request }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    [Fact]
    public void ReleaseQuery_TargetsOfficialDgVoodoo2Zip()
    {
        var query = DgVoodooDeploymentService.CreateLatestReleaseQuery();

        Assert.Contains("dege-diosg/dgVoodoo2", query.ReleaseApiUrl, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(query.AssetNamePattern);
        Assert.Matches(query.AssetNamePattern!, "dgVoodoo2_87_4.zip");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
