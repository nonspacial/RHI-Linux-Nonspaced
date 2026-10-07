using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>Detector and policy failures are reported, not passed off as "no games" (Refs #56).</summary>
public sealed class LibraryScanReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-scan-report-" + Guid.NewGuid().ToString("N"));

    public LibraryScanReportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ThrowingDetector_IsReportedAsFailed_AndTheOtherSourcesStillScan()
    {
        var scanner = new GameLibraryScanner([new Throwing("Steam"), new Fixed("Heroic", Game("Game", "Heroic"))]);

        var scan = await scanner.ScanWithReportAsync(null);

        Assert.Single(scan.Games);
        var steam = scan.Detectors.Single(d => d.Name == "Steam");
        Assert.True(steam.Failed);
        Assert.Equal("Permission denied", steam.Error);
        Assert.True(scan.HasProblems);
        var status = LibraryScanStatusText.Format(scan, scan.Games.Count, "/state/rhi.log");
        Assert.Contains("Steam", status);
        Assert.Contains("Permission denied", status);
        Assert.Contains("/state/rhi.log", status);
    }

    [Fact]
    public async Task PartialRefresh_KeepsReportingTheEarlierFailure()
    {
        var scanner = new GameLibraryScanner([new Throwing("Steam"), new Fixed("Manual", Game("Game", "Manual"))]);
        await scanner.ScanWithReportAsync(null);

        var partial = await scanner.ScanWithReportAsync(new HashSet<string> { "Manual" });

        var steam = partial.Detectors.Single(d => d.Name == "Steam");
        Assert.True(steam.Failed);
        Assert.True(steam.Reused);
    }

    [Fact]
    public async Task PolicyFailure_IsReported_AndTheLibraryStaysAsDetected()
    {
        var scanner = new GameLibraryScanner([new Fixed("Heroic", Game("Game", "Heroic"))], [new ThrowingPolicy()]);

        var scan = await scanner.ScanWithReportAsync(null);

        Assert.Single(scan.Games);
        Assert.Equal(("Broken policy", "offline"), (scan.PolicyFailures.Single().Name, scan.PolicyFailures.Single().Error));
        Assert.Contains("Broken policy", LibraryScanStatusText.Format(scan, 1, null));
    }

    [Fact]
    public async Task CleanScan_KeepsThePlainStatusLine()
    {
        var scan = await new GameLibraryScanner([new Fixed("Heroic", Game("Game", "Heroic"))]).ScanWithReportAsync(null);

        Assert.False(scan.HasProblems);
        Assert.DoesNotContain("·", LibraryScanStatusText.Format(scan, 1, "/state/rhi.log"));
    }

    [Fact]
    public async Task UnreadableSteamLibrary_IsSkippedWithAWarning_AndOtherLibrariesStillList()
    {
        if (!OperatingSystem.IsLinux()) return; // permission bits are the fixture
        var steamRoot = Directory.CreateDirectory(Path.Combine(_root, "Steam")).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(_root, "locked", "steamapps")).FullName;
        var good = Path.Combine(_root, "good");
        Directory.CreateDirectory(Path.Combine(good, "steamapps", "common", "Good Game"));
        File.WriteAllText(Path.Combine(good, "steamapps", "appmanifest_42.acf"), "\"AppState\"\n{\n\t\"name\"\t\t\"Good Game\"\n\t\"installdir\"\t\t\"Good Game\"\n}\n");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{Path.Combine(_root, "locked")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{good}\"\n\t}}\n}}\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (games, warnings) = await DetectorDiagnostics.CollectAsync(() => Task.FromResult(SteamGameDetector.DetectIn([steamRoot], CancellationToken.None)));

            Assert.Equal("Good Game", Assert.Single(games).Name);
            Assert.Contains(warnings, w => w.Contains(Path.Combine(_root, "locked"), StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Failures_AreWrittenToTheAppLog_WhichRotates()
    {
        var log = new AppLog(Path.Combine(_root, "state", "rhi-linux", "rhi.log"));
        var previous = AppLog.Current;
        AppLog.Current = log;
        try
        {
            await new GameLibraryScanner([new Throwing("Lutris")]).ScanWithReportAsync(null);
        }
        finally
        {
            AppLog.Current = previous;
        }

        var text = await File.ReadAllTextAsync(log.FilePath);
        Assert.Contains("Lutris detector failed", text);
        Assert.Contains("Permission denied", text);

        await File.WriteAllTextAsync(log.FilePath, new string('x', 1024 * 1024 + 1));
        log.Write("test", "after rotation");
        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.Contains("after rotation", await File.ReadAllTextAsync(log.FilePath));
    }

    private DetectedGame Game(string name, string source)
        => new() { Name = name, Source = source, InstallPath = _root };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Fixed(string name, params DetectedGame[] games) : IGameDetector
    {
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games.Select(g => g.ShallowClone()).ToArray());
    }

    private sealed class Throwing(string name) : IGameDetector
    {
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Permission denied");
    }

    private sealed class ThrowingPolicy : IGameLibraryPolicy
    {
        public string Name => "Broken policy";
        public Task<IReadOnlyList<DetectedGame>> ApplyAsync(IReadOnlyList<DetectedGame> games, CancellationToken cancellationToken = default)
            => throw new InvalidDataException("offline");
    }
}
