using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// w12 final review M1: RTX 20 FG Unlock's record without its proxy DLL's name — Remove follows the check's advice through
// (proxy and INI that still hold RHI's bytes go, the override is restored, the record is dropped), then Install works; a
// changed INI stays and is named (EN + PT-BR, path last); a crash before the record is gone is put back by the next start.
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    private static void DropProxyName(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var record = records.Find(game, ManagedComponentKind.Dlssg2030Unlock)!;
        record.Metadata.Remove("FG2030.ProxyName");
        records.Upsert(record);
    }

    private static string? Override(GameFixture fixture, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task RecordWithoutProxyName_Remove_DeletesProxyAndIni_RestoresTheOverride_DropsTheRecord_ThenInstallWorks()
    {
        using var fixture = CreateGame("proxyless", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x7A), UpstreamIni("SM86")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("RTX 20 FG Unlock" + ProxylessRecordRemoval.RemovedSuffix, remove.Message);
        Assert.False(File.Exists(proxy));
        Assert.False(File.Exists(ini));
        Assert.Null(Override(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.StartsWith("RTX 20 FG Unlock removido. O registro do RHI", UiLocalizationService.PresentFor("pt-BR", remove.Message), StringComparison.Ordinal);

        var install = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll");

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(proxy));
        Assert.Equal("native,builtin", Override(fixture, "winmm"));
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_LeavesAChangedIni_AndNamesIt_PathLast()
    {
        using var fixture = CreateGame("proxyless-changed", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x7B), UpstreamIni("SM86")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        DropProxyName(records, fixture.Game);
        File.AppendAllText(ini, "\n; the user's own line\n");
        var userIni = File.ReadAllBytes(ini);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(proxy));
        Assert.Equal(userIni, File.ReadAllBytes(ini));
        Assert.Equal("RTX 20 FG Unlock" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.NotDeletedPrefix + ini, remove.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.EndsWith(" Arquivos que o RHI não apagou: " + ini, pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(ini, "", StringComparison.Ordinal)));
    }

    // The plan hashes the listed files only after the game's hold is settled: the user's bytes the undo puts back stay.
    [PermissionDenialFact]
    public async Task RecordWithoutProxyName_HeldGame_TheUndoFinishesBeforeThePlan_TheUsersBytesStay()
    {
        using var fixture = CreateGame("proxyless-held", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x7D), UpstreamIni("SM86")));
        var (service, records, journal, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(proxy);
        DropProxyName(records, fixture.Game);
        var users = CreatePe(0x8664, 320 * 1024, 0x7E);
        File.WriteAllBytes(proxy, users);
        using (await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.Dlssg2030Unlock, proxy, installed))
        {
        }

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(users, File.ReadAllBytes(proxy));
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + proxy, remove.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_CrashBeforeTheRecordIsGone_NextStartPutsAllBack_ThenRemoveWorks()
    {
        using var fixture = CreateGame("proxyless-crash", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x7C), UpstreamIni("SM86")));
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        var (proxyBytes, iniBytes) = (File.ReadAllBytes(proxy), File.ReadAllBytes(ini));
        DropProxyName(records, fixture.Game);
        using (var death = new SimulatedDeath(journal))
        {
            overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= death.Die;

            var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
            Assert.True(died);
            Assert.False(File.Exists(proxy));
            Assert.False(File.Exists(ini));

            var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
            Assert.Equal(iniBytes, File.ReadAllBytes(ini));
            Assert.Equal("native,builtin", Override(fixture, "winmm"));
            Assert.NotNull(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
            Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));

            var restartedRecords = new LinuxDeploymentRecordStore(ledger);
            var (restartedJournal, restartedOverrides) = SimulatedDeath.NewJournal(journalDir, restartedRecords);
            var restarted = new Dlssg2030UnlockDeploymentService(http, restartedRecords, restartedOverrides,
                new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"), restartedJournal);
            var remove = await restarted.RemoveAsync(fixture.Game);

            Assert.True(remove.Success, remove.Message);
            Assert.False(File.Exists(proxy));
            Assert.False(File.Exists(ini));
            Assert.Null(Override(fixture, "winmm"));
            Assert.Null(restartedRecords.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        }
    }
}
