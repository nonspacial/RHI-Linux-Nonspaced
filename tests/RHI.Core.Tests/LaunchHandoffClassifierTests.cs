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
    [InlineData("Manual", "flatpak run org.example.Game", LaunchHandoff.Other)]
    [InlineData("Manual", "xdg-open 'itch://caves/abc/launch'", LaunchHandoff.Other)]
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
    // A URL in an argument is not a hand-off (the old launcher rule treated any "://" as one).
    [InlineData("Manual", "WINEPREFIX=/tmp/pfx wine Game.exe --server=https://example.org")]
    [InlineData("Manual", "umu-run '/games/Game.exe'")]
    // A Steam card whose command the user replaced with a direct one runs that command itself.
    [InlineData("Steam", "gamemoderun wine '/games/Game.exe'")]
    // A program merely named like a store word later on the line is still a direct launch.
    [InlineData("Manual", "'/opt/my steam tools/run.sh' steam")]
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

    [Fact]
    public void ShellWords_HonourQuotesAndStopAtSeparators()
    {
        Assert.Equal(["a b", "c\"d", "e f", "g"], LaunchHandoffClassifier.ShellWords("'a b' \"c\\\"d\" e\\ f g; rm -rf x"));
        Assert.Equal(["x"], LaunchHandoffClassifier.ShellWords("x && y"));
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
