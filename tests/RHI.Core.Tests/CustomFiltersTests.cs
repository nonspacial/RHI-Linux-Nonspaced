using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #193 (parity T-18, upstream <c>CustomFilter</c> / <c>SaveFilterButton_Click</c>): the current search is saved under a
/// name in the library file, shown as a chip, and applied on top of the filter tab and the search box; clicking the
/// active chip turns it off; names are required and unique in any case; delete removes it. Library-write rules apply.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CustomFiltersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-custom-filters-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ACustomQuery_IsASecondSearch_OnTopOfTheTabAndTheSearchBox()
    {
        var unreal = new DetectedGame { Name = "Hogwarts Legacy", Source = "Steam", EngineHint = "Unreal Engine 4" };
        var unity = new DetectedGame { Name = "Hollow Knight", Source = "GOG", EngineHint = "Unity" };

        Assert.True(GameLibraryFilter.Matches(unreal, GameLibraryFilterMode.AllGames, null, default, "unreal"));
        Assert.False(GameLibraryFilter.Matches(unity, GameLibraryFilterMode.AllGames, null, default, "unreal"));
        Assert.False(GameLibraryFilter.Matches(unreal, GameLibraryFilterMode.AllGames, "knight", default, "unreal"));
        Assert.False(GameLibraryFilter.Matches(unreal, GameLibraryFilterMode.Unity, null, default, "steam"));
        Assert.True(GameLibraryFilter.Matches(unity, GameLibraryFilterMode.AllGames, "hollow", default, " "));
    }

    [Fact]
    public void SavedFilters_AreTrimmed_NamesUniqueInAnyCase_AndNoneWritesNothing()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var state = store.Load();
        store.Save(state);
        Assert.DoesNotContain("CustomFilters", File.ReadAllText(store.LibraryPath));

        state.CustomFilters =
        [
            new() { Name = " UE ", Query = " unreal " },
            new() { Name = "ue", Query = "other" },
            new() { Name = "", Query = "x" },
            new() { Name = "Empty", Query = " " }
        ];
        store.Save(state);

        var saved = Assert.Single(store.Load().CustomFilters!);
        Assert.Equal(("UE", "unreal"), (saved.Name, saved.Query));
    }

    [Fact]
    public async Task SavingTheSearch_ShowsAnActiveChip_FiltersTheList_ClearsTheSearch_AndTogglesOff()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var filters = fx.Vm.CustomFilters;
        Assert.False(filters.IsShown); // nothing saved, nothing typed: the row is hidden
        fx.Vm.SearchText = "unreal";
        await fx.Vm.SearchApplied;
        Assert.True(filters.CanSave);
        Assert.True(filters.IsShown);

        var (saved, message) = filters.Save("Unreal games", fx.Vm.SearchText);

        Assert.True(saved, message);
        Assert.Equal("Filter \"Unreal games\" saved and turned on.", message);
        Assert.Equal("", fx.Vm.SearchText);
        Assert.False(filters.CanSave);
        var chip = Assert.Single(filters.Chips);
        Assert.True(chip.IsActive);
        Assert.Equal("unreal", Assert.Single(fx.Library.Load().CustomFilters!).Query);
        Assert.Equal(["Hogwarts Legacy", "Stray"], fx.Vm.Games.Select(row => row.Name));
        Assert.EndsWith(" · Unreal games", fx.Vm.GameCountText);

        // The search box narrows further, on top of the chip.
        fx.Vm.SearchText = "stray";
        await fx.Vm.SearchApplied;
        Assert.Equal(["Stray"], fx.Vm.Games.Select(row => row.Name));
        fx.Vm.SearchText = "";
        await fx.Vm.SearchApplied;

        // Clicking the active chip turns it off.
        filters.ToggleCommand.Execute("Unreal games");
        Assert.False(chip.IsActive);
        Assert.Equal(3, fx.Vm.Games.Count);
        filters.ToggleCommand.Execute("Unreal games");
        Assert.Equal(2, fx.Vm.Games.Count);
    }

    [Fact]
    public async Task NamesAreRequiredAndUniqueInAnyCase_AndARefusalSavesNothing()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var filters = fx.Vm.CustomFilters;
        Assert.True(filters.Save("UE", "unreal").Saved);

        var (saved, message) = filters.Save("ue", "steam");
        Assert.False(saved);
        Assert.Equal("A filter named \"ue\" already exists.", message);
        (saved, message) = filters.Save("  ", "steam");
        Assert.False(saved);
        Assert.Equal(UiLocalizationService.Text("Shell.CustomFilterNameEmpty"), message);
        (saved, _) = filters.Save("Steam", " ");
        Assert.False(saved);

        Assert.Equal(["UE"], fx.Library.Load().CustomFilters!.Select(f => f.Name));
        Assert.Single(filters.Chips);
    }

    [Fact]
    public async Task DeletingTheActiveFilter_TurnsItOff_AndRemovesItFromTheLibraryFile()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        var filters = fx.Vm.CustomFilters;
        Assert.True(filters.Save("UE", "unreal").Saved);
        Assert.True(filters.Save("GOG", "gog").Saved);
        Assert.Equal(["Hollow Knight"], fx.Vm.Games.Select(row => row.Name));

        filters.DeleteCommand.Execute("gog");

        Assert.Null(filters.ActiveName);
        Assert.Equal(3, fx.Vm.Games.Count);
        Assert.Equal(["UE"], fx.Library.Load().CustomFilters!.Select(f => f.Name));
        filters.DeleteCommand.Execute("UE");
        Assert.Null(fx.Library.Load().CustomFilters);
        Assert.Empty(filters.Chips);
    }

    [Fact]
    public async Task DuringRepairAfterMove_NothingIsSaved()
    {
        using var fx = await ViewModelFixture.StartAsync(Games());
        fx.Vm.LibraryRepairRunning = true;
        try
        {
            var (saved, message) = fx.Vm.CustomFilters.Save("UE", "unreal");
            Assert.False(saved);
            Assert.Equal(UiLocalizationService.Text("Vm.WaitForRepairAfterMove"), message);
            Assert.Null(fx.Library.Load().CustomFilters);
        }
        finally { fx.Vm.LibraryRepairRunning = false; }
    }

    [Fact]
    public async Task SavedFilters_ComeBackWithTheLibrary_NoneActive()
    {
        var games = Games();
        using (var first = await ViewModelFixture.StartAsync(games))
        {
            Assert.True(first.Vm.CustomFilters.Save("UE", "unreal").Saved);
            File.Copy(first.Library.LibraryPath, Path.Combine(_root, "kept.json"));
        }

        using var second = await ViewModelFixture.StartAsync(games);
        File.Copy(Path.Combine(_root, "kept.json"), second.Library.LibraryPath, overwrite: true);
        await second.Vm.RefreshCommand.ExecuteAsync(null);

        var chip = Assert.Single(second.Vm.CustomFilters.Chips);
        Assert.Equal("UE", chip.Name);
        Assert.False(chip.IsActive);
        Assert.Equal(3, second.Vm.Games.Count);
    }

    [Fact]
    public async Task PortugueseUi_ShowsTheFilterLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            using var fx = await ViewModelFixture.StartAsync(Games(), "pt-BR");
            Assert.True(fx.Vm.CustomFilters.Save("UE", "unreal").Saved);
            Assert.Equal("Já existe um filtro com o nome \"ue\".", fx.Vm.CustomFilters.Save("ue", "x").Message);
            Assert.Equal("+ Salvar como filtro", UiLocalizationService.Text("Shell.SaveAsFilter"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

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
