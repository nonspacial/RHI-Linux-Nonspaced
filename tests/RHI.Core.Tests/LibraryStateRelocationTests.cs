using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #147: favourite, hidden, game edits and HDR settings follow a game whose Steam library (or Heroic folder) moved,
/// RHI components or not. Library state is keyed by the store's id (Steam AppID plus a split child's name, Heroic app
/// id), other games by their folder's identity; keys older builds wrote are adopted when the game is found, never
/// dropped; a patch target (Edit game) left naming the old folder is re-pointed by "Repair after move".
/// </summary>
public sealed class LibraryStateRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-library-relocation-" + Guid.NewGuid().ToString("N"));

    public LibraryStateRelocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static DetectedGame Steam(string library, string installDir = "Game", int appId = 1234, string name = "Game") => new()
    {
        Name = name, Source = "Steam", SteamAppId = appId, InstallPath = Path.Combine(library, "steamapps", "common", installDir), Runner = "Proton"
    };

    private static DetectedGame SplitChild(string library, string sub, string name)
    {
        var child = Steam(library, Path.Combine("Bundle", sub), name: name);
        child.IsManifestSplitChild = true;
        child.ManifestInstallRoot = Path.Combine(library, "steamapps", "common", "Bundle");
        return child;
    }

    // ---- Keys --------------------------------------------------------------------------------

    [Fact]
    public void Key_SurvivesAMove_AndTellsSplitChildrenApart()
    {
        var oldLib = Path.Combine(_root, "a", "lib");
        var newLib = Path.Combine(_root, "b", "lib");

        Assert.Equal("steam:1234", LinuxGameLibraryStore.GameKey(Steam(oldLib)));
        Assert.Equal(LinuxGameLibraryStore.GameKey(Steam(oldLib)), LinuxGameLibraryStore.GameKey(Steam(newLib)));
        Assert.Equal(LinuxGameLibraryStore.GameKey(SplitChild(oldLib, "One", "Saga One")), LinuxGameLibraryStore.GameKey(SplitChild(newLib, "One", "Saga One")));
        Assert.NotEqual(LinuxGameLibraryStore.GameKey(SplitChild(oldLib, "One", "Saga One")), LinuxGameLibraryStore.GameKey(SplitChild(oldLib, "Two", "Saga Two")));
        Assert.Equal("heroic:Fortnite",
            LinuxGameLibraryStore.GameKey(new DetectedGame { Source = "Heroic/Epic", HeroicAppName = "Fortnite", InstallPath = Path.Combine(_root, "x") }));
        // Session keys (probe cache, selection) stay per folder.
        Assert.NotEqual(LinuxGameLibraryStore.SessionKey(Steam(oldLib)), LinuxGameLibraryStore.SessionKey(Steam(newLib)));
    }

    [Fact]
    public void FolderKey_IsTheFoldersIdentity_SoASymlinkSpellingIsTheSameGame()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "media", "Indie")).FullName;
        var link = Path.Combine(_root, "Games");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "media"));
        var viaLink = new DetectedGame { Name = "Indie", Source = "Manual", InstallPath = Path.Combine(link, "Indie") + "/" };
        var direct = new DetectedGame { Name = "Indie", Source = "Manual", InstallPath = real };

        Assert.Equal(LinuxGameLibraryStore.GameKey(direct), LinuxGameLibraryStore.GameKey(viaLink));
        Assert.Contains(real, LinuxGameLibraryStore.GameKey(direct), StringComparison.Ordinal); // case kept: Linux paths are case-sensitive
    }

    // ---- Migration of keys older builds wrote ----------------------------------------------------

    [Fact]
    public void OlderKeys_AreAdoptedWhenTheGameIsFound_AndKeysNoGameClaimsAreKept()
    {
        var library = Path.Combine(_root, "lib");
        var moved = Steam(Path.Combine(_root, "new-lib"));                       // moved before this build
        var here = Steam(library, "Other", appId: 99, name: "Other");            // still where it was
        var child = SplitChild(library, "One", "Saga One");
        var notFound = Steam(Path.Combine(_root, "unmounted"), "Gone", appId: 77, name: "Gone");
        var sameFolderOtherApp = Steam(Path.Combine(_root, "old-lib"), appId: 555); // same installdir, another AppID
        var state = new LinuxGameLibraryState();
        string Legacy(DetectedGame g) => LinuxGameLibraryStore.LegacyGameKey(g);
        var movedOldKey = Legacy(Steam(Path.Combine(_root, "old-lib")));
        state.FavouriteGames.UnionWith([movedOldKey, Legacy(here), Legacy(child), Legacy(notFound), Legacy(sameFolderOtherApp)]);
        state.HiddenGames.Add(movedOldKey);
        state.GameEdits[movedOldKey] = new LinuxGameEditOverride { LaunchProvider = "Steam" };
        state.HdrProfiles[movedOldKey] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope };
        state.HdrProfiles[Legacy(here)] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.NativeWayland };
        state.HdrProfiles[LinuxGameLibraryStore.GameKey(here)] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope }; // newer choice

        Assert.True(state.AdoptOlderKeys(moved));
        Assert.True(state.AdoptOlderKeys(here));
        Assert.True(state.AdoptOlderKeys(child));
        Assert.False(state.AdoptOlderKeys(child)); // nothing left to move

        Assert.Equal(
            new[] { "steam:1234", "steam:99", LinuxGameLibraryStore.GameKey(child), Legacy(notFound), Legacy(sameFolderOtherApp) }.Order(),
            state.FavouriteGames.Order());
        Assert.Equal(["steam:1234"], state.HiddenGames);
        Assert.Equal("Steam", state.GameEdits["steam:1234"].LaunchProvider);
        Assert.Equal(LinuxHdrModes.Gamescope, state.HdrProfiles["steam:1234"].Mode);
        // The profile already under the new key wins; the older one is not dropped.
        Assert.Equal(LinuxHdrModes.Gamescope, state.HdrProfiles["steam:99"].Mode);
        Assert.Equal(LinuxHdrModes.NativeWayland, state.HdrProfiles[Legacy(here)].Mode);
    }

    [Fact]
    public void MergedDuplicate_OlderAndCurrentKeys_AreBothAdopted()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Indie")).FullName;
        var alias = new LibraryEntryAlias("Manual", folder, null);
        var card = new DetectedGame { Name = "Indie", Source = "Heroic", HeroicAppName = "indie", InstallPath = folder, MergedAliases = [alias] };
        var state = new LinuxGameLibraryState();
        state.FavouriteGames.Add(LinuxGameLibraryStore.LegacyGameKey(alias));
        state.HiddenGames.Add(LinuxGameLibraryStore.GameKey(alias));

        Assert.True(state.AdoptOlderKeys(card));

        Assert.Equal(["heroic:indie"], state.FavouriteGames);
        Assert.Equal(["heroic:indie"], state.HiddenGames);
    }

    // ---- A patch target left at the old folder ----------------------------------------------------

    /// <summary>A Steam game with an Edit game patch target in a sub-folder, at <paramref name="library"/>; files created.</summary>
    private (DetectedGame Game, LinuxGameEditOverride Edit) SteamWithEdit(string library)
    {
        var game = Steam(library);
        var binaries = Directory.CreateDirectory(Path.Combine(game.InstallPath, "Game", "Binaries", "Win64")).FullName;
        File.WriteAllText(Path.Combine(game.InstallPath, "Launcher.exe"), "MZ");
        var exe = Path.Combine(binaries, "Game-Win64-Shipping.exe");
        File.WriteAllText(exe, "MZ");
        return (game, new LinuxGameEditOverride
        {
            LaunchProvider = "Steam", GameRootPath = Path.Combine(game.InstallPath, "Game"), BinaryDirectory = binaries, PatchExecutablePath = exe
        }.Normalize());
    }

    [Fact]
    public void PatchTargetWhoseFolderIsGone_IsNotReplacedByAnAutomaticGuess()
    {
        var (_, edit) = SteamWithEdit(Path.Combine(_root, "a", "lib"));
        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        var moved = Steam(Path.Combine(_root, "b", "lib"));
        moved.ExecutablePath = Path.Combine(moved.InstallPath, "Launcher.exe");

        LinuxGameIdentityService.ApplyDetectedIdentity(moved, edit);

        Assert.False(moved.HasConfirmedPatchTarget);
        Assert.Null(moved.BinaryDirectory);
        Assert.Equal(Path.Combine(_root, "b", "lib", "steamapps", "common", "Game"), moved.InstallPath);
        Assert.Equal("Steam", moved.LaunchProvider);
    }

    [Fact]
    public void MovedSteamLibrary_WithoutComponents_RepairRepointsTheEditAndKeepsEverythingElse()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var (game, edit) = SteamWithEdit(Path.Combine(_root, "a", "lib"));
        var key = LinuxGameLibraryStore.GameKey(game);
        var state = store.Load();
        state.FavouriteGames.Add(key);
        state.HiddenGames.Add(key);
        state.GameEdits[key] = edit;
        state.HdrProfiles[key] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope };
        store.Save(state);
        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        var moved = Steam(Path.Combine(_root, "b", "lib"));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        var relocation = new DeploymentRelocation(records);

        Assert.False(relocation.NeedsRepair(moved)); // no records to show the move…
        Assert.True(relocation.NeedsRepair(moved, [store])); // …but the edit still names the old folder
        var result = relocation.Repair(moved, [store], out _);

        Assert.True(result.Success, result.Message);
        Assert.Equal(DeploymentRelocation.RepairSettingsDone, result.Message);
        var after = store.Load();
        Assert.Equal([key], after.FavouriteGames);
        Assert.Equal([key], after.HiddenGames);
        Assert.Equal(LinuxHdrModes.Gamescope, after.HdrProfiles[key].Mode);
        var newGame = Path.Combine(moved.InstallPath, "Game");
        Assert.Equal((newGame, Path.Combine(newGame, "Binaries", "Win64"), Path.Combine(newGame, "Binaries", "Win64", "Game-Win64-Shipping.exe")),
            (after.GameEdits[key].GameRootPath, after.GameEdits[key].BinaryDirectory, after.GameEdits[key].PatchExecutablePath));
        Assert.False(relocation.NeedsRepair(moved, [store]));

        LinuxGameIdentityService.ApplyDetectedIdentity(moved, after.GameEdits[key]);
        Assert.True(moved.HasConfirmedPatchTarget);
        Assert.Equal(newGame, moved.InstallPath);
    }

    [Fact]
    public void MovedEditTarget_MissingAtTheNewLocation_RepairChangesNothing()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var (game, edit) = SteamWithEdit(Path.Combine(_root, "a", "lib"));
        var state = store.Load();
        state.GameEdits[LinuxGameLibraryStore.GameKey(game)] = edit;
        store.Save(state);
        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        File.Delete(Path.Combine(_root, "b", "lib", "steamapps", "common", "Game", "Game", "Binaries", "Win64", "Game-Win64-Shipping.exe"));
        var before = File.ReadAllBytes(store.LibraryPath);

        var result = new DeploymentRelocation(new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json")))
            .Repair(Steam(Path.Combine(_root, "b", "lib")), [store], out _);

        Assert.False(result.Success);
        Assert.StartsWith(DeploymentRelocation.RepairMissingAfterMovePrefix, result.Message);
        Assert.Equal(before, File.ReadAllBytes(store.LibraryPath));
    }

    [Fact]
    public void FolderKeyedGame_StateUnderAnOlderKey_FollowsItOnRepair()
    {
        var oldFolder = Directory.CreateDirectory(Path.Combine(_root, "old", "Games", "Indie")).FullName;
        var game = new DetectedGame { Name = "Indie", Source = "Manual", InstallPath = oldFolder, LibraryIdentityPath = oldFolder };
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var state = store.Load();
        state.FavouriteGames.Add(LinuxGameLibraryStore.LegacyGameKey(game));
        state.HdrProfiles[LinuxGameLibraryStore.LegacyGameKey(game)] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope };
        store.Save(state);
        Directory.Move(Path.Combine(_root, "old"), Path.Combine(_root, "new"));
        var newFolder = Path.Combine(_root, "new", "Games", "Indie");
        var moved = new DetectedGame { Name = "Indie", Source = "Manual", InstallPath = newFolder, LibraryIdentityPath = newFolder };

        var write = store.StageMove(new GameStateMove(moved, [(Path.Combine(_root, "old"), Path.Combine(_root, "new"))], []));
        Assert.NotNull(write);
        RHI.Platform.Linux.Persistence.StagedFileCommit.Commit([write!]);

        var after = store.Load();
        Assert.Equal([LinuxGameLibraryStore.GameKey(moved)], after.FavouriteGames);
        Assert.Equal(LinuxHdrModes.Gamescope, after.HdrProfiles[LinuxGameLibraryStore.GameKey(moved)].Mode);
    }
}

/// <summary>#147 through the view model: a Steam game found in another library keeps the state an older build saved.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LibraryStateRelocationViewModelTests
{
    [Fact]
    public async Task SteamGameFoundInAnotherLibrary_KeepsItsFavouriteAndHidden_AndTheKeysAreMigrated()
    {
        var newLibrary = Path.Combine(Path.GetTempPath(), $"rhi-moved-{Guid.NewGuid():N}", "lib");
        var install = Directory.CreateDirectory(Path.Combine(newLibrary, "steamapps", "common", "Tunic")).FullName;
        File.WriteAllBytes(Path.Combine(install, "Tunic.exe"), [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = "Tunic", Source = "Steam", SteamAppId = 553420, InstallPath = install, ExecutablePath = Path.Combine(install, "Tunic.exe"),
            BinaryDirectory = install, Runner = "Proton"
        };
        var oldKey = LinuxGameLibraryStore.LegacyGameKey("Steam", "/media/old-disk/SteamLibrary/steamapps/common/Tunic", 553420);
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game], seed: library =>
            {
                var state = library.Load();
                state.FavouriteGames.Add(oldKey);
                state.HiddenGames.Add(oldKey);
                library.Save(state);
            });

            var saved = fx.Library.Load();
            Assert.Equal(["steam:553420"], saved.FavouriteGames);
            Assert.Equal(["steam:553420"], saved.HiddenGames);
            Assert.DoesNotContain(fx.Vm.Games, g => g.Name == "Tunic"); // hidden
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(newLibrary)!, true); } catch (IOException) { }
        }
    }

    // The view model asks for "Repair after move" with its own stores too: a moved Steam game whose Edit game target
    // still names the old library shows the button with no RHI records, and the repair re-points the target.
    [Fact]
    public async Task MovedGameWithAStaleEditTarget_ShowsRepairAfterMove_AndTheRepairRestoresItsPatchTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-moved-{Guid.NewGuid():N}");
        var install = Directory.CreateDirectory(Path.Combine(root, "new", "steamapps", "common", "Tunic")).FullName;
        var binaries = Directory.CreateDirectory(Path.Combine(install, "Game", "Win64")).FullName;
        File.WriteAllBytes(Path.Combine(binaries, "Tunic.exe"), [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = "Tunic", Source = "Steam", SteamAppId = 553420, InstallPath = install, ExecutablePath = Path.Combine(binaries, "Tunic.exe"),
            BinaryDirectory = binaries, Runner = "Proton"
        };
        var oldGame = Path.Combine(root, "old", "steamapps", "common", "Tunic", "Game");
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game], seed: library =>
            {
                var state = library.Load();
                state.GameEdits["steam:553420"] = new LinuxGameEditOverride
                {
                    GameRootPath = oldGame, BinaryDirectory = Path.Combine(oldGame, "Win64"), PatchExecutablePath = Path.Combine(oldGame, "Win64", "Tunic.exe")
                };
                library.Save(state);
            });
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic");
            Assert.False(fx.Vm.SelectedGame!.HasConfirmedPatchTarget); // the old target is not replaced by a guess
            await WaitAsync(() => fx.Vm.ShowRepairAfterMove);

            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };

            await fx.Vm.RepairAfterMoveCommand.ExecuteAsync(null);

            // A later "Ready" line from the new probe may replace it, so look for it among the lines shown.
            Assert.Contains(lines, line => line.Contains("Tunic: RHI's settings now point to the new folder.", StringComparison.Ordinal));
            var edit = fx.Library.Load().GameEdits["steam:553420"];
            Assert.Equal(Path.Combine(install, "Game"), edit.GameRootPath);
            Assert.True(fx.Vm.SelectedGame!.HasConfirmedPatchTarget);
            Assert.Equal(binaries, fx.Vm.SelectedGame.BinaryDirectory);
            await WaitAsync(() => !fx.Vm.ShowRepairAfterMove);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "timed out");
            await Task.Delay(20);
        }
    }
}
