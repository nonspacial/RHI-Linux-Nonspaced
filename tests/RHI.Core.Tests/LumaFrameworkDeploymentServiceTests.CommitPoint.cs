using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204: the DX9 Luma install and removal save Luma's record and ReShade's translated record in ONE ledger write. When it
// lands and then throws, the operation is finished as committed: both records are kept and nothing is undone (no file, no
// dxgi.dll/d3d9.dll rename, no ReShade.ini/Engine.ini step, no override).
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
}
