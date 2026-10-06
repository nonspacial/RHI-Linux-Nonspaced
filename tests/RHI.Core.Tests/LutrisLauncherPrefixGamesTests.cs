using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#119: games inside a Lutris launcher's prefix get their own cards, launched via the launcher.</summary>
public sealed class LutrisLauncherPrefixGamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-lutris-launcher-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private string ConfigRoot => Path.Combine(Home, ".local", "share", "lutris", "games");

    public LutrisLauncherPrefixGamesTests() => Directory.CreateDirectory(ConfigRoot);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void UbisoftPrefix_ListsEachLibraryGame_LaunchedThroughTheLauncher()
    {
        var prefix = Prefix("ubisoft-connect");
        var launcherDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher");
        Pe(launcherDir, "UbisoftConnect.exe");
        Pe(launcherDir, "UbisoftExtension.exe", 8192);
        var farCry = Path.Combine(launcherDir, "games", "Far Cry 6");
        var farCryExe = Pe(Path.Combine(farCry, "bin"), "FarCry6.exe", 4096);
        Pe(Path.Combine(farCry, "Support"), "CrashSender.exe");
        var pop = Path.Combine(launcherDir, "games", "Prince of Persia The Lost Crown");
        Pe(pop, "TheLostCrown.exe", 4096);
        // Launcher support apps and Proton's Steam stub are not library games.
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher Core"), "UpcElevationService.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Steam"), "steam.exe");
        Pe(Path.Combine(prefix, "drive_c", "Program Files", "Common Files", "Thing"), "Thing.exe");
        Config("ubisoft-connect-1763420553", $"""
            game:
              exe: drive_c/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/UbisoftConnect.exe
              prefix: {prefix}
            name: Ubisoft Connect
            wine:
              version: ge-proton
            """);

        var games = Detect().ToDictionary(g => g.Name);

        Assert.Equal(["Far Cry 6", "Prince of Persia The Lost Crown", "Ubisoft Connect"], games.Keys.Order());
        var launcher = games["Ubisoft Connect"];
        Assert.Null(launcher.ParentLauncherName);
        var game = games["Far Cry 6"];
        Assert.Equal(farCry, game.InstallPath);
        Assert.Equal(farCryExe, game.ExecutablePath);
        Assert.Equal(prefix, game.PrefixPath);
        Assert.Equal("Lutris", game.Source);
        Assert.Equal("lutris 'lutris:rungame/ubisoft-connect'", game.LaunchCommand);
        Assert.Equal(launcher.LaunchCommand, game.LaunchCommand);
        Assert.Equal("ge-proton", game.CompatibilityTool);
        Assert.Equal(launcher.Runner, game.Runner);
        Assert.Equal("Ubisoft Connect", game.ParentLauncherName);

        // The launcher stays the launch target; the game folder stays the confirmed patch target.
        LinuxGameIdentityService.ApplyDetectedIdentity(game);
        Assert.Equal("Ubisoft", game.LaunchProvider);
        Assert.True(game.HasConfirmedPatchTarget);
        Assert.Equal(farCryExe, game.ExecutablePath);
        Assert.Equal(Path.GetDirectoryName(farCryExe), game.BinaryDirectory);
    }

    [Fact]
    public async Task BattleNetPrefix_TwoConfigs_ListGamesOnceAndKeepOneLauncherCard()
    {
        var prefix = Prefix("battlenet");
        var launcherDir = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net");
        var launcherExe = Pe(launcherDir, "Battle.net Launcher.exe");
        Pe(launcherDir, "Battle.net.exe", 8192);
        var outer = Path.Combine(prefix, "drive_c", "Games", "The Outer Worlds 2");
        Pe(outer, "TheOuterWorlds2.exe");
        var shipping = Pe(Path.Combine(outer, "Arkansas", "Binaries", "Win64"), "TheOuterWorlds2-Win64-Shipping.exe", 8192);
        // Folded `exe:` as Lutris writes long values, and an installer script with its own `wine:`.
        Config("battlenet-1763437958", $"""
            game:
              arch: win64
              exe: {launcherDir}/Battle.net
                Launcher.exe
              prefix: {prefix}
              working_dir: {prefix}/drive_c/Games
            name: battle.net
            wine:
              version: GE-Proton10-29
            """);
        Config("battlenet-1774722738", $"""
            game:
              exe: drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe
              prefix: {prefix}
            name: Battle.net
            script:
              wine:
                overrides:
                  locationapi: d
            wine:
              version: ge-proton
            """);

        var raw = Detect();

        var launchers = raw.Where(g => g.ParentLauncherName is null).ToArray();
        Assert.All(launchers, g => Assert.Equal(launcherExe, g.ExecutablePath));
        Assert.Equal(["GE-Proton10-29", "ge-proton"], launchers.Select(g => g.CompatibilityTool!).Order(StringComparer.Ordinal));
        var game = Assert.Single(raw, g => g.ParentLauncherName is not null);
        Assert.Equal(outer, game.InstallPath);
        Assert.Equal(shipping, game.ExecutablePath);
        Assert.Equal("battle.net", game.ParentLauncherName); // the oldest config, which `rungame/<slug>` resolves to
        Assert.Equal("GE-Proton10-29", game.CompatibilityTool);
        Assert.Equal("lutris 'lutris:rungame/battlenet'", game.LaunchCommand);

        var library = await new GameLibraryScanner([new FixedDetector(raw)]).ScanAsync();
        Assert.Equal(["battle.net", "The Outer Worlds 2"], library.Select(g => g.Name));
        Assert.Null(library[0].ParentLauncherName);
        Assert.Equal("battle.net", library[1].ParentLauncherName);
    }

    [Fact]
    public void GameWithItsOwnLutrisEntry_StaysOneCard()
    {
        var prefix = Prefix("battlenet");
        Pe(Path.Combine(prefix, "drive_c", "Program Files (x86)", "Battle.net"), "Battle.net Launcher.exe");
        // A GDK package copy holding only the launch stub: no game executable, so no card.
        Pe(Path.Combine(prefix, "drive_c", "Games", "Blue Prince", "Content"), "gamelaunchhelper.exe", 8192);
        // The same game folder reached through a link is the same game: no second card.
        var other = Path.Combine(Home, "Games", "other-game");
        Pe(other, "Other.exe");
        Directory.CreateSymbolicLink(Path.Combine(prefix, "drive_c", "Games", "Other"), other);
        var blue = Path.Combine(Home, "Games", "blue-prince-xodus");
        Pe(blue, "BLUE PRINCE.exe");
        var wrapper = Path.Combine(Home, ".local", "bin", "blue-prince-xodus-launch");
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        File.WriteAllText(wrapper, "#!/bin/sh\n");
        Config("battlenet-1763437958", $"game:\n  exe: drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe\n  prefix: {prefix}\nname: Battle.net\n");
        Config("blue-prince-1763483489", $"game:\n  exe: {wrapper}\n  working_dir: {blue}\nname: Blue Prince\n");
        Config("other-1700000000", $"game:\n  exe: {other}/Other.exe\nname: Other\n");

        var games = Detect();

        Assert.Equal(["Battle.net", "Blue Prince", "Other"], games.Select(g => g.Name).Order());
        Assert.Equal(blue, games.Single(g => g.Name == "Blue Prince").InstallPath);
    }

    [Fact]
    public void FoldedPrefix_EveryPrefixTheWineDetectorSkips_IsListedByLutris()
    {
        // fix-50 review minor 1: a long prefix folded onto two lines must be read in full by both.
        var prefix = Prefix("My Long Launcher Prefix");
        Pe(Path.Combine(prefix, "drive_c", "Program Files", "Store"), "Store.exe");
        var gameDir = Path.Combine(prefix, "drive_c", "Program Files", "EA Games", "Game A");
        var gameExe = Pe(Path.Combine(gameDir, "bin"), "GameA.exe", 4096);
        Config("store-1700000000", $"""
            game:
              exe: drive_c/Program Files/Store/Store.exe
              prefix: {Path.Combine(Home, "Games", "My Long")}
                Launcher Prefix
            name: Store
            """);

        var games = Detect();
        var owned = LauncherOwnedPrefixes.Load(Home, [ConfigRoot]);

        Assert.True(owned.Owns(prefix));
        Assert.All(games, g => Assert.Equal(prefix, g.PrefixPath));
        Assert.Equal(["Game A", "Store"], games.Select(g => g.Name).Order());
        Assert.Equal(gameExe, games.Single(g => g.Name == "Game A").ExecutablePath);
    }

    private IReadOnlyList<DetectedGame> Detect() => LutrisGameDetector.DetectIn([ConfigRoot], Home, CancellationToken.None);

    private string Prefix(string name)
    {
        var prefix = Path.Combine(Home, "Games", name);
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows"));
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "");
        return prefix;
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

    private void Config(string stem, string yaml) => File.WriteAllText(Path.Combine(ConfigRoot, stem + ".yml"), yaml);

    private sealed class FixedDetector(IReadOnlyList<DetectedGame> games) : IGameDetector
    {
        public string Name => "Lutris";
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(games);
    }
}
