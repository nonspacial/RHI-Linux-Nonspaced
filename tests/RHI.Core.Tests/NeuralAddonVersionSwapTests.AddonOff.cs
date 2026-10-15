using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// #170 review: an add-on ReShade's No Addons build turned off (renamed to <name>.rhi-off) is never swapped to another
// version or renamed for ShortFuse's load order (each would put a loadable copy back or miss the renamed file); those paths
// refuse with the plain "turned off" text before any download. Removing it is the user's own choice and works on both
// Remove paths, with one answer.
public sealed partial class NeuralAddonVersionSwapTests
{
    [Fact]
    public async Task AddonTurnedOff_VersionSwapsAndZzzToggle_RefuseBeforeAnyDownload_AndLeaveItOff()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "off-swap", "game")).FullName;
        var game = Game(bin);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "off-swap-deployments.json"));
        var offFiles = new Dictionary<ManagedComponentKind, string>
        {
            [ManagedComponentKind.ShortFuse] = Write(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName + ReShadeAddonSwitch.OffSuffix), Payload(0x11, 40 * 1024)),
            [ManagedComponentKind.Dlss5Feeder] = Write(Path.Combine(bin, "dlss5-feed.addon64" + ReShadeAddonSwitch.OffSuffix), Pe(0x41)),
            [ManagedComponentKind.Dlss5Dx11Bridge] = Write(Path.Combine(bin, "dlss5-bridge.addon64" + ReShadeAddonSwitch.OffSuffix), Pe(0x42))
        };
        foreach (var (kind, path) in offFiles)
        {
            var record = Record(game, kind, "1.0", path);
            if (kind == ManagedComponentKind.Dlss5Feeder) record.Metadata["Feeder.FullStack"] = "1";
            records.Upsert(record);
        }
        var handler = new MapHandler(new());
        using var http = new HttpClient(handler);
        var pins = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "off-swap-pins.json"));
        var service = OffTestService(http, records, "off-swap", pins);
        var ledger = File.ReadAllText(Path.Combine(_root, "off-swap-deployments.json"));

        foreach (var (kind, path) in offFiles)
        {
            pins.Set(game, kind, "v9.9");
            var bytes = File.ReadAllBytes(path);

            var result = await service.SwapNeuralAddonVersionAsync(game, kind);

            Assert.False(result.Success);
            Assert.Equal(ReShadeAddonSwitch.HeldPrefix + LinuxManagedComponentService.DisplayDependencyName(kind), result.Message);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path[..^ReShadeAddonSwitch.OffSuffix.Length])); // no loadable copy put back
        }

        var zzz = await service.SetShortFuseZzzLoadOrderAsync(game, enabled: true);

        Assert.False(zzz.Success);
        Assert.Equal(ReShadeAddonSwitch.HeldPrefix + "ShortFuse", zzz.Message);
        Assert.False(service.GetShortFuseZzzLoadOrderEnabled(game));
        Assert.False(File.Exists(Path.Combine(bin, ShortFuseDeploymentService.ZzzAddonFileName)));
        Assert.Empty(handler.Gets);
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(_root, "off-swap-deployments.json")));
    }

    [Fact]
    public async Task ShortFuseTurnedOff_BothRemovePaths_DeleteTheOffFile_WithOneAnswer()
    {
        var results = new List<ComponentOperationResult>();
        foreach (var extras in new[] { true, false })
        {
            var tag = extras ? "off-remove-extras" : "off-remove-generic";
            var bin = Directory.CreateDirectory(Path.Combine(_root, tag, "game")).FullName;
            var game = Game(bin);
            var records = new LinuxDeploymentRecordStore(Path.Combine(_root, tag + "-deployments.json"));
            var off = Write(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName + ReShadeAddonSwitch.OffSuffix), Payload(0x12, 40 * 1024));
            records.Upsert(Record(game, ManagedComponentKind.ShortFuse, "8.5.0-rc5", off));
            using var http = new HttpClient(new MapHandler(new()));
            var service = OffTestService(http, records, tag, new NeuralAddonVersionPreferenceStore(Path.Combine(_root, tag + "-pins.json")));
            Assert.True(ReShadeAddonSwitch.IsOff(records.Find(game, ManagedComponentKind.ShortFuse)));

            var result = extras
                ? await service.RemoveShortFuseAsync(game) // the Extras ShortFuse button
                : await service.RemoveAsync(game, ManagedComponentKind.ShortFuse); // the component row

            Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(off));
            Assert.Null(records.Find(game, ManagedComponentKind.ShortFuse));
            results.Add(result);
        }
        Assert.Equal(results[0].Message, results[1].Message);
    }

    private LinuxManagedComponentService OffTestService(HttpClient http, LinuxDeploymentRecordStore records, string tag, NeuralAddonVersionPreferenceStore pins)
    {
        var bridge = new UpstreamRhiAddonDeploymentService(http, records, new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json"),
            new UpstreamRhiAddonDeploymentOptions(ManagedComponentKind.Dlss5Dx11Bridge, "dlss5-dx11-bridge", "DLSS5 DX11 Bridge",
                "https://api.github.com/repos/NIGos/dlss5-bridge/releases?per_page=1", 128 * 1024,
                ExactAssetName64: "dlss5-bridge.addon64", DeployFileName64: "dlss5-bridge.addon64", X64Only: true),
            Path.Combine(_root, tag + "-cache-bridge"));
        return new LinuxManagedComponentService(
            records: records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: Feeder(http, records), dlss5Bridge: bridge, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: ShortFuse(http, records, tag), dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!, neuralVersionPins: pins);
    }
}
