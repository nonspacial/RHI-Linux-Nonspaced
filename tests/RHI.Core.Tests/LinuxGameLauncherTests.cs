using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxGameLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-launch-test-" + Guid.NewGuid().ToString("N"));

    public LinuxGameLauncherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WinePrefixCommand_RunsFromExecutableDirectory()
    {
        // Fake wine records the cwd it was started in; the real one maps that to the Windows cwd.
        var fakeWine = Path.Combine(_root, "fake wine");
        var output = Path.Combine(_root, "wine.out");
        File.WriteAllText(fakeWine, $"#!/bin/sh\nprintf '%s\\n' \"$(pwd)\" \"$WINEPREFIX\" > {Quote(output)}\n");
        File.SetUnixFileMode(fakeWine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (install, exe) = GameTree("Bin");
        var prefix = Path.Combine(_root, "pfx");
        var game = new DetectedGame
        {
            Name = "Fake", Source = "Wine", InstallPath = install, LaunchExecutablePath = exe,
            LaunchCommand = $"WINEPREFIX={Quote(prefix)} {Quote(fakeWine)} {Quote(exe)}"
        };

        await new LinuxGameLauncher().LaunchAsync(game);

        var lines = await ReadWhenWritten(output);
        Assert.Equal([Path.GetDirectoryName(exe)!, prefix], lines);
    }

    [Fact]
    public void DirectLaunches_UseExecutableDirectory_StoreHandoffsDoNot()
    {
        var (install, exe) = GameTree(Path.Combine("Binaries", "Win64"));
        var manual = new DetectedGame
        {
            Name = "Manual", Source = "Manual", InstallPath = install, LaunchExecutablePath = exe,
            LaunchCommand = $"WINEPREFIX=/tmp/pfx wine {Quote(exe)}"
        };
        var hdr = LinuxGameLauncher.BuildStartInfo(manual,
            new GameLaunchOptions(new Dictionary<string, string> { ["DXVK_HDR"] = "1" }, "gamescope --"));
        Assert.Equal(Path.GetDirectoryName(exe), hdr.WorkingDirectory);
        Assert.Equal("1", hdr.Environment["DXVK_HDR"]);

        manual.LaunchExecutablePath = Path.Combine(install, "missing", "Game.exe");
        Assert.Equal(install, LinuxGameLauncher.BuildStartInfo(manual, new GameLaunchOptions()).WorkingDirectory);

        (string Source, string Command)[] handoffs =
        [
            ("Steam", "steam -applaunch 1234"),
            ("Heroic/Epic", "xdg-open 'heroic://launch?appName=x&runner=legendary'"),
            ("Lutris", "lutris 'lutris:rungame/fake'"),
            ("Manual", "steam steam://rungameid/1234"),
            ("Manual", "LANG=C lutris lutris:rungame/fake")
        ];
        foreach (var (source, command) in handoffs)
        {
            var game = new DetectedGame
            {
                Name = "Store", Source = source, InstallPath = install, LaunchExecutablePath = exe, LaunchCommand = command
            };
            Assert.Equal(string.Empty, LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).WorkingDirectory);
        }
    }

    private (string Install, string Exe) GameTree(string binaryDirectory)
    {
        var install = Path.Combine(_root, "Fake Game");
        var bin = Path.Combine(install, binaryDirectory);
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllText(exe, "MZ");
        return (install, exe);
    }

    private static async Task<string[]> ReadWhenWritten(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length >= 2) return lines;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Fake wine was never started.");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
