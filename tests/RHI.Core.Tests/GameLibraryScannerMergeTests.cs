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
            Game("Second Game", "Lutris", _root, "/cfg/second-game-2.yml"));

        var games = await new GameLibraryScanner([lutris]).ScanAsync();

        Assert.Equal(["Blue Prince", "Second Game"], games.Select(g => g.Name).ToArray());
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

    [Fact]
    public async Task Merge_KeepsAFailClosedPatchTargetVerdictFromAnyDuplicate()
    {
        var wine = new FixedDetector("Wine", Game("Game", "Wine", _root));
        var flagged = Game("Game", "Lutris", _root);
        flagged.RequiresExplicitPatchTarget = true;
        var lutris = new FixedDetector("Lutris", flagged);

        var merged = Assert.Single(await new GameLibraryScanner([wine, lutris]).ScanAsync());

        Assert.Equal("Wine", merged.Source);
        Assert.True(merged.RequiresExplicitPatchTarget);
    }

    [Fact]
    public async Task ManualDuplicateOfALauncherEntry_StaysRemovableAndKeepsTheChosenExe()
    {
        var manualExe = Path.Combine(_root, "bin", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(manualExe)!);
        var heroic = Game("Game", "Heroic", _root);
        heroic.ExecutablePath = Path.Combine(_root, "Launcher.exe");
        heroic.HeroicAppName = "abc123";
        var manual = Game("My Game", "Manual", _root + Path.DirectorySeparatorChar);
        manual.IsManuallyAdded = true;
        manual.ExecutablePath = manualExe;
        manual.BinaryDirectory = Path.GetDirectoryName(manualExe);
        manual.UserGraphicsApiOverride = GraphicsApi.Vulkan;

        var merged = Assert.Single(await new GameLibraryScanner([new FixedDetector("Heroic", heroic), new FixedDetector("Manual", manual)]).ScanAsync());

        Assert.True(merged.IsManuallyAdded);
        Assert.Equal(manualExe, merged.ExecutablePath);
        Assert.Equal(Path.GetDirectoryName(manualExe), merged.BinaryDirectory);
        Assert.Equal(GraphicsApi.Vulkan, merged.UserGraphicsApiOverride);
        Assert.Equal(("Game", "Heroic", "abc123"), (merged.Name, merged.Source, merged.HeroicAppName));
    }

    [Fact]
    public async Task OneFolderThroughASymlink_IsOneCard_ButCaseVariantsAreTwoFolders()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "media", "Games", "Game")).FullName;
        var link = Path.Combine(_root, "home-games");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(real)!);
        var lower = Directory.CreateDirectory(Path.Combine(_root, "media", "Games", "game")).FullName;

        var games = await new GameLibraryScanner(
        [
            new FixedDetector("Heroic", Game("Game", "Heroic", Path.Combine(link, "Game"))),
            new FixedDetector("Wine", Game("Game", "Wine", real), Game("game", "Wine", lower))
        ]).ScanAsync();

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Source == "Heroic");
        Assert.Contains(games, g => g.InstallPath == lower);
    }

    [Fact]
    public void Merge_FillsEverySettableFieldADuplicateKnows()
    {
        // Identity and per-session state are owned by the first entry or applied after the scan.
        string[] owned = [nameof(DetectedGame.Name), nameof(DetectedGame.InstallPath), nameof(DetectedGame.Source),
            nameof(DetectedGame.IsFavourite), nameof(DetectedGame.IsHidden)];
        var properties = typeof(DetectedGame).GetProperties().Where(p => p.CanWrite && !owned.Contains(p.Name)).ToArray();
        var full = new DetectedGame();
        foreach (var property in properties) property.SetValue(full, Sample(property.PropertyType));
        var empty = new DetectedGame();

        var merged = GameLibraryScanner.Merge([empty, full]);

        var lost = properties.Where(p => Equals(p.GetValue(merged), p.GetValue(new DetectedGame()))).Select(p => p.Name).ToArray();
        Assert.Empty(lost);
    }

    [Fact]
    public void RemoveManualEntriesFor_MatchesTheFolderHoweverItIsSpelt()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "media", "X")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "media"));
        var state = new RHI.Platform.Linux.Settings.LinuxGameLibraryState
        {
            ManualGames = [new() { Name = "X", InstallPath = Path.Combine(link, "X") + "/" }, new() { Name = "Y", InstallPath = _root }]
        };

        var removed = state.RemoveManualEntriesFor(new DetectedGame { Name = "X", InstallPath = real, IsManuallyAdded = true });

        Assert.Equal(1, removed);
        Assert.Equal("Y", Assert.Single(state.ManualGames).Name);
    }

    private static object? Sample(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "x";
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 7;
        if (t == typeof(GraphicsApi)) return GraphicsApi.DirectX11;
        if (t == typeof(GraphicsApiConfidence)) return GraphicsApiConfidence.Medium;
        if (t == typeof(PeArchitecture)) return PeArchitecture.X64;
        if (t == typeof(IReadOnlyList<GraphicsApi>)) return new[] { GraphicsApi.DirectX11 };
        if (t == typeof(IReadOnlyDictionary<string, string>)) return new Dictionary<string, string> { ["k"] = "v" };
        throw new InvalidOperationException($"Add a sample for {type}");
    }
}
