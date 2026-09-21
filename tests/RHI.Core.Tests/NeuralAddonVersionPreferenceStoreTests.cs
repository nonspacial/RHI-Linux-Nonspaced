using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class NeuralAddonVersionPreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-neural-pin-" + Guid.NewGuid().ToString("N"));

    public NeuralAddonVersionPreferenceStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Set_PersistsPinsPerGameAndComponentWhileLatestRemovesOverride()
    {
        var path = Path.Combine(_root, "pins.json");
        var game = new DetectedGame { Name = "Game", InstallPath = "/games/game", Source = "Manual" };
        var store = new NeuralAddonVersionPreferenceStore(path);

        store.Set(game, ManagedComponentKind.Dlss5Feeder, "v0.13.1-beta.1");
        store.Set(game, ManagedComponentKind.Dlss5Dx11Bridge, "v0.9.0");

        var reload = new NeuralAddonVersionPreferenceStore(path);
        Assert.Equal("v0.13.1-beta.1", reload.Get(game, ManagedComponentKind.Dlss5Feeder));
        Assert.Equal("v0.9.0", reload.Get(game, ManagedComponentKind.Dlss5Dx11Bridge));
        Assert.Null(reload.Get(game, ManagedComponentKind.ShortFuse));

        reload.Set(game, ManagedComponentKind.Dlss5Feeder, null);
        Assert.Null(new NeuralAddonVersionPreferenceStore(path).Get(game, ManagedComponentKind.Dlss5Feeder));
    }

    [Fact]
    public void Set_RejectsNonNeuralComponentsAndUnsafeTags()
    {
        var game = new DetectedGame { Name = "Game", InstallPath = "/games/game", Source = "Manual" };
        var store = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "invalid.json"));

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Set(game, ManagedComponentKind.ReShade, "v1"));
        Assert.Throws<ArgumentException>(() => store.Set(game, ManagedComponentKind.Dlss5Feeder, "../bad"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}
