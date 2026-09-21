using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class GraphicsApiOverrideStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-api-override-tests-" + Guid.NewGuid().ToString("N"));

    public GraphicsApiOverrideStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DetectedGame_EffectiveGraphicsApi_ExplicitOverrideWinsAndAutoFallsBack()
    {
        var game = new DetectedGame { GraphicsApi = GraphicsApi.DirectX12 };
        Assert.Equal(GraphicsApi.DirectX12, game.EffectiveGraphicsApi);
        Assert.False(game.HasExplicitGraphicsApiOverride);

        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;
        Assert.Equal(GraphicsApi.DirectX11, game.EffectiveGraphicsApi);
        Assert.True(game.HasExplicitGraphicsApiOverride);

        game.UserGraphicsApiOverride = null;
        Assert.Equal(GraphicsApi.DirectX12, game.EffectiveGraphicsApi);
        Assert.False(game.HasExplicitGraphicsApiOverride);
    }

    [Fact]
    public void DetectedGame_EffectiveGraphicsApi_UserWinsThenManifestThenDetector()
    {
        var game = new DetectedGame
        {
            GraphicsApi = GraphicsApi.DirectX11,
            ManifestGraphicsApiOverride = GraphicsApi.DirectX12
        };

        Assert.Equal(GraphicsApi.DirectX12, game.EffectiveGraphicsApi);
        game.UserGraphicsApiOverride = GraphicsApi.Vulkan;
        Assert.Equal(GraphicsApi.Vulkan, game.EffectiveGraphicsApi);
        game.UserGraphicsApiOverride = null;
        Assert.Equal(GraphicsApi.DirectX12, game.EffectiveGraphicsApi);
        game.ManifestGraphicsApiOverride = null;
        Assert.Equal(GraphicsApi.DirectX11, game.EffectiveGraphicsApi);
    }

    [Fact]
    public void Store_RoundTripsExplicitAuthorityAndAutoClearsIt()
    {
        var path = Path.Combine(_root, "api-overrides.json");
        var game = CreateGame("game-a");
        var store = new GraphicsApiOverrideStore(path);

        Assert.Null(store.Get(game));
        store.Set(game, GraphicsApi.DirectX11);
        Assert.Equal(GraphicsApi.DirectX11, new GraphicsApiOverrideStore(path).Get(game));

        new GraphicsApiOverrideStore(path).Set(game, null);
        Assert.Null(new GraphicsApiOverrideStore(path).Get(game));
    }

    [Fact]
    public void Store_UsesPerGameIdentityInsteadOfTitleOnly()
    {
        var path = Path.Combine(_root, "identity-overrides.json");
        var first = CreateGame("first");
        var second = CreateGame("second");
        first.Name = second.Name = "Same Display Name";
        var store = new GraphicsApiOverrideStore(path);

        store.Set(first, GraphicsApi.DirectX11);
        store.Set(second, GraphicsApi.DirectX12);

        Assert.Equal(GraphicsApi.DirectX11, store.Get(first));
        Assert.Equal(GraphicsApi.DirectX12, store.Get(second));
    }

    private DetectedGame CreateGame(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return new DetectedGame { Name = name, InstallPath = dir, Source = "Test" };
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
