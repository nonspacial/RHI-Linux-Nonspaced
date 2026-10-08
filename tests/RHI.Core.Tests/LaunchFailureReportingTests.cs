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

    // Final detect-deploy review, minor 1: a command that fails at once but leaves a background child (a wrapper's daemon,
    // the Steam client) holding the stderr FIFO was reported only when that child ended, i.e. never inside the window,
    // and the launch shell lingered for the whole session. The shell's wait for its reader is now bounded.
    [Fact]
    public async Task AnEarlyFailureThatLeavesABackgroundChild_IsReportedInsideTheWindow()
    {
        var logs = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-launch-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var launcher = new LinuxGameLauncher(LinuxGameLauncher.DefaultEarlyExitWindow, logs);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() =>
                launcher.LaunchAsync(Direct("echo 'wrapper failed' >&2; sleep 4 & exit 3")));

            Assert.Equal(3, failure.ExitCode);
            Assert.Equal("wrapper failed", failure.Detail);
            Assert.True(watch.Elapsed < LinuxGameLauncher.DefaultEarlyExitWindow, $"reported after {watch.Elapsed}");
        }
        finally { Directory.Delete(logs, recursive: true); }
    }

    // Final detect-deploy review, minor 2 (surviving mutants L2/L3): the launch shell waits for its stderr reader before
    // exiting, and runs the command in a subshell so an `exit` in it still reaches that wait. A reader slowed down here
    // (a `head` that starts 0.3 s late) loses the last line if either is dropped.
    [Fact]
    public async Task ASlowStderrReader_StillHasTheLastLineInTheFile_BeforeTheFailureIsRead()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-launch-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var realHead = new[] { "/usr/bin/head", "/bin/head" }.First(File.Exists);
            var head = Path.Combine(bin, "head");
            File.WriteAllText(head, $"#!/bin/sh\nsleep 0.3\nexec {realHead} \"$@\"\n");
            File.SetUnixFileMode(head, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var launcher = new LinuxGameLauncher(TimeSpan.FromSeconds(10), Path.Combine(root, "logs"));
            var options = new GameLaunchOptions(Environment: new Dictionary<string, string>
            {
                ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH")
            });

            var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() =>
                launcher.LaunchAsync(Direct("echo 'late line' >&2; exit 4"), options));

            Assert.Equal(4, failure.ExitCode);
            Assert.Equal("late line", failure.Detail);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A detector-built direct launch (plain <c>sh -c</c>, no login profile).</summary>
    private static DetectedGame Direct(string command) => new()
    {
        Name = "Fake", Source = "Wine", InstallPath = Path.GetTempPath(), LaunchCommand = command
    };

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
