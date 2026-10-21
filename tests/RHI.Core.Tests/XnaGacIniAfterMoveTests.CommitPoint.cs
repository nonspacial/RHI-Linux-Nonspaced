using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration (#204 × #214): the GAC ReShade.ini rewrite after a repair, with its commit write (ReShade's stamped record
// and the copy's new hash) landing and then throwing: finished as committed in both seam shapes — the copy names the new
// folder, the record names its bytes, and no "not updated" note says otherwise — the same files and record a crash right
// after that write leaves once the next start recovers.
public sealed partial class XnaGacIniAfterMoveTests
{
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task TheRewritesCommitWriteLandsThenThrows_TheNewCopyIsKept_NoNote_AndCrashRecoveryAgrees(LandedShape shape)
    {
        var live = await InstalledAsync("cp-landed-" + shape);
        var before = File.ReadAllBytes(live.GacIni);
        var moved = live.MoveLibrary();
        var newBase = XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!);
        // The repair's own commit is a staged write; the rewrite's stamped record save is the first ledger save.
        LandedLedgerWrite.Arm(live.Records, shape, at: 1);

        var result = live.Components().RepairAfterMove(moved.Game);
        LandedLedgerWrite.Disarm(live.Records);

        Assert.True(result.Success, result.Message);
        Assert.Equal("", result.Note);
        Assert.Equal(DeploymentRelocation.RepairDonePrefix + "1", result.Message);
        Assert.Equal(ReShadeIniManager.WithInstallBasePath(before, newBase), File.ReadAllBytes(moved.GacIni));
        AssertRecordMatchesTheCopy(live.LedgerPath, moved);
        Assert.Empty(Directory.GetFiles(live.JournalDir));

        // The same write landing and the process dying there: the next start keeps the same copy and record.
        var crash = await InstalledAsync("cp-crash-" + shape);
        var crashBefore = File.ReadAllBytes(crash.GacIni);
        var crashMoved = crash.MoveLibrary();
        using (var death = new SimulatedDeath(crash.Journal))
        {
            crash.Records.SaveWrittenFaultForTesting = () => death.Die();
            var components = crash.Components();
            var (died, _) = await death.RunUntilDeathAsync(() => Task.Run(() => components.RepairAfterMove(crashMoved.Game)));
            Assert.True(died);
            await SimulatedDeath.RecoverAsync(crash.JournalDir, crash.LedgerPath);
            var crashBase = XnaGacReShade.WindowsPathOf(crashMoved.Game.PrefixPath!, crashMoved.Game.BinaryDirectory!);
            Assert.Equal(ReShadeIniManager.WithInstallBasePath(crashBefore, crashBase), File.ReadAllBytes(crashMoved.GacIni));
            AssertRecordMatchesTheCopy(crash.LedgerPath, crashMoved);
        }
    }
}
