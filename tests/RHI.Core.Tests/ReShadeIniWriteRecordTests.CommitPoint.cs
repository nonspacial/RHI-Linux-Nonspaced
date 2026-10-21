using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration (#204 × #206): the ReShade.ini writes this lane made journaled on ReShade's record — preset install and
// removal, the per-game Apply of the ReShade settings (one game of "Apply to all games") — and Deploy ReShade.ini, each with
// its commit write landing and then throwing: finished as committed in both seam shapes (the file and the record that
// names its bytes stay; Apply to all counts the game as updated), and crash recovery at every write agrees.
public sealed partial class ReShadeIniWriteRecordTests
{
    public static TheoryData<LandedShape, string> LandedIniOperations() => LandedRow.ShapesTimes("preset-install", "preset-remove", "apply", "deploy-ini");

    private async Task<(Installed Fx, CommitPointCase Case)> IniRigAsync(string name, string operation)
    {
        var fx = await InstalledAsync(name);
        if (operation == "preset-remove") Assert.True((await fx.Presets.InstallAsync(fx.Game, fx.PresetSource)).Success);
        Func<Task<ComponentOperationResult>> act = operation switch
        {
            "preset-install" => () => fx.Presets.InstallAsync(fx.Game, fx.PresetSource),
            "preset-remove" => () => fx.Presets.RemoveAsync(fx.Game, "Mine.ini"),
            "apply" => () => fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings),
            _ => () => fx.Ini.DeployReShadeIniAsync(fx.Game, Settings)
        };
        var root = Path.GetDirectoryName(Path.GetDirectoryName(fx.Bin)!)!;
        return (fx, new CommitPointCase(root, [Path.Combine(root, "Game"), fx.Game.PrefixPath!], fx.Records, fx.LedgerPath, fx.Journal, fx.JournalDir, act));
    }

    [Theory]
    [MemberData(nameof(LandedIniOperations))]
    [Trait("Category", "CommitPoint")]
    public async Task IniWrite_CommitWriteLandsThenThrows_IsKept_AndTheRecordNamesTheBytes(LandedShape shape, string operation)
    {
        var (fx, c) = await IniRigAsync($"cp-kept-{operation}-{shape}", operation);

        // Every operation here makes one ledger write: its stamped record (a preset's removal changes ReShade's record; its
        // result reads as a removal).
        await LandedRow.RunAsync(fx.Records, shape, at: 1, c.Act, removal: operation == "preset-remove");

        AssertRecordDescribesTheIni(fx);
        var presets = ReShadePresetService.Read(new LinuxDeploymentRecordStore(fx.LedgerPath).Find(fx.Game, ManagedComponentKind.ReShade));
        switch (operation)
        {
            case "preset-install":
                Assert.Equal("Mine.ini", Assert.Single(presets).FileName);
                Assert.True(File.Exists(Path.Combine(fx.Bin, "Mine.ini")));
                break;
            case "preset-remove":
                Assert.Empty(presets);
                Assert.False(File.Exists(Path.Combine(fx.Bin, "Mine.ini")));
                break;
            case "apply":
                Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
                break;
        }
        Assert.Empty(CommitPointAgreement.JournalEntries(c));
    }

    [Theory]
    [MemberData(nameof(LandedIniOperations))]
    [Trait("Category", "CommitPoint")]
    public async Task IniWrite_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape, string operation)
    {
        var results = await CommitPointAgreement.AssertEveryWriteAsync(async name => (await IniRigAsync($"cp-{operation}-{shape}-{name}", operation)).Case, shape);
        Assert.True(results[^1].Success, results[^1].Message);
    }

    // One game of a two-game "Apply to all games": its commit write lands and then throws. It counts as updated (its file
    // and record are kept) and is named for the line's landed note (w8 follow-up C11), and the next game is applied as usual.
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task ApplyToAll_OneGamesCommitWriteLandsThenThrows_ItCountsAsUpdated_AndTheBatchGoesOn(LandedShape shape)
    {
        var a = await InstalledAsync("cp-all-a-" + shape);
        var b = await InstalledAsync("cp-all-b-" + shape, shareWith: a);
        LandedLedgerWrite.Arm(a.Records, shape, at: 1);

        var batch = await a.Ini.ApplyReShadeSettingsToAllAsync([a.Game, b.Game], Settings);
        LandedLedgerWrite.Disarm(a.Records);

        Assert.Equal(new IniBatchResult(2, 0, 0) { Landed = [a.Game.Name] }, batch);
        foreach (var fx in new[] { a, b })
        {
            Assert.Contains("VariableListUseTabs=0", File.ReadAllText(fx.IniPath));
            AssertRecordDescribesTheIni(fx);
        }
        Assert.Empty(Directory.GetFiles(a.JournalDir, "*.json"));
    }

    // Not committed (the save failed before it landed): the Apply still fails as before and says nothing landed.
    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Apply_LedgerSaveFailsBeforeItLands_StillThrows_AndTheFileIsBack()
    {
        var fx = await InstalledAsync("cp-apply-fails");
        var before = File.ReadAllBytes(fx.IniPath);
        fx.Records.SaveFaultForTesting = () => new IOException("disk full (test)");

        var error = await Assert.ThrowsAsync<IOException>(() => fx.Ini.ApplyReShadeSettingsAsync(fx.Game, Settings));
        fx.Records.SaveFaultForTesting = null;

        Assert.Equal("disk full (test)", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fx.IniPath));
    }
}
