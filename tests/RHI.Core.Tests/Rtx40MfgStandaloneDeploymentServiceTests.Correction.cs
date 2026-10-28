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

    // F4 (review mutant R11): a release listing a `.zip.sha256` companion beside the zip (the publisher shipped those in
    // v1.2.x) takes the zip; the companion is not counted as a second MFGUnlocker asset.
    [Fact]
    public async Task Release_ListingAZipSha256Companion_TakesTheZip()
    {
        using var fixture = CreateGame("correction-sha256-companion", 0x8664, GraphicsApi.DirectX12);
        var dll = CreatePe(0x8664, 512 * 1024, 0x84);
        using var handler = Rtx40Handler.WithVersions(("1.7.0", CreateZip(("MFGUnlocker.dll", dll))));
        handler.Prefixes["1.7.0"] = "MFGUnlocker-";
        handler.ExtraAssets.Add(("MFGUnlocker-v1.7.0.zip.sha256", System.Text.Encoding.ASCII.GetBytes(new string('b', 64) + "  MFGUnlocker-v1.7.0.zip\n")));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var release = await service.ResolveLatestReleaseAsync(CancellationToken.None);
        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.Equal("MFGUnlocker-v1.7.0.zip", release.AssetName);
        Assert.True(install.Success, install.Message);
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(fixture.Bin, "version.dll")));
    }

    // F5: a record without the proxy's hash (blank, or no file entry for the proxy) is "could not be checked" with the one
    // reason every family gives — never a planned update that fails on every pass, never "changed outside RHI".
    [Theory]
    [InlineData("blank")]
    [InlineData("no-entry")]
    public async Task RecordWithoutTheProxysHash_IsCouldNotCheck_WithTheOneReason(string shape)
    {
        using var fixture = CreateGame("correction-hashless-" + shape, 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x85, 0x86);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        if (shape == "blank") record.Files.Single().Sha256 = "";
        else record.Files.Clear();
        records.Upsert(record);
        handler.ActiveVersion = "1.7.0";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.NoRecordedHashPrefix + target, check.Message);
        Assert.Equal(CreatePe(0x8664, 512 * 1024, 0x85), File.ReadAllBytes(target));
    }

    // F5: a record without its proxy name cannot be updated (the update refuses it), so the check says so before any lookup.
    [Fact]
    public async Task RecordWithoutItsProxyName_IsCouldNotCheck_BeforeAnyLookup()
    {
        using var fixture = CreateGame("correction-noproxy", 0x8664, GraphicsApi.DirectX12);
        using var handler = V142ThenV170(0x87, 0x88);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        record.Metadata.Remove("UniversalMFG.ProxyName");
        record.Metadata.Remove("RTX40MFG.ProxyName");
        records.Upsert(record);
        handler.ActiveVersion = "1.7.0";
        var requests = handler.RequestCount;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.MissingProxyMessage, check.Message);
        Assert.Equal(requests, handler.RequestCount);
        Assert.False(update.Success);
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.MissingProxyMessage, update.Message);
    }
}
