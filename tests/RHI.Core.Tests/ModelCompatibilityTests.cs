using RHI.Core.Models;

namespace RHI.Core.Tests;

public class ModelCompatibilityTests
{
    [Fact]
    public void DetectedGame_PreservesUpstreamFields_AndAddsLinuxContext()
    {
        var game = new DetectedGame
        {
            Name = "Example",
            InstallPath = "/games/example",
            Source = "Steam",
            SteamAppId = 123,
            EpicCatalogNamespace = "namespace",
            EpicAppName = "app",
            XboxAumid = "aumid",
            PrefixPath = "/steamapps/compatdata/123/pfx",
            Runner = "Proton"
        };

        Assert.Equal("Example", game.Name);
        Assert.Equal(123, game.SteamAppId);
        Assert.True(game.UsesProton);
    }
}
