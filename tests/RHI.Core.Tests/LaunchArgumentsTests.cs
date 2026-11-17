using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-c (upstream 2.9.0+ per-game launch arguments): the text means what it means on Windows (split by the C runtime's
/// rules), a command RHI built gets the arguments on the program's argv, a Steam game goes through <c>steam -applaunch &lt;id&gt;
/// &lt;args&gt;</c>, and a command the user typed or another launcher is never changed.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LaunchArgumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-args-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // ── Splitting (Windows command-line rules, CommandLineToArgvW) ──────────────

    [Theory]
    [InlineData("-dx11 -skipintro", new[] { "-dx11", "-skipintro" })]
    [InlineData("  -windowed   -w 1920  ", new[] { "-windowed", "-w", "1920" })]
    [InlineData("+exec \"auto exec.cfg\" -novid", new[] { "+exec", "auto exec.cfg", "-novid" })]
    [InlineData("-path=\"C:\\Program Files\\Game\" -x", new[] { "-path=C:\\Program Files\\Game", "-x" })]
    [InlineData("C:\\Games\\ \"C:\\Games\\\\\"", new[] { "C:\\Games\\", "C:\\Games\\" })] // backslashes only halve before a quote
    [InlineData("a\\\"b", new[] { "a\"b" })] // 2n+1 backslashes: n and a literal quote
    [InlineData("\"a\"\"b\"", new[] { "a\"b" })] // "" inside a group: a literal quote
    [InlineData("\"\"", new[] { "" })] // an empty group is an empty argument
    [InlineData("it's $HOME `x`", new[] { "it's", "$HOME", "`x`" })] // nothing is a shell character here
    public void Split_FollowsTheWindowsRules(string text, string[] expected)
        => Assert.Equal(expected, LaunchArguments.Split(text));

    [Fact]
    public void Split_OfNothing_IsNoArguments()
    {
        Assert.Empty(LaunchArguments.Split(null));
        Assert.Empty(LaunchArguments.Split("   "));
    }

    [Fact]
    public void Normalize_TrimsAndFlattensLines_AndRefusesWhatCannotBePassed()
    {
        Assert.Equal("-a -b", LaunchArguments.Normalize("  -a\r\n-b\t"));
        Assert.Null(LaunchArguments.Normalize("   "));
        Assert.Null(LaunchArguments.Normalize("-a\0-b"));
        Assert.NotNull(LaunchArguments.Problem("-a\0-b"));
        Assert.Null(LaunchArguments.Normalize(new string('x', LaunchArguments.MaxLength + 1)));
        Assert.Equal(LaunchArguments.MaxLength, LaunchArguments.Normalize(new string('x', LaunchArguments.MaxLength))!.Length);
    }

    // ── Routes and the command RHI runs ────────────────────────────────────────

    private static readonly string[] DxArgs = ["-dx11", "+exec auto exec.cfg"];
    private const string DxQuoted = "'-dx11' '+exec auto exec.cfg'";

    private static DetectedGame Wine(string command = "WINEPREFIX='/p/pfx' '/opt/wine/bin/wine' '/g/Game.exe'") => new()
    {
        Name = "Prefix Game", Source = "Wine", InstallPath = "/g", LaunchCommand = command, LaunchExecutablePath = "/g/Game.exe"
    };

    private static DetectedGame Steam(string command) => new()
    {
        Name = "Steam Game", Source = "Steam", InstallPath = "/s/common/Game", SteamAppId = 1234, LaunchCommand = command
    };

    [Fact]
    public void DirectWineLaunch_AppendsEachArgumentShellQuoted()
    {
        var game = Wine();
        Assert.Equal(LaunchArgumentsRoute.Direct, LaunchArguments.RouteFor(game, _ => false));
        Assert.Equal(game.LaunchCommand + " " + DxQuoted, LaunchArguments.CommandWith(game, DxArgs, _ => false));
    }

    [Fact]
    public void NoArguments_LeaveEveryCommandAsItIs()
    {
        foreach (var game in new[] { Wine(), Steam("steam -applaunch 1234"), Steam("xdg-open steam://rungameid/1234") })
        {
            Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, null, _ => true));
            Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, [], _ => true));
        }
    }

    [Theory]
    [InlineData("steam -applaunch 1234", "steam -applaunch 1234")]
    [InlineData("flatpak run com.valvesoftware.Steam -applaunch 1234", "flatpak run com.valvesoftware.Steam -applaunch 1234")]
    [InlineData("xdg-open steam://rungameid/1234", "steam -applaunch 1234")] // upstream: -applaunch when arguments are set
    [InlineData("steam steam://rungameid/1234", "steam -applaunch 1234")]
    [InlineData("flatpak run com.valvesoftware.Steam steam://rungameid/1234", "flatpak run com.valvesoftware.Steam -applaunch 1234")]
    public void Steam_GoesThroughApplaunch_WithTheArgumentsAfterTheAppId(string command, string expected)
    {
        var game = Steam(command);
        Assert.Equal(LaunchArgumentsRoute.Steam, LaunchArguments.RouteFor(game, program => program == "steam"));
        Assert.Equal(expected + " " + DxQuoted, LaunchArguments.CommandWith(game, DxArgs, program => program == "steam"));
        // Still the Steam hand-off: Steam picks the game's working directory.
        Assert.Null(LinuxGameLauncher.ResolveWorkingDirectory(game));
    }

    [Fact]
    public void SteamLink_WithoutTheSteamProgram_CannotCarryArguments()
    {
        var game = Steam("xdg-open steam://rungameid/1234");
        Assert.Equal(LaunchArgumentsRoute.SteamLinkOnly, LaunchArguments.RouteFor(game, _ => false));
        Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, DxArgs, _ => false));
    }

    [Fact]
    public void Steam_ForAnotherAppId_OrAnEditedCommand_IsNotRewritten()
    {
        var other = Steam("steam -applaunch 999");
        Assert.Equal(LaunchArgumentsRoute.Launcher, LaunchArguments.RouteFor(other, _ => true));
        Assert.Equal(other.LaunchCommand, LaunchArguments.CommandWith(other, DxArgs, _ => true));
        var wrapped = Steam("gamemoderun steam -applaunch 1234");
        Assert.Equal(wrapped.LaunchCommand, LaunchArguments.CommandWith(wrapped, DxArgs, _ => true));
    }

    [Fact]
    public void ACommandTheUserTyped_IsNeverChanged()
    {
        var edited = Wine();
        edited.UserLaunchCommand = edited.LaunchCommand = "WINEPREFIX=/p wine /g/Game.exe -mine";
        var manual = new DetectedGame { Name = "Mine", Source = "Manual", IsManuallyAdded = true, InstallPath = "/m", LaunchCommand = "wine /m/a.exe" };
        foreach (var game in new[] { edited, manual })
        {
            Assert.Equal(LaunchArgumentsRoute.UserCommand, LaunchArguments.RouteFor(game, _ => true));
            Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, DxArgs, _ => true));
        }
    }

    [Theory]
    [InlineData("Heroic", "xdg-open 'heroic://launch/legendary/abc'")]
    [InlineData("Lutris", "lutris 'lutris:rungame/some-game'")]
    [InlineData("Wine", "WINEPREFIX=/p wine /p/drive_c/Ubisoft/upc.exe 'uplay://launch/123/0'")] // a store client's start
    [InlineData("Wine", "cd /g && wine Game.exe")] // not one simple command: the words would not reach the game for sure
    public void OtherLaunchers_AreLeftAsTheyAre(string source, string command)
    {
        var game = new DetectedGame { Name = "Elsewhere", Source = source, InstallPath = "/g", LaunchCommand = command };
        Assert.Equal(LaunchArgumentsRoute.Launcher, LaunchArguments.RouteFor(game, _ => true));
        Assert.Equal(command, LaunchArguments.CommandWith(game, DxArgs, _ => true));
    }

    [Fact]
    public void ALauncherThatOnlyOpens_IsNotTheGame()
    {
        var game = Wine("WINEPREFIX=/p wine /p/drive_c/EA/EADesktop.exe");
        game.ParentLauncherName = "EA app";
        Assert.Equal(LaunchArgumentsRoute.Launcher, LaunchArguments.RouteFor(game, _ => true));
        Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, DxArgs, _ => true));
    }

    [Fact]
    public void ANonSteamShortcut_TakesItsArgumentsInSteam()
    {
        var game = new DetectedGame
        {
            Name = "Shortcut", Source = SteamGameDetector.ShortcutSource, InstallPath = "/g", LaunchCommand = "steam steam://rungameid/1234567890"
        };
        Assert.Equal(LaunchArgumentsRoute.SteamShortcut, LaunchArguments.RouteFor(game, _ => true));
        Assert.Equal(game.LaunchCommand, LaunchArguments.CommandWith(game, DxArgs, _ => true));
    }

    [Fact]
    public void NoCommand_IsSaidSo()
        => Assert.Equal(LaunchArgumentsRoute.NoCommand, LaunchArguments.RouteFor(new DetectedGame { Name = "x", Source = "Wine", InstallPath = "/g" }));

    // ── The launcher ───────────────────────────────────────────────────────────

    [Fact]
    public void Launcher_PutsTheArgumentsInTheShellCommand_AfterAWrapper()
    {
        var psi = LinuxGameLauncher.BuildStartInfo(Wine(), new GameLaunchOptions(CommandPrefix: "gamescope --", Arguments: DxArgs));
        var command = psi.ArgumentList[1];
        // The wrapper runs the game's command in a child shell, so that command (arguments included) is quoted once more.
        Assert.Contains(DxQuoted.Replace("'", "'\\''", StringComparison.Ordinal), command, StringComparison.Ordinal);
        Assert.StartsWith("exec gamescope -- /bin/sh -c ", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launcher_ArgumentsArriveAsTheGameProgramsArgv_ExactlyAsSplit()
    {
        // A fake program records its argv one per line; RHI's command is a single program call, as a prefix game's is.
        var program = Path.Combine(_root, "fake game");
        var output = Path.Combine(_root, "argv.out");
        File.WriteAllText(program, $"#!/bin/sh\nfor a in \"$@\"; do printf '[%s]\\n' \"$a\"; done > {Quote(output)}.tmp\nmv {Quote(output)}.tmp {Quote(output)}\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var game = new DetectedGame { Name = "Argv", Source = "Wine", InstallPath = _root, LaunchCommand = Quote(program) + " -first" };
        var args = LaunchArguments.Split("-dx11 \"two words\" it's $HOME `id` \"C:\\Games\\\\\"");

        await new LinuxGameLauncher().LaunchAsync(game, new GameLaunchOptions(Arguments: args));

        for (var i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(100);
        Assert.Equal(["[-first]", "[-dx11]", "[two words]", "[it's]", "[$HOME]", "[`id`]", "[C:\\Games\\]"], File.ReadAllLines(output));
    }

    [Fact]
    public void Launcher_ArgumentsAloneAreNoReasonToRefuseAHandoff()
    {
        var psi = LinuxGameLauncher.BuildStartInfo(Steam("steam -applaunch 1234"), new GameLaunchOptions(Arguments: ["-x"]));
        Assert.Equal(string.Empty, psi.WorkingDirectory); // Steam's own working directory, as without arguments
    }

    // ── The library file ───────────────────────────────────────────────────────

    [Fact]
    public void LibraryState_KeepsTrimmedText_DropsBlanks_AndKeepsItsShapeWithoutAny()
    {
        var state = new LinuxGameLibraryState
        {
            LaunchArguments = new(StringComparer.OrdinalIgnoreCase) { ["a"] = "  -x  ", ["b"] = "   ", ["c"] = null! }
        }.Normalize();
        Assert.Equal(new Dictionary<string, string> { ["a"] = "-x" }, state.LaunchArguments);
        Assert.Null(new LinuxGameLibraryState { LaunchArguments = new() { ["b"] = " " } }.Normalize().LaunchArguments);

        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        store.Save(new LinuxGameLibraryState());
        Assert.DoesNotContain("LaunchArguments", File.ReadAllText(store.LibraryPath), StringComparison.Ordinal);
        store.Save(new LinuxGameLibraryState { LaunchArguments = new() { ["k"] = "-dx11" } });
        Assert.Equal("-dx11", store.Load().LaunchArguments!["k"]);
    }

    [Fact]
    public void LibraryState_ArgumentsFollowTheGameToItsNewKey()
    {
        var game = new DetectedGame { Name = "Moved", Source = "Wine", InstallPath = Path.Combine(_root, "Moved"), PrefixPath = "/p" };
        Directory.CreateDirectory(game.InstallPath);
        var oldKey = LinuxGameLibraryStore.LegacyGameKey(game);
        var key = LinuxGameLibraryStore.GameKey(game);
        Assert.NotEqual(oldKey, key);
        var state = new LinuxGameLibraryState { LaunchArguments = new(StringComparer.OrdinalIgnoreCase) { [oldKey] = "-old" } };
        Assert.True(state.AdoptOlderKeys(game));
        Assert.Equal("-old", state.LaunchArguments![key]);
        Assert.False(state.LaunchArguments.ContainsKey(oldKey));
    }

    // ── Display modes (#311): a mode can swap the launch arguments ─────────────

    [Fact]
    public async Task DisplayMode_SwapsTheArguments_AndOffPutsTheGamesOwnBack()
    {
        const string key = "wine:/g";
        var state = new LinuxGameLibraryState { LaunchArguments = new(StringComparer.OrdinalIgnoreCase) { [key] = "-own" } };
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.LaunchArgs, "-fullscreen -tv");
        store.SetModeDefault("monitor", DisplayModeItemKeys.LaunchArgs, ""); // none in this mode
        var svc = new DisplayModeService(store, new DisplayModeIo(new DisplayModeGameFiles(new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json")), null, _ => null)));
        DisplayModeContext Ctx() => new() { Game = Wine(), Key = key, LaunchArgsOffered = true, Launch = DisplayModeLaunchValues.From(state, key) };

        var tv = Ctx();
        await svc.SwitchAsync(tv, "tv");
        tv.Launch!.ApplyTo(state, key);
        Assert.Equal("-fullscreen -tv", state.LaunchArguments![key]);

        var monitor = Ctx();
        await svc.SwitchAsync(monitor, "monitor");
        monitor.Launch!.ApplyTo(state, key);
        Assert.Null(state.LaunchArguments); // no arguments in this mode

        var off = Ctx();
        await svc.TurnOffAsync(off);
        off.Launch!.ApplyTo(state, key);
        Assert.Equal("-own", state.LaunchArguments![key]); // the game's own from before modes
    }

    [Fact]
    public async Task DisplayMode_LeavesTheArgumentsAlone_WhereRhiDoesNotPassThem()
    {
        var state = new LinuxGameLibraryState();
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.LaunchArgs, "-tv");
        var svc = new DisplayModeService(store, new DisplayModeIo(new DisplayModeGameFiles(new LinuxDeploymentRecordStore(Path.Combine(_root, "r.json")), null, _ => null)));
        var ctx = new DisplayModeContext { Game = Wine(), Key = "k", LaunchArgsOffered = false, Launch = DisplayModeLaunchValues.From(state, "k") };

        var result = await svc.SwitchAsync(ctx, "tv");

        Assert.Contains(result.Items, i => i.Item.Key == DisplayModeItemKeys.LaunchArgs && i.Unavailable == DisplayModeUnavailable.LaunchArgsNotOffered);
        Assert.False(ctx.Launch!.Changed);
    }

    [Fact]
    public void DisplayMode_ItemIsUpstreamsKey_InTheLaunchGroup_AndTakesAnEmptyValue()
    {
        var item = DisplayModeItems.Get("LaunchArgs")!;
        Assert.Equal(DisplayModeGroup.Launch, item.Group);
        Assert.Equal(DisplayModeTarget.LaunchSettings, item.Target);
        Assert.Equal("", item.Normalize("  "));
        Assert.Equal("-a -b", item.Normalize(" -a\n-b "));
        Assert.Equal(DisplayModeItemKeys.Resolution, DisplayModeItems.All[^2].Key); // after Resolution on launch, as upstream
        Assert.Same(item, DisplayModeItems.All[^1]);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
