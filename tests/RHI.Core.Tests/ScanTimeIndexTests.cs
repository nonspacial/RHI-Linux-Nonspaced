using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// DET-09 (Refs #48): a library scan picks each game's executable from a shallow, bounded walk that stops at the first
/// level holding one, gets the scan's token, and never builds the full-depth index (only the selected game gets it).
/// </summary>
public sealed class ScanTimeIndexTests
{
    private static readonly byte[] Pe = [(byte)'M', (byte)'Z', .. new byte[4096]];

    /// <summary>Runs <paramref name="scan"/> and returns every file walk it made (the seam flows into Task.Run).</summary>
    private static async Task<List<GameFileIndexer.IndexWalk>> Walks(Func<Task> scan)
    {
        var walks = new List<GameFileIndexer.IndexWalk>();
        GameFileIndexer.WalkObserverForTesting = walk => { lock (walks) walks.Add(walk); };
        try { await scan(); }
        finally { GameFileIndexer.WalkObserverForTesting = null; }
        return walks;
    }

    /// <summary>A game tree with an executable at its root and a deep data tree (eight folder levels).</summary>
    private static void DeepData(DetectionTestTree tree, params string[] gameRoot)
    {
        var deep = gameRoot.Concat(new[] { "Content", "a", "b", "c", "d", "e", "f", "g" }).ToArray();
        tree.File("x", [.. deep, "data.pak"]);
    }

    [Fact]
    public async Task SteamScan_BuildsNoFullDepthIndex_AndPassesTheScanToken()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        string[] steamapps = ["home", ".local", "share", "Steam", "steamapps"];
        tree.File(SteamGameDetectorTests.Manifest(10, "Shallow Game", "Shallow"), [.. steamapps, "appmanifest_10.acf"]);
        var exe = tree.Bytes(Pe, [.. steamapps, "common", "Shallow", "Shallow.exe"]);
        DeepData(tree, [.. steamapps, "common", "Shallow"]);
        using var cts = new CancellationTokenSource();

        IReadOnlyList<RHI.Core.Models.DetectedGame> games = [];
        var walks = await Walks(() => Task.Run(() => games = SteamGameDetector.DetectIn(
            LinuxPaths.SteamRootCandidates(home, Path.Combine(home, ".local", "share")), cts.Token)));

        Assert.Equal(exe, games.Single().ExecutablePath);
        Assert.NotEmpty(walks);
        // Target: 0 full-depth index builds per scan; the walk stops at the scan depth because an exe turned up.
        Assert.All(walks, walk => Assert.Equal(DetectionHelpers.ScanExecutableDepth, walk.DepthLimit));
        Assert.All(walks, walk => Assert.Equal(cts.Token, walk.Token));
    }

    [Fact]
    public async Task WinePrefixScan_StillListsAGameWhoseOnlyExeSitsDeep_WalkingNoDeeperThanIt()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var prefix = tree.Dir("home", "Games", "standalone");
        tree.File("x", "home", "Games", "standalone", "system.reg");
        string[] game = ["home", "Games", "standalone", "drive_c", "Program Files", "Deep Game"];
        // Level 5 below the game folder: deeper than the shallow scan depth.
        var exe = tree.Bytes(Pe, [.. game, "a", "b", "c", "d", "e", "DeepGame.exe"]);
        tree.File("x", [.. game, "a", "b", "c", "d", "e", "f", "g", "h", "data.pak"]);
        var lutris = tree.Dir("home", ".local", "share", "lutris", "games");

        IReadOnlyList<RHI.Core.Models.DetectedGame> games = [];
        var walks = await Walks(async () => games = await new WinePrefixDetector(home, [lutris]).DetectAsync());

        Assert.Equal(exe, games.Single(g => g.Name == "Deep Game").ExecutablePath);
        Assert.Equal(prefix, games.Single().PrefixPath);
        Assert.Equal(5, walks.Single(w => w.Root.EndsWith("Deep Game", StringComparison.Ordinal)).DepthLimit);
    }

    [Fact]
    public async Task LutrisScan_PassesTheScanTokenToTheExecutablePick()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var configRoot = tree.Dir("home", ".config", "lutris", "games");
        var gameDir = tree.Dir("disk", "Games", "Foo");
        var exe = tree.Bytes(Pe, "disk", "Games", "Foo", "Foo.exe");
        DeepData(tree, "disk", "Games", "Foo");
        // The configured exe is gone, so the detector picks one from the folder.
        tree.File($"game:\n  exe: {Path.Combine(gameDir, "Missing.exe")}\nname: Foo\nrunner: wine\n", "home", ".config", "lutris", "games", "foo-1700000000.yml");
        using var cts = new CancellationTokenSource();

        IReadOnlyList<RHI.Core.Models.DetectedGame> games = [];
        var walks = await Walks(() => Task.Run(() => games = LutrisGameDetector.DetectIn([configRoot], home, cts.Token, [])));

        Assert.Equal(exe, games.Single().ExecutablePath);
        var walk = Assert.Single(walks);
        Assert.Equal(cts.Token, walk.Token);
        Assert.Equal(DetectionHelpers.ScanExecutableDepth, walk.DepthLimit);
    }

    [Fact]
    public async Task HeroicScan_PassesTheScanTokenToTheExecutablePick()
    {
        using var tree = new DetectionTestTree();
        var gameDir = tree.Dir("disk", "Heroic", "Bar");
        var exe = tree.Bytes(Pe, "disk", "Heroic", "Bar", "Bar.exe");
        DeepData(tree, "disk", "Heroic", "Bar");
        var legendary = tree.Dir("home", ".config", "legendary");
        tree.File(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["bar-app"] = new { app_name = "bar-app", title = "Bar", install_path = gameDir, is_installed = true }
        }), "home", ".config", "legendary", "installed.json");
        using var cts = new CancellationTokenSource();

        IReadOnlyList<RHI.Core.Models.DetectedGame> games = [];
        var walks = await Walks(async () => games = await new HeroicGameDetector([], [legendary], []).DetectAsync(cts.Token));

        Assert.Equal(exe, games.Single().ExecutablePath);
        var walk = Assert.Single(walks);
        Assert.Equal(cts.Token, walk.Token);
        Assert.Equal(DetectionHelpers.ScanExecutableDepth, walk.DepthLimit);
    }

    [Fact]
    public void LauncherEntry_GamesInItsLibraryDoNotStopTheWalkBeforeTheLauncherExe()
    {
        using var tree = new DetectionTestTree();
        var launcher = tree.Dir("drive_c", "Program Files", "Store");
        var library = tree.Dir("drive_c", "Program Files", "Store", "games");
        tree.Bytes(Pe, "drive_c", "Program Files", "Store", "games", "Game A", "GameA.exe");
        var launcherExe = tree.Bytes(Pe, "drive_c", "Program Files", "Store", "app", "x", "y", "z", "Store.exe");

        var exe = PrefixGameFolders.FindExecutable(new PrefixGameFolders.Entry(launcher, library, null), CancellationToken.None);

        Assert.Equal(launcherExe, exe);
    }

    [Fact]
    public void BuildUntilFound_ReadsEveryShallowLevel_ThenStopsAtTheFirstLevelHoldingAMatch()
    {
        using var tree = new DetectionTestTree();
        var near = tree.File("x", "Game.exe");
        var level3 = tree.File("x", "a", "b", "c", "level3.txt");
        var level4 = tree.File("x", "a", "b", "c", "d", "level4.txt");

        var shallow = GameFileIndexer.BuildUntilFound(tree.Root, 3, 8, f => f.EndsWith(".exe", StringComparison.Ordinal));

        Assert.Contains(near, shallow);
        Assert.Contains(level3, shallow);
        Assert.DoesNotContain(level4, shallow);

        var deepExe = tree.File("x", "a", "b", "c", "d", "e", "Deep.exe");
        var level6 = tree.File("x", "a", "b", "c", "d", "e", "f", "level6.txt");
        File.Delete(near);

        var deep = GameFileIndexer.BuildUntilFound(tree.Root, 3, 8, f => f.EndsWith(".exe", StringComparison.Ordinal));

        Assert.Contains(deepExe, deep);
        Assert.Contains(level4, deep);
        Assert.DoesNotContain(level6, deep);
    }

    [Fact]
    public void BuildUntilFound_NeverGoesBelowMaxDepth_NorFollowsDirectoryLinks()
    {
        using var tree = new DetectionTestTree();
        var outside = tree.Dir("outside");
        tree.File("x", "outside", "Leak.exe");
        tree.Link(outside, "game", "z:");
        var atLimit = tree.File("x", "game", "a", "b", "limit.txt");
        tree.File("x", "game", "a", "b", "c", "Below.exe");

        var files = GameFileIndexer.BuildUntilFound(tree.Path_("game"), 1, 2, f => f.EndsWith(".exe", StringComparison.Ordinal));

        Assert.Contains(atLimit, files);
        Assert.DoesNotContain(files, f => f.EndsWith("Below.exe", StringComparison.Ordinal) || f.EndsWith("Leak.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUntilFound_HonoursCancellation()
    {
        using var tree = new DetectionTestTree();
        tree.File("x", "a", "Game.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GameFileIndexer.BuildUntilFound(tree.Root, 3, 8, _ => false, cancellationToken: cts.Token));
    }
}
