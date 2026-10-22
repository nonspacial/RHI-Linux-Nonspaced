using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#229: RTX Encore keeps its own fixed 310.8.0 runtime whatever the card ("RTX Encore unchanged").</summary>
public sealed partial class RtxEncoreDeploymentServiceTests
{
    [Fact]
    public async Task NrGpu_OnAnRtx40_StillFindsItsFixed310_8_0Runtime()
    {
        var (journal, overrides) = SimulatedDeath.NewJournal(JournalDir, _records);
        var handler = new EncoreHandler();
        var dlss = new DlssManagerService(new HttpClient(handler), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"))
        {
            CardGeneration = () => RtxGeneration.Rtx40 // 310.8.0 (50xx) is hidden from every NR list on this card
        };
        var service = new RtxEncoreDeploymentService(new HttpClient(handler), _records, overrides, _names, dlss, Path.Combine(_root, "cache"), journal)
        {
            RequiredNeuralRenderingSha256 = FileHash.Sha256HexOf(NrAccepted)
        };
        Assert.True(service.SetNeuralRenderingPreference(_game, true).Success);

        var result = await service.InstallAsync(_game);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NrAccepted, File.ReadAllBytes(Path.Combine(_bin, "nvngx_dlssnr.dll")));
    }
}
