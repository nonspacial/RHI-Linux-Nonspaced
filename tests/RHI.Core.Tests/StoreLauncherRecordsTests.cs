using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Launchers;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #90 remainder: games the EA app, Ubisoft Connect, Battle.net and the Rockstar Games Launcher recorded inside a Wine
/// prefix get their own cards (real folder, real game exe as target), started through that launcher.
/// </summary>
public sealed class StoreLauncherRecordsTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private string Home => _tree.Path_("home");

    public StoreLauncherRecordsTests() => Directory.CreateDirectory(Home);

    public void Dispose() => _tree.Dispose();

    // ── registry reader ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RegistryReader_KeepsOnlyRequestedRoots_AndUnescapesWineStrings()
    {
        var hive = """
            WINE REGISTRY Version 2
            ;; All keys relative to \\Machine

            [Software\\Other\\Thing] 1700000000
            "InstallDir"="C:\\Elsewhere"

            [Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\5266] 1788643137
            #time=1dd3d7c29bce456
            "InstallDir"="C:\\Games\\Far Cry 6\\"
            "Quoted"="say \"hi\"\x00e9"
            "Wide"=str(2):"%ProgramFiles%\\Ubisoft"
            "Number"=dword:00000001
            @="default value"

            [Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\6145\\Nested] 1788643137
            "x"="y"
            """;

        var registry = WineRegistryReader.Parse(new StringReader(hive), [@"Software\Wow6432Node\Ubisoft\Launcher"]);

        var key = @"Software\Wow6432Node\Ubisoft\Launcher\Installs\5266";
        Assert.Equal(@"C:\Games\Far Cry 6\", registry.Value(key, "InstallDir"));
        Assert.Equal("say \"hi\"é", registry.Value(key, "quoted")); // value names ignore case, like Windows
        Assert.Equal(@"%ProgramFiles%\Ubisoft", registry.Value(key, "Wide"));
        Assert.Null(registry.Value(key, "Number")); // not a string
        Assert.Equal("default value", registry.Value(key, ""));
        Assert.Null(registry.Value(@"Software\Other\Thing", "InstallDir")); // outside the requested roots
        Assert.Equal(["5266", "6145"], registry.SubkeyNames(@"software\wow6432node\ubisoft\launcher\installs"));
    }

    [Fact]
    public void RegistryReader_MatchesWholeKeyNames_AndReadsWindowsLineEnds()
    {
        var hive = "WINE REGISTRY Version 2\r\n\r\n" +
                   "[Software\\\\Ubisoft\\\\LauncherOld\\\\Installs\\\\9] 1\r\n\"InstallDir\"=\"C:\\\\Old\"\r\n\r\n" +
                   "[software\\\\ubisoft\\\\launcher] 1\r\n\"InstallDir\"=\"C:\\\\Ubi\"\r\n";

        var registry = WineRegistryReader.Parse(new StringReader(hive), [@"Software\Ubisoft\Launcher"]);

        Assert.Equal(@"C:\Ubi", registry.Value(@"Software\Ubisoft\Launcher", "InstallDir"));
        Assert.Empty(registry.SubkeyNames(@"Software\Ubisoft\LauncherOld"));
        Assert.Null(registry.Value(@"Software\Ubisoft\LauncherOld\Installs\9", "InstallDir"));
    }

    // ── Windows path mapping ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PathMapper_MapsDrivesThroughThePrefix_IgnoringCase_AndRefusesEscapes()
    {
        var prefix = Prefix("mapper");
        var game = _tree.Dir("home", "Games", "mapper", "drive_c", "Games", "The Game");
        var library = _tree.Dir("library");
        _tree.Dir("library", "Ubisoft Games", "Far Cry 6");
        _tree.Link(library, "home", "Games", "mapper", "dosdevices", "d:");

        Assert.Equal(game, WindowsPathMapper.ToLinux(prefix, @"C:\games\THE GAME\"));
        Assert.Equal(game, WindowsPathMapper.ToLinux(prefix, "c:/Games/The Game"));
        Assert.Equal(Path.Combine(library, "Ubisoft Games", "Far Cry 6"), WindowsPathMapper.ToLinux(prefix, @"D:\ubisoft games\far cry 6"));
        Assert.Null(WindowsPathMapper.ToLinux(prefix, @"C:\"));                    // a whole drive is never one game
        Assert.Null(WindowsPathMapper.ToLinux(prefix, @"C:\Games\..\..\..\etc"));  // no climbing out of the drive
        Assert.Null(WindowsPathMapper.ToLinux(prefix, @"E:\Games\X"));             // drive not mapped
        Assert.Null(WindowsPathMapper.ToLinux(prefix, @"\\server\share\X"));      // not a drive path
    }

    // ── Battle.net product.db ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProductDb_ReadsInstalledProducts()
    {
        var db = ProductDb(("agent", "agent", "C:/ProgramData/Battle.net/Agent", true),
            ("ark", "ark", "C:/Games/The Outer Worlds 2", true),
            ("fenris", "fenris", "C:/Games/Diablo IV", false));

        var products = BattleNetProductDb.Parse(db);

        Assert.Equal(["agent", "ark", "fenris"], products.Select(p => p.Uid));
        Assert.Equal("C:/Games/The Outer Worlds 2", products[1].InstallPath);
        Assert.Equal([true, true, false], products.Select(p => p.Installed));
        Assert.Throws<InvalidDataException>(() => BattleNetProductDb.Parse(db.AsSpan(0, db.Length - 3)));
    }

    // ── standalone Wine prefixes ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WinePrefix_UbisoftGameOnAnotherDrive_IsListed_AndStartsThroughUbisoftConnect()
    {
        var prefix = Prefix("ubisoft");
        var launcher = _tree.Exe("home", "Games", "ubisoft", "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe");
        // The user's own library on another drive: no layout rule finds it, only Ubisoft's record.
        var library = _tree.Dir("library");
        _tree.Link(library, "home", "Games", "ubisoft", "dosdevices", "d:");
        var exe = Pe(Path.Combine(library, "Ubisoft", "Assassin's Creed Mirage"), "ACMirage.exe", 8192);
        Registry(prefix, ("""Software\\Wow6432Node\\Ubisoft\\Launcher""", [("InstallDir", """C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\""")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\7014""", [("InstallDir", "D:/Ubisoft/Assassin's Creed Mirage/")]));

        var game = Assert.Single(await DetectWine(), g => g.Name == "Assassin's Creed Mirage");

        Assert.Equal(Path.Combine(library, "Ubisoft", "Assassin's Creed Mirage"), game.InstallPath);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(launcher, game.LaunchExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' 'uplay://launch/7014'", game.LaunchCommand);
        Assert.Null(game.ParentLauncherName); // the launch argument starts the game itself
        Assert.Equal(LaunchHandoff.StoreClient, LaunchHandoffClassifier.Classify(game));
        // The launcher is what runs; the game exe stays the confirmed patch target.
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.Equal("Ubisoft", game.LaunchProvider);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(exe, game.ExecutablePath);
    }

    [Fact]
    public async Task WinePrefix_BattleNetGame_NamedFromItsUninstallEntry_StartsWithExecLaunch()
    {
        var prefix = Prefix("bnet");
        var launcherDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net");
        var launcher = Pe(launcherDir, "Battle.net.exe");
        // Installed into a custom folder outside drive_c/Games.
        var gameDir = Path.Combine(prefix, "drive_c", "BlizzardGames", "ow2");
        var exe = Pe(gameDir, "Overwatch.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "Games", "Old Uninstalled"), "Old.exe", 8192);
        ProductDbFile(prefix, ("agent", "agent", "C:/ProgramData/Battle.net/Agent", true),
            ("battle.net", "bna", "C:/Program Files (x86)/Battle.net", true),
            ("prometheus", "pro", "C:/BlizzardGames/ow2", true),
            // Removed from Battle.net (its folder left behind): not a game any more.
            ("fenris", "fen", "C:/BlizzardGames/d4", false));
        Pe(Path.Combine(prefix, "drive_c", "BlizzardGames", "d4"), "Diablo IV.exe", 8192);
        Registry(prefix, ("""Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Overwatch""",
            [("DisplayName", "Overwatch 2"), ("Publisher", "Blizzard Entertainment"),
             ("UninstallString", "\"C:\\Program Files (x86)\\Battle.net\\Battle.net Uninstaller.exe\" --lang=enUS --uid=prometheus --displayname=\"Overwatch\"")]));

        var games = await DetectWine();

        var game = Assert.Single(games, g => g.InstallPath == gameDir);
        Assert.Equal("Overwatch 2", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' '--exec=launch pro'", game.LaunchCommand);
        Assert.Equal(LaunchHandoff.StoreClient, LaunchHandoffClassifier.Classify(game));
        Assert.DoesNotContain(games, g => g.InstallPath.EndsWith("d4", StringComparison.Ordinal));
        // Battle.net's own client is no recorded game (its folder stays the layout card it was).
        Assert.DoesNotContain(games, g => g.LaunchCommand?.Contains("launch bna", StringComparison.Ordinal) == true);
        // A folder only found by layout keeps its folder name and a direct launch, as before.
        Assert.Contains(games, g => g.Name == "Old Uninstalled" && g.LaunchExecutablePath!.EndsWith("Old.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WinePrefix_EaGame_TitleAndOfferFromInstallerData()
    {
        var prefix = Prefix("ea");
        var launcher = Pe(Path.Combine(prefix, "drive_c", "Program Files", "Electronic Arts", "EA Desktop", "EA Desktop"), "EADesktop.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "EA Library", "MassEffectLE");
        var exe = Pe(Path.Combine(gameDir, "Game", "Launcher"), "MassEffectLauncher.exe", 8192);
        Directory.CreateDirectory(Path.Combine(gameDir, "__Installer"));
        File.WriteAllText(Path.Combine(gameDir, "__Installer", "installerdata.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <DiPManifest version="4.0">
              <gameTitles>
                <gameTitle locale="de_DE">Mass Effect Legendäre Edition</gameTitle>
                <gameTitle locale="en_US">Mass Effect Legendary Edition</gameTitle>
              </gameTitles>
              <contentIDs><contentID>Origin.OFR.50.0004218</contentID></contentIDs>
            </DiPManifest>
            """);
        Registry(prefix, ("""Software\\Wow6432Node\\Origin Games\\Origin.OFR.50.0004218""", [("InstallDir", "C:\\EA Library\\MassEffectLE\\"), ("DisplayName", "ME LE")]));

        var game = Assert.Single(await DetectWine(), g => g.InstallPath == gameDir);

        Assert.Equal("Mass Effect Legendary Edition", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' 'origin2://game/launch?offerIds=Origin.OFR.50.0004218&autoDownload=1'", game.LaunchCommand);
    }

    [Fact]
    public async Task WinePrefix_RockstarGame_FromItsKeys_LaunchesTitleInFolder_AndTheLauncherIsNoGame()
    {
        var prefix = Prefix("rockstar");
        var launcherDir = Path.Combine(prefix, "drive_c", "Program Files", "Rockstar Games", "Launcher");
        var launcher = Pe(launcherDir, "Launcher.exe", 8192);
        var gameDir = Path.Combine(prefix, "drive_c", "Rockstar", "Grand Theft Auto V");
        var exe = Pe(gameDir, "GTA5.exe", 16384);
        Registry(prefix,
            ("""Software\\Wow6432Node\\Rockstar Games\\Launcher""", [("InstallFolder", "C:\\Program Files\\Rockstar Games\\Launcher")]),
            ("""Software\\Wow6432Node\\Rockstar Games\\Grand Theft Auto V""", [("InstallFolder", "C:\\Rockstar\\Grand Theft Auto V")]),
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Rockstar Games Launcher""",
                [("DisplayName", "Rockstar Games Launcher"), ("Publisher", "Rockstar Games"), ("InstallLocation", "C:\\Program Files\\Rockstar Games\\Launcher")]));

        var games = await DetectWine();

        var game = Assert.Single(games, g => g.InstallPath == gameDir);
        Assert.Equal("Grand Theft Auto V", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' '-launchTitleInFolder' 'C:\\Rockstar\\Grand Theft Auto V'", game.LaunchCommand);
        Assert.Equal(LaunchHandoff.StoreClient, LaunchHandoffClassifier.Classify(game));
        // The launcher's own folder is not turned into a record game (it stays the layout card it always was).
        Assert.DoesNotContain(games, g => g.InstallPath != gameDir && g.LaunchCommand?.Contains("-launchTitleInFolder", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task WinePrefix_RecordsNamingThePrefixOrHome_AreNeverGameFolders()
    {
        var prefix = Prefix("hostile");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        _tree.Link(Home, "home", "Games", "hostile", "dosdevices", "x:");
        _tree.Link("/", "home", "Games", "hostile", "dosdevices", "z:");
        Pe(Path.Combine(Home, "Documents"), "Stray.exe");
        Registry(prefix,
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\1""", [("InstallDir", "C:\\")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\2""", [("InstallDir", "X:\\")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\3""", [("InstallDir", "Z:\\home")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\4""", [("InstallDir", "C:\\windows")]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\5""", [("InstallDir", "C:\\Program Files (x86)")]));

        var records = StoreLauncherRecords.Find(prefix, Home, CancellationToken.None);

        Assert.Empty(records);
        Assert.DoesNotContain(await DetectWine(), g => g.InstallPath.StartsWith(Path.Combine(Home, "Documents"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileStoreIds_NeverReachALaunchArgument()
    {
        // w4 review minor 1 (c): a key name or product code that is not a plain id is never put in the launch command;
        // the card still opens the store client, where the user starts the game.
        var prefix = Prefix("ids");
        var ubisoft = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), "Battle.net.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files", "Electronic Arts", "EA Desktop", "EA Desktop"), "EADesktop.exe");
        Pe(Path.Combine(prefix, "drive_c", "UbiGames", "Anno"), "Anno.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "BlizzardGames", "ow"), "Overwatch.exe", 8192);
        var eaDir = Path.Combine(prefix, "drive_c", "EAGames", "Unravel");
        Pe(eaDir, "Unravel.exe", 8192);
        _tree.File("<DiPManifest><contentIDs><contentID>O 1;x</contentID></contentIDs></DiPManifest>",
            "home", "Games", "ids", "drive_c", "EAGames", "Unravel", "__Installer", "installerdata.xml");
        ProductDbFile(prefix, ("prometheus", "pro x", "C:/BlizzardGames/ow", true));
        Registry(prefix,
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\1'; touch x""", [("InstallDir", "C:/UbiGames/Anno/")]),
            ("""Software\\Wow6432Node\\Origin Games\\Origin.OFR.1""", [("InstallDir", "C:\\EAGames\\Unravel"), ("DisplayName", "Unravel")]));

        var records = StoreLauncherRecords.Find(prefix, Home, CancellationToken.None);

        Assert.Equal([StoreLauncher.EaApp, StoreLauncher.UbisoftConnect, StoreLauncher.BattleNet], records.Select(r => r.Store).Order());
        Assert.All(records, record => Assert.Empty(record.PlayArguments));
        var anno = Assert.Single(await DetectWine(), g => g.Name == "Anno");
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{ubisoft}'", anno.LaunchCommand);
        Assert.Equal("Ubisoft Connect", anno.ParentLauncherName);
    }

    [Theory]
    [InlineData("5266", true)]
    [InlineData("Origin.OFR.50.0005083", true)]
    [InlineData("pro_x-1", true)]
    [InlineData("1'; x", false)]
    [InlineData("pro x", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void StoreId_IsPlainLettersDigitsAndDotDashUnderscore(string id, bool plain)
        => Assert.Equal(plain, StoreLauncherRecords.IsStoreId(id));

    [Fact]
    public void Record_NamingTheGamesDriveBehindTheHomeGamesLink_IsNoGameFolder()
    {
        // w4 review minor 5, owner layout: ~/Games -> /media/<disk>/Games; Z:\media\<disk>\Games is the whole games drive.
        var home = _tree.Dir("owner");
        var games = _tree.Dir("disk", "Games");
        _tree.Link(games, "owner", "Games");
        var prefix = _tree.Dir("disk", "Games", "ubi");
        _tree.Dir("disk", "Games", "ubi", "drive_c", "windows");
        _tree.Link("../drive_c", "disk", "Games", "ubi", "dosdevices", "c:");
        _tree.Link("/", "disk", "Games", "ubi", "dosdevices", "z:");
        Pe(Path.Combine(games, "Far Cry 6"), "FarCry6.exe", 8192);
        Registry(prefix,
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\1""", [("InstallDir", "Z:" + games.Replace('/', '\\'))]),
            ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\5266""", [("InstallDir", "Z:" + Path.Combine(games, "Far Cry 6").Replace('/', '\\'))]));

        var record = Assert.Single(StoreLauncherRecords.Find(prefix, home, CancellationToken.None));

        Assert.Equal(Path.Combine(games, "Far Cry 6"), record.InstallDirectory);
    }

    [Fact]
    public async Task WinePrefix_UnreadableProductDb_KeepsTheLayoutGames_AndWarns()
    {
        var prefix = Prefix("broken");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), "Battle.net.exe");
        Pe(Path.Combine(prefix, "drive_c", "Games", "Diablo IV"), "Diablo IV.exe", 8192);
        var db = Path.Combine(prefix, "drive_c", "ProgramData", "Battle.net", "Agent", "product.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllBytes(db, [0x0A, 0xFF, 0xFF, 0xFF]); // runs past the end

        var scan = await new GameLibraryScanner([new WinePrefixDetector(Home, [])]).ScanWithReportAsync(null);

        Assert.Contains(scan.Games, g => g.Name == "Diablo IV");
        Assert.Contains(scan.Detectors.Single(d => d.Name == "Wine").Warnings,
            warning => warning.Contains("Battle.net install records unreadable", StringComparison.Ordinal));
    }

    // ── Lutris launcher prefixes ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LutrisPrefix_RecordedGameOutsideTheLibraries_GetsACard_StartedThroughItsOwnStoreClient()
    {
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        var prefix = Prefix("launchers");
        // Two store clients share one Lutris prefix; each recorded game belongs to its own client.
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), "Battle.net Launcher.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        var owDir = Path.Combine(prefix, "drive_c", "BlizzardGames", "Overwatch");
        var owExe = Pe(owDir, "Overwatch.exe", 8192);
        var acDir = Path.Combine(prefix, "drive_c", "UbiGames", "Anno 1800");
        var acExe = Pe(Path.Combine(acDir, "Bin", "Win64"), "Anno1800.exe", 8192);
        ProductDbFile(prefix, ("prometheus", "pro", "C:/BlizzardGames/Overwatch", true));
        Registry(prefix, ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\4553""", [("InstallDir", "C:/UbiGames/Anno 1800/")]));
        File.WriteAllText(Path.Combine(configRoot, "battlenet-1763437958.yml"),
            $"game:\n  exe: drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe\n  prefix: {prefix}\nname: Battle.net\n");
        File.WriteAllText(Path.Combine(configRoot, "ubisoft-connect-1763420553.yml"),
            $"game:\n  exe: drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe\n  prefix: {prefix}\nname: Ubisoft Connect\n");

        var games = LutrisGameDetector.DetectIn([configRoot], Home, CancellationToken.None).ToDictionary(g => g.Name);

        Assert.Equal(["Anno 1800", "Battle.net", "Overwatch", "Ubisoft Connect"], games.Keys.Order());
        var overwatch = games["Overwatch"];
        Assert.Equal(owDir, overwatch.InstallPath);
        Assert.Equal(owExe, overwatch.ExecutablePath);
        Assert.Equal("Battle.net", overwatch.ParentLauncherName);
        Assert.Equal("lutris 'lutris:rungame/battlenet'", overwatch.LaunchCommand);
        var anno = games["Anno 1800"];
        Assert.Equal(acExe, anno.ExecutablePath);
        Assert.Equal("Ubisoft Connect", anno.ParentLauncherName);
        Assert.Equal("lutris 'lutris:rungame/ubisoft-connect'", anno.LaunchCommand);
        // Both clients are launchers: no patch target until Edit game names one.
        Assert.True(games["Battle.net"].RequiresExplicitPatchTarget);
        Assert.True(games["Ubisoft Connect"].RequiresExplicitPatchTarget);
        LinuxGameIdentityService.ApplyDetectedIdentity(anno);
        Assert.True(anno.HasConfirmedPatchTarget);
    }

    [Fact]
    public void LutrisPrefix_RecordedGameInTheLibrary_IsOneCard_WithTheRecordedName()
    {
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        var prefix = Prefix("battlenet");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), "Battle.net Launcher.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Games", "ow2");
        Pe(gameDir, "Overwatch.exe", 8192);
        ProductDbFile(prefix, ("prometheus", "pro", "C:/Games/ow2", true));
        Registry(prefix, ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Overwatch""",
            [("DisplayName", "Overwatch 2"), ("Publisher", "Blizzard Entertainment"), ("UninstallString", "x.exe --uid=prometheus")]));
        File.WriteAllText(Path.Combine(configRoot, "battlenet-1763437958.yml"),
            $"game:\n  exe: drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe\n  prefix: {prefix}\nname: Battle.net\n");

        var games = LutrisGameDetector.DetectIn([configRoot], Home, CancellationToken.None);

        var game = Assert.Single(games, g => g.ParentLauncherName is not null);
        Assert.Equal("Overwatch 2", game.Name);
        Assert.Equal(gameDir, game.InstallPath);
    }

    // ── Heroic prefixes ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HeroicPrefix_SideloadedStoreClient_IsTheParentOfItsRecordedGames()
    {
        var heroic = _tree.Dir("home", ".config", "heroic");
        var prefix = Prefix("Heroic-ea");
        var clientDir = Path.Combine(prefix, "drive_c", "Program Files", "Electronic Arts", "EA Desktop", "EA Desktop");
        var client = Pe(clientDir, "EADesktop.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Program Files", "EA Games", "Dead Space");
        var exe = Pe(gameDir, "Dead Space.exe", 8192);
        Registry(prefix, ("""Software\\Wow6432Node\\Origin Games\\Origin.OFR.50.0005083""", [("InstallDir", "C:\\Program Files\\EA Games\\Dead Space"), ("DisplayName", "Dead Space")]));
        // A prefix that belongs to an Epic game only: its other store's game has no client app to start it through.
        var epicPrefix = Prefix("Heroic-epic");
        var epicGame = Pe(_tree.Path_("epic", "Mass Effect"), "MassEffect.exe", 8192);
        var otherDir = Path.Combine(epicPrefix, "drive_c", "Program Files", "EA Games", "Unravel");
        Pe(otherDir, "Unravel.exe", 8192);
        Registry(epicPrefix, ("""Software\\Wow6432Node\\Origin Games\\Origin.OFR.1""", [("InstallDir", "C:\\Program Files\\EA Games\\Unravel")]),
            ("""Software\\Wow6432Node\\Origin Games\\Origin.OFR.2""", [("InstallDir", "Z:" + _tree.Path_("epic", "Mass Effect").Replace('/', '\\'))]));
        _tree.Link("/", "home", "Games", "Heroic-epic", "dosdevices", "z:");
        File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "sideload_apps"), "library.json"), $$$"""
            {"games":[{"runner":"sideload","app_name":"eaapp","title":"EA app","folder_name":"{{{clientDir}}}","is_installed":true,
                       "install":{"executable":"{{{client}}}","platform":"Windows"}}]}
            """);
        File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "legendaryConfig"), "installed.json"), $$$"""
            {"MassEffect":{"app_name":"MassEffect","title":"Mass Effect","install_path":"{{{_tree.Path_("epic", "Mass Effect")}}}","executable":"MassEffect.exe","is_installed":true}}
            """);
        File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "GamesConfig"), "eaapp.json"),
            "{\"eaapp\":{\"winePrefix\":\"" + prefix + "\",\"wineVersion\":{\"type\":\"wine\",\"name\":\"Wine-GE\"}}}");
        File.WriteAllText(Path.Combine(heroic, "GamesConfig", "MassEffect.json"),
            "{\"MassEffect\":{\"winePrefix\":\"" + epicPrefix + "\",\"wineVersion\":{\"type\":\"proton\",\"name\":\"GE-Proton\"}}}");

        var games = await new HeroicGameDetector([heroic], [Path.Combine(heroic, "legendaryConfig")], []).DetectAsync();

        var deadSpace = Assert.Single(games, g => g.InstallPath == gameDir);
        Assert.Equal("Dead Space", deadSpace.Name);
        Assert.Equal(exe, deadSpace.ExecutablePath);
        Assert.Equal(prefix, deadSpace.PrefixPath);
        Assert.Equal("EA app", deadSpace.ParentLauncherName);
        Assert.Equal("xdg-open 'heroic://launch?appName=eaapp&runner=sideload'", deadSpace.LaunchCommand);
        Assert.Null(deadSpace.HeroicAppName); // its own library key, not the client's
        Assert.True(games.Single(g => g.Name == "EA app").RequiresExplicitPatchTarget);
        LinuxGameIdentityService.ApplyDetectedIdentity(deadSpace);
        Assert.True(deadSpace.HasConfirmedPatchTarget);
        Assert.Equal(exe, deadSpace.ExecutablePath);

        // The Epic game is not started to reach another store's game, nor listed twice.
        var unravel = Assert.Single(games, g => g.InstallPath == otherDir);
        Assert.Null(unravel.LaunchCommand);
        Assert.Null(unravel.ParentLauncherName);
        Assert.Equal(epicPrefix, unravel.PrefixPath);
        Assert.Single(games, g => g.ExecutablePath == epicGame);
    }

    [Fact]
    public async Task HeroicPrefix_AnotherStoresClient_NeverStartsTheGame_ItsOwnItchClientDoes()
    {
        // w4 review minor 4: a Ubisoft game in a prefix whose only sideloaded client is Battle.net got Play = Battle.net.
        // Nit 7: the itch app's own client (itch.exe) is recognised, so an itch game gets it as its parent.
        var heroic = _tree.Dir("home", ".config", "heroic");
        var prefix = Prefix("Heroic-mixed");
        var bnetDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net");
        var bnet = Pe(bnetDir, "Battle.net.exe");
        var itchDir = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "itch", "app-26.1.9");
        var itch = Pe(itchDir, "itch.exe");
        var ubiDir = Path.Combine(prefix, "drive_c", "UbiGames", "Anno 1800");
        Pe(ubiDir, "Anno1800.exe", 8192);
        Registry(prefix, ("""Software\\Wow6432Node\\Ubisoft\\Launcher\\Installs\\4553""", [("InstallDir", "C:/UbiGames/Anno 1800/")]));
        var skyDir = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Roaming", "itch", "apps", "sky-runner");
        Pe(skyDir, "SkyRunner.exe", 8192);
        ItchReceipt(skyDir, 2002, "Sky Runner");
        File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "sideload_apps"), "library.json"), $$$"""
            {"games":[{"runner":"sideload","app_name":"bnet","title":"Battle.net","folder_name":"{{{bnetDir}}}","is_installed":true,
                       "install":{"executable":"{{{bnet}}}","platform":"Windows"}},
                      {"runner":"sideload","app_name":"itchapp","title":"itch","folder_name":"{{{itchDir}}}","is_installed":true,
                       "install":{"executable":"{{{itch}}}","platform":"Windows"}}]}
            """);
        foreach (var app in new[] { "bnet", "itchapp" })
            File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "GamesConfig"), app + ".json"),
                "{\"" + app + "\":{\"winePrefix\":\"" + prefix + "\",\"wineVersion\":{\"type\":\"wine\",\"name\":\"Wine-GE\"}}}");

        var games = await new HeroicGameDetector([heroic], [Path.Combine(heroic, "legendaryConfig")], []).DetectAsync();

        var anno = Assert.Single(games, g => g.InstallPath == ubiDir);
        Assert.Null(anno.LaunchCommand);
        Assert.Null(anno.ParentLauncherName);
        var sky = Assert.Single(games, g => g.InstallPath == skyDir);
        Assert.Equal("itch", sky.ParentLauncherName);
        Assert.Equal("xdg-open 'heroic://launch?appName=itchapp&runner=sideload'", sky.LaunchCommand);
    }

    // ── hand-off ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WINEPREFIX='/p' wine '/p/drive_c/x/UbisoftConnect.exe' 'uplay://launch/5266'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/drive_c/x/Battle.net.exe' '--exec=launch pro'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/drive_c/x/Battle.net.exe' --exec=\"launch pro\"", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' '/opt/proton/files/bin/wine' '/p/x/EADesktop.exe' 'origin2://game/launch?offerIds=O.1&autoDownload=1'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/x/Launcher.exe' -launchTitleInFolder 'C:\\Rockstar\\GTA'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/x/itch.exe' 'itch://games/1001'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/drive_c/Games/X/X.exe' --exec=fast", LaunchHandoff.None)]
    [InlineData("WINEPREFIX='/p' wine '/p/drive_c/Games/X/X.exe'", LaunchHandoff.None)]
    public void StoreClientGameArguments_AreHandOffs(string command, LaunchHandoff expected)
        => Assert.Equal(expected, LaunchHandoffClassifier.Classify(new DetectedGame { Name = "x", InstallPath = "/g", Source = "Wine", LaunchCommand = command }));

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<DetectedGame>> DetectWine() => new WinePrefixDetector(Home, []).DetectAsync();

    private string Prefix(string name)
    {
        var prefix = _tree.Dir("home", "Games", name);
        _tree.Dir("home", "Games", name, "drive_c", "windows");
        _tree.Dir("home", "Games", name, "dosdevices");
        _tree.Link("../drive_c", "home", "Games", name, "dosdevices", "c:");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        return prefix;
    }

    /// <summary>Writes keys (names spelt with the hive's doubled backslashes) into the prefix's system.reg.</summary>
    private static void Registry(string prefix, params (string Key, (string Name, string Value)[] Values)[] keys)
    {
        var text = new StringBuilder("WINE REGISTRY Version 2\n;; All keys relative to \\\\Machine\n\n#arch=win64\n\n");
        foreach (var (key, values) in keys)
        {
            text.Append('[').Append(key).Append("] 1700000000\n#time=1d000000000000\n");
            foreach (var (name, value) in values)
                text.Append('"').Append(name).Append("\"=\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
            text.Append('\n');
        }
        File.WriteAllText(Path.Combine(prefix, "system.reg"), text.ToString(), Encoding.Latin1);
    }

    private static void ItchReceipt(string folder, long gameId, string title)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".itch"));
        using var file = File.Create(Path.Combine(folder, ".itch", "receipt.json.gz"));
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes($$$"""{"game":{"id":{{{gameId}}},"title":"{{{title}}}"}}"""));
    }

    private static string Pe(string directory, string name, int bytes = 64)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var data = new byte[bytes];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void ProductDbFile(string prefix, params (string Uid, string Code, string Path, bool Installed)[] products)
    {
        var path = Path.Combine(prefix, "drive_c", "ProgramData", "Battle.net", "Agent", "product.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProductDb(products));
    }

    /// <summary>A protobuf ProductDb as Battle.net writes it (Lutris' product_db.proto field numbers).</summary>
    private static byte[] ProductDb(params (string Uid, string Code, string Path, bool Installed)[] products)
    {
        var db = new List<byte>();
        foreach (var (uid, code, path, installed) in products)
        {
            var install = new List<byte>();
            install.AddRange(Field(1, Encoding.UTF8.GetBytes(uid)));
            install.AddRange(Field(2, Encoding.UTF8.GetBytes(code)));
            install.AddRange(Field(3, [.. Field(1, Encoding.UTF8.GetBytes(path)), .. Field(2, Encoding.UTF8.GetBytes("enUS"))]));
            // cached_product_state { base_product_state { installed, playable } }, with a varint field before it.
            install.AddRange([0x28, 0x01]);
            install.AddRange(Field(4, Field(1, [0x08, (byte)(installed ? 1 : 0), 0x10, 0x01])));
            db.AddRange(Field(1, [.. install]));
        }
        db.AddRange(Field(2, Encoding.UTF8.GetBytes("active processes")));
        return [.. db];
    }

    private static byte[] Field(int number, byte[] value)
    {
        var bytes = new List<byte>();
        Varint(bytes, (ulong)(number << 3 | 2));
        Varint(bytes, (ulong)value.Length);
        bytes.AddRange(value);
        return [.. bytes];
    }

    private static void Varint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }
}
