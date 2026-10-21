using System.Net;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;
using Xunit.Abstractions;

namespace RHI.Core.Tests;

/// <summary>
/// w7 integration (#195 × #179; adopted from the smalls re-review's merged probes, which showed the problem): a dropped
/// non-RenoDX add-on makes the game's add-on choice "Choose for this game" with the add-ons it got plus the new one,
/// whatever it was before (Use the global set / Choose / None), so neither the Shaders/Add-ons dialog's catch-up, a global
/// Save nor Update All takes the add-on out again.
/// </summary>
public sealed class DroppedAddonShaderSetChoiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-drop-sets-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private sealed class Remote : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ReShadeAddonManagerService.AddonsIniUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8) });
            if (url == "https://manifest.test/manifest.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":2}", Encoding.UTF8) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private (ReShadeAddonManagerService Manager, LinuxManagedComponentService Managed, LinuxDeploymentRecordStore Records, GlobalShaderAddonStore Sets) Fixture()
    {
        Directory.CreateDirectory(_root);
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
        manager.Sets = sets;
        return (manager, managed, records, sets);
    }

    private DetectedGame Game(LinuxDeploymentRecordStore records, string name, int appId)
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
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = name, Component = ManagedComponentKind.ReShade, BinaryDirectory = dir,
            Files = [new LinuxDeployedFile { Path = Path.Combine(dir, "dxgi.dll"), Sha256 = "00" }],
            Metadata = { ["ReShade.Channel"] = "Stable", ["ReShade.ShaderPacks"] = "[]" },
        });
        return game;
    }

    [Theory]
    [InlineData(ShaderAddonMode.Global)]
    [InlineData(ShaderAddonMode.Select)]
    [InlineData(ShaderAddonMode.Off)]
    public async Task DroppedAddon_MakesTheGamesOwnList_AndTheNextSyncKeepsIt(ShaderAddonMode mode)
    {
        var s = Fixture();
        var game = Game(s.Records, "drop-" + mode, 30 + (int)mode);
        s.Sets.SetAddonMode(game, mode, []); // a saved (pinned) choice, as every game with RHI's ReShade has after #179
        var dropped = P("drop", "ShaderToggler.addon64");
        Directory.CreateDirectory(P("drop"));
        PeArchitectureDetectorTests.WritePe(dropped, 0x8664, 4096);

        var result = await s.Managed.InstallDroppedAddonForGameAsync(game, dropped);
        output.WriteLine($"install: {result.Success} {result.Message}");
        Assert.True(result.Success, result.Message);
        var placed = Path.Combine(game.BinaryDirectory!, "ShaderToggler.addon64");
        Assert.True(File.Exists(placed));

        var choice = s.Sets.ChoiceFor(game)!;
        output.WriteLine($"choice after drop: {choice.AddonMode} [{string.Join(",", choice.AddonIds)}]; behind={s.Managed.AddonSetBehind(game)}");
        Assert.Equal(ShaderAddonMode.Select, choice.AddonMode);              // "Choose for this game"
        Assert.Equal(["custom-ShaderToggler"], choice.AddonIds);              // what it got (nothing) plus the new add-on
        Assert.Empty(s.Sets.GlobalAddonIds);                                  // the global set is never edited
        Assert.False(s.Managed.AddonSetBehind(game));                         // the dialog does not say "behind"

        var synced = await s.Managed.ApplyAddonSetAsync(game);                // dialog Apply / global Save catch-up / batch
        output.WriteLine($"sync: {synced.Outcome} {synced.Message}");
        Assert.Equal(ShaderSetOutcome.Unchanged, synced.Outcome);
        Assert.True(File.Exists(placed));                                     // the dropped add-on stays
    }

    [Fact]
    public async Task DroppedAddon_UpdateAll_KeepsIt()
    {
        var s = Fixture();
        var game = Game(s.Records, "drop-update", 40);
        s.Sets.SetAddonMode(game, ShaderAddonMode.Global);
        var dropped = P("drop2", "ShaderToggler.addon64");
        Directory.CreateDirectory(P("drop2"));
        PeArchitectureDetectorTests.WritePe(dropped, 0x8664, 4096);
        Assert.True((await s.Managed.InstallDroppedAddonForGameAsync(game, dropped)).Success);
        var placed = Path.Combine(game.BinaryDirectory!, "ShaderToggler.addon64");

        var updated = await s.Manager.UpdateAsync(game);
        output.WriteLine($"update: {updated.Success} {updated.Message}");
        Assert.True(File.Exists(placed));
        Assert.Equal(ShaderAddonMode.Select, s.Sets.ChoiceFor(game)!.AddonMode);
    }
}
