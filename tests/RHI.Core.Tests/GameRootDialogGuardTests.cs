using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #59 at the view-model seam (w1/detect review findings 2, 9, 10): Add and Edit refuse a whole Wine
/// prefix, $HOME and / as a game folder and leave the library untouched; an older saved entry with such a
/// folder stays listed as "folder not allowed" and can be removed; the dialogs can close as soon as their
/// change is saved instead of waiting, disabled, for a running operation.
/// Assertions are structural (flags and stores), never on localised text.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GameRootDialogGuardTests
{
    private static string Exe(string directory, string name = "Game.exe")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', .. new byte[4096]]);
        return path;
    }

    /// <summary>A Wine prefix root holding a game, and that game's exe.</summary>
    private static (string Prefix, string GameExe) PrefixWithGame(string root)
    {
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n");
        return (prefix, Exe(Path.Combine(prefix, "drive_c", "Games", "Inside")));
    }

    private static string Snapshot(string path) => File.Exists(path) ? File.ReadAllText(path) : "<none>";

    private static string HomeGameExe() => Exe(Path.Combine(LinuxPaths.Home, $"rhi-guard-{Guid.NewGuid():N}"));

    public static TheoryData<string> RefusedRoots => new() { "prefix", "home", "root" };

    private static (string Root, string Exe) Refused(string kind, ViewModelFixture fixture) => kind switch
    {
        "prefix" => PrefixWithGame(fixture.Root),
        "home" => (LinuxPaths.Home, HomeGameExe()),
        _ => ("/", Exe(Path.Combine(fixture.Root, "anywhere")))
    };

    [Theory]
    [MemberData(nameof(RefusedRoots))]
    public async Task AddManualGame_RefusesTheFolder_AndLeavesTheLibraryUnchanged(string kind)
    {
        using var fixture = await ViewModelFixture.StartAsync([]);
        var (root, exe) = Refused(kind, fixture);
        Assert.NotEqual(GameRootProblem.None, GameRootGuard.Check(root));
        var before = Snapshot(fixture.Library.LibraryPath);
        var saved = false;

        var (added, message) = await fixture.Vm.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Guarded", InstallPath = root, ExecutablePath = exe }, () => saved = true);

        Assert.False(added);
        Assert.False(saved);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(fixture.Library.Load().ManualGames);
        Assert.Equal(before, Snapshot(fixture.Library.LibraryPath));
        Assert.DoesNotContain(fixture.Vm.Games, g => g.Name == "Guarded");
    }

    [Fact]
    public async Task AddManualGame_ASymlinkSpellingOfAListedFolder_IsRefusedAsADuplicate()
    {
        // The scan merges cards by PathIdentity; the Add check must agree (integration w1 detect review 7).
        using var fixture = await ViewModelFixture.StartAsync([]);
        var tunic = fixture.Game("Tunic");
        var (first, firstMessage) = await fixture.Vm.TryAddManualGameAsync(new LinuxManualGameEntry { Name = "Tunic", InstallPath = tunic.InstallPath, ExecutablePath = tunic.ExecutablePath });
        Assert.True(first, firstMessage);
        var link = Path.Combine(fixture.Root, "games-link");
        Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, "games"));

        var (added, message) = await fixture.Vm.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Tunic again", InstallPath = Path.Combine(link, "Tunic") + "/" });

        Assert.False(added);
        Assert.Equal("That game directory is already in the library.", message);
        Assert.Equal(["Tunic"], fixture.Library.Load().ManualGames.Select(g => g.Name));
    }

    [Fact]
    public async Task AddManualGame_ASymlinkSpellingOfASavedEntry_IsRefusedAsAlreadySaved()
    {
        // Add checks the working library copy, which is the in-memory state except when that only stands in for an
        // unreadable file: then the file is read again. A saved entry the scan has not listed yet is reached that way
        // (the user restored the file while RHI ran), and its symlink spelling must still read as "already saved".
        using var fixture = await ViewModelFixture.StartAsync([], seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
        var hades = fixture.Game("Hades");
        File.WriteAllText(fixture.Library.LibraryPath, System.Text.Json.JsonSerializer.Serialize(new LinuxGameLibraryState
        {
            ManualGames = [new LinuxManualGameEntry { Name = "Hades", InstallPath = hades.InstallPath }]
        }));
        var link = Path.Combine(fixture.Root, "games-link");
        Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, "games"));

        var (added, message) = await fixture.Vm.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Hades again", InstallPath = Path.Combine(link, "Hades") });

        Assert.False(added);
        Assert.Equal("That manual game is already saved.", message);
        Assert.Single(fixture.Library.Load().ManualGames);
    }

    [Fact]
    public async Task AddManualGame_ThroughASymlinkedFolder_SelectsTheNewCard()
    {
        using var fixture = await ViewModelFixture.StartAsync([]);
        var celeste = fixture.Game("Celeste");
        var link = Path.Combine(fixture.Root, "games-link");
        Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, "games"));

        var (added, message) = await fixture.Vm.TryAddManualGameAsync(new LinuxManualGameEntry
        {
            Name = "Celeste", InstallPath = Path.Combine(link, "Celeste"), ExecutablePath = Path.Combine(link, "Celeste", "Celeste.exe")
        });

        Assert.True(added, message);
        Assert.Equal("Celeste", fixture.Vm.SelectedGame?.Name);
    }

    [Theory]
    [MemberData(nameof(RefusedRoots))]
    public async Task EditGame_RefusesTheFolder_AndSavesNoOverride(string kind)
    {
        using var fixture = await ViewModelFixture.StartAsync([]);
        var listed = Exe(Path.Combine(fixture.Root, "games", "Listed"));
        var state = fixture.Library.Load();
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "Listed", InstallPath = Path.GetDirectoryName(listed)!, ExecutablePath = listed });
        fixture.Library.Save(state);
        await fixture.Vm.RefreshCommand.ExecuteAsync(null);
        fixture.Vm.SelectedGame = Assert.Single(fixture.Vm.Games, g => g.Name == "Listed").Game;
        // The exe and binaries folder sit inside the refused root, so only the root guard can refuse.
        var (root, exe) = Refused(kind, fixture);

        var (savedEdit, message) = await fixture.Vm.SaveSelectedGameEditAsync(exe, root, Path.GetDirectoryName(exe)!, null, null);

        Assert.False(savedEdit);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(fixture.Library.Load().GameEdits);
    }

    [Fact]
    public async Task SavedEntriesWithARefusedFolder_StayListedAsNotAllowed_AndCanBeRemoved()
    {
        using var fixture = await ViewModelFixture.StartAsync([], seed: store =>
        {
            var state = store.Load();
            state.ManualGames.Add(new LinuxManualGameEntry { Name = "Whole Home", InstallPath = LinuxPaths.Home, LaunchCommand = "true" });
            var prefix = PrefixWithGame(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(store.LibraryPath)!, ".."))).Prefix;
            state.ManualGames.Add(new LinuxManualGameEntry { Name = "Whole Prefix", InstallPath = prefix });
            store.Save(state);
        });

        var home = Assert.Single(fixture.Vm.Games, g => g.Name == "Whole Home").Game;
        var whole = Assert.Single(fixture.Vm.Games, g => g.Name == "Whole Prefix").Game;
        foreach (var game in new[] { home, whole })
        {
            Assert.True(game.GameRootNotAllowed);
            Assert.False(game.HasConfirmedPatchTarget);
            Assert.True(game.IsManuallyAdded);
            Assert.Contains("⚠", game.LibraryBadge);
        }
        Assert.Equal(LinuxPaths.Home, home.InstallPath);
        Assert.Equal("true", home.LaunchCommand);

        fixture.Vm.SelectedGame = home;
        Assert.Contains("Whole Home", fixture.Vm.PatchTargetStatusText);
        Assert.Contains(LinuxPaths.Home, fixture.Vm.SelectedInstallPath);
        Assert.True(fixture.Vm.CanRemoveManualGame);
        await fixture.Vm.RemoveManualGameCommand.ExecuteAsync(null);

        Assert.Equal(["Whole Prefix"], fixture.Library.Load().ManualGames.Select(e => e.Name));
        Assert.DoesNotContain(fixture.Vm.Games, g => g.Name == "Whole Home");
    }

    // ---- #145: a saved manual entry whose folder is gone stays listed as "folder missing" ----

    private static Task<ViewModelFixture> WithMissingFolderEntry(out string missing)
    {
        var gone = Path.Combine(Path.GetTempPath(), "rhi-gone-" + Guid.NewGuid().ToString("N"), "Old Drive", "Gone Game");
        missing = gone;
        return ViewModelFixture.StartAsync([], seed: store =>
        {
            var state = store.Load();
            state.ManualGames.Add(new LinuxManualGameEntry
            {
                Name = "Gone Game", InstallPath = gone, ExecutablePath = Path.Combine(gone, "Gone.exe"), LaunchCommand = "true"
            });
            store.Save(state);
        });
    }

    [Fact]
    public async Task MissingFolderEntry_IsListedAsFolderMissing_NotProbed_AndRemovable()
    {
        using var fixture = await WithMissingFolderEntry(out var missing);

        var game = Assert.Single(fixture.Vm.Games, g => g.Name == "Gone Game").Game;
        Assert.True(game.FolderMissing);
        Assert.False(game.GameRootNotAllowed);
        Assert.False(game.HasConfirmedPatchTarget);
        Assert.Null(game.ExecutablePath);
        Assert.Null(game.BinaryDirectory);
        Assert.Contains("⚠", game.LibraryBadge);
        Assert.Equal("true", game.LaunchCommand);

        fixture.Vm.SelectedGame = game;
        Assert.Equal($"Gone Game: folder missing ({missing}). Remove the manual entry or use Edit game to point it at the new folder; nothing was indexed or changed.",
            fixture.Vm.PatchTargetStatusText);
        Assert.Equal($"{missing} — folder missing", fixture.Vm.SelectedInstallPath);
        // Not probed: with no patch target the selection analysis indexes nothing and detects no API.
        await Task.Delay(300);
        Assert.Equal(GraphicsApi.Unknown, game.GraphicsApi);
        Assert.Null(game.BinaryDirectory);

        Assert.True(fixture.Vm.CanRemoveManualGame);
        await fixture.Vm.RemoveManualGameCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Library.Load().ManualGames);
        Assert.DoesNotContain(fixture.Vm.Games, g => g.Name == "Gone Game");
    }

    [Fact]
    public async Task MissingFolderEntry_EditedOntoTheNewFolder_IsUsableAgain()
    {
        using var fixture = await WithMissingFolderEntry(out _);
        fixture.Vm.SelectedGame = Assert.Single(fixture.Vm.Games, g => g.Name == "Gone Game").Game;
        var exe = Exe(Path.Combine(fixture.Root, "New Drive", "Gone Game"), "Gone.exe");

        var (saved, message) = await fixture.Vm.SaveSelectedGameEditAsync(exe, Path.GetDirectoryName(exe)!, Path.GetDirectoryName(exe)!, null, null);

        Assert.True(saved, message);
        var repointed = Assert.Single(fixture.Vm.Games, g => g.Name == "Gone Game").Game;
        Assert.False(repointed.FolderMissing);
        Assert.True(repointed.HasConfirmedPatchTarget);
        Assert.Equal(Path.GetDirectoryName(exe), repointed.InstallPath);
    }

    [Fact]
    public async Task EditingARefusedEntryOntoTheGamesOwnFolder_ClearsTheState()
    {
        using var fixture = await ViewModelFixture.StartAsync([], seed: store =>
        {
            var state = store.Load();
            state.ManualGames.Add(new LinuxManualGameEntry { Name = "Whole Home", InstallPath = LinuxPaths.Home });
            store.Save(state);
        });
        fixture.Vm.SelectedGame = Assert.Single(fixture.Vm.Games, g => g.Name == "Whole Home").Game;
        var exe = HomeGameExe();

        var (saved, _) = await fixture.Vm.SaveSelectedGameEditAsync(exe, Path.GetDirectoryName(exe)!, Path.GetDirectoryName(exe)!, null, null);

        Assert.True(saved);
        var fixedGame = Assert.Single(fixture.Vm.Games, g => g.Name == "Whole Home").Game;
        Assert.False(fixedGame.GameRootNotAllowed);
        Assert.True(fixedGame.HasConfirmedPatchTarget);
        Assert.Equal(Path.GetDirectoryName(exe), fixedGame.InstallPath);
    }

    [Fact]
    public async Task AddAndEdit_ReportSavedBeforeTheRefreshWaitsForARunningOperation()
    {
        using var fixture = await ViewModelFixture.StartAsync([]);
        var exe = Exe(Path.Combine(fixture.Root, "games", "Busy Add"));
        var vm = fixture.Vm;

        var running = vm.Operations.TryBegin("test")!; // e.g. Update All running
        var addSaved = false;
        var add = vm.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Busy Add", InstallPath = Path.GetDirectoryName(exe)!, ExecutablePath = exe }, () => addSaved = true);

        Assert.True(addSaved);
        Assert.False(add.IsCompleted);
        Assert.Single(fixture.Library.Load().ManualGames);

        running.Dispose();
        var (added, _) = await add.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(added);

        vm.SelectedGame = Assert.Single(vm.Games, g => g.Name == "Busy Add").Game;
        running = vm.Operations.TryBegin("test")!;
        var editSaved = false;
        var edit = vm.SaveSelectedGameEditAsync(exe, Path.GetDirectoryName(exe)!, Path.GetDirectoryName(exe)!, null, null, () => editSaved = true);

        Assert.True(editSaved);
        Assert.False(edit.IsCompleted);
        running.Dispose();
        Assert.True((await edit.WaitAsync(TimeSpan.FromSeconds(30))).Saved);
    }
}
