using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179 correction round: the independent review's probes (reviews/w7-review-shader-sets-probes.cs.txt), adopted, and the
/// tests for each finding's fix.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    // Review major 1: the ReShade profile (and the Shaders/Add-ons dialog) start from what the profile shows; for a game
    // without RHI's ReShade that used to be the record's packs (none), and an untouched Apply then saved "Select []".
    [Fact]
    public async Task Review_R1_FirstInstallFromProfile_UntouchedPacks_StaysGlobal()
    {
        var (game, store) = SetGame("r1");
        var cache = Path.Combine(_root, "r1-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r1");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var shown = managed.GetReShadeOptions(game); // what the profile panel shows for a game without ReShade

        var result = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = shown.ShaderPackIds });

        Assert.True(result.Success, result.Message);
        var choice = sets.ChoiceFor(game)!;
        Assert.True(choice.ShaderMode == ShaderAddonMode.Global, $"mode {choice.ShaderMode} [{string.Join(",", choice.ShaderPackIds)}]");
        Assert.True(File.Exists(PackFile(game)), "global set not deployed");
    }

    // Review major 1, second half: a Global game whose files lag (a batch skipped it while it ran) keeps Global on a
    // channel-only profile Apply, and that Apply brings its packs up to the global set.
    [Fact]
    public async Task Review_R1b_LaggingGlobalGame_ChannelOnlyApply_StaysGlobal()
    {
        var (game, store) = SetGame("r1b");
        var cache = Path.Combine(_root, "r1b-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r1b");
        var service = SetService(store, cache, sets);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]); // the batch never reached it
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;

        var profile = managed.GetReShadeOptions(game);
        var result = await managed.ApplyReShadeAsync(game, profile);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ShaderAddonMode.Global, sets.ChoiceFor(game)!.ShaderMode);
        Assert.True(File.Exists(PackFile(game)));
    }

    // ── Review major 2: every ReShade install path gets the compiler for the game's set first ──────────────────────

    // A D3DCompiler helper that "installs" a native compiler into the game's prefix (or fails), counting its runs.
    private sealed class CompilerTool
    {
        public int Runs;
        public D3DCompilerToolInstaller Installer(DetectedGame game, bool succeeds) => new(new WineDllOverrideService())
        {
            RunTool = (_, _, _) =>
            {
                Runs++;
                if (!succeeds) return Task.FromResult(new ExternalProcessResult(1, "", "stand-in tool failed"));
                WriteNativeD3DCompiler(Path.Combine(game.PrefixPath!, "drive_c", "windows", "system32", "d3dcompiler_47.dll"), 0x8664);
                return Task.FromResult(new ExternalProcessResult(0, "", ""));
            }
        };
    }

    private static LinuxManagedComponentService ManagedWithCompilerTool(LinuxDeploymentRecordStore store, ReShadeDeploymentService reshade,
        D3DCompilerToolInstaller tools)
        => new(store, reshade: reshade, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: tools,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    // The review's probe, with a stand-in Protontricks: the ReShade row on a prefix without a native compiler and a
    // non-empty global set gets the compiler first and deploys the set; without the store nothing asks for it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_R2_RowInstall_PrefixWithoutCompiler(bool withStore)
    {
        var (game, store) = CreateGame("r2-" + withStore);
        var cache = Path.Combine(_root, "r2-cache-" + withStore);
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r2-" + withStore);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, withStore ? sets : null);
        var tool = new CompilerTool();
        var managed = ManagedWithCompilerTool(store, service, tool.Installer(game, succeeds: true));
        if (withStore) managed.ShaderSets = sets;

        var result = await managed.InstallAsync(game, ManagedComponentKind.ReShade, []);

        Assert.True(result.Success, result.Message);
        Assert.Equal(withStore ? 1 : 0, tool.Runs);
        Assert.Equal(withStore, File.Exists(PackFile(game)));
    }

    // The compiler cannot be had (the helper fails): ReShade is still installed — without the packs — and says why.
    [Fact]
    public async Task Review_R2c_RowInstall_CompilerCannotBeHad_InstallsReShade_AndSaysWhy()
    {
        var (game, store) = CreateGame("r2c");
        var cache = Path.Combine(_root, "r2c-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r2c");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        var tool = new CompilerTool();
        var managed = ManagedWithCompilerTool(store, service, tool.Installer(game, succeeds: false));
        managed.ShaderSets = sets;

        var result = await managed.InstallAsync(game, ManagedComponentKind.ReShade, []);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, tool.Runs);
        Assert.NotNull(store.Find(game, ManagedComponentKind.ReShade));
        Assert.False(File.Exists(PackFile(game)));
        Assert.EndsWith(LinuxManagedComponentService.ShaderPacksNotInstalledNote, result.Message);
        Assert.Equal(ShaderAddonMode.Global, sets.ChoiceFor(game)!.ShaderMode); // still Global: a later Apply brings the packs
    }

    // ReShade installed for another component (RenoDX, Luma, ShortFuse, Feeder) goes through the dependency installer.
    [Fact]
    public async Task Review_R2_DependencyInstall_GetsTheCompilerFirst()
    {
        var (game, store) = CreateGame("r2-dep");
        var cache = Path.Combine(_root, "r2-dep-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r2-dep");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        var tool = new CompilerTool();
        var managed = ManagedWithCompilerTool(store, service, tool.Installer(game, succeeds: true));
        managed.ShaderSets = sets;
        var dependency = typeof(LinuxManagedComponentService).GetMethod("InstallDependencyCoreAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = await (Task<ComponentOperationResult>)dependency.Invoke(managed, [game, ManagedComponentKind.ReShade, CancellationToken.None])!;

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, tool.Runs);
        Assert.True(File.Exists(PackFile(game)));
    }

    // The review's probe: ReShade's own update (no helper reachable) on a Global game whose set was edited after its
    // install, on a prefix without a native compiler: ReShade updates, keeping the packs it had.
    [Fact]
    public async Task Review_R2b_UpdateAfterGlobalEdit_PrefixWithoutCompiler()
    {
        var (game, store) = CreateGame("r2b");
        var cache = Path.Combine(_root, "r2b-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r2b");
        var service = SetService(store, cache, sets);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);

        var result = await service.UpdateAsync(game);

        Assert.True(result.Success || result.SourceUnavailable, result.Message);
    }

    // Update All / a managed update of that game gets the compiler first and then deploys the global set.
    [Fact]
    public async Task Review_R2d_ManagedUpdate_GetsTheCompilerFirst_AndDeploysTheSet()
    {
        var (game, store) = CreateGame("r2d");
        var cache = Path.Combine(_root, "r2d-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "r2d");
        var handler = new MutableHandler { StableVersion = "6.1.0" };
        var http = new HttpClient(handler);
        var packs = new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")) { Sets = sets };
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new FakeExtractor(), cache, packs);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        handler.StableVersion = "6.2.0";
        var tool = new CompilerTool();
        var managed = ManagedWithCompilerTool(store, service, tool.Installer(game, succeeds: true));
        managed.ShaderSets = sets;

        var result = await managed.UpdateManagedAsync(game, ManagedComponentKind.ReShade, []);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, tool.Runs);
        Assert.True(File.Exists(PackFile(game)));
    }
}
