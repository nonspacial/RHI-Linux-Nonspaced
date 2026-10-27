using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #238 (upstream v2.8.8 Beta bb9b2fd: DLSSG 20/30 deployed at start): the RTX 20 FG Unlock (DLSSG 20/30) is an Update All
// family. Its check compares the pinned upstream commit, keeps the proxy name, never plans a swap over a proxy (or INI)
// removed or changed outside RHI, and could not check when the lookup fails.
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    private async Task<(GameFixture Fixture, DlssgHandler Handler, Dlssg2030UnlockDeploymentService Service, LinuxDeploymentRecordStore Records)>
        InstalledAsync(string name)
    {
        var fixture = CreateGame(name, 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x35), UpstreamIni("SM86"));
        var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        return (fixture, handler, service, records);
    }

    private const string NewerCommit = "fedcba9876543210fedcba9876543210fedcba98";

    [Fact]
    public async Task CheckManaged_NewerCommit_IsAnUpdate_AndUpdateKeepsTheProxyName()
    {
        var (fixture, handler, service, records) = await InstalledAsync("check-update");
        var current = await service.CheckManagedUpdateAsync(fixture.Game);
        handler.CommitSha = NewerCommit;
        handler.Dll = CreatePe(0x8664, 320 * 1024, 0x36);

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, false), (current.Managed, current.UpdateAvailable, current.CouldNotCheck));
        Assert.Equal((true, true, false), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.True(update.Success, update.Message);
        Assert.Equal(handler.Dll, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal(NewerCommit, records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!.Metadata["FG2030.CommitSha"]);
    }

    [Theory]
    [InlineData("winmm.dll")]
    [InlineData(Dlssg2030UnlockDeploymentService.IniFileName)]
    public async Task CheckManaged_FileRemovedOutsideRhi_IsCouldNotCheck_BeforeAnyLookup_AndNeverPutBack(string removedName)
    {
        var (fixture, handler, service, _) = await InstalledAsync("removed-" + removedName);
        var removed = Path.Combine(fixture.Bin, removedName);
        File.Delete(removed);
        handler.CommitSha = NewerCommit;
        var requests = handler.RequestCount;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + removed, check.Message);
        Assert.Equal(requests, handler.RequestCount);
        Assert.False(update.Success);
        Assert.False(File.Exists(removed));
    }

    [Fact]
    public async Task CheckManaged_ProxyChangedOutsideRhi_IsCouldNotCheck_WhenAnUpdateIsPlanned()
    {
        var (fixture, handler, service, _) = await InstalledAsync("changed");
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var replaced = CreatePe(0x8664, 320 * 1024, 0x37);
        File.WriteAllBytes(proxy, replaced);
        handler.CommitSha = NewerCommit;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((false, true), (check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + proxy, check.Message);
        Assert.Equal(replaced, File.ReadAllBytes(proxy));
    }

    [Fact]
    public async Task CheckManaged_LookupFails_IsCouldNotCheck_NeverCurrent()
    {
        var (fixture, handler, service, _) = await InstalledAsync("lookup-fails");
        handler.CommitSha = "not-a-commit";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
    }
    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // Proved first on 8f15e44: Update All left the RTX 20 FG Unlock out (no family, no check or update arm).
    [Fact]
    public async Task UpdateAll_NewerCommit_UpdatesIt_ThroughTheAppsArms_InBothModes()
    {
        var (fixture, handler, service, records) = await InstalledAsync("update-all");
        handler.CommitSha = NewerCommit;
        handler.Dll = CreatePe(0x8664, 320 * 1024, 0x38);
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: service,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);
        var updateAll = new LinuxUpdateAllService(records, managed, new NotRunning());

        var background = await updateAll.RunAsync([fixture.Game], _ => [], LinuxUpdateRunMode.Background);
        var manual = await updateAll.RunAsync([fixture.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal((1, 0, 0), (background.ChangedCount, background.FailedCount, background.UncheckedCount));
        Assert.Equal((0, 1, 0), (manual.ChangedCount, manual.CurrentCount, manual.FailedCount));
        Assert.Equal(handler.Dll, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmm.dll")));
    }
}
