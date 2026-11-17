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

    // A file the ledger owns for <paramref name="kind"/> in the game folder (hash recorded, so its removal finds it unchanged).
    private static string Owned(ViewModelFixture fx, DetectedGame game, ManagedComponentKind kind, string fileName)
    {
        var path = Path.Combine(game.InstallPath, fileName);
        File.WriteAllText(path, kind.ToString());
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.InstallPath, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = path, Sha256 = FileHash.Sha256Hex(path) }]
        });
        return path;
    }

    // w14 final review M1: every kind a record can carry (bar the DLSS/Streamline swaps and the Addon Manager selection,
    // which are not components of their own) is offered and attempted by "Also uninstall".
    [Fact]
    public async Task EveryKindThatCanHaveARecord_IsRemovable()
    {
        var game = Steam("All Kinds", 70006);
        using var fx = await ViewModelFixture.StartAsync([game]);
        var notComponents = new[] { ManagedComponentKind.Unknown, ManagedComponentKind.Dlss, ManagedComponentKind.Streamline, ManagedComponentKind.ReShadeAddonManager };
        var kinds = Enum.GetValues<ManagedComponentKind>().Except(notComponents).ToArray();
        foreach (var kind in kinds) Owned(fx, game, kind, kind + ".bin");
        foreach (var kind in notComponents.Skip(1)) Owned(fx, game, kind, kind + ".bin");

        var removable = fx.Managed.RemovableComponents(game);

        Assert.Equal(kinds.OrderBy(k => k), removable.OrderBy(k => k));
        Assert.All(notComponents, kind => Assert.False(LinuxManagedComponentService.IsRemovableComponent(kind)));
        // What others sit on goes last; the add-ons that depend on ReShade go before it.
        Assert.Equal([ManagedComponentKind.UltimateAsiLoader, ManagedComponentKind.ReShade, ManagedComponentKind.REFramework], removable.TakeLast(3));
        Assert.True(removable.ToList().IndexOf(ManagedComponentKind.GitcUplift) < removable.ToList().IndexOf(ManagedComponentKind.ReShade));
        Assert.True(removable.ToList().IndexOf(ManagedComponentKind.NrCostScaler) == 0);
    }

    // w14 final review M1: HSF, GITC Uplift and DLSS5 Tool were never attempted, so ReShade refused and their files and
    // records stayed with a game that had left RHI. Each now goes through its own removal.
    [Theory]
    [InlineData(ManagedComponentKind.Hsf, "hsf.addon64")]
    [InlineData(ManagedComponentKind.GitcUplift, "gitc-uplift.addon64")]
    [InlineData(ManagedComponentKind.Dlss5Tool, "renodx-dlss5.addon64")]
    public async Task AlsoUninstall_TakesOutTheWave14Components(ManagedComponentKind kind, string fileName)
    {
        var game = Steam("Wave14 " + kind, 70007 + (int)kind);
        using var fx = await ViewModelFixture.StartAsync([game]);
        var file = Owned(fx, game, kind, fileName);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        fx.Vm.ConfirmRemoveFromRhiAsync = _ => Task.FromResult<bool?>(true);

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.Empty(fx.Vm.AllLibraryGames);
        Assert.DoesNotContain("Still installed", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
        Assert.Null(fx.Records.Find(game, kind));
    }

    // w14 final review M1: a removal that leaves components behind names each one with its reason (the library-folders
    // rule), and the game still leaves RHI as upstream does; nothing that had a record goes unmentioned.
    [Fact]
    public async Task AlsoUninstall_ThatLeavesComponentsBehind_NamesEachWithItsReason()
    {
        var game = Steam("Left Behind", 70040);
        using var fx = await ViewModelFixture.StartAsync([game]);
        var gitc = Owned(fx, game, ManagedComponentKind.GitcUplift, "gitc-uplift.addon64");
        Owned(fx, game, ManagedComponentKind.ReShade, "dxgi.dll");
        File.WriteAllText(gitc, "the user changed it");
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        fx.Vm.ConfirmRemoveFromRhiAsync = _ => Task.FromResult<bool?>(true);

        await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);

        Assert.Empty(fx.Vm.AllLibraryGames);
        Assert.Contains("Still installed (they come back with the game on Restore): ", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains($"{GitcUpliftDeploymentService.DisplayName} ({GitcUpliftDeploymentService.ModifiedPrefix}", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains($"{LinuxManagedComponentService.DisplayDependencyName(ManagedComponentKind.ReShade)} (Remove RHI-managed {GitcUpliftDeploymentService.DisplayName} first.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.NotNull(fx.Records.Find(game, ManagedComponentKind.GitcUplift));
        Assert.NotNull(fx.Records.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task Remove_WhileAnotherOperationRuns_SaysSo()
    {
        using var fx = await ViewModelFixture.StartAsync([Steam("Busy", 70050)]);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single();
        fx.Vm.StatusText = "";
        using (((IPanelHost)fx.Vm).BeginOperation("install", cancellable: false))
            await fx.Vm.RemoveFromRhiCommand.ExecuteAsync(null);
        Assert.Equal("Another RHI operation is already running.", fx.Vm.StatusText);
        Assert.Single(fx.Vm.AllLibraryGames);
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
