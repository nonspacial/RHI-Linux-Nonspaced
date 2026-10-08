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
        Assert.Contains("Heroic", LibraryScanStatusText.Problems(scan, "/state/rhi.log"));
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
        Assert.Contains("Wine", LibraryScanStatusText.Problems(scan, null));
    }

    [Fact]
    public async Task Wine_UnreadableRuntimeMarker_SkipsThePrefix_InsteadOfLaunchingItWithHostWine()
    {
        // An unreadable config_info used to read as "no markers" = plain Wine, launched with host `wine`, which runs
        // wineboot over a Proton prefix.
        var home = Path.Combine(_root, "home");
        var prefix = Prefix(Path.Combine(home, "Games", "proton-built"));
        Exe(prefix, "Program Files/Game/Game.exe");
        var marker = Write(Path.Combine(prefix, "config_info"), "GE-Proton10-32\n/opt/ge/files/share/fonts/\n");
        File.SetUnixFileMode(marker, UnixFileMode.None);

        var scan = await new GameLibraryScanner([new WinePrefixDetector(home, [])]).ScanWithReportAsync(null);

        Assert.Empty(scan.Games);
        Assert.Contains(prefix, Assert.Single(scan.Detectors.Single().Warnings), StringComparison.Ordinal);
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
