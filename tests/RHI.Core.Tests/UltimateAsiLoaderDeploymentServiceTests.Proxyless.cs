using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w12 final review M1: the ASI Loader's record without its proxy DLL's name — Remove follows the check's advice through,
// then Install works. With a chained original: back over RHI's proxy from UAL.HookedPath while that file exists; without
// it the proxy stays (named) and the record is still dropped. A changed proxy stays and is named (EN + PT-BR, path last).
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    private static void DropProxyName(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var record = records.Find(game, ManagedComponentKind.UltimateAsiLoader)!;
        record.Metadata.Remove("UAL.ProxyName");
        records.Upsert(record);
    }

    private static UltimateAsiLoaderDeploymentService PlainService(GameFixture fixture, HttpClient http, LinuxDeploymentRecordStore records)
        => new(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));

    [Fact]
    public async Task RecordWithoutProxyName_Remove_DeletesItsFile_RestoresTheOverride_DropsTheRecord_ThenInstallWorks()
    {
        using var fixture = CreateGame("proxyless", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5A), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        var target = Path.Combine(fixture.Bin, "version.dll");
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(UalOverride(fixture, "version"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.StartsWith("Ultimate ASI Loader removido. O registro do RHI", UiLocalizationService.PresentFor("pt-BR", remove.Message), StringComparison.Ordinal);

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(target));
        Assert.Equal("native,builtin", UalOverride(fixture, "version"));
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_LeavesAChangedProxy_AndNamesIt_PathLast()
    {
        using var fixture = CreateGame("proxyless-changed", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5B), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        var target = Path.Combine(fixture.Bin, "version.dll");
        DropProxyName(records, fixture.Game);
        File.WriteAllText(target, "changed by another mod manager");

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("changed by another mod manager", File.ReadAllText(target));
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.NotDeletedPrefix + target, remove.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.EndsWith(" Arquivos que o RHI não apagou: " + target, pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(target, "", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecordWithoutProxyName_ChainedOriginal_GoesBackOverTheProxy_ThenInstallChainsAgain()
    {
        using var fixture = CreateGame("proxyless-chain", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5C), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.False(File.Exists(hooked));
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.ChainedOriginalBackNote, remove.Message);
        Assert.Null(UalOverride(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        Assert.EndsWith(" A DLL original encadeada voltou ao próprio nome.", UiLocalizationService.PresentFor("pt-BR", remove.Message), StringComparison.Ordinal);

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(install.Success, install.Message);
        Assert.Equal(original, File.ReadAllBytes(hooked));
    }

    // The proxy is already gone (deleted outside RHI) while the chained original is there: the original goes back to its
    // own name, as the named removal does.
    [Fact]
    public async Task RecordWithoutProxyName_ProxyGone_ChainedOriginalGoesBackToItsName()
    {
        using var fixture = CreateGame("proxyless-chain-gone", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x60), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        DropProxyName(records, fixture.Game);
        File.Delete(target);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.False(File.Exists(hooked));
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.ChainedOriginalBackNote, remove.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task RecordWithoutProxyName_ChainedOriginalMissing_LeavesTheProxy_NamesIt_AndDropsTheRecord()
    {
        using var fixture = CreateGame("proxyless-chain-missing", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        File.WriteAllText(target, "third-party-original");
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5D), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = File.ReadAllBytes(target);
        DropProxyName(records, fixture.Game);
        File.Delete(hooked);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(proxy, File.ReadAllBytes(target));
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.ChainedOriginalMissingNote
                     + ProxylessRecordRemoval.NotDeletedPrefix + target, remove.Message);
        Assert.Null(UalOverride(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.EndsWith(" Arquivos que o RHI não apagou: " + target, pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(target, "", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecordWithoutProxyName_ChainedOriginal_ChangedProxy_NeitherMoves_BothNamed()
    {
        using var fixture = CreateGame("proxyless-chain-changed", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5E), "9.7.4"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = PlainService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        DropProxyName(records, fixture.Game);
        File.WriteAllText(target, "changed by another mod manager");

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("changed by another mod manager", File.ReadAllText(target));
        Assert.Equal(original, File.ReadAllBytes(hooked));
        Assert.Equal("Ultimate ASI Loader" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.ChainedOriginalKeptNote
                     + ProxylessRecordRemoval.NotDeletedPrefix + target + "; " + hooked, remove.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(target, "", StringComparison.Ordinal).Replace(hooked, "", StringComparison.Ordinal)));
    }

    // The plan hashes the listed files only after the game's hold is settled: the user's bytes the undo puts back stay.
    [PermissionDenialFact]
    public async Task RecordWithoutProxyName_HeldGame_TheUndoFinishesBeforeThePlan_TheUsersBytesStay()
    {
        using var fixture = CreateGame("proxyless-held", 0x8664);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x61), "9.7.4"));
        var j = NewUalJournal(fixture);
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        var target = Path.Combine(fixture.Bin, "version.dll");
        var installed = File.ReadAllBytes(target);
        DropProxyName(j.Records, fixture.Game);
        File.WriteAllText(target, "the user's own version.dll");
        using (await FailedUndoHold.ArmAsync(j.Journal, j.Records, fixture.Game, ManagedComponentKind.UltimateAsiLoader, target, installed))
        {
        }

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("the user's own version.dll", File.ReadAllText(target));
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + target, remove.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordWithoutProxyName_ChainedRemove_CrashBeforeTheRecordIsGone_NextStartPutsAllBack_ThenRemoveWorks()
    {
        using var fixture = CreateGame("proxyless-crash", 0x8664);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var hooked = Path.Combine(fixture.Bin, "winmmHooked.dll");
        var original = Encoding.ASCII.GetBytes("third-party-original-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(target, original);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x5F), "9.7.4"));
        var j = NewUalJournal(fixture);
        var service = new UltimateAsiLoaderDeploymentService(http, j.Records, j.Overrides, Path.Combine(fixture.Root, "cache"), j.Journal);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = File.ReadAllBytes(target);
        DropProxyName(j.Records, fixture.Game);
        using (var death = new SimulatedDeath(j.Journal))
        {
            j.Overrides.Registry.InsideLockForTesting = _ => j.Records.SaveFaultForTesting ??= death.Die;

            var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
            Assert.True(died);
            Assert.Equal(original, File.ReadAllBytes(target));

            var report = await j.RecoverAsync();

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(proxy, File.ReadAllBytes(target));
            Assert.Equal(original, File.ReadAllBytes(hooked));
            Assert.Equal("native,builtin", UalOverride(fixture, "winmm"));
            Assert.NotNull(new LinuxDeploymentRecordStore(j.Ledger).Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
            Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));

            var restarted = NewUalJournal(fixture);
            var again = new UltimateAsiLoaderDeploymentService(http, restarted.Records, restarted.Overrides, Path.Combine(fixture.Root, "cache"), restarted.Journal);
            var remove = await again.RemoveAsync(fixture.Game);

            Assert.True(remove.Success, remove.Message);
            Assert.Equal(original, File.ReadAllBytes(target));
            Assert.False(File.Exists(hooked));
            Assert.Null(UalOverride(fixture, "winmm"));
            Assert.Null(restarted.Records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
        }
    }
}
