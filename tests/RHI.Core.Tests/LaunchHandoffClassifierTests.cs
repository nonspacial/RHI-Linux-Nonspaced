using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

/// <summary>One store hand-off rule for the launcher's working directory and the HDR profile (Refs #106, #46).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LaunchHandoffClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-handoff-test-" + Guid.NewGuid().ToString("N"));

    public LaunchHandoffClassifierTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("Heroic/Epic", "xdg-open 'heroic://launch?appName=Fortnite&runner=legendary'", LaunchHandoff.Heroic)]
    [InlineData("Heroic/GOG", "xdg-open 'heroic://launch?appName=1207658924&runner=gog'", LaunchHandoff.Heroic)]
    [InlineData("Heroic/Amazon", "xdg-open 'heroic://launch?appName=amzn1&runner=nile'", LaunchHandoff.Heroic)]
    [InlineData("Heroic/Sideload", "xdg-open 'heroic://launch?appName=side&runner=sideload'", LaunchHandoff.Heroic)]
    [InlineData("Manual", "xdg-open heroic://launch?appName=x", LaunchHandoff.Heroic)]
    [InlineData("Manual", "flatpak run com.heroicgameslauncher.hgl --no-gui", LaunchHandoff.Heroic)]
    [InlineData("Lutris", "lutris 'lutris:rungame/ubisoft-connect'", LaunchHandoff.Lutris)]
    [InlineData("Manual", "flatpak run net.lutris.Lutris lutris:rungame/x", LaunchHandoff.Lutris)]
    [InlineData("Manual", "LANG=C lutris lutris:rungame/fake", LaunchHandoff.Lutris)]
    [InlineData("Steam", "steam -applaunch 1234", LaunchHandoff.Steam)]
    [InlineData("Steam", "flatpak run com.valvesoftware.Steam -applaunch 1234", LaunchHandoff.Steam)]
    [InlineData("Steam", "xdg-open steam://rungameid/1234", LaunchHandoff.Steam)]
    [InlineData("Manual", "env VAR=x steam -applaunch 1234", LaunchHandoff.Steam)]
    [InlineData("Manual", "env -u DISPLAY VAR=x /usr/bin/steam steam://rungameid/1234", LaunchHandoff.Steam)]
    [InlineData("Manual", "gio open 'steam://rungameid/1234'", LaunchHandoff.Steam)]
    [InlineData("Manual", "xdg-open 'itch://caves/abc/launch'", LaunchHandoff.Other)]
    // w2-launch review, the shapes it found correct (kept by the wrapper-aware rewrite):
    [InlineData("Manual", "steam steam://rungameid/1", LaunchHandoff.Steam)]
    [InlineData("Manual", "steam://rungameid/1", LaunchHandoff.Steam)]
    [InlineData("Manual", "xdg-open heroic://launch/legendary/Fortnite", LaunchHandoff.Heroic)]
    [InlineData("Manual", "flatpak run --branch=stable com.valvesoftware.Steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "/usr/bin/flatpak run net.lutris.Lutris lutris:rungameid/5", LaunchHandoff.Lutris)]
    [InlineData("Manual", "lutris lutris:rungameid/5", LaunchHandoff.Lutris)]
    [InlineData("Manual", "env -i HOME=/home/u steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "~/.steam/steam.sh -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "/usr/games/steam -applaunch 1", LaunchHandoff.Steam)]
    // w2-launch review, major 1: a store client behind a wrapper was read as a direct launch.
    [InlineData("Steam", "gamemoderun steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Steam", "prime-run steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "primusrun steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "pvkrun steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "optirun -b primus steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "nvidia-offload steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Steam", "mangohud xdg-open steam://rungameid/1", LaunchHandoff.Steam)]
    [InlineData("Manual", "mangohud --dlsym steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Steam", "gamescope -- steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Steam", "gamescope -W 2560 -H 1440 -r 144 --hdr-enabled -- steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "obs-gamecapture steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "obs-vkcapture steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "nice -n 5 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "nice -5 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "ionice -c 3 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "ionice -c2 -n 7 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "taskset -c 0-7 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "taskset 0xff steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "chrt -f 10 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "stdbuf -o L steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "time -p steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "strangle 60 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "nohup steam -applaunch 1 &", LaunchHandoff.Steam)]
    [InlineData("Manual", "setsid -f steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "exec steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "dbus-run-session -- steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "DRI_PRIME=1 gamemoderun mangohud steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "env DXVK_HDR=1 gamemoderun steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "gamemoderun lutris lutris:rungame/x", LaunchHandoff.Lutris)]
    [InlineData("Manual", "gamemoderun xdg-open 'heroic://launch?appName=x'", LaunchHandoff.Heroic)]
    [InlineData("Manual", "gamemoderun xdg-open 'itch://caves/abc/launch'", LaunchHandoff.Other)]
    // sh/bash -c: the inner command decides.
    [InlineData("Steam", "sh -c 'steam -applaunch 1'", LaunchHandoff.Steam)]
    [InlineData("Manual", "bash -lc \"gamemoderun steam -applaunch 1\"", LaunchHandoff.Steam)]
    [InlineData("Manual", "/bin/sh -e -c 'cd /g && lutris lutris:rungame/x'", LaunchHandoff.Lutris)]
    // Shell set-up before the program.
    [InlineData("Steam", "cd /g && steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "cd '/my games' || exit 1; steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "export DXVK_HDR=1; steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "# my launcher\nsteam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "( steam -applaunch 1 )", LaunchHandoff.Steam)]
    // A store URL handed to anything: a Heroic AppImage, a Flatpak, the user's own wrapper.
    [InlineData("Heroic/Epic", "/home/u/Applications/Heroic-2.15.2.AppImage --no-gui heroic://launch/legendary/Fortnite", LaunchHandoff.Heroic)]
    [InlineData("Manual", "/home/u/Applications/Heroic.AppImage", LaunchHandoff.Heroic)]
    [InlineData("Manual", "flatpak run org.example.HeroicFork --no-gui heroic://launch/gog/1207658924", LaunchHandoff.Heroic)]
    [InlineData("Manual", "'/home/u/bin/my wrapper' steam://rungameid/1", LaunchHandoff.Steam)]
    [InlineData("Manual", "/home/u/bin/wrap --url=lutris:rungame/x", LaunchHandoff.Lutris)]
    // w2-launch review, nit 11: a space-separated flatpak option value; launchers shipped as Flatpaks.
    [InlineData("Manual", "flatpak run --branch stable com.valvesoftware.Steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "flatpak run --user --arch x86_64 net.lutris.Lutris lutris:rungame/x", LaunchHandoff.Lutris)]
    [InlineData("Manual", "flatpak run com.usebottles.bottles -b Games -p Game", LaunchHandoff.Other)]
    // w2-launch re-review, nit 3: a Flatpak ref with its branch, bash's --rcfile value, gamescope without `--`.
    [InlineData("Manual", "flatpak run com.valvesoftware.Steam//beta -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Manual", "flatpak run net.lutris.Lutris/x86_64/stable lutris:rungame/x", LaunchHandoff.Lutris)]
    [InlineData("Manual", "bash --rcfile /home/u/.steamrc -c 'steam -applaunch 1'", LaunchHandoff.Steam)]
    [InlineData("Manual", "bash --init-file x -ic 'heroic --no-gui'", LaunchHandoff.Heroic)]
    [InlineData("Steam", "gamescope -W 1920 steam -applaunch 1", LaunchHandoff.Steam)]
    [InlineData("Steam", "gamescope -W 2560 -H 1440 -F fsr --backend sdl -f --hdr-enabled steam -applaunch 1", LaunchHandoff.Steam)]
    public void StoreHandoffs_AreHandoffsForTheLauncherAndForHdr(string source, string command, LaunchHandoff expected)
    {
        var game = Game(source, command);

        Assert.Equal(expected, LaunchHandoffClassifier.Classify(game));
        Assert.True(LinuxHdrLaunchProfileService.IsLauncherMediated(game));
        // The client picks the game's working directory itself.
        Assert.Equal(string.Empty, LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).WorkingDirectory);
    }

    [Theory]
    [InlineData("Manual", "WINEPREFIX='/tmp/pfx' wine '/tmp/Game.exe'")]
    [InlineData("Wine", "WINEPREFIX='/p' '/proton/files/bin/wine' '/p/drive_c/Game/game.exe'")]
    // A non-store URL in an argument is not a hand-off (the old launcher rule treated any "://" as one).
    [InlineData("Manual", "WINEPREFIX=/tmp/pfx wine Game.exe --server=https://example.org")]
    [InlineData("Manual", "umu-run '/games/Game.exe'")]
    [InlineData("Manual", "GAMEID=umu-0 umu-run '/games/Game.exe'")]
    [InlineData("Manual", "STEAM_COMPAT_DATA_PATH=/c '/x/proton' waitforexitandrun /g/Game.exe")]
    [InlineData("Manual", "legendary launch Fortnite")]
    // A Steam card whose command the user replaced with a direct one runs that command itself.
    [InlineData("Steam", "gamemoderun wine '/games/Game.exe'")]
    // A program merely named like a store word later on the line is still a direct launch.
    [InlineData("Manual", "'/opt/my steam tools/run.sh' steam")]
    [InlineData("Manual", "wine Game.exe -steam")]
    [InlineData("Manual", "steam-run ./Game.x86_64")]
    // Wrappers in front of a direct launch stay direct.
    [InlineData("Steam", "gamescope -W 1920 -H 1080 -- wine '/games/Game.exe'")]
    [InlineData("Manual", "taskset -c 0-3 nice -n 5 wine Game.exe")]
    [InlineData("Manual", "sh -c 'wine Game.exe'")]
    [InlineData("Manual", "sh ./start-steam.sh")]
    [InlineData("Manual", "cd /g && ./Game.x86_64")]
    // A game shipped as a Flatpak is run (and waited for) by flatpak run itself; gamescope can wrap it (nit 11).
    [InlineData("Manual", "flatpak run org.example.Game")]
    [InlineData("Manual", "flatpak run --branch stable org.example.Game")]
    [InlineData("Manual", "flatpak run org.example.Game//beta")]
    // gamescope without `--` in front of a direct launch stays direct (w2-launch re-review, nit 3).
    [InlineData("Steam", "gamescope -W 1920 -f wine '/games/Game.exe'")]
    [InlineData("Manual", "bash --rcfile /home/u/.steamrc -c 'wine Game.exe'")]
    public void DirectCommands_AreNotHandoffs(string source, string command)
    {
        var game = Game(source, command);

        Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(game));
        Assert.False(LinuxHdrLaunchProfileService.IsLauncherMediated(game));
        Assert.Equal(Path.GetDirectoryName(game.LaunchExecutablePath), LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).WorkingDirectory);
    }

    [Theory]
    [InlineData("Steam", LaunchHandoff.Steam)]
    [InlineData("Heroic/Epic", LaunchHandoff.Heroic)]
    [InlineData("Lutris", LaunchHandoff.Lutris)]
    [InlineData("Wine", LaunchHandoff.None)]
    [InlineData("Manual", LaunchHandoff.None)]
    public void WithoutCommand_TheSourceDecides(string source, LaunchHandoff expected)
        => Assert.Equal(expected, LaunchHandoffClassifier.Classify(Game(source, null)));

    [Theory]
    [InlineData("Heroic/Amazon", "xdg-open 'heroic://launch?appName=amzn1&runner=nile'", "Heroic")]
    [InlineData("Heroic/Sideload", "xdg-open 'heroic://launch?appName=side&runner=sideload'", "Heroic")]
    [InlineData("Heroic/Epic", "xdg-open 'heroic://launch?appName=x&runner=legendary'", "Epic")]
    [InlineData("Manual", "xdg-open steam://rungameid/1", "Steam")]
    [InlineData("Lutris", "lutris lutris:rungame/x", null)]
    public void LaunchProvider_UsesTheSameHeroicClassification(string source, string command, string? expected)
    {
        // The provider rule required Source == "Heroic", which no detector emits (#106).
        var game = Game(source, command);
        game.LaunchExecutablePath = null;
        Assert.Equal(expected, RHI.Platform.Linux.Settings.LinuxGameIdentityService.InferLaunchProvider(game));
    }

    [Fact]
    public void ShellWords_HonourQuotesAndStopAtSeparators()
    {
        Assert.Equal(["a b", "c\"d", "e f", "g"], LaunchHandoffClassifier.ShellWords("'a b' \"c\\\"d\" e\\ f g; rm -rf x"));
        Assert.Equal(["x"], LaunchHandoffClassifier.ShellWords("x && y"));
        Assert.Equal("cd,/g h|&& a||| b|; c|| d|",
            string.Join(" ", LaunchHandoffClassifier.SimpleCommands("cd '/g h' && a || b; c | d # e")
                .Select(c => string.Join(",", c.Words) + "|" + c.Separator)));
    }

    private DetectedGame Game(string source, string? command)
    {
        var bin = Path.Combine(_root, "Game", "Bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllText(exe, "MZ");
        return new DetectedGame
        {
            Name = "Game", Source = source, InstallPath = Path.Combine(_root, "Game"),
            LaunchExecutablePath = exe, LaunchCommand = command
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
