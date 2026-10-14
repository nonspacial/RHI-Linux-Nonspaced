using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// w3/journal: the file side of Universal MFG's journal (#23): a crash before the record is saved or removed is rolled back
// at the next start, files included; an in-process failure rolls back from the scope's verified copies (#31).
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    private (Rtx40MfgStandaloneDeploymentService Service, LinuxDeploymentRecordStore Records, DeploymentJournal Journal, string JournalDir, string Ledger, WineDllOverrideService Overrides)
        JournaledService(GameFixture fixture, HttpClient http)
    {
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        return (new Rtx40MfgStandaloneDeploymentService(http, records, overrides, new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")),
            Path.Combine(fixture.Root, "cache"), journal), records, journal, journalDir, ledger, overrides);
    }

    private static string? ProxyOverride(GameFixture fixture, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesTheProxyAndTheOverride()
    {
        using var fixture = CreateGame("crash-install", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x61)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, "winmm.dll"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Null(ProxyOverride(fixture, "winmm"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    [Fact]
    public async Task Update_CrashWhileSavingTheRecord_NextStartPutsThePreviousProxyBack()
    {
        using var fixture = CreateGame("crash-update", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.1", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x62)))),
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x63)))));
        handler.ActiveVersion = "1.3.1";
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(target);
        handler.ActiveVersion = "1.3.2";
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(fixture.Game));
        Assert.True(died);
        Assert.NotEqual(installed, File.ReadAllBytes(target));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
    }

    [Fact]
    public async Task Update_RecordSaveFails_PutsThePreviousProxyBackInProcess()
    {
        using var fixture = CreateGame("update-fails", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(
            ("1.3.1", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x64)))),
            ("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x65)))));
        handler.ActiveVersion = "1.3.1";
        using var http = new HttpClient(handler);
        var (service, records, _, journalDir, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(target);
        handler.ActiveVersion = "1.3.2";
        records.SaveFaultForTesting = () => new IOException("disk full");

        var update = await service.UpdateAsync(fixture.Game);

        Assert.False(update.Success);
        Assert.Equal(installed, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsTheProxyAndTheOverrideBack()
    {
        using var fixture = CreateGame("crash-remove", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x66)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(target);
        using var death = new SimulatedDeath(journal);
        // The removal's stamp is saved before the override is restored; the next save (taking the record away) dies.
        overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
        Assert.True(died);
        Assert.False(File.Exists(target));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(target));
        Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
        Assert.NotNull(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // Review w3-journal #3: the preference is saved before the commit point. When that save fails the install rolls back
    // before any record exists; it used to save the stamped record first and then roll back past it, so a crash in that
    // rollback left a committed record whose proxy was gone.
    [Fact]
    public async Task Install_PreferenceSaveFails_RollsBackBeforeTheRecordIsEverSaved()
    {
        using var fixture = CreateGame("prefs-fail", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x67)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        using var death = new SimulatedDeath(journal);
        // The override is written after the up-front preference check: the preference file turns unreadable there.
        overrides.Registry.InsideLockForTesting = _ => File.WriteAllText(Path.Combine(fixture.Root, "prefs.json"), "{ not json");
        var saves = 0;
        records.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, run) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, "winmm.dll"));

        Assert.False(died);
        Assert.False((await run).Success);
        Assert.Equal(0, saves);
        Assert.Null(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }
}
