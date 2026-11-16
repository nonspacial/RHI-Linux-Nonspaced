using Avalonia.Controls;
using Avalonia.LogicalTree;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #300 (upstream 2.9.2 "Game collections"): the collection rules (ported from upstream's <c>GameCollectionTests</c>),
/// the library file, the list filter, and the shell: chips above the list, Add to collection in the game menu, rename,
/// move, delete (never the games), a filter tab leaves the collection, the active one is remembered.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GameCollectionsTests : IDisposable
{
    private const string Forza = "steam:1551360";
    private const string Dirt = "steam:690790";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-collections-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static List<LinuxGameCollection> With(params string[] names)
    {
        var list = new List<LinuxGameCollection>();
        foreach (var name in names) Assert.Equal(CollectionNameProblem.None, LinuxGameCollections.Create(list, name, null, out _));
        return list;
    }

    // ---- rules (upstream GameCollectionTests) ------------------------------------------------------------------

    [Fact]
    public void Membership_AddRemove_AnyCase()
    {
        var list = With("Racing");
        Assert.True(LinuxGameCollections.SetMembership(list, "Racing", Forza, true));
        Assert.False(LinuxGameCollections.SetMembership(list, "Racing", Forza, true)); // already in
        Assert.True(LinuxGameCollections.Contains(list, "racing", Forza.ToUpperInvariant()));
        Assert.True(LinuxGameCollections.SetMembership(list, "Racing", Dirt, true));
        Assert.Equal([Forza, Dirt], list[0].Games);
        Assert.True(LinuxGameCollections.SetMembership(list, "Racing", Forza, false));
        Assert.Equal([Dirt], list[0].Games);
        Assert.False(LinuxGameCollections.SetMembership(list, "Nope", Forza, true));
    }

    [Fact]
    public void Game_CanBeInSeveralCollections()
    {
        var list = With("Racing", "Favourite racers");
        LinuxGameCollections.SetMembership(list, "Racing", Forza, true);
        LinuxGameCollections.SetMembership(list, "Favourite racers", Forza, true);
        Assert.True(LinuxGameCollections.Contains(list, "Racing", Forza));
        Assert.True(LinuxGameCollections.Contains(list, "Favourite racers", Forza));
    }

    [Theory]
    [InlineData("", CollectionNameProblem.Empty)]
    [InlineData("   ", CollectionNameProblem.Empty)]
    [InlineData("Installed", CollectionNameProblem.BuiltInFilter)]
    [InlineData("favourites", CollectionNameProblem.BuiltInFilter)]
    [InlineData("All", CollectionNameProblem.BuiltInFilter)]
    [InlineData("Hidden", CollectionNameProblem.BuiltInFilter)]
    [InlineData("Detected", CollectionNameProblem.BuiltInFilter)]
    [InlineData("Unreal", CollectionNameProblem.BuiltInFilter)] // Linux: every filter tab's name
    [InlineData("Favoritos", CollectionNameProblem.BuiltInFilter)] // and its PT-BR word
    [InlineData("RACING", CollectionNameProblem.AlreadyUsed)]
    [InlineData("Tab\there", CollectionNameProblem.ControlCharacters)]
    [InlineData("Souls-likes", CollectionNameProblem.None)]
    [InlineData("  Co-op  ", CollectionNameProblem.None)]
    public void Validate_NewCollection(string name, CollectionNameProblem expected)
    {
        Assert.Equal(expected, LinuxGameCollections.Validate(With("Racing"), name, null, out var clean));
        if (expected == CollectionNameProblem.None) Assert.Equal(name.Trim(), clean);
    }

    [Fact]
    public void Validate_TooLong()
    {
        Assert.Equal(CollectionNameProblem.TooLong, LinuxGameCollections.Validate([], new string('a', LinuxGameCollections.MaxNameLength + 1), null, out _));
        Assert.Equal(CollectionNameProblem.None, LinuxGameCollections.Validate([], new string('a', LinuxGameCollections.MaxNameLength), null, out _));
    }

    [Fact]
    public void Rename_KeepsGames_AllowsCaseChange_RefusesAnotherCollectionsName()
    {
        var list = With("Racing", "RPG");
        LinuxGameCollections.SetMembership(list, "Racing", Forza, true);
        Assert.Equal(CollectionNameProblem.AlreadyUsed, LinuxGameCollections.Rename(list, "Racing", "rpg", out _));
        Assert.Equal(CollectionNameProblem.None, LinuxGameCollections.Rename(list, "Racing", "racing", out _));
        Assert.Equal(CollectionNameProblem.None, LinuxGameCollections.Rename(list, "racing", "Driving", out var renamed));
        Assert.Equal("Driving", renamed);
        Assert.True(LinuxGameCollections.Contains(list, "Driving", Forza));
        Assert.Null(LinuxGameCollections.Find(list, "Racing"));
        Assert.Equal(CollectionNameProblem.Missing, LinuxGameCollections.Rename(list, "Gone", "X", out _));
    }

    [Fact]
    public void Delete_RemovesOnlyTheCollection()
    {
        var list = With("Racing", "RPG");
        LinuxGameCollections.SetMembership(list, "Racing", Forza, true);
        LinuxGameCollections.SetMembership(list, "RPG", Forza, true);
        Assert.True(LinuxGameCollections.Delete(list, "racing"));
        Assert.Equal(["RPG"], list.Select(c => c.Name));
        Assert.True(LinuxGameCollections.Contains(list, "RPG", Forza));
        Assert.False(LinuxGameCollections.Delete(list, "Racing"));
    }

    [Fact]
    public void Move_ReordersAndClamps()
    {
        var list = With("A1", "B2", "C3");
        Assert.True(LinuxGameCollections.Move(list, "C3", -1));
        Assert.Equal(["A1", "C3", "B2"], list.Select(c => c.Name));
        Assert.False(LinuxGameCollections.Move(list, "A1", -1)); // already first
        Assert.True(LinuxGameCollections.Move(list, "A1", 5));
        Assert.Equal(["C3", "B2", "A1"], list.Select(c => c.Name));
    }

    [Fact]
    public void Normalize_DropsBadEntries_AndAnUnknownActive()
    {
        var state = new LinuxGameLibraryState
        {
            GameCollections =
            [
                new() { Name = " Racing ", Games = [Forza, Forza.ToUpperInvariant(), " "] },
                new() { Name = "racing", Games = [Dirt] }, // repeated name
                new() { Name = "Installed" },             // built-in name
                new() { Name = "  " }
            ],
            ActiveCollection = "Gone"
        }.Normalize();
        var racing = Assert.Single(state.GameCollections!);
        Assert.Equal("Racing", racing.Name);
        Assert.Equal([Forza], racing.Games);
        Assert.Null(state.ActiveCollection);

        state.ActiveCollection = "RACING";
        Assert.Equal("Racing", state.Normalize().ActiveCollection);
    }

    [Fact]
    public void LibraryFile_KeepsCollections_AndWritesNothingWithoutThem()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var state = store.Load();
        store.Save(state);
        Assert.DoesNotContain("GameCollections", File.ReadAllText(store.LibraryPath));
        Assert.DoesNotContain("ActiveCollection", File.ReadAllText(store.LibraryPath));

        state.GameCollections = With("Racing", "RPG");
        LinuxGameCollections.SetMembership(state.GameCollections, "Racing", Forza, true);
        state.ActiveCollection = "Racing";
        store.Save(state);

        var loaded = store.Load();
        Assert.Equal(["Racing", "RPG"], loaded.GameCollections!.Select(c => c.Name));
        Assert.Equal([Forza], loaded.GameCollections![0].Games);
        Assert.Equal("Racing", loaded.ActiveCollection);
    }

    [Fact]
    public void AGamesOlderKey_IsReplacedInItsCollections_InPlace()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", "tunic")).FullName;
        var game = new DetectedGame { Name = "Tunic", Source = "Manual", InstallPath = dir };
        var legacy = LinuxGameLibraryStore.LegacyGameKey(game);
        var current = LinuxGameLibraryStore.GameKey(game);
        Assert.NotEqual(legacy, current);
        var state = new LinuxGameLibraryState { GameCollections = [new() { Name = "Indie", Games = [Dirt, legacy, Forza] }] };

        Assert.True(state.AdoptOlderKeys(game));

        Assert.Equal([Dirt, current, Forza], state.GameCollections![0].Games);
    }

    // ---- list filter -----------------------------------------------------------------------------------------

    [Fact]
    public void Filter_ShowsTheCollectionsGames_NotHidden_WhateverTheTab_SearchStillNarrows()
    {
        var forza = new DetectedGame { Name = "Forza Horizon 5", Source = "Steam" };
        var dirt = new DetectedGame { Name = "DiRT Rally 2.0", Source = "Steam" };
        var hiddenDirt = new DetectedGame { Name = "DiRT 5", Source = "Steam", IsHidden = true };
        var other = new DetectedGame { Name = "Elden Ring", Source = "Steam" };
        var members = new HashSet<DetectedGame> { forza, dirt, hiddenDirt };
        bool In(DetectedGame game) => members.Contains(game);
        var all = new[] { forza, dirt, hiddenDirt, other };

        Assert.Equal(["DiRT Rally 2.0", "Forza Horizon 5"],
            GameLibraryFilter.Filter(all, GameLibraryFilterMode.Hidden, null, null, null, In).Select(g => g.Name));
        Assert.Equal(["DiRT Rally 2.0"],
            GameLibraryFilter.Filter(all, GameLibraryFilterMode.AllGames, "rally", null, null, In).Select(g => g.Name));
        Assert.Equal(3, GameLibraryFilter.Filter(all, GameLibraryFilterMode.AllGames, null, null).Count); // no collection: the tab
    }

    // ---- the shell ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Chips_FilterTheList_ClickAgainShowsAll_AndAFilterTabLeavesTheCollection()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var collections = fx.Vm.Collections;
        Assert.Empty(collections.Chips);
        var hogwarts = Game(fx, "Hogwarts Legacy");

        Assert.True(collections.Create("Wizards", hogwarts).Done);
        Assert.StartsWith("Added Hogwarts Legacy to Wizards.", fx.Vm.StatusText);
        var chip = Assert.Single(collections.Chips);
        Assert.Equal(("Wizards", 1), (chip.Name, chip.Count));
        Assert.Equal(3, fx.Vm.Games.Count); // creating does not switch the list

        fx.Vm.SetLibraryFilterCommand.Execute("Unity");
        collections.ToggleCommand.Execute("wizards");
        Assert.True(chip.IsActive);
        Assert.Equal("All Games", fx.Vm.LibraryFilterMode); // the main filter goes back to All underneath
        Assert.Equal(["Hogwarts Legacy"], fx.Vm.Games.Select(row => row.Name));
        Assert.EndsWith(" · Wizards", fx.Vm.GameCountText);
        Assert.Equal("Wizards", fx.Library.Load().ActiveCollection);

        collections.ToggleCommand.Execute("Wizards"); // click again: every game
        Assert.Null(collections.ActiveName);
        Assert.Equal(3, fx.Vm.Games.Count);
        Assert.Null(fx.Library.Load().ActiveCollection);

        collections.ToggleCommand.Execute("Wizards");
        fx.Vm.SetLibraryFilterCommand.Execute("All Games"); // a tab (even the current one) leaves the collection
        Assert.Null(collections.ActiveName);
        Assert.Equal(3, fx.Vm.Games.Count);
    }

    [Fact]
    public async Task GameMenu_AddToCollection_TicksAndNewCollection()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        DetectedGame? asked = null;
        fx.Vm.Collections.AskNewCollectionAsync = game => { asked = game; return Task.CompletedTask; };
        Assert.True(fx.Vm.Collections.Create("Unreal faves", null).Done);
        var stray = fx.Vm.Games.Single(row => row.Name == "Stray");

        var menu = fx.Vm.CardMenuFor(stray);
        var ids = menu.Select(item => item.Id).ToList();
        Assert.Equal(ids.IndexOf("hide") + 1, ids.IndexOf("add-to-collection")); // under Hide, as upstream
        var add = menu.Single(item => item.Id == "add-to-collection");
        Assert.Equal("Add to collection", add.Label);
        Assert.Equal(["    Unreal faves", "New collection…"], add.Children!.Select(c => c.Label));

        add.Children![0].Command.Execute(null);
        Assert.True(fx.Vm.Collections.IsMember("Unreal faves", stray.Game));
        Assert.Equal("✓ Unreal faves", fx.Vm.CardMenuFor(stray).Single(item => item.Id == "add-to-collection").Children![0].Label);
        fx.Vm.CardMenuFor(stray).Single(item => item.Id == "add-to-collection").Children![0].Command.Execute(null);
        Assert.False(fx.Vm.Collections.IsMember("Unreal faves", stray.Game));
        Assert.Equal("Removed Stray from Unreal faves.", fx.Vm.StatusText);

        add.Children![1].Command.Execute(null);
        Assert.Same(stray.Game, asked);
    }

    [Fact]
    public async Task RenameMoveDelete_DeleteNeverRemovesGames_AndTheActiveOneFollowsARename()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var collections = fx.Vm.Collections;
        Assert.True(collections.Create("Racing", Game(fx, "Stray")).Done);
        Assert.True(collections.Create("RPG", null).Done);
        collections.ToggleCommand.Execute("Racing");

        var (done, message) = collections.Rename("Racing", "rpg");
        Assert.False(done);
        Assert.Equal("You already have a collection called \"rpg\".", message);
        Assert.True(collections.Rename("Racing", "Cats").Done);
        Assert.Equal("Cats", collections.ActiveName);
        Assert.Equal(["Stray"], fx.Vm.Games.Select(row => row.Name));

        collections.MoveRightCommand.Execute("Cats");
        Assert.Equal(["RPG", "Cats"], collections.Chips.Select(c => c.Name));
        Assert.False(collections.Chips[0].CanMoveLeft);
        Assert.False(collections.Chips[1].CanMoveRight);

        Assert.True(collections.Delete("Cats"));
        Assert.Null(collections.ActiveName);
        Assert.Equal(3, fx.Vm.Games.Count); // the games are all still there
        Assert.Equal(["RPG"], fx.Library.Load().GameCollections!.Select(c => c.Name));
        Assert.True(collections.Delete("RPG"));
        Assert.Null(fx.Library.Load().GameCollections);
    }

    [Fact]
    public async Task EmptyCollection_SaysWhy()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var collections = fx.Vm.Collections;
        Assert.True(collections.Create("Later", null).Done);
        Assert.Null(collections.EmptyText);
        collections.ToggleCommand.Execute("Later");
        Assert.Equal("No games in Later yet. Add games from a game's menu.", collections.EmptyText);

        collections.SetMembership("Later", Game(fx, "Stray"), true);
        Assert.Null(collections.EmptyText);
        fx.Vm.SearchText = "hollow";
        await fx.Vm.SearchApplied;
        Assert.Equal("None of the games in Later match the search.", collections.EmptyText);
    }

    [Fact]
    public async Task ActiveCollection_IsRememberedAcrossLaunches()
    {
        var games = Games();
        using (var first = await ViewModelFixture.StartAsync(games))
        {
            Assert.True(first.Vm.Collections.Create("Wizards", Game(first, "Hogwarts Legacy")).Done);
            first.Vm.Collections.ToggleCommand.Execute("Wizards");
            File.Copy(first.Library.LibraryPath, Path.Combine(_root, "kept.json"));
        }

        using var second = await ViewModelFixture.StartAsync(games);
        File.Copy(Path.Combine(_root, "kept.json"), second.Library.LibraryPath, overwrite: true);
        await second.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Wizards", second.Vm.Collections.ActiveName);
        Assert.Equal(["Hogwarts Legacy"], second.Vm.Games.Select(row => row.Name));
    }

    [Fact]
    public async Task DuringRepairAfterMove_NothingIsSaved()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        fx.Vm.LibraryRepairRunning = true;
        try
        {
            var (done, message) = fx.Vm.Collections.Create("Racing", null);
            Assert.False(done);
            Assert.Equal(UiLocalizationService.Text("Vm.WaitForRepairAfterMove"), message);
            Assert.Null(fx.Library.Load().GameCollections);
        }
        finally { fx.Vm.LibraryRepairRunning = false; }
    }

    [Fact]
    public async Task ChipRow_ShowsAChipPerCollection_ThenNewCollection_WithTheMenu()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        Assert.True(fx.Vm.Collections.Create("Racing", null).Done);
        Assert.True(fx.Vm.Collections.Create("RPG_2", null).Done);
        var row = new CollectionsRow { DataContext = fx.Vm };

        var buttons = row.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Equal(["Racing", "RPG__2", "+ Collection"], buttons.Select(b => b.Content as string));
        var racing = buttons[0];
        Assert.Equal(["Rename…", "Move right", "Delete…"],
            row.MenuItemsFor(fx.Vm.Collections, fx.Vm.Collections.Chips[0]).OfType<MenuItem>().Select(item => item.Header as string));
        Assert.Equal(["Rename…", "Move left", "Delete…"],
            row.MenuItemsFor(fx.Vm.Collections, fx.Vm.Collections.Chips[1]).OfType<MenuItem>().Select(item => item.Header as string));

        racing.Command!.Execute(racing.CommandParameter);
        Assert.Contains("active", row.GetLogicalDescendants().OfType<Button>().First().Classes);
    }

    [Fact]
    public async Task PortugueseUi()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            using var fx = await ViewModelFixture.StartAsync(Games(), "pt-BR");
            Assert.Equal("\"Favoritos\" é o nome de um filtro do RHI. Escolha outro nome.", fx.Vm.Collections.Create("Favoritos", null).Message);
            Assert.Equal("Adicionar à coleção", UiLocalizationService.Text("Coll.AddTo"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static DetectedGame Game(ViewModelFixture fx, string name) => fx.Vm.AllLibraryGames.Single(g => g.Name == name);

    private List<DetectedGame> Games()
    {
        string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        return
        [
            new() { Name = "Hogwarts Legacy", Source = "Steam", SteamAppId = 990080, InstallPath = Dir("hogwarts"), EngineHint = "Unreal Engine 4" },
            new() { Name = "Stray", Source = "Steam", SteamAppId = 1332010, InstallPath = Dir("stray"), EngineHint = "Unreal Engine 4" },
            new() { Name = "Hollow Knight", Source = "Heroic/GOG", HeroicAppName = "hk", InstallPath = Dir("hk"), EngineHint = "Unity" }
        ];
    }
}
