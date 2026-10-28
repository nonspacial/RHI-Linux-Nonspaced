using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// w12 final review M1: the check's way out for a record without its proxy DLL's name ("Remove it, then Install it again")
// is followed to the end. Remove deletes the listed files that still hold RHI's bytes, restores the recorded override and
// drops the record; a changed file stays and is named (EN + PT-BR, path last); Install then works again.
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    private static void DropProxyName(LinuxDeploymentRecordStore records, DetectedGame game)
    {
        var record = records.Find(game, ManagedComponentKind.Rtx40MfgStandalone)!;
        record.Metadata.Remove("UniversalMFG.ProxyName");
        record.Metadata.Remove("RTX40MFG.ProxyName");
        records.Upsert(record);
    }

    private static string? Override(GameFixture fixture, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task RecordWithoutProxyName_Remove_DeletesItsFile_RestoresTheOverride_DropsTheRecord_ThenInstallWorks()
    {
        using var fixture = CreateGame("proxyless", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x6A)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("Universal MFG" + ProxylessRecordRemoval.RemovedSuffix, remove.Message);
        Assert.False(File.Exists(target));
        Assert.Null(Override(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.StartsWith("Universal MFG removido. O registro do RHI não tinha o nome da DLL proxy", pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt));
        Assert.Equal(remove.Message, UiLocalizationService.PresentFor("en-US", remove.Message));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(target));
        Assert.Equal("native,builtin", Override(fixture, "winmm"));
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_LeavesAChangedFile_AndNamesIt_PathLast()
    {
        using var fixture = CreateGame("proxyless-changed", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x6B)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        DropProxyName(records, fixture.Game);
        var changed = CreatePe(0x8664, 512 * 1024, 0x7E);
        File.WriteAllBytes(target, changed);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(changed, File.ReadAllBytes(target));
        Assert.Equal("Universal MFG" + ProxylessRecordRemoval.RemovedSuffix + ProxylessRecordRemoval.NotDeletedPrefix + target, remove.Message);
        Assert.Null(Override(fixture, "winmm"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        var pt = UiLocalizationService.PresentFor("pt-BR", remove.Message);
        Assert.EndsWith(" Arquivos que o RHI não apagou: " + target, pt, StringComparison.Ordinal);
        Assert.Empty(UiLocalizationService.EnglishMarkersIn(pt.Replace(target, "", StringComparison.Ordinal)));
    }

    // A listed file outside the record's binary folder (an edited ledger) is never deleted, even with a matching hash; a
    // listed file without a hash, or one already gone, is named too.
    [Fact]
    public async Task RecordWithoutProxyName_Remove_NeverDeletesOutsideItsFolder_NamesUnhashedAndMissingFiles()
    {
        using var fixture = CreateGame("proxyless-outside", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x6C)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var outside = Path.Combine(fixture.Root, "outside.dll");
        var unhashed = Path.Combine(fixture.Bin, "unhashed.dll");
        var missing = Path.Combine(fixture.Bin, "missing.dll");
        File.WriteAllBytes(outside, [7, 7, 7]);
        File.WriteAllBytes(unhashed, [8, 8, 8]);
        var record = records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!;
        record.Files.Add(new LinuxDeployedFile { Path = outside, Sha256 = FileHash.Sha256Hex(outside) });
        record.Files.Add(new LinuxDeployedFile { Path = unhashed, Sha256 = "" });
        record.Files.Add(new LinuxDeployedFile { Path = missing, Sha256 = new string('a', 64) });
        records.Upsert(record);
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(unhashed));
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + string.Join("; ", outside, unhashed, missing), remove.Message, StringComparison.Ordinal);
    }

    // The plan hashes the listed files only after the game's hold is settled (toolkit rule for hashes read before a journaled
    // operation): a held game whose undo can finish now puts the user's bytes back first, and those are never deleted, even
    // though the half-undone file the hold left matched RHI's record.
    [PermissionDenialFact]
    public async Task RecordWithoutProxyName_HeldGame_TheUndoFinishesBeforeThePlan_TheUsersBytesStay()
    {
        using var fixture = CreateGame("proxyless-held", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x6E)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, _, _, _) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(target);
        DropProxyName(records, fixture.Game);
        var users = CreatePe(0x8664, 512 * 1024, 0x7F);
        File.WriteAllBytes(target, users); // the user's own DLL under that name
        // An earlier change put RHI's bytes back over it and could not be undone (folder read-only): the file now matches
        // the record, and the undo, once it can run, puts the user's bytes back.
        using (await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.Rtx40MfgStandalone, target, installed))
        {
        }

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(users, File.ReadAllBytes(target));
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + target, remove.Message, StringComparison.Ordinal);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
    }

    // Crash after the removal's first writes (file deleted, override restored), before the record is gone: the next start
    // puts the file, the override and the record back; Remove after that restart still works.
    [Fact]
    public async Task RecordWithoutProxyName_Remove_CrashBeforeTheRecordIsGone_NextStartPutsAllBack_ThenRemoveWorks()
    {
        using var fixture = CreateGame("proxyless-crash", 0x8664, GraphicsApi.DirectX12);
        using var handler = Rtx40Handler.WithVersions(("1.3.2", CreateZip(("RTXMFG.dll", CreatePe(0x8664, 512 * 1024, 0x6D)))));
        using var http = new HttpClient(handler);
        var (service, records, journal, journalDir, ledger, overrides) = JournaledService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var target = Path.Combine(fixture.Bin, "winmm.dll");
        var installed = File.ReadAllBytes(target);
        DropProxyName(records, fixture.Game);
        using (var death = new SimulatedDeath(journal))
        {
            overrides.Registry.InsideLockForTesting = _ => records.SaveFaultForTesting ??= death.Die;

            var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(fixture.Game));
            Assert.True(died);
            Assert.False(File.Exists(target));

            var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            Assert.Equal(installed, File.ReadAllBytes(target));
            Assert.Equal("native,builtin", Override(fixture, "winmm"));
            var back = new LinuxDeploymentRecordStore(ledger).Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone);
            Assert.NotNull(back);
            Assert.False(back!.Metadata.ContainsKey("UniversalMFG.ProxyName"));
            Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));

            // The restart: fresh stores over the same ledger and journal.
            var restartedRecords = new LinuxDeploymentRecordStore(ledger);
            var (restartedJournal, restartedOverrides) = SimulatedDeath.NewJournal(journalDir, restartedRecords);
            var restarted = new Rtx40MfgStandaloneDeploymentService(http, restartedRecords, restartedOverrides,
                new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"), restartedJournal);
            var remove = await restarted.RemoveAsync(fixture.Game);

            Assert.True(remove.Success, remove.Message);
            Assert.False(File.Exists(target));
            Assert.Null(Override(fixture, "winmm"));
            Assert.Null(restartedRecords.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone));
        }
    }
}
