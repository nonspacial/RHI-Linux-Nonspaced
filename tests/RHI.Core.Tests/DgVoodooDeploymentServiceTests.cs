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
