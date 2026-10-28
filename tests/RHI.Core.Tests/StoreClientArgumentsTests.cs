using RHI.Platform.Linux.Detection.Launchers;

namespace RHI.Core.Tests;

/// <summary>
/// Which game a store client's arguments start (w12/epic correction 1): Lutris' store services write one entry per game
/// (the client plus its start argument); an entry naming no game is the store's own client.
/// </summary>
public sealed class StoreClientArgumentsTests
{
    [Theory]
    // Lutris' Epic Games Store entries, and the namespace:item:AppName form.
    [InlineData("-opengl -SkipBuildPatchPrereq -com.epicgames.launcher://apps/Dill?action=launch", "Dill")]
    [InlineData("com.epicgames.launcher://apps/ns%3Aitem%3ADill?action=launch&silent=true", "ns:item:Dill|Dill")]
    [InlineData("uplay://launch/4553/0", "4553")]
    [InlineData("origin2://game/launch?offerIds=Origin.OFR.50.0001&autoDownload=1", "Origin.OFR.50.0001")]
    [InlineData("origin2://game/launch?autoDownload=1&offerIds=O.1%2CO.2", "O.1|O.2")]
    [InlineData("link2ea://launchgame/Origin.OFR.50.0002?platform=steam", "Origin.OFR.50.0002")]
    [InlineData("--exec=\"launch Pro\"", "Pro")]
    [InlineData("--exec=launch pro", "pro")]
    [InlineData("battlenet://WoW", "WoW")]
    [InlineData("itch://games/1001", "1001")]
    [InlineData("-launchTitleInFolder \"C:/Program Files/Rockstar Games/GTAV/\"", "folder:C:\\Program Files\\Rockstar Games\\GTAV")]
    // A client that only opens: no game.
    [InlineData("-opengl -SkipBuildPatchPrereq", "")]
    [InlineData("--exec=fast", "")]
    [InlineData("uplay://open", "")]
    [InlineData(null, "")]
    public void NamedGames_ReadsEachStoresStartArgument(string? arguments, string expected)
        => Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal),
            StoreLauncherRecords.NamedGames(arguments).Order(StringComparer.Ordinal));

    [Fact]
    public void NamedGames_OfARecordsOwnArguments_MatchTheLutrisEntrysSpelling()
    {
        // A record keeps one argument per item (a Rockstar folder with spaces stays one argument).
        Assert.True(StoreLauncherRecords.NamedGames(["-launchTitleInFolder", @"C:\Program Files\Rockstar Games\GTAV"])
            .Overlaps(StoreLauncherRecords.NamedGames("-launchTitleInFolder 'C:\\Program Files\\Rockstar Games\\GTAV\\'")));
        Assert.True(StoreLauncherRecords.NamedGames(["--exec=launch pro"]).Overlaps(StoreLauncherRecords.NamedGames("--exec=\"launch Pro\"")));
        Assert.False(StoreLauncherRecords.NamedGames(["uplay://launch/4553"]).Overlaps(StoreLauncherRecords.NamedGames("uplay://launch/7014")));
    }
}
