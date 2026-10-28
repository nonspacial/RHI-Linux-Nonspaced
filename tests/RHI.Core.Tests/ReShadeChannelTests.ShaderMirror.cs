using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #241 through ReShade itself: the shaders-only sync, ReShade's update (DX proxy and Vulkan layer) leave
/// pack files that did not change exactly where they are (same inode, same time), and a mirror sync that replaces, adds,
/// deletes and prunes is journaled like every other write: Cancel at its write phase runs it to the end (no download after
/// the gate), a forced rollback, a death mid-sync or at the commit point put the game back as it was, and crash recovery
/// and the in-process outcome agree at every ledger write.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    private sealed record MirrorFx(RrFx Rr, ReShadeShaderPackService Packs, GateWatchingHandler Watch)
    {
        public DetectedGame Game => Rr.Game;
        public string Bin => Rr.Game.BinaryDirectory!;
    }

    private MirrorFx MirrorFixture(string name)
    {
        var (game, store) = SetGame(name);
        var cache = Path.Combine(_root, name + "-cache");
        SeedShaderCache(cache, "CrosireMaster");
        SeedShaderCache(cache, "CrosireLegacy");
        var sets = Sets(store, name);
        var journalDir = Path.Combine(_root, name + "-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var watch = new GateWatchingHandler(new MutableHandler());
        var http = new HttpClient(watch);
        var packs = new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")) { Sets = sets };
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(journal), store, overrides, new FakeExtractor(), cache,
            packs, journal: journal);
        return new(new RrFx(game, store, Path.Combine(_root, name, "records.json"), journalDir, journal, service, sets, cache), packs, watch);
    }

    /// <summary>
    /// ReShade with CrosireMaster and CrosireLegacy deployed; then CrosireMaster moves on upstream (Test.fx changes at the
    /// same length, Shaders/New/Deep.fx appears), so a sync of [CrosireMaster] replaces, adds (in a new folder), deletes
    /// CrosireLegacy and prunes its folders, and leaves the core headers alone.
    /// </summary>
    private async Task<MirrorFx> MirrorReadyAsync(string name)
    {
        var fx = MirrorFixture(name);
        Assert.True((await fx.Rr.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var sync = await fx.Rr.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster", "CrosireLegacy"]);
        Assert.True(sync.Success, sync.Message);
        var extracted = Path.Combine(fx.Rr.Cache, "shader-packs", "CrosireMaster", "extracted");
        File.WriteAllText(Path.Combine(extracted, "Shaders", "Test.fx"), MovedOnEffect);
        Directory.CreateDirectory(Path.Combine(extracted, "Shaders", "New"));
        File.WriteAllText(Path.Combine(extracted, "Shaders", "New", "Deep.fx"), "// new upstream effect");
        const string url = "https://github.com/crosire/reshade-shaders/archive/refs/heads/master.zip";
        CacheStamp.Write(extracted, url, url);
        return fx;
    }

    private const string MovedOnEffect = "float4 PS() : SV_Target { return 0; }"; // same length as the seeded one

    private Task<ComponentOperationResult> MirrorSync(MirrorFx fx, CancellationToken ct = default)
        => fx.Rr.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"], ct);

    private void AssertMirrored(MirrorFx fx)
    {
        Assert.Equal(MovedOnEffect, File.ReadAllText(PackFile(fx.Game)));
        Assert.True(File.Exists(RhiDir(fx.Game, "CrosireMaster", "Shaders", "New", "Deep.fx")));
        Assert.False(Directory.Exists(RhiDir(fx.Game, "CrosireLegacy")));
        Assert.Equal(["CrosireMaster"], ReShadeDeploymentService.RecordedShaderPacks(new LinuxDeploymentRecordStore(fx.Rr.Ledger).Find(fx.Game, ManagedComponentKind.ReShade)));
    }

    private static void AssertBackAsBefore(MirrorFx fx, IReadOnlyDictionary<string, string> before)
    {
        CancelAtWritePhase.AssertUnchanged(before, fx.Bin, fx.Game.PrefixPath!);
        Assert.False(Directory.Exists(RhiDir(fx.Game, "CrosireMaster", "Shaders", "New"))); // the folder it created went too
        Assert.Equal(["CrosireMaster", "CrosireLegacy"],
            ReShadeDeploymentService.RecordedShaderPacks(new LinuxDeploymentRecordStore(fx.Rr.Ledger).Find(fx.Game, ManagedComponentKind.ReShade)));
    }

    [Fact]
    public async Task Mirror_ShadersOnlySync_ReplacesAddsDeletes_AndLeavesTheRestInPlace()
    {
        var fx = await MirrorReadyAsync("mx-sync");
        var header = RhiDir(fx.Game, "_Core", "Shaders", "ReShade.fxh");
        var headerBefore = ShaderPackMirrorTests.Fingerprint(RhiDir(fx.Game, "_Core"));

        var result = await MirrorSync(fx);

        Assert.True(result.Success, result.Message);
        AssertMirrored(fx);
        Assert.Equal(headerBefore, ShaderPackMirrorTests.Fingerprint(RhiDir(fx.Game, "_Core")));
        Assert.True(File.Exists(header));
        Assert.Empty(Directory.GetFiles(fx.Rr.JournalDir, "*.json"));
    }

    // Prove-first (red on 8f15e44): every entry point that re-applies the same packs used to delete and re-copy them.
    [Fact]
    public async Task Mirror_SameSetAgain_ShadersOnlySync_TouchesNoPackFile()
    {
        var fx = MirrorFixture("mx-same");
        Assert.True((await fx.Rr.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        Assert.True((await fx.Rr.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster", "CrosireLegacy"])).Success);
        var before = ShaderPackMirrorTests.Fingerprint(RhiDir(fx.Game));

        var again = await fx.Rr.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster", "CrosireLegacy"]);

        Assert.True(again.Success, again.Message);
        Assert.Equal(before, ShaderPackMirrorTests.Fingerprint(RhiDir(fx.Game)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mirror_ReShadeUpdate_DxAndVulkan_TouchesNoUnchangedPackFile(bool vulkan)
    {
        var name = vulkan ? "mx-update-vk" : "mx-update-dx";
        var cache = Path.Combine(_root, name + "-cache");
        SeedShaderCache(cache, "CrosireMaster");
        DetectedGame game;
        ReShadeDeploymentService service;
        MutableHandler handler;
        ReShadeInstallOptions options;
        if (vulkan)
        {
            (game, var store) = CreateVulkanGame(name);
            var lunarG = new LunarGHandler();
            handler = lunarG.Inner;
            service = NewVulkanService(store, lunarG, cache);
            options = LayerOptions();
        }
        else
        {
            (game, var store) = SetGame(name);
            handler = new MutableHandler();
            service = NewService(store, handler, cache);
            options = new ReShadeInstallOptions { ManageIni = true };
        }
        options.ShaderPackIds = ["CrosireMaster"];
        var install = await service.InstallAsync(game, options);
        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(PackFile(game)));
        var before = ShaderPackMirrorTests.Fingerprint(RhiDir(game));

        handler.StableVersion = "9.9.10"; // a newer ReShade: the update replaces the payload, the packs stay as they are
        var update = await service.UpdateAsync(game);

        Assert.True(update.Success, update.Message);
        Assert.Equal(before, ShaderPackMirrorTests.Fingerprint(RhiDir(game)));
    }

    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Mirror_CancelAtWritePhase_RunsToCompletion_WithNoDownloadAfterTheGate()
    {
        var fx = await MirrorReadyAsync("mx-cancel");

        var outcome = await CancelAtWritePhase.RunAsync(ct => MirrorSync(fx, ct), [fx.Bin, fx.Game.PrefixPath!], atWritePhase: fx.Watch.CloseGate);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        AssertMirrored(fx);
        Assert.Empty(fx.Watch.LateRequests);
    }

    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Mirror_CancelAtWritePhase_WithAForcedRollback_PutsEverythingBack()
    {
        var fx = await MirrorReadyAsync("mx-cancel-rollback");

        var outcome = await CancelAtWritePhase.RunAsync(ct => MirrorSync(fx, ct), [fx.Bin, fx.Game.PrefixPath!], atWritePhase: () =>
        {
            fx.Watch.CloseGate();
            fx.Rr.Store.SaveFaultForTesting = () => new IOException("disk full (mirror)");
        });
        fx.Rr.Store.SaveFaultForTesting = null;

        Assert.False(outcome.Result.Success);
        AssertBackAsBefore(fx, outcome.Before);
        Assert.Empty(fx.Watch.LateRequests);
        Assert.Empty(Directory.GetFiles(fx.Rr.JournalDir, "*.json"));
    }

    // Death mid-sync: straight after the manifest was written (every pack file already replaced, placed or deleted), and at
    // the commit point. The next start puts files, manifest and record back as they agreed before.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CommitPoint")]
    public async Task Mirror_DeathMidSync_RecoveryLeavesFilesManifestAndRecordAgreeing(bool atCommit)
    {
        var fx = await MirrorReadyAsync($"mx-death-{atCommit}");
        var before = CancelAtWritePhase.Snapshot(fx.Bin, fx.Game.PrefixPath!);
        using (var death = new SimulatedDeath(fx.Rr.Journal))
        {
            if (atCommit) fx.Rr.Store.SaveFaultForTesting = death.Die;
            else fx.Packs.ManifestWrittenForTesting = _ => death.Die();
            var (died, run) = await death.RunUntilDeathAsync(() => MirrorSync(fx));
            Assert.True(died, run.IsCompleted ? (await run).Message : "running");
            Assert.Equal(MovedOnEffect, File.ReadAllText(PackFile(fx.Game))); // it really got that far
            var report = await SimulatedDeath.RecoverAsync(fx.Rr.JournalDir, fx.Rr.Ledger);
            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            AssertBackAsBefore(fx, before);
        }
    }

    [Theory]
    [MemberData(nameof(LandedLedgerWrite.Shapes), MemberType = typeof(LandedLedgerWrite))]
    [Trait("Category", "CommitPoint")]
    public async Task Mirror_CrashRecoveryAndTheInProcessOutcomeAgree_AtEveryLedgerWrite(LandedShape shape)
    {
        async Task<CommitPointCase> Make(string name)
        {
            var fx = await MirrorReadyAsync($"mx-cp-{shape}-{name}");
            var root = Path.Combine(_root, $"mx-cp-{shape}-{name}");
            return new CommitPointCase(root, [Path.Combine(root, "game"), fx.Game.PrefixPath!], fx.Rr.Store, fx.Rr.Ledger, fx.Rr.Journal,
                fx.Rr.JournalDir, () => MirrorSync(fx));
        }

        var results = await CommitPointAgreement.AssertEveryWriteAsync(Make, shape);

        Assert.True(results[^1].Success);
        Assert.StartsWith(DeploymentScope.LandedChangePrefix, results[^1].Message, StringComparison.Ordinal);
    }
}
