using System.IO.Compression;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Launchers;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#90 remainder: games the itch app installed (native app, Flatpak, or the Windows app inside a Wine prefix).</summary>
public sealed class ItchDetectionTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private string Home => _tree.Path_("home");

    public ItchDetectionTests() => Directory.CreateDirectory(Home);

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Library_ReadsButlersCaves_Titles_Verdicts_AndReceipts()
    {
        var userData = _tree.Dir("itch");
        Directory.CreateDirectory(Path.Combine(userData, "db"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "itch-butler.db"), Path.Combine(userData, "db", "butler.db"));
        var lantern = _tree.Exe("library", "lantern-keeper", "bin", "Lantern.exe");
        var moss = _tree.File("#!/bin/sh\n", "custom", "Moss", "moss.x86_64");
        _tree.File("<html></html>", "library", "web-toy", "index.html");
        // In the default apps folder, known only by its receipt (butler's database may sit in its write-ahead log).
        var sky = _tree.Exe("itch", "apps", "sky-runner", "SkyRunner.exe");
        Receipt(Path.Combine(userData, "apps", "sky-runner"), 2002, "Sky Runner");
        _tree.Exe("itch", "apps", "no-receipt", "Stray.exe");
        // A cave whose folder name climbs out of its install location is not read.
        _tree.Exe("outside", "Outside.exe");

        var installs = ItchLibrary.Read(userData, MapFixture, Home, CancellationToken.None).ToDictionary(i => i.Name);

        Assert.Equal(["Lantern Keeper", "Moss Garden", "Sky Runner", "Web Toy"], installs.Keys.Order());
        Assert.Equal((1001L, lantern, "windows"), (installs["Lantern Keeper"].GameId!.Value, installs["Lantern Keeper"].Executable, installs["Lantern Keeper"].Flavor));
        Assert.Equal(_tree.Path_("custom", "Moss"), installs["Moss Garden"].Folder);
        Assert.Equal((moss, "linux"), (installs["Moss Garden"].Executable, installs["Moss Garden"].Flavor));
        Assert.Equal("html", installs["Web Toy"].Flavor);
        Assert.Equal((2002L, (string?)null), (installs["Sky Runner"].GameId!.Value, installs["Sky Runner"].Executable));
        Assert.Equal(_tree.Path_("itch", "apps", "sky-runner"), installs["Sky Runner"].Folder);
        _ = sky;
    }

    [Fact]
    public async Task NativeApp_ButlersVerdict_PicksTheProgram_AndSkipsWebGames()
    {
        var userData = _tree.Dir("itch");
        Directory.CreateDirectory(Path.Combine(userData, "db"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "itch-butler.db"), Path.Combine(userData, "db", "butler.db"));
        var lantern = _tree.Exe("library", "lantern-keeper", "bin", "Lantern.exe");
        _tree.Bytes([(byte)'M', (byte)'Z', .. new byte[65536]], "library", "lantern-keeper", "LanternKeeperEditor.exe");
        // Not marked executable: only butler's verdict says this is the game's program.
        var moss = _tree.File("binary", "custom", "Moss", "moss.x86_64");
        _tree.File("<html></html>", "library", "web-toy", "index.html");

        var games = (await new ItchGameDetector(Home, [userData], MapFixture).DetectAsync()).ToDictionary(g => g.Name);

        Assert.Equal(["Lantern Keeper", "Moss Garden"], games.Keys.Order());
        Assert.Equal((lantern, (string?)null), (games["Lantern Keeper"].ExecutablePath, games["Lantern Keeper"].Runner));
        Assert.Equal((moss, "Native Linux"), (games["Moss Garden"].ExecutablePath, games["Moss Garden"].Runner));
        Assert.Equal("xdg-open 'itch://games/1002'", games["Moss Garden"].LaunchCommand);
    }

    [Fact]
    public async Task NativeApp_ListsItsGames_OpenedInItch_AndSkipsWhatIsNoProgram()
    {
        var userData = _tree.Dir("home", ".config", "itch");
        var windowsGame = _tree.Exe("home", ".config", "itch", "apps", "sky-runner", "SkyRunner.exe");
        Receipt(Path.Combine(userData, "apps", "sky-runner"), 2002, "Sky Runner");
        var nativeGame = _tree.File("#!/bin/sh\n", "home", ".config", "itch", "apps", "moss", "moss.sh");
        File.SetUnixFileMode(nativeGame, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Receipt(Path.Combine(userData, "apps", "moss"), 1002, "Moss Garden");
        _tree.File("<html></html>", "home", ".config", "itch", "apps", "web-toy", "index.html");
        Receipt(Path.Combine(userData, "apps", "web-toy"), 1003, "Web Toy");
        var flatpakData = _tree.Dir("home", ".var", "app", "io.itch.itch", "config", "itch");
        _tree.Exe("home", ".var", "app", "io.itch.itch", "config", "itch", "apps", "lantern", "Lantern.exe");
        Receipt(Path.Combine(flatpakData, "apps", "lantern"), 1001, "Lantern Keeper");

        var games = (await new ItchGameDetector(Home, ItchGameDetector.UserDataFolders(Home, Path.Combine(Home, ".config"))).DetectAsync())
            .ToDictionary(g => g.Name);

        Assert.Equal(["Lantern Keeper", "Moss Garden", "Sky Runner"], games.Keys.Order());
        var sky = games["Sky Runner"];
        Assert.Equal("itch", sky.Source);
        Assert.Equal(windowsGame, sky.ExecutablePath);
        Assert.Equal("xdg-open 'itch://games/2002'", sky.LaunchCommand);
        Assert.Equal("itch", sky.ParentLauncherName); // Play opens the game in itch; the user starts it there
        Assert.Equal(LaunchHandoff.Other, LaunchHandoffClassifier.Classify(sky));
        Assert.Null(sky.Runner);
        Assert.Equal("Native Linux", games["Moss Garden"].Runner);
        Assert.Equal(nativeGame, games["Moss Garden"].ExecutablePath);
        Assert.Equal("flatpak run io.itch.itch 'itch://games/1001'", games["Lantern Keeper"].LaunchCommand);
        Assert.Equal(LaunchHandoff.Other, LaunchHandoffClassifier.Classify(games["Lantern Keeper"]));
        LinuxGameIdentityService.ApplyDetectedIdentity(sky);
        Assert.True(sky.HasConfirmedPatchTarget);
    }

    [Fact]
    public async Task WindowsAppInAWinePrefix_ListsItsGames_StartedThroughItchExe()
    {
        var prefix = _tree.Dir("home", "Games", "itch");
        _tree.Dir("home", "Games", "itch", "drive_c", "windows");
        _tree.Link("../drive_c", "home", "Games", "itch", "dosdevices", "c:");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        var user = Path.Combine(prefix, "drive_c", "users", "steamuser");
        var client = _tree.Exe("home", "Games", "itch", "drive_c", "users", "steamuser", "AppData", "Local", "itch", "app-26.1.9", "itch.exe");
        var gameDir = Path.Combine(user, "AppData", "Roaming", "itch", "apps", "sky-runner");
        var exe = _tree.Exe("home", "Games", "itch", "drive_c", "users", "steamuser", "AppData", "Roaming", "itch", "apps", "sky-runner", "SkyRunner.exe");
        Receipt(gameDir, 2002, "Sky Runner");

        var game = Assert.Single(await new WinePrefixDetector(Home, []).DetectAsync(), g => g.InstallPath == gameDir);

        Assert.Equal("Sky Runner", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{client}' 'itch://games/2002'", game.LaunchCommand);
        Assert.Equal(LaunchHandoff.Other, LaunchHandoffClassifier.Classify(game));
    }

    [Theory]
    [InlineData("wine")]
    [InlineData("lutris")]
    public async Task WindowsAppInAPrefix_ButlersLaunchCandidate_IsTheTarget_OnAnotherDrive(string owner)
    {
        // butler's verdict names the game's own program; the folder search alone would pick the bigger editor beside it.
        var prefix = _tree.Dir("home", "Games", "itch-db");
        _tree.Dir("home", "Games", "itch-db", "drive_c", "windows");
        _tree.Link("../drive_c", "home", "Games", "itch-db", "dosdevices", "c:");
        _tree.Link(_tree.Dir("drive-d"), "home", "Games", "itch-db", "dosdevices", "d:");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        var roaming = _tree.Dir("home", "Games", "itch-db", "drive_c", "users", "steamuser", "AppData", "Roaming", "itch", "db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "itch-butler-windows.db"), Path.Combine(roaming, "butler.db"));
        var client = _tree.Exe("home", "Games", "itch-db", "drive_c", "users", "steamuser", "AppData", "Local", "itch", "itch.exe");
        var game = _tree.Bytes([(byte)'M', (byte)'Z', .. new byte[64]], "drive-d", "itch", "library", "tiny-fleet", "Launcher", "Game.exe");
        _tree.Bytes([(byte)'M', (byte)'Z', .. new byte[65536]], "drive-d", "itch", "library", "tiny-fleet", "TinyFleetEditor.exe");
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        File.WriteAllText(Path.Combine(configRoot, "itch-1700000000.yml"),
            $"game:\n  exe: {client}\n  prefix: {prefix}\nname: itch\n");

        var games = owner == "wine"
            ? await new WinePrefixDetector(Home, []).DetectAsync()
            : LutrisGameDetector.DetectIn([configRoot], Home, CancellationToken.None);

        var card = Assert.Single(games, g => g.Name == "Tiny Fleet");
        Assert.Equal(_tree.Path_("drive-d", "itch", "library", "tiny-fleet"), card.InstallPath);
        Assert.Equal(game, card.ExecutablePath);
        if (owner == "wine") Assert.Equal($"WINEPREFIX='{prefix}' wine '{client}' 'itch://games/3003'", card.LaunchCommand);
        else Assert.Equal(("itch", "lutris 'lutris:rungame/itch'"), (card.ParentLauncherName, card.LaunchCommand));
    }

    [Fact]
    public void HostileButlerRecords_NeverMakeACardOfTheRootHomeAPrefix_OrATargetOutsideTheGame()
    {
        // w4 review minor 1 (a, b): install folders that hold far more than one game, and launch candidates that leave
        // the game's folder, from butler's caves and from receipts in a hostile install location.
        var userData = _tree.Dir("itch");
        Directory.CreateDirectory(Path.Combine(userData, "db"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "itch-butler-hostile.db"), Path.Combine(userData, "db", "butler.db"));
        _tree.Exe("home", "Stray.exe");
        Receipt(Home, 4002, "Whole Home");
        _tree.Exe("home", ".wine", "Wine.exe");
        Receipt(Path.Combine(Home, ".wine"), 4003, "Wine Folder");
        _tree.Exe("home", "Games", "Games.exe");
        Receipt(Path.Combine(Home, "Games"), 4004, "Games Folder");
        _tree.Dir("library", "pfx", "drive_c", "windows");
        _tree.File("WINE REGISTRY Version 2\n", "library", "pfx", "system.reg");
        Receipt(_tree.Path_("library", "pfx"), 4005, "Prefix Root");
        // The escape cave: only bin/Good.exe is the game's own program.
        var escape = _tree.Dir("library", "escape");
        var good = _tree.Exe("library", "escape", "bin", "Good.exe");
        _tree.Exe("library", "escape", "Dot.exe");
        _tree.Exe("library", "outside", "Evil.exe");
        _tree.Exe("library", "outside", "deeper", "Evil.exe");
        _tree.Link(_tree.Path_("library", "outside", "deeper"), "library", "escape", "linked");

        var installs = ItchLibrary.Read(userData, recorded => recorded == "/" ? "/" : MapFixture(recorded), Home, CancellationToken.None);

        var only = Assert.Single(installs);
        Assert.Equal(("Escape Artist", escape), (only.Name, only.Folder));
        Assert.Equal(good, only.Executable);
    }

    [Fact]
    public void Receipt_ThatIsNotGzip_IsSkipped_WithTheFolderName()
    {
        var folder = _tree.Dir("game");
        _tree.File("not gzip", "game", ".itch", "receipt.json.gz");

        Assert.Equal((null, null), ItchLibrary.ReadReceipt(folder));
    }

    [Fact]
    public void SqliteReader_TableLevelIntegerKey_IsTheRowId()
    {
        var sql = """CREATE TABLE "games" ("id" INTEGER NOT NULL, "title" TEXT, PRIMARY KEY ("id"))""";
        Assert.Equal("id", SqliteTableReader.IntegerPrimaryKey(sql, SqliteTableReader.ColumnNames(sql)));
        // A text key or a composite key is no rowid alias.
        var text = """CREATE TABLE "caves" ("id" TEXT NOT NULL, "game_id" INTEGER, PRIMARY KEY ("id"))""";
        Assert.Null(SqliteTableReader.IntegerPrimaryKey(text, SqliteTableReader.ColumnNames(text)));
        var composite = """CREATE TABLE "x" ("a" INTEGER NOT NULL, "b" INTEGER NOT NULL, PRIMARY KEY ("a", "b"))""";
        Assert.Null(SqliteTableReader.IntegerPrimaryKey(composite, SqliteTableReader.ColumnNames(composite)));
    }

    private string? MapFixture(string recorded)
        => recorded.StartsWith("/fixture/", StringComparison.Ordinal) ? _tree.Path_(recorded["/fixture/".Length..].Split('/')) : null;

    private static void Receipt(string folder, long gameId, string title)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".itch"));
        using var file = File.Create(Path.Combine(folder, ".itch", "receipt.json.gz"));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        var json = $$"""{"game":{"id":{{gameId}},"title":"{{title}}","classification":"game"},"upload":{"id":1},"files":["x"]}""";
        gzip.Write(Encoding.UTF8.GetBytes(json));
    }
}
