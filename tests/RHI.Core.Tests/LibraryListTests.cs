using RHI.Core.Services;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// Unit 3 of the view-model split (epic #89): the library list is filtered in place and the search waits for a pause
/// in typing, so the selected game is kept (and not re-analysed) while it still matches (#75); cards are observable, so
/// a favourite/hidden change updates the badge without rebuilding the list (#85).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LibraryListTests
{
    // UI-07 verification: typing a search that still matches the selected game causes no selection transitions.
    [Fact]
    public async Task Typing_ASearchThatStillMatches_KeepsTheSelection()
    {
        using var fx = await StartWithGamesAsync("Tunic", "Celeste", "Hades");
        fx.Vm.SelectedGame = Row(fx, "Tunic").Game;
        await Settle(fx);
        var transitions = Record(fx);
        var analysis = fx.Vm.Session.Analysis;

        foreach (var text in new[] { "T", "Tu", "Tun", "Tuni" }) fx.Vm.SearchText = text;
        Assert.Equal(3, fx.Vm.Games.Count); // nothing is filtered while typing
        await fx.Vm.SearchApplied;

        Assert.Equal(["Tunic"], fx.Vm.Games.Select(row => row.Name));
        Assert.Empty(transitions);
        Assert.Same(analysis, fx.Vm.Session.Analysis); // not re-analysed
        Assert.Same(Row(fx, "Tunic"), fx.Vm.SelectedGameRow);
    }

    [Fact]
    public async Task ASearchTypedPastAMismatch_AppliesOnlyTheLastText()
    {
        using var fx = await StartWithGamesAsync("Tunic", "Celeste");
        fx.Vm.SelectedGame = Row(fx, "Tunic").Game;
        await Settle(fx);
        var transitions = Record(fx);

        fx.Vm.SearchText = "zzz";      // would empty the list and drop the selection…
        fx.Vm.SearchText = "tun";      // …but the user kept typing
        await fx.Vm.SearchApplied;

        Assert.Empty(transitions);
        Assert.Equal("Tunic", Assert.Single(fx.Vm.Games).Name);
    }

    [Fact]
    public async Task FilterChange_ThatStillShowsTheSelection_KeepsIt_AndOneThatHidesItSelectsTheFirst()
    {
        using var fx = await StartWithGamesAsync("Tunic", "Celeste");
        fx.Vm.SelectedGame = Row(fx, "Tunic").Game;
        await Settle(fx);
        fx.Vm.ToggleFavouriteCommand.Execute(null);
        var transitions = Record(fx);

        fx.Vm.LibraryFilterMode = "Favourites";
        Assert.Empty(transitions);
        Assert.Equal("Tunic", Assert.Single(fx.Vm.Games).Name);

        fx.Vm.LibraryFilterMode = "Hidden";
        Assert.Empty(fx.Vm.Games);
        Assert.Equal([null], transitions);
    }

    // UI-18 verification: the favourite toggle updates the same card in place; the list is not rebuilt.
    [Fact]
    public async Task ToggleFavourite_UpdatesTheSameCardInPlace()
    {
        using var fx = await StartWithGamesAsync("Tunic", "Celeste");
        var card = Row(fx, "Tunic");
        fx.Vm.SelectedGame = card.Game;
        await Settle(fx);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var resets = 0;
        fx.Vm.Games.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        fx.Vm.ToggleFavouriteCommand.Execute(null);

        Assert.Same(card, Row(fx, "Tunic"));
        Assert.Contains(nameof(GameRowViewModel.LibraryBadge), changed);
        Assert.StartsWith("★", card.LibraryBadge);
        Assert.True(card.IsFavourite);
        Assert.Equal(0, resets);
        Assert.Same(card, fx.Vm.SelectedGameRow);
    }

    [Fact]
    public void ApplyFilter_InsertsAndRemovesInPlace_SortedByName()
    {
        var library = new LibraryViewModel();
        var games = new[] { "Hades", "Celeste", "Tunic" }.Select(name => new DetectedGame { Name = name, Source = "Manual" }).ToArray();
        library.SetGames(games);
        library.ApplyFilter(GameLibraryFilterMode.AllGames, "", null);
        Assert.Equal(["Celeste", "Hades", "Tunic"], library.Games.Select(row => row.Name));
        var tunic = library.RowFor(games[2]);

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        library.Games.CollectionChanged += (_, e) => actions.Add(e.Action);
        library.ApplyFilter(GameLibraryFilterMode.AllGames, "u", null); // Tunic only
        library.ApplyFilter(GameLibraryFilterMode.AllGames, "", null);

        Assert.Equal(["Celeste", "Hades", "Tunic"], library.Games.Select(row => row.Name));
        Assert.Same(tunic, library.Games[2]);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
    }

    private static List<DetectedGame?> Record(ViewModelFixture fx)
    {
        var transitions = new List<DetectedGame?>();
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame)) transitions.Add(fx.Vm.SelectedGame);
        };
        return transitions;
    }

    private static GameRowViewModel Row(ViewModelFixture fx, string name) => fx.Vm.Games.Single(row => row.Name == name);

    private static async Task<ViewModelFixture> StartWithGamesAsync(params string[] names)
    {
        var fx = await ViewModelFixture.StartAsync([]);
        foreach (var name in names)
        {
            var game = fx.Game(name);
            var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
            {
                Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
            });
            Assert.True(added, message);
        }
        await Settle(fx);
        return fx;
    }

    private static async Task Settle(ViewModelFixture fx)
    {
        for (var i = 0; i < 500 && (fx.Vm.IsBusy || fx.Vm.IsSelectionProbeBusy); i++) await Task.Delay(10);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
    }
}
