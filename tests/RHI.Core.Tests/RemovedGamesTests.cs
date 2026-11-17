using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
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

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.Equal("Remove Me was removed from RHI. Settings › Tools & Maintenance › Troubleshooting › Removed games brings it back.", fx.Vm.StatusText);
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

    // A DOF Fix the ledger owns in the game folder (a component RHI "installed").
    private static string Installed(ViewModelFixture fx, DetectedGame game)
    {
        var addon = Path.Combine(game.InstallPath, "dof-fix.addon64");
        File.WriteAllText(addon, "dof");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DofFix,
            BinaryDirectory = game.InstallPath, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        });
        return addon;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_AsksFirst_AndOffersToUninstallTheGamesComponents(bool uninstall)
    {
        var game = Steam("Modded", 70003);
        using var fx = await ViewModelFixture.StartAsync([game]);
        var addon = Installed(fx, game);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        MainWindowViewModel.RemoveFromRhiPrompt? asked = null;
        fx.Vm.ConfirmRemoveFromRhiAsync = prompt => { asked = prompt; return Task.FromResult<bool?>(uninstall); };

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Equal("Remove Modded from RHI?", asked.Question);
        Assert.True(asked.OffersUninstall);
        Assert.Equal($"Installed: {LinuxManagedComponentService.DisplayDependencyName(ManagedComponentKind.DofFix)}. If you don't uninstall them, they stay in the game folder and RHI stops updating them.", asked.Installed);
        Assert.Equal("Also uninstall all RHI-managed components from game folder", asked.UninstallText);
        Assert.StartsWith("Modded was removed from RHI.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(fx.Vm.AllLibraryGames);
        Assert.Equal(!uninstall, File.Exists(addon));
        Assert.Equal(!uninstall, fx.Records.Find(game, ManagedComponentKind.DofFix) is not null);
    }

    [Fact]
    public async Task Remove_Cancelled_ChangesNothing_AndAGameWithoutComponentsIsNotOfferedTheUninstall()
    {
        using var fx = await ViewModelFixture.StartAsync([Steam("Plain", 70004)]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        MainWindowViewModel.RemoveFromRhiPrompt? asked = null;
        fx.Vm.ConfirmRemoveFromRhiAsync = prompt => { asked = prompt; return Task.FromResult<bool?>(null); };

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.False(asked!.OffersUninstall);
        Assert.Single(fx.Vm.AllLibraryGames);
        Assert.Null(fx.Library.Load().RemovedGames);
    }

    [Fact]
    public async Task AComponentThatCannotBeUninstalled_IsNamed_AndTheGameIsStillRemovedAsUpstream()
    {
        var game = Steam("Changed", 70005);
        using var fx = await ViewModelFixture.StartAsync([game]);
        var addon = Installed(fx, game);
        File.WriteAllText(addon, "the user changed it");
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        fx.Vm.ConfirmRemoveFromRhiAsync = _ => Task.FromResult<bool?>(true);

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.Empty(fx.Vm.AllLibraryGames);
        Assert.True(File.Exists(addon));
        Assert.Contains("Still installed (they come back with the game on Restore): ", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("modified after installation", fx.Vm.StatusText, StringComparison.Ordinal);
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
