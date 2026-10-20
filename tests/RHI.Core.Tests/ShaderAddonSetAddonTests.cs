using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179, add-ons half (upstream <c>GetPerGameAddonMode</c> + <c>EnabledGlobalAddons</c>): each game's Addon Manager
/// add-ons follow its Global / Select / Off choice; Update All's Addon Manager family (#194) re-applies the effective set;
/// Off removes only the add-ons RHI placed; a No Addons game (#170) is held; the global set's batch never touches a Select
/// or Off game. Plus the start-up shader-pack prefetch staging the global set first.
/// </summary>
public sealed class ShaderAddonSetAddonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-addon-sets-" + Guid.NewGuid().ToString("N"));

    public ShaderAddonSetAddonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private const string ApiOne = "https://api.github.com/repos/rhi-tests/one/releases/latest";
    private const string ApiTwo = "https://api.github.com/repos/rhi-tests/two/releases/latest";

    /// <summary>Two release add-ons ("01" one.addon64, "02" two.addon64).</summary>
    private sealed class Remote : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReShadeAddonManagerService.AddonsIniUrl)
                return Ok(new StringContent($"[01]\nPackageName=One\nReleaseApiUrl={ApiOne}\n\n[02]\nPackageName=Two\nReleaseApiUrl={ApiTwo}\n", Encoding.UTF8));
            if (url == "https://manifest.test/manifest.json") return Ok(new StringContent("{\"version\":2}", Encoding.UTF8));
            foreach (var (api, name, marker) in new[] { (ApiOne, "one", (byte)1), (ApiTwo, "two", (byte)2) })
            {
                if (url == api)
                    return Ok(new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = "v1",
                        assets = new[] { new { name = name + ".addon64", browser_download_url = $"https://github.com/rhi-tests/{name}/releases/download/v1/{name}.addon64" } }
                    }), Encoding.UTF8));
                if (url == $"https://github.com/rhi-tests/{name}/releases/download/v1/{name}.addon64")
                    return Ok(new ByteArrayContent(CacheForeignFileRejectionTests.Pe(0x8664, 4096, marker)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(HttpContent content) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed record Setup(ReShadeAddonManagerService Manager, LinuxManagedComponentService Managed, LinuxDeploymentRecordStore Records,
        GlobalShaderAddonStore Sets)
    {
        public static string Addon(DetectedGame game, string name) => Path.Combine(game.BinaryDirectory!, name + ".addon64");
    }

    private Setup Fixture()
    {
        var http = new HttpClient(new Remote());
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, P("manifest-cache.json"));
        var records = new LinuxDeploymentRecordStore(P("deployments.json"));
        Directory.CreateDirectory(P("custom"));
        var manager = new ReShadeAddonManagerService(http, manifest, P("addon-cache"), P("custom"), P("state", "addon-manager.json"), records);
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: manager, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);
        var sets = new GlobalShaderAddonStore(records, P("shader-addon-sets.json"));
        managed.ShaderSets = sets;
        return new(manager, managed, records, sets);
    }

    // A game with RHI's ReShade (a record; the add-on folder holds a ReShade-looking dxgi.dll).
    private DetectedGame Game(Setup s, string name, int appId, string channel = "Stable")
    {
        var dir = P(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "ReShade by crosire");
        PeArchitectureDetectorTests.WritePe(Path.Combine(dir, "Game.exe"), 0x8664, 64 * 1024);
        var game = new DetectedGame
        {
            Name = name, Source = "Steam", SteamAppId = appId, InstallPath = dir, BinaryDirectory = dir,
            ExecutablePath = Path.Combine(dir, "Game.exe"), Runner = "Proton"
        };
        s.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = name, Component = ManagedComponentKind.ReShade, BinaryDirectory = dir,
            Files = [new LinuxDeployedFile { Path = Path.Combine(dir, "dxgi.dll"), Sha256 = "00" }],
            Metadata = { ["ReShade.Channel"] = channel, ["ReShade.ShaderPacks"] = "[]" },
        });
        return game;
    }

    public static TheoryData<ShaderAddonMode, bool, string> AddonMatrix()
    {
        var data = new TheoryData<ShaderAddonMode, bool, string>();
        foreach (var mode in Enum.GetValues<ShaderAddonMode>())
            foreach (var globalEmpty in new[] { true, false })
                data.Add(mode, globalEmpty, mode switch
                {
                    ShaderAddonMode.Off => "",
                    ShaderAddonMode.Select => "own",
                    _ => globalEmpty ? "" : "01,02",
                });
        return data;
    }

    [Theory]
    [MemberData(nameof(AddonMatrix))]
    public void AddonResolution_GlobalSelectOff_ByGlobalSet(ShaderAddonMode mode, bool globalEmpty, string expected)
    {
        var choice = new ShaderAddonGameChoice(ShaderAddonMode.Global, [], mode, ["own"], Saved: true);
        Assert.Equal(expected, string.Join(",", ReShadeAddonManagerService.EffectiveIds(choice, globalEmpty ? [] : ["01", "02"])));
    }

    [Theory]
    [InlineData(ShaderAddonMode.Global)]
    [InlineData(ShaderAddonMode.Select)]
    public void NoAddonsChannel_GetsNoAddOns_AndIsHeld(ShaderAddonMode mode)
    {
        var s = Fixture();
        var game = Game(s, "no-addons", 10, channel: "NoAddons");
        s.Sets.SetGlobalAddonIds(["01"]);
        s.Sets.SetAddonMode(game, mode, ["02"]);

        var effective = s.Manager.EffectiveSelectionFor(game)!;

        Assert.True(effective.HeldByNoAddons);
        Assert.Empty(effective.Ids);
    }

    [Fact]
    public async Task UpdateAll_AddonManagerFamily_ReappliesTheEffectiveSet()
    {
        var s = Fixture();
        var game = Game(s, "update", 11);
        Assert.True((await s.Manager.ApplySelectionAsync(game, ["01"])).Success); // recorded before the global set changed
        s.Sets.SetAddonMode(game, ShaderAddonMode.Global);
        s.Sets.SetGlobalAddonIds(["02"]);

        var updated = await s.Manager.UpdateAsync(game);

        Assert.True(updated.Success, updated.Message);
        Assert.False(File.Exists(Setup.Addon(game, "one")));
        Assert.True(File.Exists(Setup.Addon(game, "two")));
        Assert.Equal(["02"], s.Manager.RecordedSelection(game));
    }

    [Fact]
    public async Task UpdateAll_WithoutAStore_ReappliesTheRecordedSelection()
    {
        var s = Fixture();
        var game = Game(s, "update-legacy", 12);
        Assert.True((await s.Manager.ApplySelectionAsync(game, ["01"])).Success);
        s.Managed.ShaderSets = null;

        Assert.True((await s.Manager.UpdateAsync(game)).Success);

        Assert.Equal(["01"], s.Manager.RecordedSelection(game));
        Assert.True(File.Exists(Setup.Addon(game, "one")));
    }

    [Fact]
    public async Task Off_RemovesOnlyTheAddOnsRhiPlaced_GlobalBringsThemBack()
    {
        var s = Fixture();
        var game = Game(s, "off", 13);
        s.Sets.SetGlobalAddonIds(["01"]);
        Assert.Equal(ShaderSetOutcome.Changed, (await s.Managed.ApplyAddonSetAsync(game)).Outcome);
        var placed = File.ReadAllBytes(Setup.Addon(game, "one"));
        var mine = Setup.Addon(game, "mine"); // the user's own add-on
        File.WriteAllBytes(mine, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 77));

        s.Sets.SetAddonMode(game, ShaderAddonMode.Off);
        Assert.Equal(ShaderSetOutcome.Changed, (await s.Managed.ApplyAddonSetAsync(game)).Outcome);

        Assert.False(File.Exists(Setup.Addon(game, "one")));
        Assert.True(File.Exists(mine));

        s.Sets.SetAddonMode(game, ShaderAddonMode.Global);
        Assert.Equal(ShaderSetOutcome.Changed, (await s.Managed.ApplyAddonSetAsync(game)).Outcome);

        Assert.Equal(placed, File.ReadAllBytes(Setup.Addon(game, "one")));
        Assert.True(File.Exists(mine));
        Assert.Equal(ShaderSetOutcome.Unchanged, (await s.Managed.ApplyAddonSetAsync(game)).Outcome);
    }

    [Fact]
    public async Task GlobalAddonEdit_ChangesOnlyGamesFollowingIt_SelectOffAndNoAddonsGamesStayAsTheyAre()
    {
        var s = Fixture();
        var global = Game(s, "edit-global", 21);
        var select = Game(s, "edit-select", 22);
        var off = Game(s, "edit-off", 23);
        var noAddons = Game(s, "edit-noaddons", 24, channel: "NoAddons");
        s.Sets.SetAddonMode(select, ShaderAddonMode.Select, ["01"]);
        s.Sets.SetAddonMode(off, ShaderAddonMode.Off);
        Assert.True((await s.Manager.ApplySelectionAsync(select, ["01"])).Success);
        var dirs = new[] { select, off, noAddons }.Select(game => game.BinaryDirectory!).ToArray();
        var before = CancelAtWritePhase.Snapshot(dirs);
        var selectRecord = JsonSerializer.Serialize(s.Records.Find(select, ManagedComponentKind.ReShadeAddonManager));

        s.Sets.SetGlobalAddonIds(["02"]);
        var result = await new ShaderAddonSetBatch(s.Managed, new NeverRunning()).ApplyAsync([global, select, off, noAddons], ShaderAddonSetPart.Addons, CancellationToken.None);

        Assert.Equal((1, 1, 0), (result.Changed, result.Held, result.Failed));
        Assert.True(File.Exists(Setup.Addon(global, "two")));
        CancelAtWritePhase.AssertUnchanged(before, dirs);
        Assert.Equal(selectRecord, JsonSerializer.Serialize(s.Records.Find(select, ManagedComponentKind.ReShadeAddonManager)));
    }

    [Fact]
    public void ExplicitAddonChoice_ADifferentListMakesTheGameSelect_TheSameKeepsGlobal()
    {
        var s = Fixture();
        var game = Game(s, "explicit", 31);
        s.Sets.SetGlobalAddonIds(["01"]);

        Assert.Null(s.Managed.RecordExplicitAddonChoice(game, ["01"]));
        Assert.Equal(ShaderAddonMode.Global, s.Sets.ChoiceFor(game)!.AddonMode);
        Assert.Null(s.Managed.RecordExplicitAddonChoice(game, ["01", "02"]));

        var choice = s.Sets.ChoiceFor(game)!;
        Assert.Equal((ShaderAddonMode.Select, "01,02"), (choice.AddonMode, string.Join(",", choice.AddonIds)));
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // ---- Start-up prefetch (CacheAllShaders) follows the global set --------------------------------------------------

    private sealed class PackHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); // every pack fails: only the order matters
        }
    }

    [Theory]
    [InlineData(GlobalShaderMode.Managed, true)]
    [InlineData(GlobalShaderMode.Off, false)]
    public async Task Prefetch_StagesTheGlobalSetsPacksFirst(GlobalShaderMode mode, bool depth3DFirst)
    {
        var handler = new PackHandler();
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(P("prefetch", "deployments.json"));
        var sets = new GlobalShaderAddonStore(records, P("prefetch", "sets.json"));
        sets.SetGlobalShaderPackIds(["Depth3D"]);
        sets.SetShaderMode(mode);
        var service = new ReShadeShaderPackService(http, P("prefetch", "cache"), customRoot: P("prefetch", "custom")) { Sets = sets };

        await service.PrefetchMissingAsync();

        var depth3D = ReShadeShaderPackCatalog.DefaultPacks.Single(pack => pack.Id == "Depth3D").Url;
        Assert.Equal(depth3DFirst, handler.Requests[0] == depth3D);
        Assert.Contains(depth3D, handler.Requests);
    }
}
