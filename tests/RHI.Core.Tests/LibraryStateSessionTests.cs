using System.Text.RegularExpressions;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (stores minor 5, nits 9 and 10): add/edit/reset/remove work on a copy of the in-memory library
/// state (so a change kept only for this session is not silently dropped by the next successful save), but never on the
/// empty stand-in for an unreadable library file; favourite/hidden/HDR/filter saves wait while Repair after move rewrites
/// the library file; every view-model store save goes through TryPersist.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LibraryStateSessionTests
{
    [Fact]
    public async Task SessionOnlyFavourite_IsKept_AndSavedByALaterSuccessfulAdd()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);
        var mode = File.GetUnixFileMode(fx.DataDir);
        File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            fx.Vm.ToggleFavouriteCommand.Execute(null);
            Assert.StartsWith("Could not save the game library:", fx.Vm.StatusText);
        }
        finally { File.SetUnixFileMode(fx.DataDir, mode); }

        await AddAndSelectAsync(fx, fx.Game("Celeste"));

        Assert.Contains(key, fx.Library.Load().FavouriteGames);
        Assert.True(fx.Vm.Games.Single(g => g.Name == "Tunic").IsFavourite);
    }

    [Fact]
    public async Task UnreadableLibrary_FixedOnDisk_IsNotOverwrittenByTheEmptyStandIn()
    {
        using var fx = await ViewModelFixture.StartAsync([], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
        // The user restores the file while RHI runs (from the copy RHI set aside, or a backup).
        File.WriteAllText(fx.Library.LibraryPath, "{\"FavouriteGames\":[\"restored-key\"]}");

        await AddAndSelectAsync(fx, fx.Game("Celeste"));

        var saved = fx.Library.Load();
        Assert.Contains("restored-key", saved.FavouriteGames);
        Assert.Contains(saved.ManualGames, entry => entry.Name == "Celeste");
    }

    [Fact]
    public async Task FavouriteWhileRepairAfterMoveRuns_Waits_AndChangesNothing()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = File.ReadAllText(fx.Library.LibraryPath);

        fx.Vm.LibraryRepairRunning = true;
        try
        {
            fx.Vm.ToggleFavouriteCommand.Execute(null);
            fx.Vm.ToggleHiddenCommand.Execute(null);
        }
        finally { fx.Vm.LibraryRepairRunning = false; }

        Assert.StartsWith("Wait for Repair after move to finish", fx.Vm.StatusText);
        Assert.False(fx.Vm.SelectedGame!.IsFavourite);
        Assert.False(fx.Vm.SelectedGame!.IsHidden);
        Assert.Equal(before, File.ReadAllText(fx.Library.LibraryPath));
    }

    [Fact]
    public void EveryViewModelStoreSave_GoesThroughTryPersist()
    {
        var vm = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "ViewModels", "MainWindowViewModel.cs");
        var direct = File.ReadAllLines(vm)
            .Select((line, i) => (line, i))
            .Where(x => StoreWrite.IsMatch(x.line) && !x.line.Contains("TryPersist(", StringComparison.Ordinal))
            .Select(x => $"{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(direct);
    }

    private static readonly Regex StoreWrite = new(@"_\w*(Store|store)\.(Save|Set|Reset)\(");

    private static async Task AddAndSelectAsync(ViewModelFixture fx, RHI.Core.Models.DetectedGame game)
    {
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
    }
}
