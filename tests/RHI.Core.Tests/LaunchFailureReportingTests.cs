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
    public void LaunchShell_IsNotALoginShell()
    {
        var start = LinuxGameLauncher.BuildStartInfo(Manual("true"), new GameLaunchOptions());
        Assert.Equal(["-c", "true"], start.ArgumentList);
        Assert.DoesNotContain("-lc", LinuxGameLauncher.BuildShellCommand("true", "gamescope --"));
    }

    [Theory]
    [InlineData("/home/u/.var/app/com.valvesoftware.Steam/.local/share/Steam", true, "flatpak run com.valvesoftware.Steam -applaunch 42")]
    [InlineData("/home/u/.local/share/Steam", true, "steam -applaunch 42")]
    [InlineData("/home/u/.local/share/Steam", false, "xdg-open steam://rungameid/42")]
    public void SteamLaunch_TargetsTheClientThatOwnsTheLibrary(string root, bool hostSteam, string expected)
        => Assert.Equal(expected, SteamGameDetector.LaunchCommandFor(42, root, hostSteam));
}
