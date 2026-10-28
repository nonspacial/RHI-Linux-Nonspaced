using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// Correction round 1 of lane w12/mfg (review w12-review-mfg.md): F1 hold text before any file check, F3 the recorded bitness
// (never an unattended swap to the other architecture), F5 one "could not be checked" line for a record without the proxy's
// hash.
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    [PermissionDenialFact]
    public async Task HeldGame_CheckAndUpdate_SayTheHoldsRestartText_BeforeAnyFileCheck()
    {
        using var fixture = CreateGame("correction-held", 0x8664);
        using var release = new UalRelease().Add("9.7.4", 0x90);
        release.Add("9.7.5", 0x92);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var (journal, overrides) = SimulatedDeath.NewJournal(Path.Combine(fixture.Root, "journal"), records);
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        release.Version = "9.7.5";
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var halfUndone = CreatePe(0x8664, 64 * 1024, 0x94);
        using var hold = await FailedUndoHold.ArmAsync(journal, records, fixture.Game, ManagedComponentKind.UltimateAsiLoader, proxy, halfUndone);
        var lookups = release.ApiRequests;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, check.Message);
        Assert.False(update.Success);
        Assert.Equal(DeploymentJournal.EarlierUndoFailedMessage, update.Message);
        Assert.Equal(lookups, release.ApiRequests);
        Assert.Equal(halfUndone, File.ReadAllBytes(proxy));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("no-entry")]
    public async Task RecordWithoutTheProxysHash_IsCouldNotCheck_WithTheOneReason(string shape)
    {
        using var fixture = CreateGame("correction-hashless-" + shape, 0x8664);
        using var release = new UalRelease().Add("9.7.4", 0x9A);
        release.Add("9.7.5", 0x9C);
        release.Version = "9.7.4";
        using var http = new HttpClient(release);
        var (service, records) = UalService(fixture, http);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        var proxy = Path.Combine(fixture.Bin, "winmm.dll");
        var record = records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!;
        var entry = record.Files.Single(file => string.Equals(file.Path, proxy, StringComparison.Ordinal));
        if (shape == "blank") entry.Sha256 = "";
        else record.Files.Remove(entry);
        records.Upsert(record);
        release.Version = "9.7.5";

        var check = await service.CheckManagedUpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.NoRecordedHashPrefix + proxy, check.Message);
    }
}
