using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #293 (upstream 2.9.0 "Remove from RHI" and Settings › Removed games): a removed game is gone from every list, Hidden
/// included, a scan does not bring it back, the choice is saved, and Restore brings it back with the next scan.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class RemovedGamesTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-removed-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private DetectedGame Steam(string name, int appId)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, "game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return new DetectedGame { Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, SteamAppId = appId, Source = "Steam", Runner = "Proton" };
    }

    [Fact]
    public async Task Remove_TakesTheGameOutOfEveryList_ScansKeepItOut_RestoreBringsItBack()
    {
        using var fx = await ViewModelFixture.StartAsync([Steam("Keep Me", 70001), Steam("Remove Me", 70002)]);
        fx.Vm.LibraryFilterMode = "All Games";
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Remove Me");
        Assert.True(fx.Vm.CanRemoveFromRhi);

        fx.Vm.RemoveFromRhiCommand.Execute(null);

        Assert.Equal("Remove Me was removed from RHI. Settings → Removed games brings it back.", fx.Vm.StatusText);
        Assert.DoesNotContain(fx.Vm.AllLibraryGames, game => game.Name == "Remove Me");
        fx.Vm.LibraryFilterMode = "Hidden";
        Assert.DoesNotContain(fx.Vm.Games, row => row.Game.Name == "Remove Me");
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fx.Vm.AllLibraryGames, game => game.Name == "Remove Me");
        Assert.Single(fx.Library.Load().RemovedGames!);
        var row = Assert.Single(fx.Vm.RemovedGames);
        Assert.Equal("Remove Me", row.Name);

        var line = await fx.Vm.RestoreRemovedGameAsync(row.Key);

        Assert.Equal("Remove Me was restored.", line);
        Assert.Contains(fx.Vm.AllLibraryGames, game => game.Name == "Remove Me");
        Assert.Null(fx.Library.Load().RemovedGames);
        Assert.Empty(fx.Vm.RemovedGames);
    }

    [Fact]
    public async Task AManuallyAddedGame_IsNotOfferedRemoveFromRhi()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Manual")).FullName;
        using var fx = await ViewModelFixture.StartAsync([new DetectedGame { Name = "Manual", InstallPath = dir, Source = "Manual", Runner = "Manual Windows", IsManuallyAdded = true }]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        Assert.False(fx.Vm.CanRemoveFromRhi);
    }

    [Fact]
    public void LibraryFile_WithoutRemovedGames_KeepsItsShape()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "library.json"));
        store.Save(new LinuxGameLibraryState());
        Assert.DoesNotContain("RemovedGames", File.ReadAllText(store.LibraryPath), StringComparison.Ordinal);
    }
}
