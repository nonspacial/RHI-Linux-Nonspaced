using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// Correction round 1 of lane w12/mfg (review w12-review-mfg.md): F1 hold text before any file check, F5 one "could not be
// checked" line for a record without the proxy's hash.
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    [PermissionDenialFact]
    public async Task HeldGame_CheckAndUpdate_SayTheHoldsRestartText_BeforeAnyFileCheck()
    {
        var fixture = CreateGame("correction-held", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x35), UpstreamIni("SM86"));
        var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var (journal, overrides) = SimulatedDeath.NewJournal(Path.Combine(fixture.Root, "journal"), records);
        var service = new Dlssg2030UnlockDeploymentService(http, records, overrides,
            new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"), journal);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        handler.CommitSha = NewerCommit;
        handler.Dll = CreatePe(0x8664, 320 * 1024, 0x36);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var halfUndone = CreatePe(0x8664, 320 * 1024, 0x38);
        using var hold = await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.Dlssg2030Unlock, proxy, halfUndone);
        var requests = handler.RequestCount;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, check.Message);
        Assert.False(update.Success);
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, update.Message);
        Assert.Equal(requests, handler.RequestCount);
        Assert.Equal(halfUndone, File.ReadAllBytes(proxy));
    }
}
