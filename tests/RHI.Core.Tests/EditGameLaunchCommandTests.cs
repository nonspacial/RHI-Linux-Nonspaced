using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w2 final review, major 1: Edit game has the editable launch command the Play tooltip sends the user to. It is saved
/// in the game's library edit (trimmed, blank = none), replaces the detected command on load and rescan, follows a
/// library move, is dropped by Reset, and is classified like any command (a direct command on a store card runs
/// directly, in a login shell like an Add game command).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class EditGameLaunchCommandTests
{
    private const int TunicAppId = 553420;

    [Fact]
    public async Task TypingACommandInEditGame_EnablesPlay_AndItSurvivesARescanAndASteamLibraryMove()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-edit-cmd-{Guid.NewGuid():N}");
        var games = new List<DetectedGame> { SteamTunic(Path.Combine(root, "a")) };
        const string typed = "  gamemoderun wine \"C:\\\\Tunic\\\\Tunic.exe\"  ";
        const string command = "gamemoderun wine \"C:\\\\Tunic\\\\Tunic.exe\"";
        try
        {
            using var fx = await ViewModelFixture.StartAsync(games);
            await SelectAsync(fx, "Tunic");
            Assert.False(fx.Vm.CanLaunch);
            Assert.Equal(UiLocalizationService.Text("Vm.NoLaunchCommandSetOneInEdit"), fx.Vm.LaunchToolTip);

            var card = fx.Vm.SelectedGame!;
            var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(card.ExecutablePath!, card.InstallPath, card.BinaryDirectory!, null, typed);

            Assert.True(saved, message);
            Assert.Equal("Tunic: changes saved. Play runs the launch command you entered.", message);
            Assert.Equal(command, fx.Library.Load().GameEdits[$"steam:{TunicAppId}"].LaunchCommand); // trimmed
            await SelectAsync(fx, "Tunic");
            AssertPlaysTheUsersCommand(fx, command);

            // A full rescan builds the card again from the detector; the saved command applies again.
            await fx.Vm.RefreshCommand.ExecuteAsync(null);
            await SelectAsync(fx, "Tunic");
            AssertPlaysTheUsersCommand(fx, command);

            // The Steam library moves: the edit's target names the old folder until Repair after move re-points it,
            // and the command goes along with the rest of the edit.
            var moved = SteamTunic(Path.Combine(root, "b"));
            Directory.Delete(Path.Combine(root, "a"), recursive: true);
            games[0] = moved;
            await fx.Vm.RefreshCommand.ExecuteAsync(null);
            await SelectAsync(fx, "Tunic");
            AssertPlaysTheUsersCommand(fx, command);
            await WaitAsync(() => fx.Vm.ShowRepairAfterMove);

            await fx.Vm.RepairAfterMoveCommand.ExecuteAsync(null);

            await WaitAsync(() => !fx.Vm.IsSelectionProbeBusy && !fx.Vm.ShowRepairAfterMove);
            var edit = fx.Library.Load().GameEdits[$"steam:{TunicAppId}"];
            Assert.Equal(moved.InstallPath, edit.GameRootPath);
            Assert.Equal(command, edit.LaunchCommand);
            Assert.True(fx.Vm.SelectedGame!.HasConfirmedPatchTarget);
            AssertPlaysTheUsersCommand(fx, command);
            await fx.Vm.RefreshCommand.ExecuteAsync(null);
            await SelectAsync(fx, "Tunic");
            AssertPlaysTheUsersCommand(fx, command);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ClearingTheCommand_OrTypingTheDetectedOne_OrResetting_RestoresTheDetectedCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-edit-cmd-{Guid.NewGuid():N}");
        var detected = $"steam -applaunch {TunicAppId}";
        var game = SteamTunic(root);
        game.LaunchCommand = detected;
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game]);
            await SelectAsync(fx, "Tunic");
            Assert.Equal(detected, fx.Vm.SelectedGame!.LaunchCommand);
            Assert.True(fx.Vm.CanLaunch);

            async Task<string> SaveAsync(string? command)
            {
                var card = fx.Vm.SelectedGame!;
                var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(card.ExecutablePath!, card.InstallPath, card.BinaryDirectory!, null, command);
                Assert.True(saved, message);
                await SelectAsync(fx, "Tunic");
                return message;
            }

            await SaveAsync("mangohud wine Tunic.exe");
            Assert.Equal("mangohud wine Tunic.exe", fx.Vm.SelectedGame!.LaunchCommand);
            Assert.Equal(LaunchHandoff.None, LaunchHandoffClassifier.Classify(fx.Vm.SelectedGame));

            // Whitespace only is no command: Play goes back to Steam's command (the edit keeps its patch target).
            Assert.Equal("Tunic: changes saved. Play uses the detected launch command again.", await SaveAsync(" \t "));
            Assert.Equal(detected, fx.Vm.SelectedGame!.LaunchCommand);
            Assert.Null(fx.Vm.SelectedGame.UserLaunchCommand);
            Assert.Null(fx.Library.Load().GameEdits[$"steam:{TunicAppId}"].LaunchCommand);
            Assert.Equal(LaunchHandoff.Steam, LaunchHandoffClassifier.Classify(fx.Vm.SelectedGame));

            // The dialog shows the command that runs; saving it unchanged pins nothing.
            await SaveAsync(detected);
            Assert.Null(fx.Library.Load().GameEdits[$"steam:{TunicAppId}"].LaunchCommand);
            Assert.Null(fx.Vm.SelectedGame!.UserLaunchCommand);

            // Reset drops the command with the rest of the edit.
            await SaveAsync("wine Tunic.exe");
            Assert.Equal("wine Tunic.exe", fx.Vm.SelectedGame!.LaunchCommand);
            var (reset, resetMessage) = await fx.Vm.ResetSelectedGameEditAsync();
            Assert.True(reset, resetMessage);
            await SelectAsync(fx, "Tunic");
            Assert.Empty(fx.Library.Load().GameEdits);
            Assert.Equal(detected, fx.Vm.SelectedGame!.LaunchCommand);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // #151 part 3: blank target fields with a new command keep the saved target, and the status line says so; a card
    // without a saved target keeps the plain line (there is no target to keep).
    [Fact]
    public async Task BlankTargetFieldsWithANewCommand_KeepTheSavedTarget_AndTheStatusLineSaysSo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-edit-cmd-{Guid.NewGuid():N}");
        var install = Directory.CreateDirectory(Path.Combine(root, "Kept Game")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(install, "bin")).FullName;
        var exe = Path.Combine(bin, "Kept.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame { Name = "Kept Game", Source = "Wine", InstallPath = install, Runner = "wine", LaunchCommand = "wine Kept.exe" };
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game], seed: library =>
            {
                var state = library.Load();
                state.GameEdits[LinuxGameLibraryStore.GameKey(game)] = new LinuxGameEditOverride
                {
                    PatchExecutablePath = exe, GameRootPath = install, BinaryDirectory = bin
                };
                library.Save(state);
            });
            await SelectAsync(fx, "Kept Game");

            var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync("", " ", "", null, "my-wrapper wine Kept.exe");

            Assert.True(saved, message);
            Assert.Equal(UiLocalizationService.Format("Vm.EditSavedTargetKeptPlayUsesYourCommand", "Kept Game"), message);
            Assert.StartsWith(message, fx.Vm.StatusText, StringComparison.Ordinal);
            var edit = Assert.Single(fx.Library.Load().GameEdits).Value;
            Assert.Equal((exe, install, bin, "my-wrapper wine Kept.exe"), (edit.PatchExecutablePath, edit.GameRootPath, edit.BinaryDirectory, edit.LaunchCommand));

            await SelectAsync(fx, "Kept Game");
            var (back, again) = await fx.Vm.SaveSelectedGameEditAsync("", "", "", null, "");
            Assert.True(back, again);
            Assert.Equal(UiLocalizationService.Format("Vm.EditSavedTargetKeptPlayUsesDetectedCommand", "Kept Game"), again);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // A launcher card (no patch target to show, e.g. a Lutris game inside a launcher's prefix) gets a command with the
    // three target fields left blank; blank fields with no new command still ask for the target as before.
    [Fact]
    public async Task ALauncherCardWithoutATarget_TakesACommandAlone_AndKeepsItsMissingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-edit-cmd-{Guid.NewGuid():N}");
        var install = Directory.CreateDirectory(Path.Combine(root, "Launcher Game")).FullName;
        var game = new DetectedGame
        {
            Name = "Launcher Game", Source = "Lutris", InstallPath = install, RequiresExplicitPatchTarget = true, Runner = "wine"
        };
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game]);
            await SelectAsync(fx, "Launcher Game");
            Assert.False(fx.Vm.CanLaunch);

            var (refused, why) = await fx.Vm.SaveSelectedGameEditAsync("", "", "", null, null);
            Assert.False(refused);
            Assert.Equal(UiLocalizationService.Text("Vm.SelectRealGameExecutableShouldReceive"), why);

            var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(" ", "", "", null, "lutris lutris:rungame/launcher-game");
            Assert.True(saved, message);
            Assert.Equal(UiLocalizationService.Format("Vm.EditSavedPlayUsesYourCommand", "Launcher Game"), message);
            await SelectAsync(fx, "Launcher Game");
            Assert.True(fx.Vm.CanLaunch);
            Assert.Null(fx.Vm.LaunchToolTip);
            Assert.False(fx.Vm.SelectedGame!.HasConfirmedPatchTarget);
            var edit = Assert.Single(fx.Library.Load().GameEdits).Value;
            Assert.False(edit.HasPatchTarget);
            Assert.Equal("lutris lutris:rungame/launcher-game", edit.LaunchCommand);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // w2 final review, nit 3: the reason arms use CanLaunch's own blank test, so a blank command disables Play with a reason.
    [Fact]
    public async Task ABlankCommand_DisablesPlay_AndTheTooltipSaysWhy()
    {
        var game = new DetectedGame { Name = "Blank Command", Source = "Wine", InstallPath = Directory.CreateTempSubdirectory("rhi-blank-").FullName, LaunchCommand = "   " };
        using var fx = await ViewModelFixture.StartAsync([game]);
        await SelectAsync(fx, "Blank Command");

        Assert.False(fx.Vm.CanLaunch);
        Assert.Equal(UiLocalizationService.Text("Vm.NoLaunchCommandSetOneInEdit"), fx.Vm.LaunchToolTip);
    }

    [Fact]
    public void TheEditCommand_IsTrimmed_BlankIsNone_AndAnEditHoldingOnlyACommandIsKept()
    {
        var state = new LinuxGameLibraryState();
        state.GameEdits["steam:1"] = new LinuxGameEditOverride { LaunchCommand = "  wine x.exe \n" };
        state.GameEdits["steam:2"] = new LinuxGameEditOverride { LaunchCommand = " \t " };
        state.ManualGames.Add(new LinuxManualGameEntry { Name = "M", InstallPath = "/games/m", LaunchCommand = "  run-m  " });

        var copy = LinuxGameLibraryStore.Copy(state.Normalize());

        Assert.Equal("wine x.exe", Assert.Single(copy.GameEdits).Value.LaunchCommand);
        Assert.Equal("run-m", Assert.Single(copy.ManualGames).LaunchCommand);
        Assert.DoesNotContain("IsEmpty", JsonSerializer.Serialize(copy), StringComparison.Ordinal);
        Assert.Null(LinuxGameEditOverride.NormalizeLaunchCommand("   "));
    }

    // The command is applied by identity resolution, so everything downstream reads the command that runs: the hand-off
    // classifier (a store card's direct command is direct, a store URL is that store), the working directory, the login
    // shell for a user-typed command, and the parent launcher (which no longer starts the game).
    [Theory]
    [InlineData("wine /games/x/x.exe", LaunchHandoff.None)]
    [InlineData("gamemoderun steam -applaunch 7", LaunchHandoff.Steam)]
    [InlineData("xdg-open heroic://launch/legendary/abc", LaunchHandoff.Heroic)]
    public void AUserCommandOnAStoreCard_IsClassifiedByTheEstablishedRule_AndRunsInALoginShell(string command, LaunchHandoff expected)
    {
        var install = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rhi-edit-cmd-{Guid.NewGuid():N}")).FullName;
        try
        {
            var game = new DetectedGame
            {
                Name = "Launcher Game", Source = "Lutris", InstallPath = install, LaunchCommand = "lutris lutris:rungame/ubisoft-connect",
                ParentLauncherName = "Ubisoft Connect", LaunchExecutablePath = Path.Combine(install, "game.exe")
            };
            Assert.Equal(LaunchHandoff.Lutris, LaunchHandoffClassifier.Classify(game));

            LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { LaunchCommand = " " + command + " " });

            Assert.Equal(command, game.LaunchCommand);
            Assert.Null(game.ParentLauncherName);
            Assert.Equal(expected, LaunchHandoffClassifier.Classify(game));
            Assert.Equal(expected == LaunchHandoff.None ? install : null, LinuxGameLauncher.ResolveWorkingDirectory(game));
            Assert.Equal("-lc", LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).ArgumentList[0]);

            // Resolving the same card again (Repair after move) keeps the detected pair; without the command it is back.
            LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { LaunchCommand = command });
            Assert.Equal(new DetectedLaunch("lutris lutris:rungame/ubisoft-connect", "Ubisoft Connect"), game.DetectedLaunch);
            LinuxGameIdentityService.ApplyDetectedIdentity(game, new LinuxGameEditOverride { LaunchProvider = "Ubisoft" });
            Assert.Equal("lutris lutris:rungame/ubisoft-connect", game.LaunchCommand);
            Assert.Equal("Ubisoft Connect", game.ParentLauncherName);
            Assert.Null(game.UserLaunchCommand);
            Assert.Equal(LaunchHandoff.Lutris, LaunchHandoffClassifier.Classify(game));
            Assert.Equal("-c", LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions()).ArgumentList[0]);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    private static void AssertPlaysTheUsersCommand(ViewModelFixture fx, string command)
    {
        var card = fx.Vm.SelectedGame!;
        Assert.Equal(command, card.LaunchCommand);
        Assert.Equal(command, card.UserLaunchCommand);
        Assert.True(fx.Vm.CanLaunch);
        Assert.True(fx.Vm.LaunchCommand.CanExecute(null));
        Assert.Null(fx.Vm.LaunchToolTip);
    }

    /// <summary>A Steam Tunic (no launch command) under <paramref name="library"/>, files created.</summary>
    private static DetectedGame SteamTunic(string library)
    {
        var install = Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "Tunic")).FullName;
        var exe = Path.Combine(install, "Tunic.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = "Tunic", Source = "Steam", SteamAppId = TunicAppId, InstallPath = install, ExecutablePath = exe, BinaryDirectory = install,
            Runner = "Proton"
        };
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++)
            await Task.Delay(25);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
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
