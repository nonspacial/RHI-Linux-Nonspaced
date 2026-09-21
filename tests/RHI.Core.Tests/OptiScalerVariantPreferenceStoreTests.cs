using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class OptiScalerVariantPreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-variant-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetAndGet_PersistVariantPerGameAndStableClearsOverride()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "variants.json");
        var store = new OptiScalerVariantPreferenceStore(path);
        var game = new DetectedGame { Name = "Game", Source = "Steam", InstallPath = "/games/test", ExecutablePath = "/games/test/Game.exe" };

        Assert.Equal(OptiScalerVariant.Stable, store.Get(game));
        store.Set(game, OptiScalerVariant.Nightly);
        Assert.Equal(OptiScalerVariant.Nightly, new OptiScalerVariantPreferenceStore(path).Get(game));
        store.Set(game, OptiScalerVariant.DlssNr);
        Assert.Equal(OptiScalerVariant.DlssNr, new OptiScalerVariantPreferenceStore(path).Get(game));
        store.Set(game, OptiScalerVariant.Stable);
        Assert.Equal(OptiScalerVariant.Stable, new OptiScalerVariantPreferenceStore(path).Get(game));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
