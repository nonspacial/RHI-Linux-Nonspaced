using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-c (upstream 2.9.0+ per-game launch arguments) through the real view model: Game Overrides' box saves into the
/// library file, Launch hands the arguments to the launcher and says what happened to them, Reset launch options clears them
/// and the Edit game command (upstream: the executable), Management's Reset Overrides clears them, and the Tabs Launch menu
/// names the program.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LaunchArgumentsViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-args-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    // The expected lines are English: the UI language is process-wide and this collection's tests set it themselves.
    public LaunchArgumentsViewModelTests() => UiLocalizationService.Apply("en-US");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Box_SavesIntoTheLibraryFile_AndLaunchPassesTheArguments()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Prefix Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), launcher);
        var args = fx.Vm.LaunchArguments;
        Assert.True(args.Applies);
        Assert.Equal(UiLocalizationService.Text("LcArgs.RouteDirect"), args.Note);

        args.Text = "  -dx11 \"+exec auto.cfg\" ";
        args.Commit();

        Assert.Equal("-dx11 \"+exec auto.cfg\"", new LinuxGameLibraryStore(fx.Library.LibraryPath).Load().LaunchArguments!.Values.Single());
        Assert.StartsWith("Launch arguments saved for Prefix Quest: -dx11 \"+exec auto.cfg\"", fx.Vm.StatusText, StringComparison.Ordinal);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(["-dx11", "+exec auto.cfg"], launch.Options!.Arguments!);
        Assert.Contains("Launch arguments: -dx11 \"+exec auto.cfg\"", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArguments_LaunchAsBefore()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Plain Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), launcher);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(launcher.Launches).Options);
        Assert.DoesNotContain("Launch arguments", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherLauncher_KeepsTheArguments_AndLaunchSaysWhereToAddThem()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Heroic Quest", "Heroic", "xdg-open 'heroic://launch/legendary/abc'"), launcher);
        var args = fx.Vm.LaunchArguments;
        Assert.False(args.Applies);
        Assert.Equal(UiLocalizationService.Format("LcArgs.RouteHeroic", "Heroic Quest"), args.Note);

        args.Text = "-skipintro";
        args.Commit();
        Assert.Contains(UiLocalizationService.Format("LcArgs.RouteHeroic", "Heroic Quest"), fx.Vm.StatusText, StringComparison.Ordinal);
        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Single(launcher.Launches); // the hand-off still starts
        Assert.Contains("Launch arguments not added. Heroic starts this game", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextTypedForAGame_IsSavedForThatGame_WhenTheSelectionMovesOn()
    {
        var first = Game("First Quest", "Wine", "WINEPREFIX=/tmp/pfx wine A.exe");
        var second = Game("Second Quest", "Wine", "WINEPREFIX=/tmp/pfx wine B.exe");
        using var fx = await ViewModelFixture.StartAsync([first, second], launcher: new RecordingLauncher());
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "First Quest").Game;
        fx.Vm.LaunchArguments.Text = "-first";

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Second Quest").Game;

        Assert.Equal("", fx.Vm.LaunchArguments.Text);
        Assert.Equal("-first", fx.Vm.LaunchArguments.Saved(fx.Vm.Games.Single(g => g.Name == "First Quest").Game));
    }

    [Fact]
    public async Task ResetLaunchOptions_ClearsTheArgumentsAndTheEditGameCommand()
    {
        using var fx = await StartAsync(Game("Edited Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), new RecordingLauncher());
        var (saved, _) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, "my-wrapper wine Game.exe");
        Assert.True(saved);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Edited Quest").Game;
        Assert.Equal("my-wrapper wine Game.exe", fx.Vm.SelectedGame!.UserLaunchCommand);
        Assert.Equal(UiLocalizationService.Text("LcArgs.RouteUserCommand"), fx.Vm.LaunchArguments.Note);
        fx.Vm.LaunchArguments.Text = "-x";
        fx.Vm.LaunchArguments.Commit();

        await fx.Vm.LaunchArguments.ResetCommand.ExecuteAsync(null);

        var game = fx.Vm.Games.Single(g => g.Name == "Edited Quest").Game;
        Assert.Null(game.UserLaunchCommand);
        Assert.Equal("WINEPREFIX=/tmp/pfx wine Game.exe", game.LaunchCommand);
        Assert.Null(new LinuxGameLibraryStore(fx.Library.LibraryPath).Load().LaunchArguments);
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        Assert.Contains("Edited Quest: launch options reset (launch command, launch arguments).", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetOverrides_ClearsTheArguments()
    {
        using var fx = await StartAsync(Game("Override Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), new RecordingLauncher());
        fx.Vm.LaunchArguments.Text = "-x";
        fx.Vm.LaunchArguments.Commit();

        var line = await fx.Vm.ResetOverridesCoreAsync(fx.Vm.SelectedGame!);

        Assert.Equal("Override Quest: overrides reset (launch arguments).", line);
        Assert.Null(fx.Vm.LaunchArguments.Saved(fx.Vm.SelectedGame!));
        Assert.Equal("", fx.Vm.LaunchArguments.Text);
    }

    [Fact]
    public async Task TextRhiCannotPass_IsRefused_AndNothingIsSaved()
    {
        using var fx = await StartAsync(Game("Nul Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), new RecordingLauncher());
        fx.Vm.LaunchArguments.Text = "-a\0-b";
        fx.Vm.LaunchArguments.Commit();
        Assert.StartsWith(UiLocalizationService.Text("LcArgs.ProblemNul"), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Null(fx.Vm.LaunchArguments.Saved(fx.Vm.SelectedGame!));
        Assert.Equal("", fx.Vm.LaunchArguments.Text);
    }

    [Fact]
    public async Task LaunchMenu_NamesTheProgram_OrAutoDetect()
    {
        using var fx = await StartAsync(Game("Menu Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), new RecordingLauncher());
        Assert.Equal("Executable: MenuQuest.exe", fx.Vm.LaunchMenuExecutableText);
        await fx.Vm.ManualGames.SaveSelectedGameEditAsync("", "", "", null, "my-wrapper wine Game.exe");
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Menu Quest").Game;
        Assert.Equal("Executable: auto-detect", fx.Vm.LaunchMenuExecutableText);
    }

    [Fact]
    public void Strings_ExistInBothLanguages()
    {
        foreach (var key in new[] { "LcArgs.Label", "LcArgs.Reset", "LcArgs.RouteDirect", "LcArgs.RouteSteam", "LcArgs.RouteSteamLink", "LcArgs.RouteSteamShortcut",
                     "LcArgs.RouteUserCommand", "LcArgs.RouteHeroic", "LcArgs.RouteLutris", "LcArgs.RouteOtherLauncher", "LcArgs.RouteNoCommand",
                     "LcMenu.Executable", "LcMenu.Arguments", "DmItem.LaunchArgs", "DmHelp.LaunchArgs", "DmWhy.LaunchArgsNotOffered" })
        {
            // Read from the tables, not by switching the UI language (other tests read it).
            var row = Assert.Single(RHI.Desktop.Localization.LayoutsCUiStrings.Entries.Concat(RHI.Desktop.Localization.UiStrings.Entries), e => e.Key == key);
            Assert.False(string.IsNullOrWhiteSpace(row.EnUs));
            Assert.NotEqual(row.EnUs, row.PtBr);
        }
    }

    private static async Task<ViewModelFixture> StartAsync(DetectedGame game, RecordingLauncher launcher)
    {
        var fx = await ViewModelFixture.StartAsync([game], launcher: launcher);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == game.Name).Game;
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        return fx;
    }

    private DetectedGame Game(string name, string source, string command)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = source, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            PrefixPath = "/tmp/pfx", Runner = "Wine", LaunchCommand = command
        };
    }

    private sealed class RecordingLauncher : IGameLaunchOptionsLauncher
    {
        public List<(string Game, GameLaunchOptions? Options)> Launches { get; } = [];
        public bool CanLaunch(DetectedGame game) => true;

        public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, null));
            return Task.CompletedTask;
        }

        public Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, options));
            return Task.CompletedTask;
        }
    }
}
