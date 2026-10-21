using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #204 for ReShade: the DX proxy install and update, the Vulkan layer install and update, ReShade's removal, and DX9 HDR's
// install and removal that move ReShade to and from its Vulkan layer (two records in one write). For every ledger write of
// each, dying right after the write landed and throwing right after it landed leave the same files and records, in both
// seam shapes; the commit write's landed-then-throw is reported as success with the note, never undone.
public sealed partial class ReShadeChannelTests
{
    private CommitPointCase ReShadeCase(string name, bool vulkan, Func<ReShadeDeploymentService, DetectedGame, MutableHandler, Task<ComponentOperationResult>> act,
        Func<ReShadeDeploymentService, DetectedGame, MutableHandler, Task>? arrange, out Func<Task> runArrange)
    {
        var (game, store) = vulkan ? CreateVulkanGame(name) : CreateGame(name);
        var root = Path.Combine(_root, name);
        var journalDir = Path.Combine(root, "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var cache = Path.Combine(_root, name + "-cache");
        ReShadeDeploymentService service;
        MutableHandler inner;
        if (vulkan)
        {
            var lunarG = new LunarGHandler();
            inner = lunarG.Inner;
            service = NewVulkanService(store, lunarG, cache, overrides, journal);
        }
        else
        {
            inner = new MutableHandler();
            var http = new HttpClient(inner);
            service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides, new FakeExtractor(), cache,
                new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")), journal: journal);
        }
        runArrange = () => arrange?.Invoke(service, game, inner) ?? Task.CompletedTask;
        return new CommitPointCase(root, [Path.Combine(root, "game"), game.PrefixPath!], store, Path.Combine(root, "records.json"), journal, journalDir,
            () => act(service, game, inner));
    }

    private Task<ComponentOperationResult> InstallReShade(ReShadeDeploymentService s, DetectedGame g, bool vulkan)
        => vulkan ? s.InstallAsync(g, LayerOptions()) : s.InstallAsync(g, new ReShadeInstallOptions());

    public static TheoryData<LandedShape, bool> ShapesAndLayer => new()
    {
        { LandedShape.AfterCache, false }, { LandedShape.InsideWrite, false }, { LandedShape.AfterCache, true }, { LandedShape.InsideWrite, true }
    };

    [Theory]
    [MemberData(nameof(ShapesAndLayer))]
    [Trait("Category", "CommitPoint")]
    public async Task InstallUpdateAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape, bool vulkan)
    {
        var tag = $"{shape}-{(vulkan ? "vk" : "dx")}";
        async Task<CommitPointCase> Make(string name, Func<ReShadeDeploymentService, DetectedGame, MutableHandler, Task<ComponentOperationResult>> act, bool installFirst)
        {
            var c = ReShadeCase(name, vulkan, act, installFirst
                ? async (s, g, _) => Assert.True((await InstallReShade(s, g, vulkan)).Success)
                : null, out var arrange);
            await arrange();
            return c;
        }

        var install = await CommitPointAgreement.AssertEveryWriteAsync(
            name => Make($"cp-rs-install-{tag}-{name}", (s, g, _) => InstallReShade(s, g, vulkan), installFirst: false), shape);
        var update = await CommitPointAgreement.AssertEveryWriteAsync(name => Make($"cp-rs-update-{tag}-{name}", (s, g, h) =>
        {
            h.StableVersion = "9.9.10"; // a newer ReShade: the update replaces the payload
            return s.UpdateAsync(g);
        }, installFirst: true), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(
            name => Make($"cp-rs-remove-{tag}-{name}", (s, g, _) => s.RemoveAsync(g), installFirst: true), shape);

        Assert.True(install[^1].Success);
        Assert.True(update[^1].Success);
        Assert.True(removal[^1].Success);
        Assert.False(removal[0].Success); // the removal's stamp
    }

    [Theory]
    [MemberData(nameof(ShapesAndLayer))]
    [Trait("Category", "CommitPoint")]
    public async Task Install_CommitWriteLandsThenThrows_IsKept_ProxyOverrideAndRecord(LandedShape shape, bool vulkan)
    {
        var c = ReShadeCase($"cp-rs-kept-{shape}-{vulkan}", vulkan, (s, g, _) => InstallReShade(s, g, vulkan), null, out _);
        LandedLedgerWrite.Arm(c.Records, shape, at: 1);

        var result = await c.Act();
        LandedLedgerWrite.Disarm(c.Records);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, result.Message, StringComparison.Ordinal);
        var record = Assert.Single(new LinuxDeploymentRecordStore(c.RecordsPath).All(), r => r.Component == ManagedComponentKind.ReShade);
        Assert.Equal(vulkan, ReShadeVulkanLayer.IsLayerRecord(record));
        var userReg = File.ReadAllText(Path.Combine(c.Root, "pfx", "user.reg"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(userReg, vulkan ? "vulkan-1" : "dxgi", "Game.exe"));
        Assert.Empty(CommitPointAgreement.JournalEntries(c));
        CommitPointAgreement.Capture(c); // the record describes its files
    }

    // DX9 HDR moves RHI's ReShade from d3d9.dll to its Vulkan layer and saves both records in its commit write (and the
    // removal moves it back the same way). A landed-then-throw keeps both records as written: ReShade's names the layer while
    // the layer is there (install), or d3d9.dll while d3d9.dll is back (removal).
    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task DxvkHdrMove_InstallAndRemoval_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        // Sets the fake 7-Zip (an AsyncLocal) in this test's flow; the rigs made inside Make set it only in theirs.
        _ = Rig($"cp-dxvk-tool-{shape}");
        async Task<CommitPointCase> Make(string name, bool removal)
        {
            var rig = Rig(name, journaled: true);
            await InstallDllReShadeAsync(rig);
            if (removal) Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
            var root = Path.Combine(_root, name);
            return new CommitPointCase(root, [Path.Combine(root, "game"), rig.Game.PrefixPath!], rig.Store, rig.Ledger, rig.Journal!, rig.JournalDirectory!,
                () => removal ? rig.Dxvk.RemoveAsync(rig.Game) : rig.Dxvk.InstallAsync(rig.Game));
        }

        var install = await CommitPointAgreement.AssertEveryWriteAsync(name => Make($"cp-dxvk-install-{shape}-{name}", removal: false), shape);
        var removal = await CommitPointAgreement.AssertEveryWriteAsync(name => Make($"cp-dxvk-remove-{shape}-{name}", removal: true), shape);

        Assert.True(install[^1].Success);
        Assert.True(removal[^1].Success);
        Assert.False(removal[0].Success);
    }
    // w8 commit-point review, finding 2: ReShade opens its journal operation itself. While an earlier change to the game is
    // undecided (the ledger cannot be read after its commit write), ReShade's install is refused with the plain message,
    // not as a journal write failure, and nothing changes.
    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Install_WhileAnEarlierChangeToTheGameIsUndecided_IsRefusedWithThePlainMessage()
    {
        CommitPointCase? c = null;
        string? other = null;
        c = ReShadeCase("cp-rs-held-up", vulkan: false, (s, g, _) => InstallReShade(s, g, vulkan: false), async (_, g, _) =>
        {
            // Another component's change to this game: its commit write lands, then the ledger is damaged.
            var (opened, refusal) = await DeploymentScope.BeginAsync(c!.Journal, "earlier change", g, ManagedComponentKind.OptiScaler, CancellationToken.None);
            Assert.Null(refusal);
            await using var scope = opened!;
            other = Path.Combine(g.BinaryDirectory!, "earlier.dll");
            await scope.PlaceBytesAsync(other, [1, 2, 3]);
            LandedLedgerWrite.Set(c.Records, LandedShape.InsideWrite, () =>
            {
                File.WriteAllText(c.RecordsPath, "{ not json");
                return new IOException(LandedLedgerWrite.FaultMessage);
            });
            Assert.ThrowsAny<IOException>(() => c.Records.Upsert(scope.Stamp(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(g), GameName = g.Name, Component = ManagedComponentKind.OptiScaler,
                BinaryDirectory = g.BinaryDirectory!, Files = [new LinuxDeployedFile { Path = other, Sha256 = FileHash.Sha256Hex(other) }]
            })));
            LandedLedgerWrite.Disarm(c.Records);
            Assert.Equal(CommitDecision.Undecided, (await scope.RollbackAsync()).Decision);
        }, out var arrange);
        await arrange();
        var before = CommitPointAgreement.Capture(c);

        var result = await c.Act();

        Assert.False(result.Success);
        Assert.Equal(DeploymentJournal.EarlierChangeUndecidedMessage, result.Message);
        Assert.Equal(before, CommitPointAgreement.Capture(c));
    }
}
