using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204: the DX9 Luma install and removal save Luma's record and ReShade's translated record in ONE ledger write. When it
// lands and then throws, the operation is finished as committed: both records are kept and nothing is undone (no file, no
// dxgi.dll/d3d9.dll rename, no ReShade.ini/Engine.ini step, no override). #213: Luma's install and removal track the file
// behind a linked ReShade.ini, and the install's rollback step writes it through the link rule.
public sealed partial class LumaFrameworkDeploymentServiceTests
{
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Dx9Install_CommitWriteLandsThenThrows_KeepsBothRecordsAndTheTranslation_AndItRemovesLater(LandedShape shape)
    {
        var (service, records, game, _, _, journalDirectory, recordsPath, package) = CreateDx9LumaFixture("landed-dx9-install-" + shape);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        LandedLedgerWrite.Arm(records, shape, at: 1);

        var result = await service.InstallLocalPackageAsync(game, package);
        LandedLedgerWrite.Disarm(records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, result.Message, StringComparison.Ordinal);
        var fresh = new LinuxDeploymentRecordStore(recordsPath);
        AssertRecordDescribesTheFiles(fresh.Find(game, ManagedComponentKind.LumaFramework)!);
        var reShade = fresh.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(dxgi, Assert.Single(reShade.Files).Path);
        AssertRecordDescribesTheFiles(reShade);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dgVoodoo.conf")));
        Assert.Empty(Directory.GetFiles(journalDirectory, "*.json"));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        var after = new LinuxDeploymentRecordStore(recordsPath).Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), Assert.Single(after.Files).Path);
        AssertRecordDescribesTheFiles(after);
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Dx9Remove_CommitWriteLandsThenThrows_StaysRemoved_WithReShadeBackAtD3d9(LandedShape shape)
    {
        var (service, records, game, _, _, journalDirectory, recordsPath, package) = CreateDx9LumaFixture("landed-dx9-remove-" + shape);
        Assert.True((await service.InstallLocalPackageAsync(game, package)).Success);
        LandedLedgerWrite.Arm(records, shape, at: 2); // 1 = the removal's stamp

        var result = await service.RemoveAsync(game);
        LandedLedgerWrite.Disarm(records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedRemovalPrefix, result.Message, StringComparison.Ordinal);
        var fresh = new LinuxDeploymentRecordStore(recordsPath);
        Assert.Null(fresh.Find(game, ManagedComponentKind.LumaFramework));
        var reShade = fresh.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), Assert.Single(reShade.Files).Path);
        AssertRecordDescribesTheFiles(reShade);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dgVoodoo.conf")));
        Assert.Empty(Directory.GetFiles(journalDirectory, "*.json"));
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Dx9InstallAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        CommitPointCase Case(string name, Func<LumaFrameworkDeploymentService, DetectedGame, string, Task<ComponentOperationResult>> act,
            out Func<Task> arrange)
        {
            var (service, records, game, root, journal, journalDirectory, recordsPath, package) = CreateDx9LumaFixture(name);
            arrange = async () => Assert.True((await service.InstallLocalPackageAsync(game, package)).Success);
            return new CommitPointCase(root, [root], records, recordsPath, journal, journalDirectory, () => act(service, game, package));
        }

        var install = await CommitPointAgreement.AssertEveryWriteAsync(
            name => Task.FromResult(Case($"agree-dx9-install-{shape}-{name}", (s, g, p) => s.InstallLocalPackageAsync(g, p), out _)), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(async name =>
        {
            var c = Case($"agree-dx9-remove-{shape}-{name}", (s, g, _) => s.RemoveAsync(g), out var arrange);
            await arrange();
            return c;
        }, shape);

        Assert.True(install[^1].Success);
        Assert.Equal([false, true], removal.Select(r => r.Success));
    }

    // #213: ReShade.ini linked to the user's shared file. Luma's own ReShade.ini write refuses a link, so the install fails;
    // the scope tracked the shared file (not the link) and the rollback step writes through the link rule, so the link stays
    // a link and the shared file is byte for byte as before — for a failed ledger write and for a crash at it alike.
    [Theory]
    [MemberData(nameof(ReShadeLinkedIniJournalTests.Failures), MemberType = typeof(ReShadeLinkedIniJournalTests))]
    [Trait("Category", "CommitPoint")]
    public async Task Install_LinkedReShadeIni_TheLinkStaysALink_AndTheSharedFileIsBack(string failure)
    {
        var fx = CreateLumaCancelFixture("linked-ini-" + failure, journaled: true);
        using var http = fx.Http;
        File.Delete(Path.Combine(fx.Game.BinaryDirectory!, "ReShade.ini"));
        var shared = ReShadeLinkedIniJournalTests.LinkSharedIni(fx.Game.BinaryDirectory!);
        var before = System.Text.Encoding.UTF8.GetBytes(ReShadeLinkedIniJournalTests.SharedBefore);
        try
        {
            var saves = 0;
            var result = failure == "ledger-blocked"
                ? await RunBlocked()
                : await RunCrash();

            async Task<ComponentOperationResult?> RunBlocked()
            {
                fx.Records.SaveFaultForTesting = () => ++saves == 1 ? new IOException("disk full (test)") : null;
                var r = await fx.Service.InstallAsync(fx.Game);
                fx.Records.SaveFaultForTesting = null;
                Assert.False(r.Success);
                return r;
            }
            async Task<ComponentOperationResult?> RunCrash()
            {
                using var death = new SimulatedDeath(fx.Journal!);
                fx.Records.SaveFaultForTesting = () => ++saves == 1 ? death.Die() : null;
                var (died, run) = await death.RunUntilDeathAsync(() => fx.Service.InstallAsync(fx.Game));
                if (died) await SimulatedDeath.RecoverAsync(fx.JournalDirectory, fx.RecordsPath);
                else Assert.False((await run).Success);
                ReShadeLinkedIniJournalTests.AssertLinked(fx.Game.BinaryDirectory!, shared, before);
                return null;
            }

            ReShadeLinkedIniJournalTests.AssertLinked(fx.Game.BinaryDirectory!, shared, before);
            Assert.Null(new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.LumaFramework));
            Assert.Empty(Directory.GetFiles(fx.JournalDirectory, "*.json"));
        }
        finally
        {
            fx.Records.SaveFaultForTesting = null;
            Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true);
        }
    }
}
