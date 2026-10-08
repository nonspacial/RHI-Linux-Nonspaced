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

        // stderr is the launch log's FIFO (read by the launch shell's own capped reader), never a pipe of RHI's.
        var target = (await ReadWhenWritten(marker)).Trim();
        Assert.StartsWith(_tree.Path_("logs") + "/", target);
        Assert.Contains(".log.fifo", target);
        var log = Assert.Single(Directory.GetFiles(_tree.Path_("logs"), "*.log"));
        Assert.Contains("late", await ReadWhenWritten(log));
    }

    [Fact]
    public async Task TheLaunchLog_IsCappedWithANote_AndTheGamesWritesKeepSucceeding()
    {
        var marker = _tree.Path_("writes-ok");
        var game = new DetectedGame
        {
            Name = "Chatty", Source = "Wine", InstallPath = _tree.Dir("game"),
            LaunchCommand = $"yes 0123456789abcdef | head -c 3000000 >&2 && yes x | head -c 100000 >&2 && echo ok > '{marker}'"
        };

        await Launcher(TimeSpan.FromSeconds(20)).LaunchAsync(game);

        Assert.Equal("ok", (await ReadWhenWritten(marker)).Trim());
        var log = Assert.Single(Directory.GetFiles(_tree.Path_("logs"), "*.log"));
        var text = await File.ReadAllTextAsync(log);
        Assert.StartsWith("0123456789abcdef\n", text);
        Assert.InRange(new FileInfo(log).Length, LinuxGameLauncher.MaxLaunchLogBytes, LinuxGameLauncher.MaxLaunchLogBytes + 200);
        Assert.EndsWith("[RHI] stderr log capped at 1 MiB; later output was discarded.\n", text);
        Assert.Empty(Directory.GetFiles(_tree.Path_("logs"), "*.fifo"));
    }

    [Fact]
    public async Task AnEarlyFailure_ThatExitsOrExecsItself_StillReportsItsLastStderrLine()
    {
        var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() => Launcher(TimeSpan.FromSeconds(10)).LaunchAsync(
            new DetectedGame { Name = "G", Source = "Wine", InstallPath = _tree.Dir("game"), LaunchCommand = "echo 'wine: not found' >&2; exit 127" }));
        Assert.Equal((127, "wine: not found"), (failure.ExitCode, failure.Detail));

        var exec = await Assert.ThrowsAsync<GameLaunchFailedException>(() => Launcher(TimeSpan.FromSeconds(10)).LaunchAsync(
            new DetectedGame { Name = "G", Source = "Wine", InstallPath = _tree.Dir("game"), LaunchCommand = "exec sh -c 'echo last words >&2; exit 4'" }));
        Assert.Equal((4, "last words"), (exec.ExitCode, exec.Detail));
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
