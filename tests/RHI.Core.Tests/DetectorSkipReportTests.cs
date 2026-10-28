using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// Heroic and Wine-prefix detectors name what they skip, like Steam and Lutris since #56 (Refs #144): an unreadable
/// Heroic file or prefix folder is a "Heroic/Wine: N item(s) skipped" problem with details in rhi.log, not a game
/// that silently goes missing. Entries that read fine are still listed.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DetectorSkipReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-skip-report-" + Guid.NewGuid().ToString("N"));

    public DetectorSkipReportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Heroic_BrokenAndUnreadableFiles_AreReported_AndReadableGamesStillList()
    {
        var heroic = Path.Combine(_root, "heroic");
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "Games", "Good Game")).FullName;
        var exe = Path.Combine(gameDir, "Good.exe");
        File.WriteAllText(exe, "MZ");
        Write(Path.Combine(heroic, "sideload_apps", "library.json"), JsonSerializer.Serialize(new
        {
            games = new[] { new { runner = "sideload", app_name = "good", title = "Good Game", folder_name = gameDir, is_installed = true,
                install = new { executable = exe, platform = "Windows" } } }
        }));
        var broken = Write(Path.Combine(heroic, "gog_store", "installed.json"), "{\"installed\": [ {\"appName\": ");
        var locked = Write(Path.Combine(heroic, "GamesConfig", "good.json"), "{\"good\": {\"winePrefix\": \"/nowhere\"}}");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var scan = await new GameLibraryScanner([new HeroicGameDetector([heroic], [], [])]).ScanWithReportAsync(null);

        Assert.Equal(["Good Game"], scan.Games.Select(g => g.Name));
        var status = scan.Detectors.Single(d => d.Name == "Heroic");
        Assert.False(status.Failed);
        Assert.Contains(status.Warnings, w => w.Contains(broken, StringComparison.Ordinal));
        Assert.Contains(status.Warnings, w => w.Contains(locked, StringComparison.Ordinal));
        Assert.Contains("Heroic", LibraryScanStatusText.Problems(scan, "/state/rhi.log", scan.Games));
    }

    [Fact]
    public async Task Heroic_KeysDifferingOnlyInCase_DoNotDropTheFile()
    {
        // The old parser built a case-insensitive dictionary that threw on such keys; the bare catch hid the loss.
        var heroic = Path.Combine(_root, "heroic");
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "Games", "Case Game")).FullName;
        File.WriteAllText(Path.Combine(gameDir, "Case.exe"), "MZ");
        Write(Path.Combine(heroic, "sideload_apps", "library.json"),
            $$"""{"games":[{"runner":"sideload","app_name":"case","APP_NAME":"other","title":"Case Game","folder_name":{{JsonSerializer.Serialize(gameDir)}},"is_installed":true}]}""");

        var scan = await new GameLibraryScanner([new HeroicGameDetector([heroic], [], [])]).ScanWithReportAsync(null);

        Assert.Equal(["Case Game"], scan.Games.Select(g => g.Name));
        Assert.Empty(scan.Detectors.Single().Warnings);
    }

    [Fact]
    public void Heroic_UnexpectedError_IsReportedAsADefect_NotAsABrokenFile()
    {
        // w2-launch review, nit 12: the broad catch (one file never drops every Heroic game) reported a programming
        // error as "file skipped". Errors a bad file causes still read so; anything else names itself as a defect.
        foreach (var fileError in new Exception[]
                 {
                     new IOException("io"), new UnauthorizedAccessException("denied"), new JsonException("broken"),
                     Thrown(() => JsonDocument.Parse("1").RootElement.GetString()), // a JSON value of the wrong kind
                     Thrown(() => JsonDocument.Parse("1.5").RootElement.GetInt32()), new FormatException("not a number")
                 })
            Assert.Equal("Heroic file f skipped", HeroicGameDetector.SkipMessage("Heroic file f skipped", fileError));

        // w2-launch re-review, nit 5: InvalidOperationException outside the JSON reader and ArgumentException are the
        // usual LINQ/null-argument programming errors, not a bad file.
        foreach (var defectError in new Exception[]
                 {
                     new NullReferenceException(), Thrown(() => Array.Empty<int>().First()),
                     Thrown(() => ArgumentNullException.ThrowIfNull((object?)null, "path")), new ArgumentException("bad path")
                 })
        {
            var defect = HeroicGameDetector.SkipMessage("Heroic file f skipped", defectError);
            Assert.StartsWith($"Heroic file f skipped after an unexpected {defectError.GetType().Name}", defect, StringComparison.Ordinal);
            Assert.Contains("defect in RHI", defect, StringComparison.Ordinal);
        }

        static Exception Thrown(Action action) => Assert.ThrowsAny<Exception>(action);
    }

    [Fact]
    public async Task Wine_UnreadableFolderInAPrefix_IsReportedOnce_AndTheOtherGamesStillList()
    {
        var home = Path.Combine(_root, "home");
        var prefix = Prefix(Path.Combine(home, "Games", "standalone"));
        Exe(prefix, "Program Files/Free Game/FreeGame.exe");
        var locked = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "Program Files", "Locked Vendor")).FullName;
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var scan = await new GameLibraryScanner([new WinePrefixDetector(home, [])]).ScanWithReportAsync(null);

        Assert.Equal(["Free Game"], scan.Games.Select(g => g.Name));
        var warning = Assert.Single(scan.Detectors.Single(d => d.Name == "Wine").Warnings);
        Assert.Contains(locked, warning, StringComparison.Ordinal);
        Assert.Contains("Wine", LibraryScanStatusText.Problems(scan, null, scan.Games));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("dangling")]
    public async Task Wine_UnreadableRuntimeMarker_KeepsTheGamesListed_WithoutALaunchCommand(string marker)
    {
        // An unreadable config_info used to read as "no markers" = plain Wine, launched with host `wine`, which runs
        // wineboot over a Proton prefix. Skipping the prefix instead made its games vanish from the library (w2-launch
        // review, major 2): they stay listed and moddable, with no launch command, and the warning names the prefix.
        // A dangling config_info symlink exists for File.Exists but cannot be read: same case.
        var home = Path.Combine(_root, "home");
        var prefix = Prefix(Path.Combine(home, "Games", "proton-built"));
        Exe(prefix, "Program Files/Game/Game.exe");
        Write(Path.Combine(prefix, "version"), "GE-Proton10-32\n");
        var configInfo = Path.Combine(prefix, "config_info");
        if (marker == "dangling") File.CreateSymbolicLink(configInfo, Path.Combine(_root, "gone", "config_info"));
        else File.SetUnixFileMode(Write(configInfo, "GE-Proton10-32\n/opt/ge/files/share/fonts/\n"), UnixFileMode.None);

        var log = new AppLog(Path.Combine(_root, "rhi.log"));
        GameLibraryScanResult scan;
        using (AppLog.UseInThisFlow(log))
            scan = await new GameLibraryScanner([new WinePrefixDetector(home, [])]).ScanWithReportAsync(null);

        var game = Assert.Single(scan.Games);
        Assert.Equal("Game", game.Name);
        Assert.Equal(prefix, game.PrefixPath);
        Assert.Null(game.LaunchCommand);
        Assert.Null(game.WineBinaryPath);
        Assert.False(new RHI.Platform.Linux.Launch.LinuxGameLauncher().CanLaunch(game));
        // Nor any other host-Wine route (w2-launch re-review, major 1): the Winetricks button and the automatic
        // D3DCompiler step refuse it (RuntimeUnknownPrefixTests runs them).
        Assert.False(RHI.Platform.Linux.Deployment.D3DCompilerToolInstaller.CanInstallWithWinetricks(game));
        // w12 follow-up (merge review nit 1): the game is listed, so the scan counts it as listed without a launch command
        // (its own line, naming Edit game), never as a skipped item; the log line still names the prefix (w12 final review
        // nit 5: asserted again, through this test's own log).
        var status = scan.Detectors.Single();
        Assert.Empty(status.Warnings);
        Assert.Equal([PathIdentity.Canonicalize(game.InstallPath)], status.ListedWithoutLaunchCommand);
        Assert.True(scan.HasProblems);
        var logged = File.ReadAllLines(log.FilePath).Where(line => line.Contains("runtime markers unreadable", StringComparison.Ordinal)).ToArray();
        Assert.Contains(logged, line => line.Contains(prefix, StringComparison.Ordinal));
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Prefix(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "drive_c", "windows"));
        File.WriteAllText(Path.Combine(path, "system.reg"), "");
        return path;
    }

    private static void Exe(string prefix, string relative)
    {
        var path = Path.Combine(prefix, "drive_c", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64]);
    }

    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Prepend(_root))
        {
            try { File.SetUnixFileMode(path, Directory.Exists(path) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        // Locked folders are not enumerated above; open them explicitly before deleting.
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
