using RHI.Core.Models;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class WinePrefixDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-wineprefix-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _lutrisGames;

    public WinePrefixDetectorTests()
    {
        _home = Path.Combine(_root, "home");
        _lutrisGames = Path.Combine(_home, ".local", "share", "lutris", "games");
        Directory.CreateDirectory(_lutrisGames);
    }

    [Fact]
    public async Task LutrisReferencedPrefix_IsSkippedThroughSymlinkAndTrailingSlash()
    {
        // ~/Games is a symlink to another disk, as on the owner's machine.
        var disk = Path.Combine(_root, "disk", "Games");
        Directory.CreateDirectory(disk);
        Directory.CreateSymbolicLink(Path.Combine(_home, "Games"), disk);
        var owned = Prefix(Path.Combine(disk, "ubisoft-connect"));
        Directory.CreateSymbolicLink(Path.Combine(owned, "pfx"), ".");
        Exe(owned, "Program Files (x86)/Vendor/Launcher/Launcher.exe");
        var free = Prefix(Path.Combine(disk, "standalone"));
        Exe(free, "Program Files/Free Game/FreeGame.exe");
        // The scan reaches the prefix through ~/Games; the config names it by its real path, via Proton's `pfx -> .`.
        File.WriteAllText(Path.Combine(_lutrisGames, "ubisoft-connect-1763420553.yml"),
            $"game:\n  exe: drive_c/x.exe\n  prefix: {disk}/ubisoft-connect/pfx/\nwine:\n  version: ge-proton\n");

        var games = await Detect();

        Assert.Equal(["Free Game"], games.Select(g => g.Name));
    }

    [Fact]
    public async Task LongLutrisPrefixValue_FoldedOntoTwoLines_IsStillRecognised()
    {
        // YAML folds a plain scalar at a space; the continuation is joined back with one space.
        var owned = Prefix(Path.Combine(_home, "Games", "My Long Prefix"));
        Exe(owned, "Program Files/Game/Game.exe");
        File.WriteAllText(Path.Combine(_lutrisGames, "long-1.yml"),
            $"game:\n  prefix: {Path.Combine(_home, "Games", "My Long")}\n    Prefix\nname: Long\n");

        Assert.Empty(await Detect());
    }

    [Fact]
    public async Task UmuDefaultPrefix_IsSkipped()
    {
        var umu = Prefix(Path.Combine(_home, "Games", "umu", "umu-default"));
        Exe(umu, "Program Files/Game/Game.exe");

        Assert.Empty(await Detect());
    }

    [Fact]
    public async Task VendorFolder_ListsLauncherAndLibraryGames_NotTheVendor()
    {
        var prefix = Prefix(Path.Combine(_home, "Games", "pfx"));
        var launcher = Exe(prefix, "Program Files (x86)/Vendor/Launcher/Launcher.exe");
        var foo = Exe(prefix, "Program Files (x86)/Vendor/Launcher/games/Foo/Foo.exe", bytes: 4096);
        var bar = Exe(prefix, "Games/Bar/Bar.exe");
        var unreal = Exe(prefix, "Program Files/Unreal Game/bin/x64/UnrealGame.exe");
        Exe(prefix, "Program Files/Unreal Game/Support/CrashSender.exe");

        var games = (await Detect()).ToDictionary(g => g.Name, g => g.ExecutablePath);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["Launcher"] = launcher, ["Foo"] = foo, ["Bar"] = bar, ["Unreal Game"] = unreal
        }, games);
    }

    [Fact]
    public async Task EaAndOriginLibraries_ListEachGame_NotTheLibraryFolder()
    {
        var prefix = Prefix(Path.Combine(_home, "Games", "ea"));
        Exe(prefix, "Program Files/Electronic Arts/EA Desktop/EA Desktop/EADesktop.exe");
        // Each product is its own entry, including one whose exe sits only under `bin/`.
        var a = Exe(prefix, "Program Files/EA Games/Game A/bin/GameA.exe", bytes: 4096);
        var b = Exe(prefix, "Program Files/EA Games/Game B/GameB.exe", bytes: 4096);
        var c = Exe(prefix, "Program Files (x86)/Origin Games/Game C/GameC.exe", bytes: 4096);
        // A loose exe in the library folder itself is not a game: the folder is never one entry.
        Exe(prefix, "Program Files/EA Games/Cleanup.exe");

        var games = (await Detect()).ToDictionary(g => g.Name, g => g.ExecutablePath);

        Assert.Equal(a, games["Game A"]);
        Assert.Equal(b, games["Game B"]);
        Assert.Equal(c, games["Game C"]);
        Assert.DoesNotContain("EA Games", games.Keys);
        Assert.DoesNotContain("Origin Games", games.Keys);
    }

    [Fact]
    public async Task ProtonBuiltPrefix_LaunchesWithItsOwnWine_OrNotAtAll()
    {
        var dist = Path.Combine(_root, "GE-Proton10-32", "files");
        var wine = Path.Combine(dist, "bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(wine)!);
        File.WriteAllText(wine, "");
        File.WriteAllText(Path.Combine(dist, "bin", "wineserver"), "");

        var proton = Prefix(Path.Combine(_home, "Games", "proton"));
        Exe(proton, "Program Files/A/A.exe");
        File.WriteAllText(Path.Combine(proton, "config_info"), $"GE-Proton10-32\n{dist}/share/fonts/\n{dist}/lib/\n");
        var orphan = Prefix(Path.Combine(_home, "Games", "orphan"));
        Exe(orphan, "Program Files/B/B.exe");
        File.WriteAllText(Path.Combine(orphan, "version"), "GE-Proton9-1\n");
        var plain = Prefix(Path.Combine(_home, "Games", "plain"));
        Exe(plain, "Program Files/C/C.exe");

        var games = (await Detect()).ToDictionary(g => g.Name);

        Assert.Equal(wine, games["A"].WineBinaryPath);
        Assert.Equal("GE-Proton10-32", games["A"].CompatibilityTool);
        Assert.Contains($" '{wine}' ", games["A"].LaunchCommand);
        Assert.Null(games["B"].LaunchCommand);
        Assert.Equal("GE-Proton9-1", games["B"].CompatibilityTool);
        Assert.Contains("' wine '", games["C"].LaunchCommand);
    }

    [Fact]
    public void LutrisConfigRoots_IncludeFlatpakDataDirectory()
        => Assert.Contains(
            Path.Combine("/h", ".var", "app", "net.lutris.Lutris", "data", "lutris", "games"),
            RHI.Platform.Linux.Paths.LinuxPaths.LutrisConfigRootCandidates("/h", "/h/.config", "/h/.local/share"));

    private Task<IReadOnlyList<DetectedGame>> Detect()
        => new WinePrefixDetector(_home, [_lutrisGames]).DetectAsync();

    private static string Prefix(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "drive_c", "windows"));
        File.WriteAllText(Path.Combine(path, "system.reg"), "");
        return path;
    }

    private static string Exe(string prefix, string relative, int bytes = 64)
    {
        var path = Path.Combine(prefix, "drive_c", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
