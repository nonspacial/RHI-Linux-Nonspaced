using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-library-test-" + Guid.NewGuid().ToString("N"));

    public GameLibraryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LibraryStore_MissingFileUsesSafeDefaults()
    {
        var state = NewStore().Load();
        Assert.Equal("All Games", state.FilterMode);
        Assert.Empty(state.FavouriteGames);
        Assert.Empty(state.HiddenGames);
        Assert.Empty(state.ManualGames);
    }

    [Fact]
    public void LibraryStore_SaveLoadRoundTripsVisibilityFilterAndManualGame()
    {
        var store = NewStore();
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "manual")).FullName;
        var exe = Path.Combine(gameDir, "demo.exe");
        File.WriteAllText(exe, "MZ");
        var state = new LinuxGameLibraryState { FilterMode = "Favourites" };
        state.FavouriteGames.Add("one");
        state.HiddenGames.Add("two");
        state.ManualGames.Add(new LinuxManualGameEntry
        {
            Name = " Demo ", InstallPath = gameDir, ExecutablePath = exe, PrefixPath = Path.Combine(_root, "pfx"), LaunchCommand = "wine demo.exe"
        });
        store.Save(state);

        var loaded = store.Load();
        Assert.Equal("Favourites", loaded.FilterMode);
        Assert.Contains(loaded.FavouriteGames, value => value.Equals("ONE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.HiddenGames, value => value.Equals("TWO", StringComparison.OrdinalIgnoreCase));
        var manual = Assert.Single(loaded.ManualGames);
        Assert.Equal("Demo", manual.Name);
        Assert.Equal(Path.GetFullPath(gameDir), manual.InstallPath);
        Assert.Equal("wine demo.exe", manual.LaunchCommand);
    }

    [Fact]
    public void LibraryStore_CorruptJsonFailsClosedToEmptyLibraryState()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.LibraryPath)!);
        File.WriteAllText(store.LibraryPath, "{broken");
        var state = store.Load();
        Assert.Equal("All Games", state.FilterMode);
        Assert.Empty(state.ManualGames);
    }

    [Theory]
    [InlineData("All Games", "All Games")]
    [InlineData("Favourites", "Favourites")]
    [InlineData("Installed", "Installed")]
    [InlineData("Unreal", "Unreal")]
    [InlineData("Unity", "Unity")]
    [InlineData("Other", "Other")]
    [InlineData("RenoDX", "RenoDX")]
    [InlineData("Luma", "Luma")]
    [InlineData("Hidden", "Hidden")]
    [InlineData("nonsense", "All Games")]
    public void LibraryStore_NormalizesKnownFilterModes(string input, string expected)
        => Assert.Equal(expected, LinuxGameLibraryStore.NormalizeFilterMode(input));

    [Fact]
    public async Task ManualDetector_ReturnsFirstClassManualGameWithRuntimeMetadata()
    {
        var store = NewStore();
        var gameDir = Directory.CreateDirectory(Path.Combine(_root, "detected")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(_root, "prefix")).FullName;
        var exe = Path.Combine(gameDir, "game.exe");
        File.WriteAllText(exe, "MZ");
        store.Save(new LinuxGameLibraryState
        {
            ManualGames =
            [
                new LinuxManualGameEntry
                {
                    Name = "Manual Demo", InstallPath = gameDir, ExecutablePath = exe, PrefixPath = prefix,
                    LaunchCommand = "custom-wine game.exe", Runner = "Manual Wine"
                }
            ]
        });

        var result = await new ManualGameDetector(store).DetectAsync();
        var game = Assert.Single(result);
        Assert.True(game.IsManuallyAdded);
        Assert.Equal("Manual", game.Source);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Equal(gameDir, game.BinaryDirectory);
        Assert.Equal(prefix, game.PrefixPath);
        Assert.Equal("wine", game.CompatibilityLayerType);
        Assert.Equal("custom-wine game.exe", game.LaunchCommand);
    }

    [Fact]
    public async Task ManualDetector_ListsAMissingInstallDirectory_AsFolderMissing_WithNoTarget()
    {
        // Never hide the user's saved entry (Refs #145): listed, no patch target, nothing to index.
        var store = NewStore();
        store.Save(new LinuxGameLibraryState
        {
            ManualGames = [new LinuxManualGameEntry { Name = "Gone", InstallPath = Path.Combine(_root, "missing"), ExecutablePath = Path.Combine(_root, "missing", "Gone.exe") }]
        });

        var gone = Assert.Single(await new ManualGameDetector(store).DetectAsync());

        Assert.True(gone.FolderMissing);
        Assert.False(gone.HasConfirmedPatchTarget);
        Assert.Null(gone.BinaryDirectory);
        var listed = Assert.Single(await new GameLibraryScanner([new ManualGameDetector(store)]).ScanAsync());
        Assert.True(listed.FolderMissing);
    }

    [Fact]
    public void LibraryKey_DistinguishesManifestSplitChildrenSharingSteamAppId()
    {
        var a = Game(Path.Combine(_root, "bundle", "A"), "Child A");
        var b = Game(Path.Combine(_root, "bundle", "B"), "Child B");
        Directory.CreateDirectory(a.InstallPath);
        Directory.CreateDirectory(b.InstallPath);
        a.SteamAppId = b.SteamAppId = 123;
        Assert.NotEqual(LinuxGameLibraryStore.GameKey(a), LinuxGameLibraryStore.GameKey(b));
    }

    [Fact]
    public void Filter_AllGamesHidesHiddenButFavouritesIncludesHiddenFavourite()
    {
        var game = Game(_root, "Demo");
        game.IsHidden = true;
        game.IsFavourite = true;
        Assert.False(GameLibraryFilter.Matches(game, GameLibraryFilterMode.AllGames, "", default));
        Assert.True(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Favourites, "", default));
        Assert.True(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Hidden, "", default));
    }

    [Fact]
    public void Filter_InstalledUsesExplicitFactAndStillRespectsHidden()
    {
        var game = Game(_root, "Demo");
        Assert.True(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Installed, "", new(true, false, false)));
        Assert.False(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Installed, "", new(false, false, false)));
        game.IsHidden = true;
        Assert.False(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Installed, "", new(true, false, false)));
    }

    [Fact]
    public void Filter_EngineModesSeparateUnrealUnityAndOther()
    {
        var unreal = Game(_root, "UE"); unreal.EngineHint = "Unreal Engine 5";
        var unity = Game(_root, "Unity"); unity.EngineHint = "Unity";
        var other = Game(_root, "Other"); other.EngineHint = "RE Engine";
        Assert.True(GameLibraryFilter.Matches(unreal, GameLibraryFilterMode.Unreal, "", default));
        Assert.True(GameLibraryFilter.Matches(unity, GameLibraryFilterMode.Unity, "", default));
        Assert.True(GameLibraryFilter.Matches(other, GameLibraryFilterMode.Other, "", default));
        Assert.False(GameLibraryFilter.Matches(unreal, GameLibraryFilterMode.Other, "", default));
    }

    [Fact]
    public void Filter_RenoDxAndLumaUseCapabilityFacts()
    {
        var game = Game(_root, "Demo");
        Assert.True(GameLibraryFilter.Matches(game, GameLibraryFilterMode.RenoDX, "", new(false, true, false)));
        Assert.True(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Luma, "", new(false, false, true)));
        Assert.False(GameLibraryFilter.Matches(game, GameLibraryFilterMode.Luma, "", new(false, true, false)));
    }

    [Fact]
    public void UniversalSearchMatchesNameStoreEngineApiRunnerPathsAndManualMarker()
    {
        var game = Game(Path.Combine(_root, "MyFolder"), "Expedition");
        game.Source = "Heroic GOG";
        game.Runner = "Wine-GE";
        game.EngineHint = "Unreal Engine 5";
        game.GraphicsApi = GraphicsApi.DirectX12;
        game.GraphicsApis = [GraphicsApi.DirectX12, GraphicsApi.Vulkan];
        game.IsManuallyAdded = true;
        Assert.True(GameLibraryFilter.MatchesSearch(game, "exped"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "GOG"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "Unreal"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "DX12"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "Vulkan"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "Wine-GE"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "MyFolder"));
        Assert.True(GameLibraryFilter.MatchesSearch(game, "Manual"));
        Assert.False(GameLibraryFilter.MatchesSearch(game, "definitely-no-match"));
    }

    private LinuxGameLibraryStore NewStore() => new(Path.Combine(_root, "game-library.json"));

    private static DetectedGame Game(string path, string name) => new()
    {
        Name = name,
        InstallPath = path,
        Source = "Manual"
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
