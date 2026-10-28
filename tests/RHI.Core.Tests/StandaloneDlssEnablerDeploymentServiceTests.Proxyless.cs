using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// w12 closing (B2's rule, ProxylessRecordRemoval): DLSS Enabler Standalone's record without its proxy DLL's name — Remove
// follows the check's advice through (the proxy that still holds RHI's bytes goes, the override is restored, the record
// is dropped), then Install works; a changed proxy stays and is named (EN + PT-BR, path last); a held game's undo finishes
// before the plan hashes anything; a crash after the first write is put back by the next start, then Remove works.
public sealed partial class StandaloneDlssEnablerDeploymentServiceTests
{
    private static void DropProxyName(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var record = records.Find(game, ManagedComponentKind.DlssEnablerStandalone)!;
        record.Metadata.Remove("DLSSEnabler.ProxyName");
        records.Upsert(record);
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_DeletesTheProxy_RestoresTheOverride_DropsTheRecord_ThenInstallWorks()
    {
        using var fixture = CreateGame("proxyless", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x71)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("DLSS Enabler Standalone" + ProxylessRecordRemoval.RemovedSuffix, remove.Message);
        Assert.False(File.Exists(proxy));
        Assert.Null(ProxyOverride(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.StartsWith("DLSS Enabler Standalone removido. O registro do RHI", pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace("DLSS Enabler Standalone", "", StringComparison.Ordinal)));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(proxy));
        Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_LeavesAChangedProxy_AndNamesIt_PathLast()
    {
        using var fixture = CreateGame("proxyless-changed", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x72)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        DropProxyName(records, fixture.Game);
        var users = CreatePe(0x8664, 256 * 1024, 0x73);
        File.WriteAllBytes(proxy, users);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(users, File.ReadAllBytes(proxy));
        Assert.Equal("DLSS Enabler Standalone" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.NotDeletedPrefix + proxy, remove.Message);
        Assert.Null(ProxyOverride(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.EndsWith(" Arquivos que o RHI não apagou: " + proxy, pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(proxy, "", StringComparison.Ordinal).Replace("DLSS Enabler Standalone", "", StringComparison.Ordinal)));
    }

    // The plan hashes the listed files only after the game's hold is settled: the user's bytes the undo puts back stay.
    [PermissionDenialFact]
    public async Task RecordWithoutProxyName_HeldGame_TheUndoFinishesBeforeThePlan_TheUsersBytesStay()
    {
        using var fixture = CreateGame("proxyless-held", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x74)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(proxy);
        DropProxyName(records, fixture.Game);
        var users = CreatePe(0x8664, 256 * 1024, 0x75);
        File.WriteAllBytes(proxy, users);
        using (await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.DlssEnablerStandalone, proxy, installed))
        {
        }

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(users, File.ReadAllBytes(proxy));
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + proxy, remove.Message, StringComparison.Ordinal);
    }

    // The held game is refused with the hold's own restart text while its folder is still read-only, before any hash.
    [PermissionDenialFact]
    public async Task RecordWithoutProxyName_HeldGame_StillReadOnly_RefusedWithTheRestartText_NothingChanges()
    {
        using var fixture = CreateGame("proxyless-held-ro", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x76)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(proxy);
        DropProxyName(records, fixture.Game);
        var users = CreatePe(0x8664, 256 * 1024, 0x77);
        File.WriteAllBytes(proxy, users);

        using (await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.DlssEnablerStandalone, proxy, installed))
        {
            var remove = await service.RemoveAsync(fixture.Game);

            Assert.False(remove.Success);
            Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, remove.Message);
            Assert.Equal(installed, File.ReadAllBytes(proxy));
            Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
            Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
        }
    }

    // Dies after the first writes (the proxy deleted, the override restored) while the record is being taken away; the next
    // start puts both back and keeps the record, and Remove then works.
    [Fact]
    public async Task RecordWithoutProxyName_Remove_CrashAfterTheFirstWrites_NextStartPutsAllBack_ThenRemoveWorks()
    {
        using var fixture = CreateGame("proxyless-crash", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x78)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var proxyBytes = File.ReadAllBytes(proxy);
        DropProxyName(records, fixture.Game);
        using (var death = new SimulatedDeath(journal))
        {
            // The removal's stamp is saved before the override is restored; the next save (taking the record away) dies.
            overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= death.Die;

            var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
            Assert.True(died);
            Assert.False(File.Exists(proxy));
            Assert.Null(ProxyOverride(fixture, "winmm"));

            var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
            Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
            Assert.NotNull(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
            Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));

            var restartedRecords = new LinuxDeploymentRecordStore(ledger);
            var (restartedJournal, restartedOverrides) = SimulatedDeath.NewJournal(journalDir, restartedRecords);
            var restarted = new StandaloneDlssEnablerDeploymentService(http, restartedRecords, restartedOverrides,
                new StandaloneDlssEnablerPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"), restartedJournal);
            var remove = await restarted.RemoveAsync(fixture.Game);

            Assert.True(remove.Success, remove.Message);
            Assert.False(File.Exists(proxy));
            Assert.Null(ProxyOverride(fixture, "winmm"));
            Assert.Null(restartedRecords.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
        }
    }
}
