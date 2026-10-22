using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// Wave 11 follow-up (nr-gpu re-review findings 1, 3 and 4): the two guards only the reviewer's probes pinned (adopted
/// here as tests), the check line that names a DLL changed outside RHI while the add-on has its own update, and the
/// blank NR version a Feeder install writes when it staged none.
/// </summary>
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    // E3 + E5 (reviewer's probe 1, adopted): the NR DLL was changed outside RHI while the add-on has an update. The check
    // is an update (the add-on's) whose line also says the DLL was changed outside RHI and is left as it is (E5: the same
    // run, not only the next). Update All updates the add-on only — never the NR swap the update arm would have to
    // refuse ("modified outside RHI") — then has nothing left but "could not check"; the user's DLL is untouched.
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task FollowUp_ChangedOutside_WithAnAddonUpdate_UpdatesTheAddonOnly_SaysSo_NeverFails(string language)
    {
        using var f = await LegacyLatestOn50xxAsync();
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        record.Version = "0.12.0";
        f.Records.Upsert(record);
        File.WriteAllBytes(NrPath(f), Dll(ForAll));
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out _);

        var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.False(check.CouldNotCheck, check.Message);
        Assert.EndsWith(LinuxManagedComponentService.NrChangedOutsideMessage, check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LinuxManagedComponentService.NrMovePrefix, check.Message, StringComparison.Ordinal);
        var line = UiLocalizationService.PresentFor(language, check.Message);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
        if (language == "pt-BR") Assert.DoesNotContain("changed or removed outside RHI", line, StringComparison.Ordinal);

        var r1 = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);
        var r2 = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal((1, 0), (r1.ChangedCount, r1.FailedCount));
        Assert.Equal((0, 0, 1), (r2.ChangedCount, r2.FailedCount, r2.UncheckedCount));
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
        Assert.Equal("0.13.1-beta.1", f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!.Version);
    }

    // E3 (reviewer's probe 2, adopted): an NR DLL RHI cannot read counts as "not matching" (the IO catch), so the check is
    // "could not check" and Update All neither fails nor writes a history row.
    [PermissionDenialFact]
    public async Task FollowUp_AnUnreadableNrDll_IsCouldNotCheck_NeverFails()
    {
        using var f = await LegacyLatestOn50xxAsync();
        PermissionDenial.MakeUnreadable(NrPath(f));
        try
        {
            var managed = Managed(f, RtxGeneration.Rtx40, out _, out var log);

            var check = await managed.CheckManagedUpdateAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);
            var all = await new LinuxUpdateAllService(f.Records, managed, new NeverRunning()).RunAsync([f.Game], _ => [], LinuxUpdateRunMode.Manual);

            Assert.True(check.CouldNotCheck, check.Message);
            Assert.Contains(LinuxManagedComponentService.NrChangedOutsideMessage, check.Message, StringComparison.Ordinal);
            Assert.Equal((0, 0, 1), (all.ChangedCount, all.FailedCount, all.UncheckedCount));
            Assert.Empty(log.GetAll());
        }
        finally
        {
            File.SetUnixFileMode(NrPath(f), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // E6: a Feeder install that staged no NR version records it as "" (Dlss5FeederFullDeploymentService). A blank
    // recorded version is no version: the move's history row takes the version told at move time (the cache stamp),
    // never "" or "(null)".
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task FollowUp_ABlankRecordedNrVersion_HistoryFromIsTheVersionToldAtMoveTime(string blank)
    {
        using var f = await LegacyLatestOn50xxAsync();
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        record.Metadata[NeuralRenderingDllSwap.MetaDllVersion] = blank;
        f.Records.Upsert(record);
        var managed = Managed(f, RtxGeneration.Rtx40, out _, out var log);

        var updated = await managed.UpdateManagedAsync(f.Game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(Dll(ForAll), File.ReadAllBytes(NrPath(f)));
        var entry = Assert.Single(log.GetAll());
        Assert.Equal(((string?)For50, ForAll), (entry.FromVersion, entry.ToVersion));
    }
}
