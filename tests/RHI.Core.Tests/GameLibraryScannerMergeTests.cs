using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>DET-05: one launcher's distinct entries must never collapse into a single card.</summary>
public sealed class GameLibraryScannerMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-scanner-merge-" + Guid.NewGuid().ToString("N"));

    public GameLibraryScannerMergeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FixedDetector(string name, params DetectedGame[] games) : IGameDetector
    {
        public string Name => name;
        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedGame>>(games);
    }

    private static DetectedGame Game(string name, string source, string path, string? identity = null)
        => new() { Name = name, Source = source, InstallPath = path, LibraryIdentityPath = identity };

    [Fact]
    public async Task SameSourceDistinctIdentitiesAtOnePath_StaySeparate()
    {
        var lutris = new FixedDetector("Lutris",
            Game("Blue Prince", "Lutris", _root, "/cfg/blue-prince-1.yml"),
            Game("Rekordbox", "Lutris", _root, "/cfg/rekordbox-setup-2.yml"));

        var games = await new GameLibraryScanner([lutris]).ScanAsync();

        Assert.Equal(["Blue Prince", "Rekordbox"], games.Select(g => g.Name).ToArray());
    }

    [Fact]
    public async Task SamePathAcrossSourcesOrWithoutIdentity_StillMerges()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var lutris = new FixedDetector("Lutris",
            Game("Battle.net", "Lutris", _root),
            Game("battle.net", "Lutris", _root),
            Game("Game", "Lutris", other, "/cfg/game.yml"));
        var wine = new FixedDetector("Wine", Game("Game (Wine)", "Wine", other));

        var games = await new GameLibraryScanner([lutris, wine]).ScanAsync();

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Name == "Battle.net" && g.InstallPath == _root);
        Assert.Contains(games, g => g.Name == "Game" && g.Source == "Lutris" && g.InstallPath == other);
    }
}
