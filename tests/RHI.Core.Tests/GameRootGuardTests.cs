using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class GameRootGuardTests
{
    [Fact]
    public void WholeMachineFolders_AreRefused_AGameFolderIsNot()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home", "me");
        var prefix = tree.Dir("home", "me", "Games", "battlenet");
        tree.Dir("home", "me", "Games", "battlenet", "drive_c", "Program Files (x86)", "Battle.net");
        tree.File("", "home", "me", "Games", "battlenet", "system.reg");
        var library = tree.Dir("disk", "SteamLibrary", "steamapps", "common", "Game");
        var game = tree.Dir("disk", "Games", "Some Game");
        var homeAlias = tree.Link(home, "home-alias");

        Assert.Equal(GameRootProblem.FilesystemRoot, GameRootGuard.Check("/", home));
        Assert.Equal(GameRootProblem.SystemFolder, GameRootGuard.Check("/usr", home));
        Assert.Equal(GameRootProblem.HomeFolder, GameRootGuard.Check(home + "/", home));
        Assert.Equal(GameRootProblem.HomeFolder, GameRootGuard.Check(homeAlias, home));
        Assert.Equal(GameRootProblem.HomeFolder, GameRootGuard.Check(tree.Path_("home"), home));
        Assert.Equal(GameRootProblem.SharedFolder, GameRootGuard.Check(tree.Dir("home", "me", ".local", "bin"), home));
        Assert.Equal(GameRootProblem.SharedFolder, GameRootGuard.Check(tree.Path_("home", "me", "Games"), home));
        Assert.Equal(GameRootProblem.WinePrefix, GameRootGuard.Check(prefix, home));
        Assert.Equal(GameRootProblem.WinePrefix, GameRootGuard.Check(Path.Combine(prefix, "drive_c"), home));
        Assert.Equal(GameRootProblem.WinePrefix, GameRootGuard.Check(Path.Combine(prefix, "drive_c", "Program Files (x86)"), home));
        Assert.Equal(GameRootProblem.SteamLibraryFolder, GameRootGuard.Check(Path.GetDirectoryName(library)!, home));
        Assert.Equal(GameRootProblem.SteamLibraryFolder, GameRootGuard.Check(tree.Path_("disk", "SteamLibrary"), home));
        Assert.Equal(GameRootProblem.Missing, GameRootGuard.Check(tree.Path_("nope"), home));

        Assert.Equal(GameRootProblem.None, GameRootGuard.Check(library, home));
        Assert.Equal(GameRootProblem.None, GameRootGuard.Check(game, home));
        Assert.Equal(GameRootProblem.None, GameRootGuard.Check(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), home));
    }

    [Fact]
    public void AddAndEditRefusal_NamesTheProblem()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var prefix = tree.Dir("pfx", "drive_c");

        Assert.NotNull(MainWindowViewModel.GameRootRefusal(Path.GetDirectoryName(prefix)!, home));
        Assert.NotNull(MainWindowViewModel.GameRootRefusal(home, home));
        Assert.NotNull(MainWindowViewModel.GameRootRefusal("/", home));
        Assert.Null(MainWindowViewModel.GameRootRefusal(tree.Dir("games", "x"), home));
    }

    [Fact]
    public async Task ManualDetector_DoesNotListAWholePrefixOrRoot()
    {
        using var tree = new DetectionTestTree();
        var store = new LinuxGameLibraryStore(tree.Path_("game-library.json"));
        var state = store.Load();
        var prefix = tree.Dir("pfx");
        tree.Dir("pfx", "drive_c");
        var game = tree.Dir("games", "Real");
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Prefix", InstallPath = prefix });
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Root", InstallPath = "/" });
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Real", InstallPath = game });
        store.Save(state);

        var games = await new ManualGameDetector(store).DetectAsync();

        Assert.Equal("Real", Assert.Single(games).Name);
    }

    [Fact]
    public void HeroicRecordPointingAtTheFilesystemRoot_IsNotListed()
    {
        using var tree = new DetectionTestTree();
        var json = tree.File("""{ "installed": [ { "title": "Bad", "appName": "bad", "install_path": "/", "is_installed": true } ] }""", "installed.json");

        Assert.Empty(HeroicGameDetector.ParseInstalledJsonForTests(json, "gog"));
    }

    [Fact]
    public void Indexer_ReportsWhenItStoppedAtTheCap()
    {
        using var tree = new DetectionTestTree();
        for (var i = 0; i < 5; i++) tree.File("x", "game", $"f{i}.dat");

        Assert.True(GameFileIndexer.IsTruncated(GameFileIndexer.Build(tree.Path_("game"), 8, 3)));
        Assert.False(GameFileIndexer.IsTruncated(GameFileIndexer.Build(tree.Path_("game"), 8, 5)));
        Assert.False(GameFileIndexer.IsTruncated(GameFileIndexer.Build(tree.Path_("missing"), 8, 5)));
    }
}
