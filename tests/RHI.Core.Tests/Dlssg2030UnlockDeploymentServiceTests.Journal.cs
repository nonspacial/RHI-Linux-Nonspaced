using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// w3/journal: the file side of RTX 20 FG Unlock's journal (#23): proxy and INI are rolled back by the next start when
// the process dies before the record is saved or removed; an update failure puts the previous proxy back (#31).
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    private (Dlssg2030UnlockDeploymentService Service, LinuxDeploymentRecordStore Records, DeploymentJournal Journal, string JournalDir, string Ledger, WineDllOverrideService Overrides)
        JournaledService(GameFixture fixture, HttpClient http)
    {
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var journalDir = Path.Combine(fixture.Root, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        return (new Dlssg2030UnlockDeploymentService(http, records, overrides, new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json")),
            Path.Combine(fixture.Root, "cache"), journal), records, journal, journalDir, ledger, overrides);
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesProxyAndIni()
    {
        using var fixture = CreateGame("crash-install", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x71), UpstreamIni("SM86")));
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
    }

    [Fact]
    public async Task Update_CrashWhileSavingTheRecord_NextStartPutsThePreviousProxyBack()
    {
        using var fixture = CreateGame("crash-update", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x72), UpstreamIni("SM86"));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(proxy);
        handler.CommitSha = "fedcba9876543210fedcba9876543210fedcba98";
        handler.Dll = CreatePe(0x8664, 320 * 1024, 0x73);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(fixture.Game));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(proxy));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
    }

    [Fact]
    public async Task Remove_CrashBeforeTheRecordIsGone_NextStartPutsProxyIniAndOverrideBack()
    {
        using var fixture = CreateGame("crash-remove", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x74), UpstreamIni("SM86")));
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        var (proxyBytes, iniBytes) = (File.ReadAllBytes(proxy), File.ReadAllBytes(ini));
        using var death = new SimulatedDeath(journal);
        overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
        Assert.True(died);
        Assert.False(File.Exists(proxy));
        Assert.False(File.Exists(ini));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
        Assert.Equal(iniBytes, File.ReadAllBytes(ini));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
    }

    // Review w3-journal #3: the preference is saved before the commit point. When that save fails the install rolls back
    // before any record exists; it used to save the stamped record first and then roll back past it, so a crash in that
    // rollback left a committed record whose proxy was gone.
    [Fact]
    public async Task Install_PreferenceSaveFails_RollsBackBeforeTheRecordIsEverSaved()
    {
        using var fixture = CreateGame("prefs-fail", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x77), UpstreamIni("SM86"));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        using var death = new SimulatedDeath(journal);
        // The override is written after the up-front preference check: the preference file turns unreadable there.
        overrides.Registry.InsideLockForTesting = _ => File.WriteAllText(Path.Combine(fixture.Root, "prefs.json"), "{ not json");
        var saves = 0;
        records.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, run) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll"));

        Assert.False(died);
        Assert.False((await run).Success);
        Assert.Equal(0, saves);
        Assert.Null(new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.Empty(Directory.GetFiles(journalDir));
    }
}
