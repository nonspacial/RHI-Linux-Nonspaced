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
        Assert.True(fx.Vm.Games.Single(g => g.Name == "Tunic").Game.IsFavourite);
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

    // Final ui-stores review, major 1: an operation that re-read the restored file and then stopped (duplicate refused)
    // left the empty stand-in in memory marked as real; the next add wrote it over the restored library.
    [Fact]
    public async Task UnreadableLibrary_Restored_RefusedDuplicateAdd_ThenAdd_KeepsTheRestoredLibrary()
    {
        using var fx = await ViewModelFixture.StartAsync([], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
        var hades = fx.Game("Hades");
        Restore(fx, new LinuxGameLibraryState
        {
            ManualGames = [new LinuxManualGameEntry { Name = "Hades", InstallPath = hades.InstallPath, Runner = "Manual Windows" }],
            FavouriteGames = new(StringComparer.OrdinalIgnoreCase) { "restored-key" }
        });

        var (added, _) = await fx.Vm.TryAddManualGameAsync(new() { Name = "Hades", InstallPath = hades.InstallPath, Runner = "Manual Windows" });
        Assert.False(added);
        await AddAndSelectAsync(fx, fx.Game("Celeste"));

        var saved = fx.Library.Load();
        Assert.Equal(["Celeste", "Hades"], saved.ManualGames.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Contains("restored-key", saved.FavouriteGames);
    }

    // Final ui-stores review, finding 5: favourite/hidden/HDR/filter saves wrote the stand-in over a restored file.
    [Theory]
    [InlineData("favourite")]
    [InlineData("hidden")]
    [InlineData("hdr")]
    [InlineData("filter")]
    public async Task UnreadableLibrary_Restored_ThenAFlagSave_KeepsTheRestoredEntries(string change)
    {
        var tunic = DetectedFolder("Tunic");
        try
        {
            using var fx = await ViewModelFixture.StartAsync([tunic], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
            var hades = fx.Game("Hades");
            Restore(fx, new LinuxGameLibraryState
            {
                ManualGames = [new LinuxManualGameEntry { Name = "Hades", InstallPath = hades.InstallPath, Runner = "Manual Windows" }],
                FavouriteGames = new(StringComparer.OrdinalIgnoreCase) { "restored-key" }
            });
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic").Game;
            var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame);

            switch (change)
            {
                case "favourite": fx.Vm.ToggleFavouriteCommand.Execute(null); break;
                case "hidden": fx.Vm.ToggleHiddenCommand.Execute(null); break;
                case "hdr": fx.Vm.Runtime.SelectedHdrMode = fx.Vm.Runtime.HdrModeOptions.ForDisplay(RHI.Desktop.Localization.UiLocalizationService.Text("Ui.HdrGamescope")); break;
                default: fx.Vm.LibraryFilterMode = "Favourites"; break;
            }

            var saved = fx.Library.Load();
            Assert.Equal("Hades", Assert.Single(saved.ManualGames).Name);
            Assert.Contains("restored-key", saved.FavouriteGames);
            switch (change)
            {
                case "favourite": Assert.Contains(key, saved.FavouriteGames); break;
                case "hidden": Assert.Contains(key, saved.HiddenGames); break;
                case "hdr": Assert.True(saved.HdrProfiles.ContainsKey(key)); break;
                default: Assert.Equal("Favourites", saved.FilterMode); break;
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(tunic.InstallPath)!, recursive: true); }
    }

    // Final ui-stores review, nit 6 (and surviving mutant F): a filter chosen while Repair after move runs is shown, not
    // written over the file the repair is rewriting, and saved once the repair has finished.
    [Fact]
    public async Task FilterChosenWhileRepairAfterMoveRuns_IsSavedWhenTheRepairEnds()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = File.ReadAllText(fx.Library.LibraryPath);

        fx.Vm.LibraryRepairRunning = true;
        try
        {
            fx.Vm.LibraryFilterMode = "Favourites";
            Assert.Equal(before, File.ReadAllText(fx.Library.LibraryPath));
        }
        finally { fx.Vm.LibraryRepairRunning = false; }

        Assert.Equal("Favourites", fx.Library.Load().FilterMode);
        Assert.Equal("Tunic", Assert.Single(fx.Library.Load().ManualGames).Name);
    }

    // Surviving mutant E: the HDR save waits for the repair and the dropdown goes back to the saved mode.
    [Fact]
    public async Task HdrModeWhileRepairAfterMoveRuns_Waits_AndRevertsTheDropdown()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = File.ReadAllText(fx.Library.LibraryPath);
        var off = fx.Vm.Runtime.SelectedHdrMode.Display;

        fx.Vm.LibraryRepairRunning = true;
        try { fx.Vm.Runtime.SelectedHdrMode = fx.Vm.Runtime.HdrModeOptions.ForDisplay(RHI.Desktop.Localization.UiLocalizationService.Text("Ui.HdrGamescope")); }
        finally { fx.Vm.LibraryRepairRunning = false; }

        Assert.Equal(off, fx.Vm.Runtime.SelectedHdrMode.Display);
        Assert.StartsWith("Wait for Repair after move to finish", fx.Vm.StatusText);
        Assert.Equal(before, File.ReadAllText(fx.Library.LibraryPath));
    }

    // Final ui-stores review, nit 7: Add game (button and drag-and-drop are not busy-gated) waits for the repair.
    [Fact]
    public async Task AddGameWhileRepairAfterMoveRuns_IsRefused_AndChangesNothing()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAndSelectAsync(fx, fx.Game("Tunic"));
        var before = File.ReadAllText(fx.Library.LibraryPath);
        var celeste = fx.Game("Celeste");

        fx.Vm.LibraryRepairRunning = true;
        try
        {
            var (added, message) = await fx.Vm.TryAddManualGameAsync(new() { Name = "Celeste", InstallPath = celeste.InstallPath, Runner = "Manual Windows" });
            Assert.False(added);
            Assert.StartsWith("Wait for Repair after move to finish", message);
        }
        finally { fx.Vm.LibraryRepairRunning = false; }

        Assert.Equal(before, File.ReadAllText(fx.Library.LibraryPath));
    }

    // Surviving mutant L: the automatic merged-duplicate save reports a refusal only through the scan's own line.
    [Fact]
    public async Task RefusedMergedDuplicateSave_NeverTakesTheStatusLineAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-adopt-" + Guid.NewGuid().ToString("N"));
        var real = Directory.CreateDirectory(Path.Combine(root, "media", "X")).FullName;
        var link = Path.Combine(root, "Games");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "media"));
        var heroic = new RHI.Core.Models.DetectedGame { Name = "X", Source = "Heroic", InstallPath = real, Runner = "Proton" };
        try
        {
            using var fx = await ViewModelFixture.StartAsync([heroic]);
            var manual = new RHI.Core.Models.DetectedGame { Name = "X", Source = "Manual", InstallPath = Path.Combine(link, "X"), IsManuallyAdded = true };
            var manualKey = LinuxGameLibraryStore.GameKey(manual);
            Restore(fx, new LinuxGameLibraryState
            {
                ManualGames = [new LinuxManualGameEntry { Name = "X", InstallPath = manual.InstallPath, Runner = "Manual Windows" }],
                FavouriteGames = new(StringComparer.OrdinalIgnoreCase) { manualKey }
            });
            var lines = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText); };
            var mode = File.GetUnixFileMode(fx.DataDir);
            File.SetUnixFileMode(fx.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { await fx.Vm.RefreshCommand.ExecuteAsync(null); }
            finally { File.SetUnixFileMode(fx.DataDir, mode); }

            Assert.Contains(lines, line => line.Contains("Could not save the game library", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.StartsWith("Could not save the game library", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
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
        // Every view-model file: the split (epic #89) moves code out of MainWindowViewModel.cs.
        var viewModels = Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "ViewModels");
        var direct = Directory.EnumerateFiles(viewModels, "*.cs")
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .Where(x => StoreWrite.IsMatch(x.line) && !x.line.Contains("TryPersist(", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.Empty(direct);
    }

    private static readonly Regex StoreWrite = new(@"_\w*(Store|store)\.(Save|Set|Reset)\(");

    /// <summary>The user puts a readable library file back while RHI runs (from the set-aside copy or a backup).</summary>
    private static void Restore(ViewModelFixture fx, LinuxGameLibraryState state)
    {
        var staged = Path.Combine(fx.Root, "restored-" + Guid.NewGuid().ToString("N") + ".json");
        new LinuxGameLibraryStore(staged).Save(state);
        File.Copy(staged, fx.Library.LibraryPath, overwrite: true);
    }

    /// <summary>A launcher-detected game whose folder exists (outside the fixture, which is built after the games).</summary>
    private static RHI.Core.Models.DetectedGame DetectedFolder(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-detected-{Guid.NewGuid():N}", name)).FullName;
        return new RHI.Core.Models.DetectedGame { Name = name, Source = "Heroic", InstallPath = dir, Runner = "Proton" };
    }

    private static async Task AddAndSelectAsync(ViewModelFixture fx, RHI.Core.Models.DetectedGame game)
    {
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
    }
}
