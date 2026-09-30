using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class GameLibraryScannerPartialRescanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-scanner-test-" + Guid.NewGuid().ToString("N"));

    public GameLibraryScannerPartialRescanTests() => Directory.CreateDirectory(_root);

    private sealed class CountingDetector(string name, Func<IReadOnlyList<DetectedGame>> produce) : IGameDetector
    {
        public int Calls;
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(produce());
        }
    }

    private DetectedGame Game(string name, string source)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, source, name)).FullName;
        return new DetectedGame { Name = name, InstallPath = dir, Source = source };
    }

    [Fact]
    public async Task PartialRescan_ReRunsOnlyRequestedDetectorsAndReusesTheRest()
    {
        var steam = new CountingDetector("Steam", () => [Game("Alpha", "Steam")]);
        var manualCount = 0;
        var manual = new CountingDetector("Manual", () =>
            manualCount++ == 0 ? [] : [Game("Added", "Manual")]);
        var scanner = new GameLibraryScanner([steam, manual]);

        var first = await scanner.ScanAsync();
        Assert.Single(first);
        Assert.Equal(1, steam.Calls);
        Assert.Equal(1, manual.Calls);

        // Mutating the returned instance must not poison the cached snapshot.
        first[0].Name = "Mutated";

        var second = await scanner.ScanAsync(new HashSet<string> { "Manual" });
        Assert.Equal(1, steam.Calls);
        Assert.Equal(2, manual.Calls);
        Assert.Equal(2, second.Count);
        Assert.Contains(second, g => g.Name == "Alpha" && g.Source == "Steam");
        Assert.Contains(second, g => g.Name == "Added" && g.Source == "Manual");

        // No detectors requested: everything reused.
        var third = await scanner.ScanAsync(new HashSet<string>());
        Assert.Equal(1, steam.Calls);
        Assert.Equal(2, manual.Calls);
        Assert.Equal(2, third.Count);

        // Full scan re-runs everything.
        await scanner.ScanAsync();
        Assert.Equal(2, steam.Calls);
        Assert.Equal(3, manual.Calls);
    }

    [Fact]
    public async Task PartialRescan_WithoutPriorScanFallsBackToFullScan()
    {
        var steam = new CountingDetector("Steam", () => [Game("Alpha", "Steam")]);
        var manual = new CountingDetector("Manual", () => []);
        var scanner = new GameLibraryScanner([steam, manual]);

        await scanner.ScanAsync(new HashSet<string> { "Manual" });
        Assert.Equal(1, steam.Calls);
        Assert.Equal(1, manual.Calls);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
