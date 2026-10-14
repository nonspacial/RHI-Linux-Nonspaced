using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: the file side of UAL's journal (#23) — the chained original's move and the proxy are rolled back by the
// next start when the process dies before the record is saved or removed — and owned-file rollback in process (#31).
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    private sealed record UalJournal(DeploymentJournal Journal, string Dir, string Ledger, LinuxDeploymentRecordStore Records, WineDllOverrideService Overrides)
    {
        public Task<JournalRecoveryReport?> RecoverAsync()
        {
            var ledger = new LinuxDeploymentRecordStore(Ledger);
            return LinuxDeploymentRecovery.RecoverAsync(
                LinuxDeploymentRecovery.CreateJournal(new WineDllOverrideService(new WineRegistryEditor(_ => false)), Dir, _ => false, ledger), ledger);
        }
    }

    private static UalJournal NewUalJournal(GameFixture fixture)
    {
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var dir = Path.Combine(fixture.Root, "journal");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        return new UalJournal(LinuxDeploymentRecovery.CreateJournal(overrides, dir, _ => false, records), dir, ledger, records, overrides);
    }

    private static string? UalOverride(GameFixture fixture, string dll)
        => WineDllOverrideService.TryReadOverride(UserReg(fixture), dll, "Game.exe");

    [Fact]
    public async Task ChainedInstall_CrashWhileSavingTheRecord_NextStartPutsTheOriginalBackAtItsName()
    {
        using var fixture = CreateGame("crash-chain", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x51), "9.7.4"));
        var j = NewUalJournal(fixture);
        using var death = new SimulatedDeath(j.Journal);
        j.Records.SaveFaultForTesting = death.Die;
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, "winmm.dll"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmmHooked.dll")));

        var report = await j.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmmHooked.dll")));
        Assert.Null(UalOverride(fixture, "winmm"));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
        Assert.Empty(Directory.GetFiles(j.Dir));
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesTheProxy()
    {
        using var fixture = CreateGame("crash-proxy", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x52), "9.7.4"));
        var j = NewUalJournal(fixture);
        using var death = new SimulatedDeath(j.Journal);
        j.Records.SaveFaultForTesting = death.Die;
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, "version.dll"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "version.dll")));

        await j.RecoverAsync();

        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll"))); // before: left behind, refused as foreign afterwards
        Assert.Null(UalOverride(fixture, "version"));
    }

    [Fact]
    public async Task Update_CrashWhileSavingTheRecord_NextStartPutsThePreviousProxyBack()
    {
        using var fixture = CreateGame("crash-update", 0x8664);
        var zip1 = CreateUalZip(0x8664, 0x53);
        var zip2 = CreateUalZip(0x8664, 0x54);
        var handler = new SequenceReleaseHandler(
            CreateReleaseJson(zip1, "9.7.4", "https://github.com/rhi-tests/ual-1.zip"), zip1,
            CreateReleaseJson(zip2, "9.7.5", "https://github.com/rhi-tests/ual-2.zip"), zip2);
        using var http = new HttpClient(handler);
        var j = NewUalJournal(fixture);
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "version.dll");
        var installed = File.ReadAllBytes(proxy);
        handler.AdvanceRelease();
        using var death = new SimulatedDeath(j.Journal);
        j.Records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(fixture.Game));
        Assert.True(died);
        Assert.NotEqual(installed, File.ReadAllBytes(proxy));

        var report = await j.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(installed, File.ReadAllBytes(proxy));
        Assert.Equal("native,builtin", UalOverride(fixture, "version"));
    }

    [Fact]
    public async Task ChainedRemove_CrashBeforeTheRecordIsGone_NextStartPutsProxyOriginalAndOverrideBack()
    {
        using var fixture = CreateGame("crash-remove", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x55), "9.7.4"));
        var j = NewUalJournal(fixture);
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = File.ReadAllBytes(target);
        using var death = new SimulatedDeath(j.Journal);
        // The removal's stamp is saved before the override is restored; the next save (taking the record away) dies.
        j.Overrides.Registry.InsideLockForTesting = _ => j.Records.SaveFaultForTesting ??= death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
        Assert.True(died);
        Assert.Equal(original, File.ReadAllBytes(target));

        var report = await j.RecoverAsync();

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxy, File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmmHooked.dll")));
        Assert.Equal("native,builtin", UalOverride(fixture, "winmm"));
        Assert.NotNull(new LinuxDeploymentRecordStore(j.Ledger).Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task ChainedRemove_RecordRemovalFails_PutsProxyAndOriginalBackInProcess()
    {
        using var fixture = CreateGame("remove-fails", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x56), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"));
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = File.ReadAllBytes(target);
        overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= () => new IOException("disk full");

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(proxy, File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Bin, "winmmHooked.dll")));
        Assert.Equal("native,builtin", UalOverride(fixture, "winmm"));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
    }
}
