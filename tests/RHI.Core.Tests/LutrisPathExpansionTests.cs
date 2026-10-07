using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class LutrisPathExpansionTests
{

    [Theory]
    [InlineData("$HOME/Games/x/x.exe", "/h/me/Games/x/x.exe")]
    [InlineData("${HOME}/Games/x.exe", "/h/me/Games/x.exe")]
    [InlineData("~", "/h/me")]
    [InlineData("~/Games/x.exe", "/h/me/Games/x.exe")]
    [InlineData("$GAMEDIR/drive_c/x.exe", "/g/dir/drive_c/x.exe")]
    [InlineData("/mnt/${GAMEDIR}x", "/mnt//g/dirx")]
    [InlineData("/d/$LIB/x.exe", "/d/lib-dir/x.exe")]
    [InlineData("/d/${LIB}_2/x.exe", "/d/lib-dir_2/x.exe")]
    [InlineData("/d/$NOPE/x.exe", "/d/$NOPE/x.exe")]
    [InlineData("/d/${NOPE}/x.exe", "/d/${NOPE}/x.exe")]
    [InlineData("/d/$/x", "/d/$/x")]
    [InlineData("/d/a$", "/d/a$")]
    public void ExpandsPosixVariablesAnywhere_AndLeavesUnknownOnesLiteral(string input, string expected)
    {
        string? Environment(string name) => name == "LIB" ? "lib-dir" : null;
        Assert.Equal(expected, LutrisPathExpander.Expand(input, "/h/me", "/g/dir/", Environment));
    }

    [Fact]
    public void GameDirWithoutAPgaDirectory_StaysLiteral()
        => Assert.Equal("$GAMEDIR/x.exe", LutrisPathExpander.Expand("$GAMEDIR/x.exe", "/h", null, _ => null));

    [Fact]
    public void TildeUser_UsesThePasswdHome()
    {
        using var tree = new DetectionTestTree();
        var passwd = tree.File("root:x:0:0::/root:/bin/sh\nalice:x:1000:1000:Alice:/srv/alice:/bin/bash\n", "passwd");

        Assert.Equal("/srv/alice", LutrisPathExpander.UserHome("alice", passwd));
        Assert.Null(LutrisPathExpander.UserHome("bob", passwd));
        Assert.Equal("/root/Games", LutrisPathExpander.Expand("~root/Games", "/h", null, _ => null));
        Assert.Equal("~nosuchuser-rhi/Games", LutrisPathExpander.Expand("~nosuchuser-rhi/Games", "/h", null, _ => null));
    }

    [Fact]
    public void Detector_ExpandsHomeAndGameDir_AndSkipsGamesPgaMarksUninstalled()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var configs = tree.Dir("lutris", "games");
        var viaHome = tree.Exe("home", "Games", "ViaHome", "ViaHome.exe");
        var viaBraces = tree.Exe("home", "Games", "ViaBraces", "ViaBraces.exe");
        var gameDir = tree.Dir("library", "dir-game");
        var viaGameDir = tree.Exe("library", "dir-game", "bin", "Game.exe");
        var gone = tree.Exe("home", "Games", "Gone", "Gone.exe");
        tree.File("game:\n  exe: $HOME/Games/ViaHome/ViaHome.exe\n", "lutris", "games", "via-home-1700000001.yml");
        tree.File("game:\n  exe: ${HOME}/Games/ViaBraces/ViaBraces.exe\n", "lutris", "games", "via-braces-1700000002.yml");
        tree.File("game:\n  exe: ~/Games/ViaHome/ViaHome.exe\n  working_dir: ~\n", "lutris", "games", "via-tilde-1700000003.yml");
        tree.File("game:\n  exe: ${GAMEDIR}/bin/Game.exe\n", "lutris", "games", "dir-game-1700000004.yml");
        tree.File("game:\n  exe: $HOME/Games/Gone/Gone.exe\n", "lutris", "games", "gone-1700000005.yml");
        var library = new Dictionary<string, LutrisPgaGame>
        {
            ["dir-game-1700000004"] = new("dir-game-1700000004", "Dir Game", "dir-game", gameDir, true),
            ["gone-1700000005"] = new("gone-1700000005", "Gone", "gone", Path.GetDirectoryName(gone), false)
        };

        var games = LutrisGameDetector.DetectWith([configs], home, library, CancellationToken.None)
            .ToDictionary(g => g.Name);

        Assert.Equal(viaHome, games["via home"].LaunchExecutablePath);
        Assert.Equal(viaBraces, games["via braces"].LaunchExecutablePath);
        Assert.Equal(viaHome, games["via tilde"].LaunchExecutablePath);
        Assert.Equal(viaGameDir, games["Dir Game"].LaunchExecutablePath);
        Assert.False(games.ContainsKey("Gone"));
    }
}
