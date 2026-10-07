using System.Runtime.Versioning;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

/// <summary>
/// Launch environment and stderr handling (w1/detect review findings 1 and 4): a command the user typed
/// finds wrappers in ~/.local/bin and ~/bin and keeps what ~/.profile exports, even when RHI was started
/// from the desktop menu with a PATH that lacks them; the game's stderr is a per-launch file, never a pipe.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class UserLaunchCommandTests : IDisposable
{
    private readonly DetectionTestTree _tree = new();

    // What plasmashell hands a menu-started app on the owner's machine: no ~/.local/bin.
    private const string SessionPath = "/usr/local/bin:/usr/bin:/bin";

    private DetectedGame Manual(string command) => new()
    {
        Name = "Wrapper Game", Source = "Manual", IsManuallyAdded = true, InstallPath = _tree.Dir("game"), LaunchCommand = command
    };

    private GameLaunchOptions SessionOptions(string home)
        => new(new Dictionary<string, string> { ["HOME"] = home, ["PATH"] = SessionPath });

    private LinuxGameLauncher Launcher(TimeSpan window) => new(window, _tree.Dir("logs"));

    private string Script(string body, params string[] parts)
    {
        var path = _tree.File("#!/bin/sh\n" + body + "\n", parts);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Theory]
    [InlineData(".local/bin")]
    [InlineData("bin")]
    public async Task BareNamedWrapperInTheUsersBinFolder_RunsWithASessionPathThatLacksIt(string binFolder)
    {
        var home = _tree.Dir("home");
        var marker = _tree.Path_("wrapper-ran");
        Script($"echo ok > '{marker}'", ["home", .. binFolder.Split('/'), "rhi-fake-wrapper"]);

        await Launcher(TimeSpan.FromSeconds(20)).LaunchAsync(Manual("rhi-fake-wrapper"), SessionOptions(home));

        Assert.Equal("ok", (await ReadWhenWritten(marker)).Trim());
    }

    [Fact]
    public async Task UserCommand_KeepsWhatTheProfileExports()
    {
        var home = _tree.Dir("home");
        _tree.File("export RHI_PROFILE_MARK=from-profile\n", "home", ".profile");
        var marker = _tree.Path_("profile-mark");

        await Launcher(TimeSpan.FromSeconds(20)).LaunchAsync(Manual($"printf '%s' \"$RHI_PROFILE_MARK\" > '{marker}'"), SessionOptions(home));

        Assert.Equal("from-profile", await ReadWhenWritten(marker));
    }

    [Fact]
    public void UserBinFolders_AreAddedOnlyWhenTheyExistAndAreMissing()
    {
        var home = _tree.Dir("home");
        var local = _tree.Dir("home", ".local", "bin");

        var start = LinuxGameLauncher.BuildStartInfo(Manual("x"), SessionOptions(home));
        Assert.Equal($"{local}:{SessionPath}", start.Environment["PATH"]);

        var already = new GameLaunchOptions(new Dictionary<string, string> { ["HOME"] = home, ["PATH"] = $"/usr/bin:{local}/" });
        Assert.Equal($"/usr/bin:{local}/", LinuxGameLauncher.BuildStartInfo(Manual("x"), already).Environment["PATH"]);

        var steam = new DetectedGame { Name = "S", Source = "Steam", InstallPath = home, LaunchCommand = "steam -applaunch 1" };
        Assert.Equal(SessionPath, LinuxGameLauncher.BuildStartInfo(steam, SessionOptions(home)).Environment["PATH"]);
    }

    [Fact]
    public async Task AGameStillRunningAfterTheWindow_WritesStderrToItsLogFile_NotToAPipe()
    {
        var marker = _tree.Path_("stderr-target");
        var game = new DetectedGame
        {
            Name = "Long Runner", Source = "Wine", InstallPath = _tree.Dir("game"),
            LaunchCommand = $"sleep 1; echo late >&2; readlink /proc/$$/fd/2 > '{marker}'"
        };

        await Launcher(TimeSpan.FromMilliseconds(200)).LaunchAsync(game);

        var target = (await ReadWhenWritten(marker)).Trim();
        Assert.StartsWith(_tree.Path_("logs") + "/", target);
        Assert.EndsWith(".log", target);
        Assert.Contains("late", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task OldLaunchLogs_ArePruned()
    {
        var logs = _tree.Dir("logs");
        for (var i = 0; i < 30; i++) _tree.File("old", "logs", $"20000101-0000{i:00}-000-old.log");
        var keep = _tree.File("not a log", "logs", "notes.txt");

        await Launcher(TimeSpan.FromSeconds(10)).LaunchAsync(new DetectedGame { Name = "G", Source = "Wine", InstallPath = logs, LaunchCommand = "true" });

        var remaining = Directory.GetFiles(logs, "*.log");
        Assert.Equal(LinuxGameLauncher.KeptLaunchLogs, remaining.Length);
        Assert.DoesNotContain(remaining, f => Path.GetFileName(f) == "20000101-000000-000-old.log");
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task LaunchLogProblems_AreWrittenToTheAppLog()
    {
        // Trace output is invisible in Release (integration w1 detect review 5).
        var log = new AppLog(_tree.Path_("state", "rhi.log"));
        var notADirectory = _tree.File("x", "logs-is-a-file");
        using (AppLog.UseInThisFlow(log))
        {
            await new LinuxGameLauncher(TimeSpan.FromSeconds(10), notADirectory)
                .LaunchAsync(new DetectedGame { Name = "G", Source = "Wine", InstallPath = _tree.Dir("game"), LaunchCommand = "true" });

            var logs = _tree.Dir("logs");
            var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() => Launcher(TimeSpan.FromSeconds(10)).LaunchAsync(
                new DetectedGame { Name = "G", Source = "Wine", InstallPath = _tree.Dir("game"), LaunchCommand = $"echo boom >&2; chmod 000 '{logs}'/*.log; exit 3" }));
            Assert.Null(failure.Detail);
        }

        var text = await File.ReadAllTextAsync(log.FilePath);
        Assert.Contains($"[launch] no stderr log in {notADirectory}", text);
        Assert.Contains("unreadable", text);
    }

    private static async Task<string> ReadWhenWritten(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                if (text.Length > 0) return text;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"{path} was never written.");
    }

    public void Dispose() => _tree.Dispose();
}
