using System.IO.Compression;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #288 (upstream 2.9.3 badb2d9): Vulkan ReShade follows the chosen channel and version, shows its updates and Update All
// updates it. Upstream shares one layer between all Vulkan games; RHI Linux keeps one layer per game (#92), so each game's
// layer is pinned here against the three upstream bugs.
public sealed partial class ReShadeChannelTests
{
    [Fact]
    public async Task VulkanLayer_OnAnOlderStable_ShowsTheUpdate_AndTheUpdateBringsTheLayerUp()
    {
        var (game, store) = CreateVulkanGame("vk-288-stable");
        var handler = new LunarGHandler();
        handler.Inner.StableVersion = "6.7.3";
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-288-stable-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);

        handler.Inner.StableVersion = "6.8.0";
        var check = await service.CheckUpdateAsync(game);
        var update = await service.UpdateAsync(game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal("6.8.0", check.LatestVersion);
        Assert.True(update.Success, update.Message);
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(record));
        Assert.Equal("6.8.0", record.Version);
        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable);
    }

    [Fact]
    public async Task VulkanLayer_NightlyAndStable_SwitchTheLayerBuild_EvenWhenBothAreTheSameSize()
    {
        var (game, store) = CreateVulkanGame("vk-288-nightly");
        var handler = new LunarGHandler();
        handler.Inner.NightlyPayload = NightlyLayerZip();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-288-nightly-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        var layerDll = Path.Combine(LayerFolder(game), "ReShade64.dll");
        var stableBytes = File.ReadAllBytes(layerDll);

        var nightly = await service.InstallAsync(game, new ReShadeInstallOptions { VulkanLayer = true, Channel = ReShadeChannel.Nightly });

        Assert.True(nightly.Success, nightly.Message);
        var nightlyBytes = File.ReadAllBytes(layerDll);
        Assert.Equal(stableBytes.Length, nightlyBytes.Length);
        Assert.NotEqual(stableBytes, nightlyBytes);
        Assert.Equal(ReShadeChannel.Nightly, service.GetInstalledOptions(game).Channel);

        var stable = await service.InstallAsync(game, new ReShadeInstallOptions { VulkanLayer = true, Channel = ReShadeChannel.Stable });

        Assert.True(stable.Success, stable.Message);
        Assert.Equal(stableBytes, File.ReadAllBytes(layerDll));
        Assert.Equal(ReShadeChannel.Stable, service.GetInstalledOptions(game).Channel);
    }

    [Fact]
    public async Task VulkanLayer_LegacyVersion_SwitchesTheLayerToThatVersion_AndUpdateAllKeepsIt()
    {
        var (game, store) = CreateVulkanGame("vk-288-legacy");
        var handler = new LunarGHandler();
        var service = NewVulkanService(store, handler, Path.Combine(_root, "vk-288-legacy-cache"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);

        var legacy = await service.InstallAsync(game, new ReShadeInstallOptions { VulkanLayer = true, Channel = ReShadeChannel.Legacy, LegacyVersion = "6.4.1" });

        Assert.True(legacy.Success, legacy.Message);
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(record));
        Assert.Equal("6.4.1", record.Version);
        var options = service.GetInstalledOptions(game);
        Assert.Equal(ReShadeChannel.Legacy, options.Channel);
        Assert.Equal("6.4.1", options.LegacyVersion);
        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable); // a pinned legacy build is not "updated" away
    }

    // A nightly artifact whose ReShade64.dll is a real x64 PE the size of the stable one, with other bytes.
    private static byte[] NightlyLayerZip()
    {
        var dll = Path.Combine(Path.GetTempPath(), "rhi-288-nightly-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            PeArchitectureDetectorTests.WritePe(dll, 0x8664, 1_100_000);
            using (var stream = new FileStream(dll, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = 512;
                stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me nightly"));
            }
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var entry = archive.CreateEntry("ReShade64.dll", CompressionLevel.NoCompression).Open();
                entry.Write(File.ReadAllBytes(dll));
            }
            return memory.ToArray();
        }
        finally { File.Delete(dll); }
    }
}
