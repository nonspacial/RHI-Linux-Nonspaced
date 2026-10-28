using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// Correction round 1 of lane w12/mfg (review w12-review-mfg.md): F1 hold text before any file check, F4 the `.zip` filter on
// release assets, F5 one "could not be checked" line for a record without the proxy's hash or name.
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    // F1: a game held by its own failed undo is told the hold's restart text (could not be checked; the single-row update is
    // refused with it), never "changed outside RHI" about RHI's own half-undone proxy; nothing is looked up or written.
    [PermissionDenialFact]
    public async Task HeldGame_CheckAndUpdate_SayTheHoldsRestartText_BeforeAnyFileCheck()
    {
        using var fixture = CreateGame("correction-held", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x81, 0x82);
        using var http = new HttpClient(handler);
        var (service, records, journal, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "dinput8.dll")).Success);
        handler.ActiveVersion = "1.7.0";
        var target = Path.Combine(fixture.Bin, "dinput8.dll");
        var halfUndone = CreatePe(0x8664, 512 * 1024, 0x83);
        using var hold = await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.Rtx40MfgStandalone, target, halfUndone);
        var requests = handler.RequestCount;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, check.Message);
        Assert.False(update.Success);
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, update.Message);
        Assert.Equal(requests, handler.RequestCount);
        Assert.Equal(halfUndone, File.ReadAllBytes(target));
    }
}
