using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Detection.Launchers;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #247: games the Epic Games Launcher installed inside a Wine, Lutris or Steam-shortcut prefix get their own cards, from
/// the classic launcher's manifests and the new launcher's (eglv2) install log, started through the launcher's own URI
/// (upstream c1076d3 <c>GameDetectionService.Epic.cs</c>). Written against the detectors only, so it runs on the wave base
/// too, where every card test here fails (no Epic reader).
/// </summary>
public sealed class EpicLauncherDetectionTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();
    private string Home => _tree.Path_("home");

    public EpicLauncherDetectionTests() => Directory.CreateDirectory(Home);

    public void Dispose() => _tree.Dispose();

    // ── classic launcher ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WinePrefix_ClassicManifest_GameGetsItsCard_StartedThroughTheLaunchersUri()
    {
        var prefix = Prefix("egl");
        var launcher = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win64"), "EpicGamesLauncher.exe");
        // A library folder no layout rule finds: only the manifest names it.
        var gameDir = Path.Combine(prefix, "drive_c", "EpicLibrary", "HadesII");
        var exe = Pe(gameDir, "Hades2.exe", 8192);
        // The game's DLC has a manifest of its own for the same folder, sorting first: the game keeps its own name.
        Manifest(prefix, "0000DLC.item", "Hades II Soundtrack", @"C:\EpicLibrary\HadesII", "Hades2Soundtrack", main: "Hades2App");
        Manifest(prefix, "1111GAME.item", "Hades II", @"C:\EpicLibrary\HadesII\", "Hades2App", main: "Hades2App");

        var game = Assert.Single(await DetectWine(), g => g.InstallPath == gameDir);

        Assert.Equal("Hades II", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(launcher, game.LaunchExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' 'com.epicgames.launcher://apps/Hades2App?action=launch&silent=true'", game.LaunchCommand);
        Assert.Null(game.ParentLauncherName); // the URI starts the game itself
        Assert.Equal(LaunchHandoff.StoreClient, LaunchHandoffClassifier.Classify(game));
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.Equal("Epic", game.LaunchProvider);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(exe, game.ExecutablePath);
    }

    [Fact]
    public async Task WinePrefix_ClassicManifestInTheDefaultLibrary_IsNamedAndStartedThroughTheLauncher()
    {
        // Program Files/Epic Games/<folder> is found by layout today (folder name, game exe started directly).
        var prefix = Prefix("egl-default");
        var launcher = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win32"), "EpicGamesLauncher.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Program Files", "Epic Games", "AlanWake2");
        Pe(gameDir, "AlanWake2.exe", 8192);
        Manifest(prefix, "A.item", "Alan Wake 2", @"C:\Program Files\Epic Games\AlanWake2", "Dill");
        // The launcher's own folder is never one of its games, whatever a manifest says.
        Manifest(prefix, "B.item", "Launcher", @"C:\Program Files (x86)\Epic Games\Launcher", "Launcher");

        var games = await DetectWine();

        var game = Assert.Single(games, g => g.InstallPath == gameDir);
        Assert.Equal("Alan Wake 2", game.Name);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' 'com.epicgames.launcher://apps/Dill?action=launch&silent=true'", game.LaunchCommand);
        Assert.DoesNotContain(StoreLauncherRecords.Find(prefix, Home, CancellationToken.None), r => r.Name == "Launcher");
    }

    // ── new launcher (eglv2) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WinePrefix_NewLauncher_ListsCompleteInstalls_NamedFromItsUninstallEntries()
    {
        var prefix = Prefix("eglv2");
        // The URL handler the new launcher registered names where it lives.
        var launcher = Pe(Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Programs", "Epic"), "EpicGamesLauncher.exe");
        var data = DataFolder(prefix);
        var finished = Pe(Path.Combine(prefix, "drive_c", "EGL", "Finished"), "Finished.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Removing"), "Removing.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Migrated"), "Migrated.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "NoName"), "NoName.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Unlogged"), "Unlogged.exe", 8192);
        Log(data,
            """{"version":1,"format":"installations"}""",
            Entry("i-1", false, @"C:\EGL\Finished", "art1"),
            Entry("i-2", true, @"C:\EGL\Removing", "art2"),
            Entry("i-1", true, @"C:\EGL\Finished", "art1"),   // superseded: the later entry wins
            Entry("i-2", false, @"C:\EGL\Removing", "art2"),  // being changed: no card until it completes again
            Entry("i-3", true, @"C:\EGL\Migrated", "art3"),
            Entry("i-4", true, @"C:\EGL\NoName", "art4"));
        _tree.File("""{"DisplayName":"Control","InstallLocation":"C:\\Old\\Control","AppName":"Calluna"}""",
            "home", "Games", "eglv2", "drive_c", "users", "steamuser", "AppData", "Local", "com.epicgames.eglv2", "import-scan", "v1-items", "i-3.item");
        UserRegistry(prefix,
            ("""Software\\Classes\\com.epicgames.launcher\\shell\\open\\command""", [("", $"\"C:\\users\\steamuser\\AppData\\Local\\Programs\\Epic\\EpicGamesLauncher.exe\" %1")]),
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-art1""", [("DisplayName", "Finished Game"), ("InstallLocation", @"C:\EGL\Finished")]),
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-art2""", [("DisplayName", "Removing Game"), ("InstallLocation", @"C:\EGL\Removing")]),
            // Not in the log at all (upstream lists these too).
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-art5""", [("DisplayName", "Unlogged Game"), ("InstallLocation", @"C:\EGL\Unlogged")]),
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Unrelated""", [("DisplayName", "Other app"), ("InstallLocation", @"C:\EGL\NoName")]));

        var games = (await DetectWine()).Where(g => g.InstallPath.Contains("/EGL/", StringComparison.Ordinal)).ToDictionary(g => Path.GetFileName(g.InstallPath));

        Assert.Equal(["Finished", "Migrated", "NoName", "Unlogged"], games.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Finished Game", games["Finished"].Name);
        Assert.Equal(finished, games["Finished"].ExecutablePath);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}' 'com.epicgames.launcher://apps/art1?action=launch&silent=true'", games["Finished"].LaunchCommand);
        Assert.Equal("Control", games["Migrated"].Name);     // the classic manifest a migrated game keeps
        Assert.Equal("NoName", games["NoName"].Name);        // no record names it: its folder does
        Assert.Equal("Unlogged Game", games["Unlogged"].Name);
        Assert.EndsWith("'com.epicgames.launcher://apps/art5?action=launch&silent=true'", games["Unlogged"].LaunchCommand);
    }

    [Fact]
    public async Task WinePrefix_MigratedLauncher_ClassicAndNewRecordsOfOneFolder_AreOneCard()
    {
        var prefix = Prefix("migrated");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win64"), "EpicGamesLauncher.exe");
        var both = Path.Combine(prefix, "drive_c", "EGL", "Both");
        Pe(both, "Both.exe", 8192);
        var newOnly = Path.Combine(prefix, "drive_c", "EGL", "NewOnly");
        Pe(newOnly, "NewOnly.exe", 8192);
        Manifest(prefix, "both.item", "Both Game", @"C:\EGL\Both", "BothApp");
        Log(DataFolder(prefix), """{"version":1}""", Entry("i-b", true, @"c:/egl/both/", "BothApp"), Entry("i-n", true, @"C:\EGL\NewOnly", "NewApp"));
        UserRegistry(prefix, ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-NewApp""", [("DisplayName", "New Game")]));

        var games = await DetectWine();

        Assert.Equal("Both Game", Assert.Single(games, g => g.InstallPath == both).Name);
        Assert.Equal("New Game", Assert.Single(games, g => g.InstallPath == newOnly).Name);
    }

    // ── hostile and odd records ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Records_NamingTheDriveHomePrefixOrSystemFolders_AreNoGames_AnotherMappedDriveIs()
    {
        var prefix = Prefix("paths");
        _tree.Link(Home, "home", "Games", "paths", "dosdevices", "x:");
        _tree.Link("/", "home", "Games", "paths", "dosdevices", "z:");
        var library = _tree.Dir("library");
        _tree.Link(library, "home", "Games", "paths", "dosdevices", "d:");
        var outside = Path.Combine(library, "Epic", "Outside");
        Pe(outside, "Outside.exe", 8192);
        Pe(Path.Combine(Home, "Documents"), "Stray.exe");
        Manifest(prefix, "1.item", "Drive", @"C:\", "A1");
        Manifest(prefix, "2.item", "Home", @"X:\", "A2");
        Manifest(prefix, "3.item", "Home again", "Z:" + Home.Replace('/', '\\'), "A3");
        Manifest(prefix, "4.item", "Windows", @"C:\windows", "A4");
        Manifest(prefix, "5.item", "Climb", @"C:\users\..\..\..", "A5");
        Manifest(prefix, "6.item", "Outside", @"D:\Epic\Outside", "A6");
        // The new launcher's log and uninstall entries go through the same folder rules.
        Log(DataFolder(prefix), Entry("l1", true, @"C:\", "L1"), Entry("l2", true, "Z:" + Home.Replace('/', '\\'), "L2"),
            Entry("l3", true, @"C:\windows\system32", "L3"));
        UserRegistry(prefix, ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-U1""", [("DisplayName", "U1"), ("InstallLocation", "Z:" + Home.Replace('/', '\\'))]),
            ("""Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EpicGames-U2""", [("DisplayName", "U2"), ("InstallLocation", @"C:\windows")]));

        var record = Assert.Single(StoreLauncherRecords.Find(prefix, Home, CancellationToken.None));

        Assert.Equal(outside, record.InstallDirectory);
        Assert.Equal("Outside", record.Name);
    }

    [Fact]
    public async Task HostileIds_NeverReachALaunchArgumentOrAFilePath()
    {
        var prefix = Prefix("ids");
        // The machine-wide URL handler (system.reg) names a launcher outside the default places.
        var launcher = Pe(Path.Combine(prefix, "drive_c", "Epic Launcher", "Win64"), "EpicGamesLauncher.exe");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Classes\\\\com.epicgames.launcher\\\\shell\\\\open\\\\command] 1\n" +
                                                         "@=\"\\\"C:\\\\Epic Launcher\\\\Win64\\\\EpicGamesLauncher.exe\\\" %1\"\n");
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Quoted"), "Quoted.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Climber"), "Climber.exe", 8192);
        Manifest(prefix, "q.item", "Quoted", @"C:\EGL\Quoted", "x'; touch y");
        var data = DataFolder(prefix);
        Log(data, Entry("../../escape", true, @"C:\EGL\Climber", "art 9"));
        // What the installation id would reach if it were used as a path (the folders it climbs out of exist).
        Directory.CreateDirectory(Path.Combine(data, "import-scan", "v1-items"));
        File.WriteAllText(Path.Combine(data, "escape.item"), """{"DisplayName":"Escaped"}""");

        var games = await DetectWine();

        var quoted = Assert.Single(games, g => g.Name == "Quoted");
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}'", quoted.LaunchCommand); // the launcher opens; the user starts it there
        Assert.Equal("Epic Games Launcher", quoted.ParentLauncherName);
        var climber = Assert.Single(games, g => g.InstallPath.EndsWith("/Climber", StringComparison.Ordinal));
        Assert.Equal("Climber", climber.Name);
        Assert.Equal($"WINEPREFIX='{prefix}' wine '{launcher}'", climber.LaunchCommand);
    }

    [Fact]
    public async Task MalformedLogLines_AreSkippedWithOneWarning_TheOtherEntriesStay()
    {
        var prefix = Prefix("malformed");
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Good"), "Good.exe", 8192);
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Later"), "Later.exe", 8192);
        var data = DataFolder(prefix);
        Log(data, """{"version":1}""", Entry("i-1", true, @"C:\EGL\Good", "good"), """{"installationId":"i-2","comple""",
            "not json at all", "[1,2]", Entry("i-3", true, @"C:\EGL\Later", "later"));

        var scan = await new GameLibraryScanner([new WinePrefixDetector(Home, [])]).ScanWithReportAsync(null);

        Assert.Contains(scan.Games, g => g.InstallPath.EndsWith("/EGL/Good", StringComparison.Ordinal));
        Assert.Contains(scan.Games, g => g.InstallPath.EndsWith("/EGL/Later", StringComparison.Ordinal));
        var wine = scan.Detectors.Single(d => d.Name == "Wine");
        Assert.False(wine.Failed);
        var warning = Assert.Single(wine.Warnings, w => w.Contains("installations.jsonl", StringComparison.Ordinal));
        Assert.Contains("unreadable lines skipped: 2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHugeLog_IsNotRead_TheClassicManifestsStill()
    {
        var prefix = Prefix("huge");
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Classic"), "Classic.exe", 8192);
        Manifest(prefix, "c.item", "Classic Game", @"C:\EGL\Classic", "classic");
        var log = Path.Combine(DataFolder(prefix), "installations.jsonl");
        using (var file = File.Create(log)) file.SetLength(32L * 1024 * 1024 + 1); // sparse: one byte past the cap

        var scan = await new GameLibraryScanner([new WinePrefixDetector(Home, [])]).ScanWithReportAsync(null);

        Assert.Contains(scan.Games, g => g.Name == "Classic Game");
        Assert.Contains(scan.Detectors.Single(d => d.Name == "Wine").Warnings,
            w => w.Contains(log + " is larger than 33554432 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FifoAndDeviceRecords_AreSkippedWithoutBlockingTheScan()
    {
        var prefix = Prefix("fifo");
        Pe(Path.Combine(prefix, "drive_c", "EGL", "Kept"), "Kept.exe", 8192);
        Manifest(prefix, "k.item", "Kept Game", @"C:\EGL\Kept", "kept");
        var manifests = Path.Combine(prefix, "drive_c", "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var fifoItem = Path.Combine(manifests, "fifo.item");
        var log = Path.Combine(DataFolder(prefix), "installations.jsonl");
        var zero = Path.Combine(manifests, "zero.item");
        foreach (var fifo in new[] { fifoItem, log })
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [fifo])) mkfifo.WaitForExit();
        File.CreateSymbolicLink(zero, "/dev/zero");
        // The user registry too: a prefix whose user.reg is a FIFO still lists its games.
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(prefix, "user.reg")])) mkfifo.WaitForExit();

        var scan = Task.Run(() => new GameLibraryScanner([new WinePrefixDetector(Home, [])]).ScanWithReportAsync(null));
        var done = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10))) == scan;
        if (!done)
            foreach (var fifo in new[] { fifoItem, log, Path.Combine(prefix, "user.reg") })
                _ = Task.Run(() => { using var unblock = new FileStream(fifo, FileMode.Open, FileAccess.Write); });

        Assert.True(done, "the scan blocked on a FIFO record");
        var result = await scan;
        Assert.Contains(result.Games, g => g.Name == "Kept Game");
        var warnings = result.Detectors.Single(d => d.Name == "Wine").Warnings;
        foreach (var path in new[] { fifoItem, log, zero })
            Assert.Contains(warnings, w => w.Contains(path + " is not a regular file", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("user registry unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APrefixWithoutEitherLauncher_NeverReadsItsUserRegistry()
    {
        // The user registry is read only where the Epic Games Launcher left records: one prefix scan stays one hive read.
        var prefix = Prefix("plain");
        Pe(Path.Combine(prefix, "drive_c", "Program Files", "Plain"), "Plain.exe", 8192);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(prefix, "user.reg")])) mkfifo.WaitForExit();

        var scan = await new GameLibraryScanner([new WinePrefixDetector(Home, [])]).ScanWithReportAsync(null);

        Assert.Contains(scan.Games, g => g.Name == "Plain");
        Assert.DoesNotContain(scan.Detectors.Single(d => d.Name == "Wine").Warnings, w => w.Contains("user.reg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderAnotherStoreRecorded_KeepsThatStoresHandOff()
    {
        // A Ubisoft game bought on Epic is recorded by both launchers: the card stays what it was before #247.
        var prefix = Prefix("both-stores");
        var ubisoft = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win64"), "EpicGamesLauncher.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Games2", "Valhalla");
        Pe(gameDir, "ACValhalla.exe", 8192);
        Manifest(prefix, "v.item", "Assassin's Creed Valhalla (Epic)", @"C:\Games2\Valhalla", "Eagle");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wow6432Node\\\\Ubisoft\\\\Launcher\\\\Installs\\\\13504] 1\n" +
                                                         "\"InstallDir\"=\"C:/Games2/Valhalla/\"\n");

        var game = Assert.Single(await DetectWine(), g => g.InstallPath == gameDir);

        Assert.Equal($"WINEPREFIX='{prefix}' wine '{ubisoft}' 'uplay://launch/13504'", game.LaunchCommand);
    }

    // ── Lutris, Steam shortcuts, Heroic ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LutrisPrefix_EpicGame_IsACard_StartedThroughTheLaunchersOwnLutrisEntry()
    {
        var configRoot = _tree.Dir("home", ".local", "share", "lutris", "games");
        var prefix = Prefix("lutris-egl");
        // Two store clients share the prefix: the Epic game starts through Epic's entry, not Ubisoft's.
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher"), "UbisoftConnect.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win32"), "EpicGamesLauncher.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "EGL", "Satisfactory");
        var exe = Pe(Path.Combine(gameDir, "Engine", "Binaries", "Win64"), "FactoryGame-Win64-Shipping.exe", 8192);
        Manifest(prefix, "s.item", "Satisfactory", @"C:\EGL\Satisfactory", "CrabEA");
        File.WriteAllText(Path.Combine(configRoot, "ubisoft-connect-1763420553.yml"),
            $"game:\n  exe: drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe\n  prefix: {prefix}\nname: Ubisoft Connect\n");
        File.WriteAllText(Path.Combine(configRoot, "epic-games-store-1763437958.yml"),
            $"game:\n  exe: drive_c/Program Files (x86)/Epic Games/Launcher/Portal/Binaries/Win32/EpicGamesLauncher.exe\n  prefix: {prefix}\nname: Epic Games Store\n");

        var games = LutrisGameDetector.DetectIn([configRoot], Home, CancellationToken.None);

        var game = Assert.Single(games, g => g.InstallPath == gameDir);
        Assert.Equal("Satisfactory", game.Name);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal("Epic Games Store", game.ParentLauncherName);
        Assert.Equal("lutris 'lutris:rungame/epic-games-store'", game.LaunchCommand);
        Assert.True(games.Single(g => g.Name == "Epic Games Store").RequiresExplicitPatchTarget);
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.True(game.HasConfirmedPatchTarget);
    }

    [Fact]
    public void SteamShortcutToTheLauncher_IsTheParentOfItsGames_StartedThroughSteam()
    {
        const uint id = 0x80004321;
        var prefix = _tree.Dir("steam", "steamapps", "compatdata", id.ToString(), "pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        var client = Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Epic Games", "Launcher", "Portal", "Binaries", "Win64"), "EpicGamesLauncher.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Program Files", "Epic Games", "Kena");
        var exe = Pe(gameDir, "Kena.exe", 8192);
        Manifest(prefix, "k.item", "Kena: Bridge of Spirits", @"C:\Program Files\Epic Games\Kena", "Kena");
        _tree.Bytes(ShortcutsVdf("Epic Games Launcher", client, id), "steam", "userdata", "1", "config", "shortcuts.vdf");

        var game = Assert.Single(SteamGameDetector.DetectIn([_tree.Path_("steam")], CancellationToken.None));

        Assert.Equal("Kena: Bridge of Spirits", game.Name);
        Assert.Equal(gameDir, game.InstallPath);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(SteamGameDetector.ShortcutSource, game.Source);
        Assert.Equal(client, game.LaunchExecutablePath);
        Assert.EndsWith($" steam://rungameid/{((ulong)id << 32) | 0x02000000UL}", game.LaunchCommand);
        Assert.Equal("Epic Games Launcher", game.ParentLauncherName);
        Assert.Equal(LaunchHandoff.Steam, LaunchHandoffClassifier.Classify(game));
    }

    [Fact]
    public async Task AHeroicEpicGame_TheLauncherInAPrefixAlsoRecorded_StaysOneCard()
    {
        var heroic = _tree.Dir("home", ".config", "heroic");
        var hades = _tree.Dir("home", "Games", "Heroic", "Hades");
        Pe(hades, "Hades.exe", 8192);
        File.WriteAllText(Path.Combine(_tree.Dir("home", ".config", "heroic", "legendaryConfig"), "installed.json"), $$$"""
            {"Min":{"app_name":"Min","title":"Hades","install_path":"{{{hades}}}","executable":"Hades.exe","is_installed":true}}
            """);
        var prefix = Prefix("egl-heroic");
        _tree.Link("/", "home", "Games", "egl-heroic", "dosdevices", "z:");
        Manifest(prefix, "h.item", "Hades", "Z:" + hades.Replace('/', '\\'), "Min");

        var scan = await new GameLibraryScanner([
            new HeroicGameDetector([heroic], [Path.Combine(heroic, "legendaryConfig")], []),
            new WinePrefixDetector(Home, [])
        ]).ScanWithReportAsync(null);

        Assert.Single(scan.Games, g => g.InstallPath == hades);
    }

    [Theory]
    [InlineData("WINEPREFIX='/p' wine '/p/x/EpicGamesLauncher.exe' 'com.epicgames.launcher://apps/Dill?action=launch&silent=true'", LaunchHandoff.StoreClient)]
    [InlineData("WINEPREFIX='/p' wine '/p/x/EpicGamesLauncher.exe' -openuri=com.epicgames.launcher://apps/Dill?action=launch", LaunchHandoff.StoreClient)]
    public void TheLaunchersUri_IsAStoreClientHandOff(string command, LaunchHandoff expected)
        => Assert.Equal(expected, LaunchHandoffClassifier.Classify(new DetectedGame { Name = "x", InstallPath = "/g", Source = "Wine", LaunchCommand = command }));

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<DetectedGame>> DetectWine() => new WinePrefixDetector(Home, []).DetectAsync();

    private string Prefix(string name)
    {
        var prefix = _tree.Dir("home", "Games", name);
        _tree.Dir("home", "Games", name, "drive_c", "windows");
        _tree.Dir("home", "Games", name, "drive_c", "users", "steamuser");
        _tree.Dir("home", "Games", name, "dosdevices");
        _tree.Link("../drive_c", "home", "Games", name, "dosdevices", "c:");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        return prefix;
    }

    private static string DataFolder(string prefix)
    {
        var data = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "com.epicgames.eglv2");
        Directory.CreateDirectory(data);
        return data;
    }

    /// <summary>A classic launcher manifest (the fields upstream reads, plus the DLC's main-game name).</summary>
    private static void Manifest(string prefix, string file, string name, string location, string appName, string? main = null)
    {
        var folder = Path.Combine(prefix, "drive_c", "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        Directory.CreateDirectory(folder);
        var json = new Dictionary<string, object?>
        {
            ["FormatVersion"] = 0, ["bIsIncompleteInstall"] = false, ["LaunchExecutable"] = "Game.exe",
            ["DisplayName"] = name, ["InstallLocation"] = location, ["CatalogNamespace"] = "ns", ["CatalogItemId"] = "item",
            ["AppName"] = appName, ["MainGameAppName"] = main ?? appName
        };
        File.WriteAllText(Path.Combine(folder, file), System.Text.Json.JsonSerializer.Serialize(json));
    }

    private static string Entry(string id, bool complete, string dir, string artifact)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["installationId"] = id, ["namespace"] = "ns", ["itemId"] = "item", ["artifactId"] = artifact, ["dir"] = dir,
            ["launchExecutable"] = "Game.exe", ["platform"] = "Windows", ["complete"] = complete
        });

    private static void Log(string dataFolder, params string[] lines)
        => File.WriteAllText(Path.Combine(dataFolder, "installations.jsonl"), string.Join('\n', lines) + "\n");

    /// <summary>Writes keys (names spelt with the hive's doubled backslashes; value name "" = the default value) into user.reg.</summary>
    private static void UserRegistry(string prefix, params (string Key, (string Name, string Value)[] Values)[] keys)
    {
        var text = new StringBuilder("WINE REGISTRY Version 2\n;; All keys relative to \\\\User\\\\S-1-5-21-0-0-0-1000\n\n#arch=win64\n\n");
        foreach (var (key, values) in keys)
        {
            text.Append('[').Append(key).Append("] 1700000000\n#time=1d000000000000\n");
            foreach (var (name, value) in values)
                text.Append(name.Length == 0 ? "@" : "\"" + name + "\"").Append("=\"")
                    .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
            text.Append('\n');
        }
        File.WriteAllText(Path.Combine(prefix, "user.reg"), text.ToString(), Encoding.Latin1);
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

    // One non-Steam shortcut in Steam's binary key/values (SteamShortcutTests' layout).
    private static byte[] ShortcutsVdf(string name, string exe, uint appId)
    {
        using var data = new MemoryStream();
        void Byte(byte b) => data.WriteByte(b);
        void CString(string text) { data.Write(Encoding.UTF8.GetBytes(text)); Byte(0); }
        void Str(string key, string value) { Byte(0x01); CString(key); CString(value); }
        Byte(0x00); CString("shortcuts");
        Byte(0x00); CString("0");
        Byte(0x02); CString("appid"); data.Write(BitConverter.GetBytes(unchecked((int)appId)));
        Str("AppName", name);
        Str("Exe", $"\"{exe}\"");
        Str("StartDir", $"\"{Path.GetDirectoryName(exe)}\"");
        Byte(0x08);
        Byte(0x08); Byte(0x08);
        return data.ToArray();
    }
}
