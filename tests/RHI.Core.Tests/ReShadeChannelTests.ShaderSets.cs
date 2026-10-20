using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179 (upstream 2.8.7 global shader set + per-game Global / Select / Off): what a game's ReShade install deploys, the
/// migration of saved selections (each becomes Select, unchanged), and the batch that brings games following the global
/// set up to date — a Select or Off game is never touched, Cancel stops between games, a running game is skipped.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    private GlobalShaderAddonStore Sets(LinuxDeploymentRecordStore store, string name)
        => new(store, Path.Combine(_root, name, "shader-addon-sets.json"));

    private ReShadeDeploymentService SetService(LinuxDeploymentRecordStore store, string cache, GlobalShaderAddonStore? sets)
    {
        var http = new HttpClient(new MutableHandler());
        var packs = new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")) { Sets = sets };
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new FakeExtractor(), cache, packs);
    }

    // A game whose prefix holds a native D3DCompiler_47, so shader packs install without an explicit compiler path.
    private (DetectedGame Game, LinuxDeploymentRecordStore Store) SetGame(string name, LinuxDeploymentRecordStore? store = null, int appId = 4242)
    {
        var (game, own) = CreateGame(name);
        game.SteamAppId = appId;
        WriteNativeD3DCompiler(Path.Combine(game.PrefixPath!, "drive_c", "windows", "system32", "d3dcompiler_47.dll"), 0x8664);
        return (game, store ?? own);
    }

    private static string PackFile(DetectedGame game, string pack = "CrosireMaster")
        => Path.Combine(game.BinaryDirectory!, "reshade-shaders", "RHI", pack, "Shaders", "Test.fx");

    public static TheoryData<ShaderAddonMode, GlobalShaderMode, bool, string> ShaderMatrix()
    {
        var data = new TheoryData<ShaderAddonMode, GlobalShaderMode, bool, string>();
        foreach (var mode in Enum.GetValues<ShaderAddonMode>())
            foreach (var global in Enum.GetValues<GlobalShaderMode>())
                foreach (var globalSetEmpty in new[] { true, false })
                {
                    var expected = mode switch
                    {
                        ShaderAddonMode.Off => "",
                        ShaderAddonMode.Select => "Own",
                        _ => global switch
                        {
                            GlobalShaderMode.Off => "",
                            GlobalShaderMode.Custom => ReShadeShaderPackCatalog.CustomPackId,
                            _ => globalSetEmpty ? "" : "Lilium,Prod80",
                        },
                    };
                    data.Add(mode, global, globalSetEmpty, expected);
                }
        return data;
    }

    [Theory]
    [MemberData(nameof(ShaderMatrix))]
    public void ShaderResolution_GlobalSelectOff_ByGlobalModeAndSet(ShaderAddonMode mode, GlobalShaderMode global, bool globalSetEmpty, string expected)
    {
        var choice = new ShaderAddonGameChoice(mode, ["Own"], ShaderAddonMode.Global, [], Saved: true);
        IReadOnlyList<string> globalSet = globalSetEmpty ? [] : ["Lilium", "Prod80"];

        var packs = ReShadeShaderPackService.EffectivePackIds(choice, global, globalSet);

        Assert.Equal(expected, string.Join(",", packs));
    }

    [Fact]
    public async Task Install_DeploysEachGamesEffectiveSet_GlobalFollowsTheGlobalSet_SelectAndOffKeepTheirOwn()
    {
        var (global, store) = SetGame("sets-global", appId: 1001);
        var (select, _) = SetGame("sets-select", store, 1002);
        var (off, _) = SetGame("sets-off", store, 1003);
        var cache = Path.Combine(_root, "sets-cache");
        SeedShaderCache(cache, "CrosireMaster");
        SeedShaderCache(cache, "CrosireLegacy");
        var sets = Sets(store, "sets");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        sets.SetShaderMode(select, ShaderAddonMode.Select, ["CrosireLegacy"]);
        sets.SetShaderMode(off, ShaderAddonMode.Off);
        var service = SetService(store, cache, sets);

        // Every install path hands ReShade its own list (the row: none); the game's choice decides.
        foreach (var game in new[] { global, select, off })
            Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);

        Assert.True(File.Exists(PackFile(global)));
        Assert.Equal(["CrosireMaster"], service.GetInstalledOptions(global).ShaderPackIds);
        Assert.False(File.Exists(PackFile(select)));
        Assert.True(File.Exists(PackFile(select, "CrosireLegacy")));
        Assert.Empty(service.GetInstalledOptions(off).ShaderPackIds);
        Assert.False(Directory.Exists(Path.Combine(off.BinaryDirectory!, "reshade-shaders", "RHI")));
        // The game installed through Global has its choice saved first: its record is never taken for an older build's pick.
        Assert.Equal((ShaderAddonMode.Global, true), (sets.ChoiceFor(global)!.ShaderMode, sets.ChoiceFor(global)!.Saved));
    }

    [Fact]
    public async Task UnreadableChoices_KeepTheCallersList_AndAreNeverSavedOver()
    {
        var (game, store) = SetGame("sets-unreadable");
        var cache = Path.Combine(_root, "sets-unreadable-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "sets-unreadable");
        Directory.CreateDirectory(Path.GetDirectoryName(sets.FilePath)!);
        File.WriteAllText(sets.FilePath, "{ not json");
        var bytes = File.ReadAllBytes(sets.FilePath);
        var service = SetService(store, cache, sets);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = ["CrosireMaster"] });

        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(PackFile(game))); // the caller's list, as before #179
        Assert.Null(sets.ChoiceFor(game));
        Assert.Throws<JsonStoreUnavailableException>(() => sets.AdoptFromLedger());
        Assert.Equal(bytes, File.ReadAllBytes(sets.FilePath));
    }

    [Fact]
    public async Task GlobalEdit_ChangesOnlyGamesFollowingIt_SelectAndOffGamesStayByteIdentical()
    {
        var (global, store) = SetGame("edit-global", appId: 2001);
        var (select, _) = SetGame("edit-select", store, 2002);
        var (off, _) = SetGame("edit-off", store, 2003);
        var cache = Path.Combine(_root, "edit-cache");
        SeedShaderCache(cache, "CrosireMaster");
        SeedShaderCache(cache, "CrosireLegacy");
        var sets = Sets(store, "edit");
        sets.SetGlobalShaderPackIds(["CrosireLegacy"]);
        var service = SetService(store, cache, sets);
        foreach (var game in new[] { global, select, off })
            Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = true })).Success);
        // Switched after their install and not applied yet: only their own choice may ever change these two.
        sets.SetShaderMode(select, ShaderAddonMode.Select, ["CrosireMaster"]);
        sets.SetShaderMode(off, ShaderAddonMode.Off);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var selectBefore = CancelAtWritePhase.Snapshot(select.InstallPath, select.PrefixPath!);
        var offBefore = CancelAtWritePhase.Snapshot(off.InstallPath, off.PrefixPath!);
        var selectRecord = JsonSerializer.Serialize(store.Find(select, ManagedComponentKind.ReShade));
        var offRecord = JsonSerializer.Serialize(store.Find(off, ManagedComponentKind.ReShade));

        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var result = await new ShaderAddonSetBatch(managed, new NotRunning()).ApplyAsync([global, select, off], ShaderAddonSetPart.Shaders, CancellationToken.None);

        Assert.Equal((1, 0, 0), (result.Changed, result.Failed, result.NotRun));
        Assert.True(File.Exists(PackFile(global)));
        Assert.False(File.Exists(PackFile(global, "CrosireLegacy")));
        CancelAtWritePhase.AssertUnchanged(selectBefore, select.InstallPath, select.PrefixPath!);
        CancelAtWritePhase.AssertUnchanged(offBefore, off.InstallPath, off.PrefixPath!);
        Assert.Equal(selectRecord, JsonSerializer.Serialize(store.Find(select, ManagedComponentKind.ReShade)));
        Assert.Equal(offRecord, JsonSerializer.Serialize(store.Find(off, ManagedComponentKind.ReShade)));
    }

    [Fact]
    public async Task OffThenGlobal_RemovesOnlyRhisPacks_AndPutsTheSameFilesBack()
    {
        var (game, store) = SetGame("off-global");
        var cache = Path.Combine(_root, "off-global-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "off-global");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = true })).Success);
        var mine = Path.Combine(game.BinaryDirectory!, "reshade-shaders", "Shaders", "Mine.fx"); // the user's own shader
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "user");
        var deployed = File.ReadAllBytes(PackFile(game));
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;

        sets.SetShaderMode(game, ShaderAddonMode.Off);
        var off = await managed.ApplyShaderSetAsync(game);

        Assert.Equal(ShaderSetOutcome.Changed, off.Outcome);
        Assert.False(File.Exists(PackFile(game)));
        Assert.Equal("user", File.ReadAllText(mine));
        Assert.True(managed.IsReShadeManaged(game)); // ReShade itself stays

        sets.SetShaderMode(game, ShaderAddonMode.Global);
        var back = await managed.ApplyShaderSetAsync(game);

        Assert.Equal(ShaderSetOutcome.Changed, back.Outcome);
        Assert.Equal(deployed, File.ReadAllBytes(PackFile(game)));
        Assert.Equal("user", File.ReadAllText(mine));
        Assert.Equal(ShaderSetOutcome.Unchanged, (await managed.ApplyShaderSetAsync(game)).Outcome);
    }

    [Fact]
    public async Task Batch_CancelAfterTheFirstGameStartedWriting_FinishesThatGame_AndStartsNoOther()
    {
        var (first, store) = SetGame("cancel-first", appId: 3001);
        var (second, _) = SetGame("cancel-second", store, 3002);
        var cache = Path.Combine(_root, "cancel-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "cancel");
        var service = SetService(store, cache, sets);
        foreach (var game in new[] { first, second })
            Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var secondBefore = CancelAtWritePhase.Snapshot(second.InstallPath, second.PrefixPath!);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);

        using var batch = new CancellationTokenSource();
        OperationCancellation.WritePhaseObserverForTesting = _ => batch.Cancel(); // Cancel pressed as the first game writes
        ShaderAddonSetBatchResult result;
        try
        {
            result = await new ShaderAddonSetBatch(managed, new NotRunning()).ApplyAsync([first, second], ShaderAddonSetPart.Shaders, batch.Token);
        }
        finally
        {
            OperationCancellation.WritePhaseObserverForTesting = null;
        }

        Assert.True(result.Cancelled);
        Assert.Equal((1, 1, 0), (result.Changed, result.NotRun, result.Failed));
        Assert.True(File.Exists(PackFile(first))); // ran to completion: files and record agree
        Assert.Equal(["CrosireMaster"], service.GetInstalledOptions(first).ShaderPackIds);
        CancelAtWritePhase.AssertUnchanged(secondBefore, second.InstallPath, second.PrefixPath!);
    }

    [Fact]
    public async Task Batch_SkipsARunningGame_AndSaysWhich()
    {
        var (game, store) = SetGame("running");
        var cache = Path.Combine(_root, "running-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "running");
        var service = SetService(store, cache, sets);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);

        var result = await new ShaderAddonSetBatch(managed, new Running()).ApplyAsync([game], ShaderAddonSetPart.Shaders, CancellationToken.None);

        Assert.Equal(1, result.Running);
        Assert.Equal([game.Name], result.RunningGames);
        Assert.Equal(0, result.Changed);
        CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
    }

    private sealed class Running : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => true;
    }

    [Fact]
    public async Task ExplicitApply_ADifferentList_MakesTheGameSelect_TheSameListKeepsItOnGlobal()
    {
        var (game, store) = SetGame("explicit");
        var cache = Path.Combine(_root, "explicit-cache");
        SeedShaderCache(cache, "CrosireMaster");
        SeedShaderCache(cache, "CrosireLegacy");
        var sets = Sets(store, "explicit");
        sets.SetGlobalShaderPackIds(["CrosireMaster"]);
        var service = SetService(store, cache, sets);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;

        var same = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = ["CrosireMaster"] });
        Assert.True(same.Success, same.Message);
        Assert.Equal(ShaderAddonMode.Global, sets.ChoiceFor(game)!.ShaderMode);

        var changed = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = ["CrosireLegacy"] });

        Assert.True(changed.Success, changed.Message);
        var choice = sets.ChoiceFor(game)!;
        Assert.Equal((ShaderAddonMode.Select, "CrosireLegacy"), (choice.ShaderMode, string.Join(",", choice.ShaderPackIds)));
        Assert.True(File.Exists(PackFile(game, "CrosireLegacy")));
        Assert.False(File.Exists(PackFile(game)));
        // The global set changing later leaves this game alone.
        sets.SetGlobalShaderPackIds([]);
        Assert.Equal(["CrosireLegacy"], managed.EffectiveShaderPacks(game));
    }

    [Fact]
    public async Task ExplicitApply_ChoiceThatCannotBeSaved_InstallsNothing()
    {
        var (game, store) = SetGame("explicit-unsaved");
        var cache = Path.Combine(_root, "explicit-unsaved-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var sets = Sets(store, "explicit-unsaved");
        sets.SetGlobalShaderPackIds([]);
        var service = SetService(store, cache, sets);
        var managed = ManagedComponents(store, service);
        managed.ShaderSets = sets;
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);
        var folder = Path.GetDirectoryName(sets.FilePath)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false, ShaderPackIds = ["CrosireMaster"] });

            Assert.False(result.Success);
            Assert.StartsWith(LinuxManagedComponentService.ShaderChoiceNotSavedPrefix, result.Message);
            CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---- Migration (owner decision: every saved selection becomes Select, unchanged; anything else is Global) ------

    private static LinuxDeploymentRecord ReShadeRecord(string key, string name, string packsJson) => new()
    {
        GameKey = key, GameName = name, Component = ManagedComponentKind.ReShade, BinaryDirectory = "/games/" + name,
        Metadata = { ["ReShade.Channel"] = "Stable", ["ReShade.ShaderPacks"] = packsJson },
    };

    [Fact]
    public void Migration_OwnerShapedLedger_EverySavedSelectionBecomesSelectWithTheSameContents()
    {
        // The shape of the owner's ledger (read-only copy, 2026-10-20): three ReShade games, one with an Xbox folder key,
        // one with no packs, plus an Addon Manager selection and records of other components.
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "migration", "deployments.json"));
        const string xboxKey = "/payloads/9P7LVXNVQ6K7";
        store.Upsert(ReShadeRecord(xboxKey, "Control Ultimate Edition", "[\"Lilium\"]"));
        store.Upsert(ReShadeRecord("steam:397540", "Borderlands 3", "[\"Lilium\",\"Prod80\"]"));
        store.Upsert(ReShadeRecord("steam:1449560", "Metro Exodus Enhanced Edition", "[]"));
        store.Upsert(new LinuxDeploymentRecord { GameKey = "steam:1449560", GameName = "Metro Exodus Enhanced Edition", Component = ManagedComponentKind.RenoDX });
        store.Upsert(new LinuxDeploymentRecord { GameKey = "steam:555", GameName = "Addon Only", Component = ManagedComponentKind.ReShadeAddonManager,
            Metadata = { ["AddonManager.SelectedIds"] = "[\"01\",\"custom-Mine\"]" } });
        var sets = Sets(store, "migration");

        var adopted = sets.AdoptFromLedger();

        Assert.Equal([xboxKey, "steam:1449560", "steam:397540", "steam:555"], adopted.Order(StringComparer.Ordinal).ToArray());
        string Shape(DetectedGame game) => sets.ChoiceFor(game) is { } c
            ? $"{c.ShaderMode}:{string.Join(",", c.ShaderPackIds)}|{c.AddonMode}:{string.Join(",", c.AddonIds)}|{c.Saved}" : "unreadable";
        Assert.Equal("Select:Lilium|Global:|True", Shape(new DetectedGame { Name = "Control", InstallPath = xboxKey }));
        Assert.Equal("Select:Lilium,Prod80|Global:|True", Shape(new DetectedGame { Name = "BL3", SteamAppId = 397540 }));
        Assert.Equal("Select:|Global:|True", Shape(new DetectedGame { Name = "Metro", SteamAppId = 1449560 }));
        Assert.Equal("Global:|Select:01,custom-Mine|True", Shape(new DetectedGame { Name = "Addon Only", SteamAppId = 555 }));
        Assert.Equal("Global:|Global:|False", Shape(new DetectedGame { Name = "New", SteamAppId = 777 })); // no selection: Global
        // Run again (every start): nothing new, and a user's later change is never overwritten.
        sets.SetShaderMode(new DetectedGame { Name = "BL3", SteamAppId = 397540 }, ShaderAddonMode.Global, []);
        Assert.Empty(sets.AdoptFromLedger());
        Assert.Equal("Global:|Global:|True", Shape(new DetectedGame { Name = "BL3", SteamAppId = 397540 }));
    }

    [Fact]
    public void Migration_GameWithoutAnEntryYet_ResolvesFromItsRecordUntilSaved()
    {
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "lazy", "deployments.json"));
        store.Upsert(ReShadeRecord("steam:42", "Older Build Game", "[\"CrosireMaster\"]"));
        var sets = Sets(store, "lazy");
        var game = new DetectedGame { Name = "Older Build Game", SteamAppId = 42 };

        var choice = sets.ChoiceFor(game)!;

        Assert.Equal((ShaderAddonMode.Select, "CrosireMaster", false), (choice.ShaderMode, string.Join(",", choice.ShaderPackIds), choice.Saved));
        Assert.False(File.Exists(sets.FilePath)); // reading writes nothing
    }

    [Fact]
    public void RepairAfterMove_AFolderKeyedGamesChoiceFollowsIt()
    {
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "move", "deployments.json"));
        var sets = Sets(store, "move");
        var oldRoot = Path.Combine(_root, "move", "old", "Game");
        var newRoot = Path.Combine(_root, "move", "new", "Game");
        var moved = new DetectedGame { Name = "Moved", InstallPath = newRoot };
        sets.SetShaderMode(new DetectedGame { Name = "Moved", InstallPath = oldRoot }, ShaderAddonMode.Off);

        var staged = sets.StageMove(new GameStateMove(moved, [(oldRoot, newRoot)], [Path.GetFullPath(oldRoot)]));

        Assert.NotNull(staged);
        StagedFileCommit.Commit([staged!]);
        Assert.Equal(ShaderAddonMode.Off, sets.ChoiceFor(moved)!.ShaderMode);
    }
    // ---- Add-ons after a ReShade install (upstream DeployAddonsForCard after ReShade) ---------------------------------

    /// <summary>ReShade's downloads (MutableHandler) plus one Addon Manager add-on ("01", one.addon64).</summary>
    private sealed class AddonRouting() : DelegatingHandler(new MutableHandler())
    {
        private const string Api = "https://api.github.com/repos/rhi-tests/one/releases/latest";
        public bool AddonListOffline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReShadeAddonManagerService.AddonsIniUrl)
                return AddonListOffline
                    ? throw new HttpRequestException("offline (test)")
                    : Ok(new StringContent($"[01]\nPackageName=One\nReleaseApiUrl={Api}\n", Encoding.UTF8));
            if (url == "https://manifest.test/manifest.json") return Ok(new StringContent("{\"version\":2}", Encoding.UTF8));
            if (url == Api)
                return Ok(new StringContent("{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"one.addon64\",\"browser_download_url\":\"https://github.com/rhi-tests/one/releases/download/v1/one.addon64\"}]}", Encoding.UTF8));
            if (url == "https://github.com/rhi-tests/one/releases/download/v1/one.addon64")
                return Ok(new ByteArrayContent(CacheForeignFileRejectionTests.Pe(0x8664, 4096, 1)));
            return base.SendAsync(request, cancellationToken);
        }

        private static Task<HttpResponseMessage> Ok(HttpContent content) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReShadeInstall_OnAGameFollowingGlobalAddOns_AppliesThem_AFailureKeepsReShadeAndSaysSo(bool addonListOffline)
    {
        var (game, store) = SetGame("after-install-" + addonListOffline);
        var cache = Path.Combine(_root, "after-install-cache-" + addonListOffline);
        var sets = Sets(store, "after-install-" + addonListOffline);
        sets.SetGlobalAddonIds(["01"]);
        var routing = new AddonRouting { AddonListOffline = addonListOffline };
        var http = new HttpClient(routing);
        var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new FakeExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")) { Sets = sets });
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, Path.Combine(cache, "manifest.json"));
        var manager = new ReShadeAddonManagerService(http, manifest, Path.Combine(cache, "addons"), Path.Combine(cache, "custom-addons"),
            Path.Combine(cache, "state", "addon-manager.json"), store);
        var managed = new LinuxManagedComponentService(store, reshade, manager, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!)
        { ShaderSets = sets };

        var result = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.True(managed.HasRhiReShade(game));
        var addon = Path.Combine(game.BinaryDirectory!, "one.addon64");
        if (addonListOffline)
        {
            Assert.Contains(LinuxManagedComponentService.AddonSetNotAppliedPrefix, result.Message);
            Assert.False(File.Exists(addon));
        }
        else
        {
            Assert.EndsWith(LinuxManagedComponentService.AddonSetAppliedNote, result.Message);
            Assert.True(File.Exists(addon));
            Assert.Equal(["01"], manager.RecordedSelection(game));
        }
    }
}
