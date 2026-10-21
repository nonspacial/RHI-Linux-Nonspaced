using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204 for OptiScaler: a single-record install, the install that moves RHI's ReShade to ReShade64.dll (two records in one
// write), the chained rename (OptiScaler's record and ReShade's chain name in one write, ProxyRename Request.Alongside) and
// the removal that puts ReShade back under its own name. For every ledger write of each, dying right after the write
// landed and throwing right after it landed leave the same files and records, in both seam shapes.
public sealed partial class OptiScalerDeploymentServiceTests
{
    private CommitPointCase OptiCase(string name, bool chainedReShade, Func<OptiScalerDeploymentService, DetectedGame, Task<ComponentOperationResult>> act,
        out Func<Task> installFirst)
    {
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        if (chainedReShade) SeedManagedReShade(game, store);
        var j = NewJournaled(name, store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage(name + "-package")), j);
        installFirst = async () => Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        var root = Path.Combine(_root, name);
        return new CommitPointCase(root, [Path.Combine(root, "game"), Path.Combine(root, "pfx")], store, j.Ledger, j.Journal, j.Dir,
            () => act(service, game));
    }

    public static TheoryData<LandedShape, bool> ShapesAndChain => new()
    {
        { LandedShape.AfterCache, false }, { LandedShape.InsideWrite, false }, { LandedShape.AfterCache, true }, { LandedShape.InsideWrite, true }
    };

    [Theory]
    [MemberData(nameof(ShapesAndChain))]
    [Trait("Category", "CommitPoint")]
    public async Task InstallAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape, bool chainedReShade)
    {
        var tag = $"{shape}-{chainedReShade}";
        var install = await CommitPointAgreement.AssertEveryWriteAsync(name => Task.FromResult(OptiCase($"cp-install-{tag}-{name}", chainedReShade,
            (s, g) => s.InstallAsync(g, OptiScalerVariant.Stable, null, "dxgi.dll"), out _)), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(async name =>
        {
            var c = OptiCase($"cp-remove-{tag}-{name}", chainedReShade, (s, g) => s.RemoveAsync(g), out var installFirst);
            await installFirst();
            return c;
        }, shape);

        Assert.True(install[^1].Success);
        Assert.True(removal[^1].Success);
        Assert.False(removal[0].Success); // the removal's stamp
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task ChainedRename_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        var results = await CommitPointAgreement.AssertEveryWriteAsync(async name =>
        {
            var c = OptiCase($"cp-rename-{shape}-{name}", chainedReShade: true, (s, g) => s.RenameProxyAsync(g, "wininet.dll", choice: null), out var installFirst);
            await installFirst();
            return c;
        }, shape);

        Assert.True(results[^1].Success);
    }

    // The rename's commit write (OptiScaler under wininet.dll, ReShade's record with the name it goes back to) lands and
    // throws: both records stay as written and the proxy stays at wininet.dll with its override (the rename used to be
    // undone while the ledger kept it).
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task ChainedRename_CommitWriteLandsThenThrows_IsKept_BothRecordsAsWritten(LandedShape shape)
    {
        var name = "cp-rename-kept-" + shape;
        var c = OptiCase(name, chainedReShade: true, (s, g) => s.RenameProxyAsync(g, "wininet.dll", choice: null), out var installFirst);
        await installFirst();
        LandedLedgerWrite.Arm(c.Records, shape, at: 1);

        var renamed = await c.Act();
        LandedLedgerWrite.Disarm(c.Records);

        Assert.True(renamed.Success, renamed.Message);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, renamed.Message, StringComparison.Ordinal);
        var bin = Path.Combine(c.Root, "game", "bin");
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(bin, "wininet.dll")));
        var fresh = new LinuxDeploymentRecordStore(c.RecordsPath);
        var game = fresh.All().First(r => r.Component == ManagedComponentKind.OptiScaler);
        Assert.Equal("wininet.dll", OptiScalerDeploymentService.GetProxyName(game));
        Assert.Equal("dxgi.dll", fresh.All().First(r => r.Component == ManagedComponentKind.ReShade).Metadata[OptiScalerDeploymentService.ReShadeChainNameMetadataKey]);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(c.Root, "pfx", "user.reg")), "wininet", "Game.exe"));
        Assert.Empty(CommitPointAgreement.JournalEntries(c));
        CommitPointAgreement.Capture(c); // every record describes its files
    }
}
