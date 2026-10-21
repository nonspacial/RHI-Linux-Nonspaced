using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration (#204 × #217): the ⚙ LUT switch (its own journaled operation on RenoDX's record) with its commit write
// landing and then throwing: finished as committed in both seam shapes — the line stays out and the file keeps the mode
// the switch left — and crash recovery at the write agrees.
public sealed partial class RenoDxEngineIniLutTests
{
    private async Task<(LutFixture Fx, CommitPointCase Case)> LutSwitchRigAsync(string name, bool on)
    {
        var fx = Fixture(name);
        File.WriteAllText(fx.EngineIni, Before);
        File.SetUnixFileMode(fx.EngineIni, Open);
        Assert.True((await fx.Service.InstallAsync(fx.Game, fx.Files)).Success);
        if (on) Assert.True((await fx.Service.SetEngineIniLutAsync(fx.Game, on: false)).Success);
        return (fx, new CommitPointCase(fx.Root, [Path.Combine(fx.Root, "Game"), Path.Combine(fx.Root, "pfx")], fx.Records, fx.Journal.LedgerPath,
            fx.Journal.Journal, fx.Journal.JournalDir, () => fx.Service.SetEngineIniLutAsync(fx.Game, on),
            Extra: () => $"engine-ini-mode={File.GetUnixFileMode(fx.EngineIni)}"));
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task LutSwitch_CommitWriteLandsThenThrows_IsKept_AndCrashRecoveryAgrees(LandedShape shape)
    {
        var (fx, c) = await LutSwitchRigAsync($"cp-lut-off-{shape}", on: false);
        await LandedRow.RunAsync(fx.Records, shape, at: 1, c.Act);
        Assert.DoesNotContain("r.LUT.UpdateEveryFrame", File.ReadAllText(fx.EngineIni));
        Assert.Equal(Open, File.GetUnixFileMode(fx.EngineIni)); // RHI's lock went with its line
        Assert.Equal(RenoDxEngineIni.Off, new LinuxDeploymentRecordStore(fx.Journal.LedgerPath).Find(fx.Game, ManagedComponentKind.RenoDX)!
            .Metadata[RenoDxEngineIni.LutChoiceMetadataKey]);
        Assert.Empty(CommitPointAgreement.JournalEntries(c));

        foreach (var on in new[] { false, true })
        {
            var results = await CommitPointAgreement.AssertEveryWriteAsync(async name => (await LutSwitchRigAsync($"cp-lut-{on}-{shape}-{name}", on)).Case, shape);
            Assert.True(results[^1].Success, results[^1].Message);
        }
    }
}
