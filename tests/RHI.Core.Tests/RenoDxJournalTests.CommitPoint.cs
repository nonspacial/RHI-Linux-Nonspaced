using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration (#204 × #205): RenoDX's journaled install, update with a renamed add-on, local drop, removal and the ⚙
// peak brightness, each with its commit write landing and then throwing: finished as committed in both seam shapes (the
// add-on, the [renodx] keys in the shared ReShade.ini, the locked Engine.ini and the record stay), and crash recovery at
// every ledger write agrees with the in-process outcome.
public sealed partial class RenoDxJournalTests
{
    public static TheoryData<LandedShape, string> LandedOperations() => LandedRow.ShapesTimes("install", "update-renamed", "drop", "remove", "peak");

    private async Task<(JournalFixture Fx, CommitPointCase Case)> CommitPointRigAsync(string name, string operation)
    {
        var fx = Fixture(name);
        if (operation != "install") Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        Func<Task<ComponentOperationResult>> act = operation switch
        {
            "install" => () => fx.Service.InstallAsync(fx.Game, fx.Files),
            "update-renamed" => () => fx.Renamed().UpdateAsync(fx.Game, fx.Files),
            "drop" => () => fx.Service.InstallLocalAddonAsync(fx.Game, fx.Dropped()),
            "peak" => () => new RenoDxPeakNits(fx.Records, fx.Journal, _ => null).SetForGameAsync(fx.Game, 1500),
            _ => () => fx.Service.RemoveAsync(fx.Game)
        };
        var ini = Path.Combine(fx.Bin, "ReShade.ini");
        // The game and the prefix (the shared ReShade.ini is read through its link in the game folder); the Engine.ini lock
        // and the link itself as extra state.
        return (fx, new CommitPointCase(fx.Root, [Path.Combine(fx.Root, "Game"), fx.Prefix], fx.Records, fx.LedgerPath, fx.Journal, fx.JournalDir, act,
            Extra: () => $"engine-ini-mode={File.GetUnixFileMode(fx.EngineIni)} reshade-ini-link={new FileInfo(ini).LinkTarget is not null}"));
    }

    [Theory]
    [MemberData(nameof(LandedOperations))]
    [Trait("Category", "CommitPoint")]
    public async Task CommitWriteLandsThenThrows_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape, string operation)
    {
        var results = await CommitPointAgreement.AssertEveryWriteAsync(
            async name => (await CommitPointRigAsync($"cp-{operation}-{shape}-{name}", operation)).Case, shape);

        Assert.True(results[^1].Success, results[^1].Message);
        Assert.StartsWith(operation == "remove" ? DeploymentScope.LandedRemovalPrefix : DeploymentScope.LandedChangePrefix, results[^1].Message, StringComparison.Ordinal);
        if (operation == "remove") Assert.False(results[0].Success); // the removal's stamp: nothing was removed yet
    }

    [Theory]
    [MemberData(nameof(LandedOperations))]
    [Trait("Category", "CommitPoint")]
    public async Task CommitWriteLandsThenThrows_IsKept_TheLinkAndTheEngineIniLockStay(LandedShape shape, string operation)
    {
        var (fx, c) = await CommitPointRigAsync($"cp-kept-{operation}-{shape}", operation);

        await LandedRow.RunAsync(fx.Records, shape, at: operation == "remove" ? 2 : 1, c.Act, removal: operation == "remove");

        var record = new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.RenoDX);
        if (operation == "remove")
        {
            Assert.Null(record);
            Assert.Empty(Directory.GetFiles(fx.Bin, "renodx-*"));
        }
        else AssertRecordsDescribeTheFiles(new LinuxDeploymentRecordStore(fx.LedgerPath), fx);
        Assert.Equal(fx.Shared, new FileInfo(Path.Combine(fx.Bin, "ReShade.ini")).LinkTarget);
        Assert.Equal(Locked, File.GetUnixFileMode(fx.EngineIni));
        Assert.Empty(CommitPointAgreement.JournalEntries(c));
    }
}
