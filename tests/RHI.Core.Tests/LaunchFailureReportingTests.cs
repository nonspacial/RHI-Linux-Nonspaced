using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

public sealed class LaunchFailureReportingTests
{
    private static DetectedGame Manual(string command) => new()
    {
        Name = "Fake", Source = "Manual", InstallPath = Path.GetTempPath(), LaunchCommand = command
    };

    [Fact]
    public async Task ACommandThatFailsAtOnce_ReportsItsExitCodeAndStderr()
    {
        var launcher = new LinuxGameLauncher(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() =>
            launcher.LaunchAsync(Manual("echo 'first line' >&2; echo 'steam: not found' >&2; exit 127")));

        Assert.Equal(127, failure.ExitCode);
        Assert.Equal("steam: not found", failure.Detail);
    }

    [Fact]
    public async Task ACommandThatSucceedsOrKeepsRunning_IsNotAFailure()
    {
        var launcher = new LinuxGameLauncher(TimeSpan.FromMilliseconds(300));
        await launcher.LaunchAsync(Manual("exit 0"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await launcher.LaunchAsync(Manual("sleep 3; exit 9"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "a running game must not hold the launch");
    }

    [Fact]
    public void DetectorBuiltCommands_SkipTheLoginShell_UserTypedOnesUseIt()
    {
        var steam = new DetectedGame { Name = "S", Source = "Steam", InstallPath = Path.GetTempPath(), LaunchCommand = "steam -applaunch 1" };
        var wine = new DetectedGame { Name = "W", Source = "Wine", InstallPath = Path.GetTempPath(), LaunchCommand = "wine game.exe" };
        Assert.Equal(["-c", "steam -applaunch 1"], LinuxGameLauncher.BuildStartInfo(steam, new GameLaunchOptions()).ArgumentList);
        Assert.Equal(["-c", "wine game.exe"], LinuxGameLauncher.BuildStartInfo(wine, new GameLaunchOptions()).ArgumentList);
        Assert.DoesNotContain("-lc", LinuxGameLauncher.BuildShellCommand("true", "gamescope --"));

        Assert.Equal(["-lc", "true"], LinuxGameLauncher.BuildStartInfo(Manual("true"), new GameLaunchOptions()).ArgumentList);
    }

    [Theory]
    [InlineData("/home/u/.var/app/com.valvesoftware.Steam/.local/share/Steam", true, "flatpak run com.valvesoftware.Steam -applaunch 42")]
    [InlineData("/home/u/.local/share/Steam", true, "steam -applaunch 42")]
    [InlineData("/home/u/.local/share/Steam", false, "xdg-open steam://rungameid/42")]
    public void SteamLaunch_TargetsTheClientThatOwnsTheLibrary(string root, bool hostSteam, string expected)
        => Assert.Equal(expected, SteamGameDetector.LaunchCommandFor(42, root, hostSteam));

    [Fact]
    public void SteamLaunch_AHostSymlinkIntoFlatpakSteam_StillUsesFlatpak()
    {
        using var tree = new DetectionTestTree();
        var flatpakRoot = tree.Dir("home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        var link = tree.Link(flatpakRoot, "home", "SteamLink");

        Assert.Equal("flatpak run com.valvesoftware.Steam -applaunch 42", SteamGameDetector.LaunchCommandFor(42, link, hostSteamOnPath: true));
        Assert.Equal("flatpak run com.valvesoftware.Steam -applaunch 42", SteamGameDetector.LaunchCommandFor(42, flatpakRoot, hostSteamOnPath: true));
        Assert.Equal("steam -applaunch 42", SteamGameDetector.LaunchCommandFor(42, tree.Dir("home", "Steam"), hostSteamOnPath: true));
    }
}
