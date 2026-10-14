using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Core.Tests;

/// <summary>
/// DET-24 (Refs #63): selecting a game used to read every per-game preference file again on the UI thread (API override,
/// OptiScaler variant, four neural-version pins, and the Repair-after-move stores), each a whole-file read and parse.
/// Unchanged store files are now served from the store's own copy (a stat, not a read): after the first visit a
/// selection reads at most one store file, and Update All's availability reads none.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SelectionStoreReadTests
{
    [Fact]
    public async Task SwitchingBetweenGames_ReadsAtMostOneStoreFile_PerSelection()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var alpha = await AddAsync(fx, "Alpha Quest");
        var beta = await AddAsync(fx, "Beta Quest");
        // Every per-game preference file exists (a store with no file is never read, which would hide the reads).
        var prefs = Path.Combine(fx.ConfigDir, "prefs");
        new GraphicsApiOverrideStore(Path.Combine(prefs, "api.json")).Set(alpha, GraphicsApi.DirectX11);
        new OptiScalerVariantPreferenceStore(Path.Combine(prefs, "opti.json")).Set(beta, OptiScalerVariant.Nightly);
        new LumaModePreferenceStore(Path.Combine(prefs, "luma.json")).Set(alpha, LumaModePreference.Disabled);
        fx.Managed.SetNeuralAddonVersionPin(beta, ManagedComponentKind.Dlss5Feeder, "v1.0");
        fx.Managed.SetNeuralRenderingDllVersionPin(alpha, "310.1.0");
        await SelectAsync(fx, "Alpha Quest");
        await SelectAsync(fx, "Beta Quest");

        foreach (var name in new[] { "Alpha Quest", "Beta Quest", "Alpha Quest" })
        {
            var before = JsonStoreReads.Under(fx.Root);
            await SelectAsync(fx, name);
            var reads = JsonStoreReads.Under(fx.Root) - before;
            Assert.True(reads <= 1, $"selecting {name} read {reads} store files");
        }
        // The cached copies still give each game its own saved preferences.
        Assert.Equal(RHI.Desktop.ViewModels.OptionLists.GraphicsApiOverride.Display(GraphicsApi.DirectX11), fx.Vm.Runtime.SelectedGraphicsApiOverride.Display);
    }

    [Fact]
    public async Task UpdateAllAvailability_ReadsNoStoreFile()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        var before = JsonStoreReads.Under(fx.Root);
        for (var i = 0; i < 50; i++) _ = fx.Vm.Updates.CanUpdateAll && fx.Vm.CanOpenDlssBatch;
        Assert.Equal(before, JsonStoreReads.Under(fx.Root));
    }

    [Fact]
    public async Task AStoreFileChangedOnDisk_IsReadAgain()
    {
        var root = Directory.CreateTempSubdirectory("rhi-store-reads-").FullName;
        try
        {
            var path = Path.Combine(root, "api.json");
            var game = new DetectedGame { Name = "Edited Outside", Source = "Wine", InstallPath = Path.Combine(root, "g") };
            var store = new GraphicsApiOverrideStore(path);
            store.Set(game, GraphicsApi.DirectX11);
            Assert.Equal(GraphicsApi.DirectX11, store.Get(game));
            var reads = JsonStoreReads.Under(root);
            Assert.Equal(GraphicsApi.DirectX11, store.Get(game));
            Assert.Equal(reads, JsonStoreReads.Under(root)); // unchanged file: no read

            // Another writer (a second RHI instance, an editor) replaces the file: the next load sees it.
            new GraphicsApiOverrideStore(path).Set(game, GraphicsApi.Vulkan);
            Assert.Equal(GraphicsApi.Vulkan, store.Get(game));
            // An in-place edit that keeps the inode and the length is seen too (ctime/mtime change).
            var text = File.ReadAllText(path).Replace("Vulkan", "OpenGL", StringComparison.Ordinal);
            await Task.Delay(20);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) { stream.Write(System.Text.Encoding.UTF8.GetBytes(text)); }
            Assert.Equal(GraphicsApi.OpenGL, store.Get(game));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<DetectedGame> AddAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        return fx.Vm.Games.Single(row => row.Name == name).Game;
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == name).Game;
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || !fx.Vm.StatusText.StartsWith("Ready", StringComparison.Ordinal)); i++)
            await Task.Delay(25);
        await Task.Delay(100); // the background latest-version refresh and the Repair-after-move check settle
    }
}
