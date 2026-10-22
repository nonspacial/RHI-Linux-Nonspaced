using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>#229: a DLSS5 Feeder Full install with no NR pin places "Latest" made for the card.</summary>
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    [Theory]
    [InlineData(RtxGeneration.Rtx40, ForAll)]
    [InlineData(RtxGeneration.Rtx50, For50)]
    [InlineData(RtxGeneration.Unknown, For50)]
    public async Task NrGpu_FullStackInstallWithoutPin_PlacesLatestForThisCard(RtxGeneration card, string expected)
    {
        using var f = new FeederFixture(liveNr: true);
        f.Dlss.CardGeneration = () => card;

        var installed = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

        Assert.True(installed.Success, installed.Message);
        Assert.Equal(Dll(expected), File.ReadAllBytes(Path.Combine(f.Bin, "nvngx_dlssnr.dll")));
    }
}
