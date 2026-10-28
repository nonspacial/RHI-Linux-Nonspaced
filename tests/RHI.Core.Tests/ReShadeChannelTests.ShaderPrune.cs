using System.Text.Json.Nodes;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w12/shaders #240: a sync (or ReShade's removal) that takes a pack out leaves no empty folder of RHI's behind — the
/// owner's Borderlands 3 kept empty <c>reshade-shaders/RHI/Prod80/{Shaders,Textures}</c>, because the journal's copy of
/// each removed file sat beside it while the folders were pruned; they are pruned after the commit now. Only folders that
/// held RHI's files, only empty ones, never through or of a folder link, never outside <c>reshade-shaders/RHI</c>; a
/// failure or a crash before the commit puts every file back in its folder.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    /// <summary>A journaled ReShade with CrosireMaster and CrosireLegacy deployed through the shaders-only sync.</summary>
    private async Task<RrFx> TwoPackGameAsync(string name)
    {
        var fx = RrFixture(name);
        SeedShaderCache(fx.Cache, "CrosireLegacy");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var sync = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster", "CrosireLegacy"]);
        Assert.True(sync.Success, sync.Message);
        Assert.True(File.Exists(PackFile(fx.Game, "CrosireLegacy")));
        return fx;
    }

    // Prove-first (red on 8f15e44): the owner's case — a pack taken out leaves its folders behind.
    [Fact]
    public async Task Prune_PackTakenOut_LeavesNoEmptyFolder_AndOffLeavesNoRhiFolder()
    {
        var fx = await TwoPackGameAsync("prune-out");

        var one = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);
        Assert.True(one.Success, one.Message);
        Assert.False(Directory.Exists(RhiDir(fx.Game, "CrosireLegacy")));
        Assert.True(File.Exists(PackFile(fx.Game)));

        var off = await fx.Service.SyncShaderPacksAsync(fx.Game, []);
        Assert.True(off.Success, off.Message);
        Assert.False(Directory.Exists(RhiDir(fx.Game)));
    }

    [Fact]
    public async Task Prune_ReShadeRemoval_LeavesNoEmptyShaderFolder()
    {
        var fx = await TwoPackGameAsync("prune-removal");

        var removal = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removal.Success, removal.Message);
        Assert.False(Directory.Exists(RhiDir(fx.Game)));
    }

    [Fact]
    public async Task Prune_UsersFileKeepsItsFolder_AndTheUsersOwnEmptyFolderIsLeftAlone()
    {
        var fx = await TwoPackGameAsync("prune-user");
        var mine = Path.Combine(RhiDir(fx.Game, "CrosireLegacy", "Shaders"), "Mine.fx");
        File.WriteAllText(mine, "// the user's own effect");
        var ownEmpty = Directory.CreateDirectory(RhiDir(fx.Game, "MyEmptyFolder")).FullName;

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"])).Success);
        Assert.Equal("// the user's own effect", File.ReadAllText(mine));
        Assert.False(File.Exists(PackFile(fx.Game, "CrosireLegacy")));
        Assert.True(Directory.Exists(ownEmpty));

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, [])).Success);
        Assert.Equal("// the user's own effect", File.ReadAllText(mine));
        Assert.True(Directory.Exists(ownEmpty)); // RHI never made it: not RHI's to prune
    }

    // A pack folder the user turned into a link: nothing behind it is removed (the files there are the user's now, even
    // when they hold the bytes RHI placed), and the link itself stays — also when what it points at is empty.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prune_LinkedPackFolder_IsNeverFollowedNorRemoved(bool targetHoldsTheFiles)
    {
        var fx = await TwoPackGameAsync($"prune-link-{targetHoldsTheFiles}");
        var linked = RhiDir(fx.Game, "CrosireLegacy", "Shaders");
        var elsewhere = Path.Combine(_root, $"prune-link-{targetHoldsTheFiles}", "elsewhere");
        if (targetHoldsTheFiles) Directory.Move(linked, elsewhere);
        else
        {
            Directory.CreateDirectory(elsewhere);
            File.Delete(Path.Combine(linked, "Test.fx"));
            Directory.Delete(linked);
        }
        Directory.CreateSymbolicLink(linked, elsewhere);

        var result = await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(new DirectoryInfo(linked).LinkTarget);
        Assert.True(Directory.Exists(elsewhere));
        Assert.Equal(targetHoldsTheFiles, File.Exists(Path.Combine(elsewhere, "Test.fx")));
    }

    // An older build left empty pack folders its manifest no longer lists (the owner's Borderlands 3: Prod80/Shaders and
    // Prod80/Textures): the next sync tidies them too, while a folder of the user's and a linked pack folder stay.
    [Fact]
    public async Task Prune_EmptyPackFoldersAnOlderBuildLeft_GoAtTheNextSync()
    {
        var fx = await TwoPackGameAsync("prune-leftover");
        Directory.CreateDirectory(RhiDir(fx.Game, "Prod80", "Shaders"));
        Directory.CreateDirectory(RhiDir(fx.Game, "Prod80", "Textures"));
        var ownEmpty = Directory.CreateDirectory(RhiDir(fx.Game, "MyEmptyFolder")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "prune-leftover", "elsewhere")).FullName;
        var linked = RhiDir(fx.Game, "Azen");
        Directory.CreateSymbolicLink(linked, elsewhere);

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster", "CrosireLegacy"])).Success);

        Assert.False(Directory.Exists(RhiDir(fx.Game, "Prod80")));
        Assert.True(Directory.Exists(ownEmpty));
        Assert.NotNull(new DirectoryInfo(linked).LinkTarget);
        Assert.True(File.Exists(PackFile(fx.Game, "CrosireLegacy")));
    }

    // A manifest entry that points outside RHI's folder (edited by hand) never lets the prune climb out of it.
    [Fact]
    public async Task Prune_NeverLeavesRhisFolder()
    {
        var fx = await TwoPackGameAsync("prune-outside");
        var outside = Directory.CreateDirectory(Path.Combine(fx.Game.BinaryDirectory!, "reshade-shaders", "UserFolder", "Empty")).FullName;
        var manifestPath = RhiDir(fx.Game, ".rhi-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["Files"]!.AsArray().Add(new JsonObject { ["RelativePath"] = "../UserFolder/Empty/x.fx", ["Sha256"] = "00" });
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, [])).Success);

        Assert.True(Directory.Exists(outside));
        Assert.False(Directory.Exists(RhiDir(fx.Game)));
    }

    // A failed sync puts every file back in its folder (the folders are pruned only after the commit).
    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Prune_FailedSync_RollbackPutsEveryFileBackInItsFolder()
    {
        var fx = await TwoPackGameAsync("prune-rollback");
        var before = CancelAtWritePhase.Snapshot(fx.Game.BinaryDirectory!);
        fx.Store.SaveFaultForTesting = () => new IOException("disk full (prune)");

        var result = await fx.Service.SyncShaderPacksAsync(fx.Game, []);
        fx.Store.SaveFaultForTesting = null;

        Assert.False(result.Success);
        CancelAtWritePhase.AssertUnchanged(before, fx.Game.BinaryDirectory!);
        Assert.Empty(Directory.GetFiles(fx.JournalDir, "*.json"));
    }

    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Prune_CrashAtTheCommitPoint_RecoveryPutsEveryFileBackInItsFolder()
    {
        var fx = await TwoPackGameAsync("prune-crash");
        var before = CancelAtWritePhase.Snapshot(fx.Game.BinaryDirectory!);
        using (var death = new SimulatedDeath(fx.Journal))
        {
            fx.Store.SaveFaultForTesting = death.Die;
            var (died, run) = await death.RunUntilDeathAsync(() => fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"]));
            Assert.True(died, run.IsCompleted ? (await run).Message : "running");
            Assert.False(File.Exists(PackFile(fx.Game, "CrosireLegacy")));
            var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.Ledger);
            Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
            CancelAtWritePhase.AssertUnchanged(before, fx.Game.BinaryDirectory!);
        }
    }
    // Review F1: a crash between the commit of a sync that took the last pack out and its after-commit prune leaves RHI's
    // empty pack folders and no manifest. ReShade's removal still tidies them (with ReShade gone no later sync would);
    // the user's own empty folder stays, and with it RHI's folder.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CommitPoint")]
    public async Task Prune_CrashBeforeThePrune_ThenReShadeRemoval_LeavesNoEmptyFolderOfRhis(bool userFolder)
    {
        var fx = await TwoPackGameAsync($"prune-crash-removal-{userFolder}");
        var ownEmpty = userFolder ? Directory.CreateDirectory(RhiDir(fx.Game, "MyEmptyFolder")).FullName : null;
        await CrashAtTheCommitAsync(fx, () => fx.Service.SyncShaderPacksAsync(fx.Game, []));
        Assert.True(Directory.Exists(RhiDir(fx.Game, "CrosireLegacy", "Shaders"))); // the crash left RHI's empty folders
        Assert.False(File.Exists(RhiDir(fx.Game, ".rhi-manifest.json")));

        var removal = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removal.Success, removal.Message);
        if (ownEmpty is null) Assert.False(Directory.Exists(RhiDir(fx.Game)));
        else Assert.Equal(["MyEmptyFolder"], Directory.EnumerateFileSystemEntries(RhiDir(fx.Game)).Select(Path.GetFileName));
    }

    // Review F1, second path: a crash between the commit of ReShade's removal and its prune; ReShade is gone, so the next
    // ReShade install (which syncs the packs, none here) is what tidies RHI's empty folders.
    [Fact]
    [Trait("Category", "CommitPoint")]
    public async Task Prune_CrashBeforeThePruneOfAReShadeRemoval_TheNextInstallTidies()
    {
        var fx = await TwoPackGameAsync("prune-crash-reinstall");
        await CrashAtTheCommitAsync(fx, () => fx.Service.RemoveAsync(fx.Game));
        Assert.True(Directory.Exists(RhiDir(fx.Game, "CrosireLegacy", "Shaders")));

        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = true })).Success);

        Assert.False(Directory.Exists(RhiDir(fx.Game)));
    }

    /// <summary>Runs <paramref name="run"/> until it "dies" at its commit (after the commit point, before the after-commit work), then recovers.</summary>
    private static async Task CrashAtTheCommitAsync(RrFx fx, Func<Task<ComponentOperationResult>> run)
    {
        fx.Journal.CrashAtCommitForTesting = true;
        try { await run(); }
        catch (SimulatedCrashException) { } // the dead thread's own outcome does not matter: the next start owns the entry
        finally { fx.Journal.CrashAtCommitForTesting = false; }
        var report = await SimulatedDeath.RecoverAsync(fx.JournalDir, fx.Ledger);
        Assert.Equal(JournalRecoveryOutcome.AlreadyCommitted, Assert.Single(report!.Entries).Outcome);
    }

    // Review F2 (coordinator: the leftover pass covers only packs no longer selected): an empty folder the user made
    // inside a selected pack's folder (or RHI's _Core) is not RHI's to prune, while the pack taken out still goes.
    [Theory]
    [InlineData("CrosireMaster/Shaders/MyWork")]
    [InlineData("CrosireMaster/MyWork")]
    [InlineData("_Core/MyWork")]
    public async Task Prune_UsersEmptyFolderInsideASelectedPack_IsKept(string relative)
    {
        var fx = await TwoPackGameAsync("prune-user-in-pack-" + relative.Replace('/', '-'));
        var mine = Directory.CreateDirectory(RhiDir(fx.Game, relative.Split('/'))).FullName;

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"])).Success);
        Assert.True(Directory.Exists(mine));
        Assert.False(Directory.Exists(RhiDir(fx.Game, "CrosireLegacy")));

        Assert.True((await fx.Service.SyncShaderPacksAsync(fx.Game, ["CrosireMaster"])).Success); // same set again
        Assert.True(Directory.Exists(mine));
    }
}
