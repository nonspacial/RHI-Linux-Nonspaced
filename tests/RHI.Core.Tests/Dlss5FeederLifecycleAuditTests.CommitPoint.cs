using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204: the DX9 Feeder's install and removal save the Feeder's record and ReShade's translated record in ONE ledger write.
// When that write lands and then throws (a failed directory fsync), the install is finished as committed: both records
// are kept and nothing is undone — no file, no dxgi.dll/d3d9.dll rename, no Wine override (the in-process rollback used
// to put ReShade back at d3d9.dll under a record naming dxgi.dll, so every later removal failed in PrepareRestoreAsync).
// #213: the Feeder tracks the file behind a linked ReShade.ini, never the link.
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    private static CommitPointCase FeederCase(FeederFixture f, Func<Task<ComponentOperationResult>> act)
    {
        var root = Path.GetDirectoryName(f.Bin)!;
        return new CommitPointCase(root, [f.Bin, f.Game.PrefixPath!], f.Records, f.RecordsPath, f.Journal!, f.JournalDirectory, act);
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Dx9Install_CommitWriteLandsThenThrows_KeepsBothRecordsAndTheTranslation_AndItRemovesLater(LandedShape shape)
    {
        using var f = new FeederFixture(dx9: true, journaled: true);
        var dxgi = Path.Combine(f.Bin, "dxgi.dll");
        LandedLedgerWrite.Arm(f.Records, shape, at: 1); // the install's commit write

        var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);
        LandedLedgerWrite.Disarm(f.Records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, result.Message, StringComparison.Ordinal);
        var fresh = new LinuxDeploymentRecordStore(f.RecordsPath);
        Assert.NotNull(fresh.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
        var reShade = fresh.Find(f.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(dxgi, Assert.Single(reShade.Files).Path);
        Assert.True(File.Exists(dxgi));
        Assert.True(File.Exists(Path.Combine(f.Bin, "dgVoodoo.conf")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(f), "dxgi", "Game.exe"));
        Assert.Empty(Directory.GetFiles(f.JournalDirectory, "*.json"));

        var removed = await f.Service.RemoveAsync(f.Game);

        Assert.True(removed.Success, removed.Message);
        var after = new LinuxDeploymentRecordStore(f.RecordsPath).Find(f.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(after.Files).Path);
        Assert.Equal(after.Files[0].Sha256, FileHash.Sha256Hex(after.Files[0].Path), ignoreCase: true);
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Dx9Remove_CommitWriteLandsThenThrows_StaysRemoved_WithReShadeBackAtD3d9(LandedShape shape)
    {
        using var f = new FeederFixture(dx9: true, journaled: true);
        Assert.True((await f.Install(Dlss5FeederConsumer.RenoDxDlss5)).Success);
        LandedLedgerWrite.Arm(f.Records, shape, at: 2); // 1 = the removal's stamp, 2 = the commit write

        var result = await f.Service.RemoveAsync(f.Game);
        LandedLedgerWrite.Disarm(f.Records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedRemovalPrefix, result.Message, StringComparison.Ordinal);
        var fresh = new LinuxDeploymentRecordStore(f.RecordsPath);
        Assert.Null(fresh.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
        var reShade = fresh.Find(f.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(reShade.Files).Path);
        Assert.True(File.Exists(Path.Combine(f.Bin, "d3d9.dll")));
        Assert.False(File.Exists(Path.Combine(f.Bin, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(f.Bin, "dgVoodoo.conf")));
        Assert.False(File.Exists(Path.Combine(f.Bin, "dlss5-feed.addon64")));
        Assert.Empty(Directory.GetFiles(f.JournalDirectory, "*.json"));
    }

    [Trait("Category", "CommitPoint")]
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    public async Task Dx9InstallAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var fixtures = new List<FeederFixture>();
        try
        {
            Task<CommitPointCase> Install(string _)
            {
                var f = new FeederFixture(dx9: true, journaled: true);
                fixtures.Add(f);
                return Task.FromResult(FeederCase(f, () => f.Install(Dlss5FeederConsumer.RenoDxDlss5)));
            }
            async Task<CommitPointCase> Remove(string _)
            {
                var f = new FeederFixture(dx9: true, journaled: true);
                fixtures.Add(f);
                Assert.True((await f.Install(Dlss5FeederConsumer.DeepFriedChicken)).Success);
                return FeederCase(f, () => f.Service.RemoveAsync(f.Game));
            }

            var install = await CommitPointAgreement.AssertEveryWriteAsync(Install, shape);
            var removal = await CommitPointAgreement.AssertEveryWriteAsync(Remove, shape);

            Assert.True(install[^1].Success); // the commit write is the last
            Assert.True(removal[^1].Success);
            Assert.False(removal[0].Success); // the removal's stamp: rolled back, nothing removed
        }
        finally
        {
            foreach (var f in fixtures) f.Dispose();
        }
    }

    // #213: ReShade.ini linked to the user's shared file. The Feeder's search-path write follows the link; its preset edit
    // refuses a link, so the install fails — and its rollback must keep the link a link and put the shared file back byte
    // for byte (it used to track the link: the rollback replaced it with a plain copy and left RHI's lines in the shared file).
    [Trait("Category", "CommitPoint")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_LinkedReShadeIni_FailsAndRollsBack_TheLinkStaysALink_AndTheSharedFileIsBack(bool dx9)
    {
        using var f = new FeederFixture(dx9: dx9, journaled: true);
        File.Delete(f.IniPath);
        var shared = ReShadeLinkedIniJournalTests.LinkSharedIni(f.Bin);
        try
        {
            var before = System.Text.Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore);

            var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

            Assert.False(result.Success, result.Message);
            ReShadeLinkedIniJournalTests.AssertLinked(f.Bin, shared, before);
            Assert.Null(f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder));
            Assert.Empty(Directory.GetFiles(f.JournalDirectory, "*.json"));
            if (dx9)
                Assert.Equal(Path.Combine(f.Bin, "d3d9.dll"), Assert.Single(f.Records.Find(f.Game, ManagedComponentKind.ReShade)!.Files).Path);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true);
        }
    }
}
