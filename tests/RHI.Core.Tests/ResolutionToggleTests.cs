using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #191 (parity T-16, upstream <c>ResolutionToggleService</c> / <c>ResToggle_Click</c>): a per-game resolution toggle.
/// A direct launch switches the primary output with kscreen-doctor (KDE) or wlr-randr (wlroots) and switches back when the
/// game's command ends; gamescope carries the size as -W/-H; a store hand-off is never switched around. The previous mode
/// is recorded before the switch and a record left behind is restored at the next start. No real display tool ever runs
/// here: a fake runner, or fake tools on a test PATH.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ResolutionToggleTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-res-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string KScreenJson = """
        {"outputs":[
          {"name":"HDMI-A-1","enabled":true,"connected":true,"priority":2,"currentModeId":"9",
           "modes":[{"id":"9","name":"1920x1080@60","refreshRate":60.0,"size":{"width":1920,"height":1080}}]},
          {"name":"DP-1","enabled":true,"connected":true,"priority":1,"currentModeId":"1",
           "modes":[{"id":"1","name":"3840x2160@144","refreshRate":144.0,"size":{"width":3840,"height":2160}},
                    {"id":"2","name":"2560x1440@60","refreshRate":60.0,"size":{"width":2560,"height":1440}},
                    {"id":"3","name":"2560x1440@144","refreshRate":143.99,"size":{"width":2560,"height":1440}},
                    {"id":"5","name":"2560x1440@165","refreshRate":165.0,"size":{"width":2560,"height":1440}},
                    {"id":"4","name":"1920x1080@120","refreshRate":120.0,"size":{"width":1920,"height":1080}}]}
        ]}
        """;

    private const string WlrJson = """
        [{"name":"DP-2","enabled":true,"modes":[
          {"width":2560,"height":1440,"refresh":165.0,"current":true},
          {"width":1920,"height":1080,"refresh":60.0,"current":false},
          {"width":1920,"height":1080,"refresh":144.0,"current":false}]}]
        """;

    private static readonly LinuxHdrHostSnapshot Kde = new(true, "KDE", false, null, false) { KScreenDoctorAvailable = true };

    // ---- plan --------------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_SwitchesOnlyForADirectLaunch_OnAKnownDesktopWithItsTool()
    {
        var direct = Game("Direct", "Wine", "wine Game.exe");
        var steam = Game("Store", "Steam", "steam -applaunch 1");
        var on = new LinuxHdrGameProfile { ResolutionToggle = true, Resolution = "2560x1440" };
        var hdrOff = new LinuxHdrLaunchProfileService().BuildPlan(direct, new LinuxHdrGameProfile(), Kde);

        Assert.Equal(ResolutionPlan.Off, DisplayModeSwitcher.Plan(direct, new LinuxHdrGameProfile { Resolution = "2560x1440" }, hdrOff, Kde));
        Assert.Equal(ResolutionPlan.Off, DisplayModeSwitcher.Plan(direct, new LinuxHdrGameProfile { ResolutionToggle = true }, hdrOff, Kde));
        var kde = DisplayModeSwitcher.Plan(direct, on, hdrOff, Kde);
        Assert.True(kde.SwitchesAroundLaunch);
        Assert.Equal((ResolutionPlanReason.KScreenDoctor, DisplayTool.KScreenDoctor, new DisplayResolution(2560, 1440)), (kde.Reason, kde.Tool, kde.Target));

        var sway = new LinuxHdrHostSnapshot(true, "sway", false, null, false) { WlrRandrAvailable = true };
        Assert.Equal(ResolutionPlanReason.WlrRandr, DisplayModeSwitcher.Plan(direct, on, hdrOff, sway).Reason);
        Assert.Equal(ResolutionPlanReason.DesktopUnsupported, DisplayModeSwitcher.Plan(direct, on, hdrOff, sway with { WaylandSession = false }).Reason);
        Assert.Equal(ResolutionPlanReason.DesktopUnsupported, DisplayModeSwitcher.Plan(direct, on, hdrOff, new LinuxHdrHostSnapshot(true, "GNOME", false, null, false)).Reason);
        var missing = DisplayModeSwitcher.Plan(direct, on, hdrOff, Kde with { KScreenDoctorAvailable = false });
        Assert.Equal(ResolutionPlanReason.ToolMissing, missing.Reason);
        Assert.False(missing.SwitchesAroundLaunch);
        Assert.Equal(ResolutionPlanReason.HostNotProbed, DisplayModeSwitcher.Plan(direct, on, hdrOff, LinuxHdrHostSnapshot.NotProbed).Reason);

        var handoff = DisplayModeSwitcher.Plan(steam, on, new LinuxHdrLaunchProfileService().BuildPlan(steam, new LinuxHdrGameProfile(), Kde), Kde);
        Assert.Equal(ResolutionPlanReason.Handoff, handoff.Reason);
        Assert.False(handoff.SwitchesAroundLaunch);
    }

    [Fact]
    public void Gamescope_CarriesTheSizeAsWidthAndHeight_AndTheDesktopIsNotSwitched()
    {
        var game = Game("Direct", "Wine", "wine Game.exe");
        var host = Kde with { GamescopeAvailable = true };
        var profile = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, ResolutionToggle = true, Resolution = "1920x1080" };

        var hdr = new LinuxHdrLaunchProfileService().BuildPlan(game, profile, host);
        var plan = DisplayModeSwitcher.Plan(game, profile, hdr, host);

        Assert.Equal("gamescope -W 1920 -H 1080 --hdr-enabled --", hdr.CommandPrefix);
        Assert.Contains("gamescope -W 1920 -H 1080 --hdr-enabled -- %command%", hdr.LaunchOptions);
        Assert.Equal(ResolutionPlanReason.GamescopeSize, plan.Reason);
        Assert.False(plan.SwitchesAroundLaunch);
        Assert.Equal("gamescope --hdr-enabled --",
            new LinuxHdrLaunchProfileService().BuildPlan(game, new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, Resolution = "1920x1080" }, host).CommandPrefix);
    }

    [Theory]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData(" 1920 X 1080 ", 1920, 1080)]
    [InlineData("100x100", 0, 0)]
    [InlineData("1920x", 0, 0)]
    [InlineData("abc", 0, 0)]
    public void Resolution_ParsesOnlyRealSizes(string text, int width, int height)
        => Assert.Equal(width == 0 ? null : new DisplayResolution(width, height), DisplayResolution.Parse(text));

    // ---- tool answers ------------------------------------------------------------------------------------------

    [Fact]
    public void KScreen_PicksThePrimaryOutput_AndTheTargetAtTheCurrentRefreshElseTheHighest()
    {
        var at144 = DisplayModeSwitcher.SelectKScreen(KScreenJson, new DisplayResolution(2560, 1440))!;
        Assert.Equal("DP-1", at144.Output);
        Assert.Equal(["output.DP-1.mode.3"], at144.SwitchArguments);
        Assert.Equal(["output.DP-1.mode.1"], at144.RestoreArguments);

        Assert.Equal(["output.DP-1.mode.4"], DisplayModeSwitcher.SelectKScreen(KScreenJson, new DisplayResolution(1920, 1080))!.SwitchArguments);
        Assert.True(DisplayModeSwitcher.SelectKScreen(KScreenJson, new DisplayResolution(3840, 2160))!.AlreadyAtTarget);
        Assert.Null(DisplayModeSwitcher.SelectKScreen(KScreenJson, new DisplayResolution(1280, 720)));
    }

    [Fact]
    public void WlrRandr_PicksTheFirstEnabledOutput_AndWritesItsModeFormat()
    {
        var pick = DisplayModeSwitcher.SelectWlr(WlrJson, new DisplayResolution(1920, 1080))!;
        Assert.Equal(["--output", "DP-2", "--mode", "1920x1080@144.000Hz"], pick.SwitchArguments);
        Assert.Equal(["--output", "DP-2", "--mode", "2560x1440@165.000Hz"], pick.RestoreArguments);
    }

    // ---- switch and restore ------------------------------------------------------------------------------------

    [Fact]
    public async Task Switch_RecordsTheModeFirst_ThenRestoreGoesBack_AndForgetsTheRecord()
    {
        var calls = new List<string>();
        DisplayModeSwitcher? switcher = null;
        switcher = Switcher(calls, (args, _) =>
        {
            if (args.SequenceEqual(["output.DP-1.mode.3"])) Assert.True(File.Exists(switcher!.StatePath)); // recorded before the switch
            return Ok(args.Count == 1 && args[0] == "-j" ? KScreenJson : "");
        });
        var plan = KdePlan("2560x1440");

        var result = await switcher.SwitchAsync(plan);

        Assert.Equal(DisplaySwitchOutcome.Switched, result.Outcome);
        Assert.Equal(["kscreen-doctor -j", "kscreen-doctor output.DP-1.mode.3"], calls);
        Assert.Equal("3840x2160@144", switcher.Pending!.PreviousMode);
        switcher.AttachGame(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, switcher.Pending!.GamePid);
        Assert.NotNull(switcher.Pending!.GameStartTicks);

        // A second launch never stacks a switch on one not yet put back.
        Assert.Equal(DisplaySwitchOutcome.EarlierSwitchPending, (await switcher.SwitchAsync(plan)).Outcome);
        Assert.Equal(2, calls.Count);

        Assert.Equal(DisplayRestoreOutcome.Restored, (await switcher.RestoreAsync()).Outcome);
        Assert.Equal("kscreen-doctor output.DP-1.mode.1", calls[^1]);
        Assert.Null(switcher.Pending);
        Assert.Equal(DisplayRestoreOutcome.NothingToRestore, (await switcher.RestoreAsync()).Outcome);
    }

    [Fact]
    public async Task AFailedSwitch_LeavesNoRecord_AndAFailedRestoreKeepsIt()
    {
        var failSet = true;
        var calls = new List<string>();
        var switcher = Switcher(calls, (args, _) => args[0] == "-j" ? Ok(KScreenJson)
            : failSet ? new ExternalProcessResult(1, "", "kscreen: backend refused\n") : Ok(""));

        var result = await switcher.SwitchAsync(KdePlan("2560x1440"));
        Assert.Equal(DisplaySwitchOutcome.Failed, result.Outcome);
        Assert.Equal(DisplayModeSwitcher.ToolFailedPrefix + "kscreen: backend refused", result.Detail);
        Assert.Null(switcher.Pending);

        failSet = false;
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
        failSet = true;
        Assert.Equal(DisplayRestoreOutcome.Failed, (await switcher.RestoreAsync()).Outcome);
        Assert.NotNull(switcher.Pending); // tried again at the next start
    }

    [Fact]
    public async Task AlreadyAtTheTarget_OrNoSuchMode_ChangesAndRecordsNothing()
    {
        var calls = new List<string>();
        var switcher = Switcher(calls, (args, _) => Ok(KScreenJson));

        Assert.Equal(DisplaySwitchOutcome.AlreadyAtTarget, (await switcher.SwitchAsync(KdePlan("3840x2160"))).Outcome);
        var none = await switcher.SwitchAsync(KdePlan("1280x720"));
        Assert.Equal(DisplaySwitchOutcome.Failed, none.Outcome);
        Assert.Equal(DisplayModeSwitcher.NoModePrefix + "1280x720", none.Detail);
        Assert.Equal(["kscreen-doctor -j", "kscreen-doctor -j"], calls);
        Assert.Null(switcher.Pending);
    }

    [Fact]
    public async Task Recovery_WaitsForTheRecordedGame_ThenPutsTheModeBack()
    {
        var calls = new List<string>();
        var running = 2;
        var switcher = Switcher(calls, (args, _) => Ok(args[0] == "-j" ? KScreenJson : ""), isRunning: (pid, _) => pid == 4242 && running-- > 0);
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
        switcher.AttachGame(4242); // no such process here: recorded without start ticks
        var fresh = Switcher(calls, (args, _) => Ok(""), isRunning: (pid, _) => pid == 4242 && running-- > 0);

        var result = await fresh.RecoverAsync(TimeSpan.FromMilliseconds(5));

        Assert.Equal(DisplayRestoreOutcome.Restored, result.Outcome);
        Assert.Equal(-1, running); // asked until the game had ended
        Assert.Equal("kscreen-doctor output.DP-1.mode.1", calls[^1]);
        Assert.Null(fresh.Pending);
    }

    [Fact]
    public async Task FakeToolsOnATestPath_RunThroughTheRealProcessRunner()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var log = Path.Combine(_root, "calls.log");
        var tool = Path.Combine(bin, DisplayModeSwitcher.WlrRandr);
        File.WriteAllText(tool, $"#!/bin/sh\necho \"$*\" >> '{log}'\nif [ \"$1\" = --json ]; then cat <<'EOF'\n{WlrJson}\nEOF\nfi\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var switcher = new DisplayModeSwitcher(Path.Combine(_root, "state", "display-mode.json"), name => name == "PATH" ? bin : null);
        var plan = new ResolutionPlan(true, true, ResolutionPlanReason.WlrRandr, new DisplayResolution(1920, 1080), DisplayTool.WlrRandr);

        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(plan)).Outcome);
        Assert.Equal(DisplayRestoreOutcome.Restored, (await switcher.RestoreAsync()).Outcome);

        Assert.Equal(["--json", "--output DP-2 --mode 1920x1080@144.000Hz", "--output DP-2 --mode 2560x1440@165.000Hz"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task TheLauncher_TellsWhenTheCommandStartedAndEnded()
    {
        var game = Game("Quick", "Manual", "true");
        var started = 0;
        var ended = new TaskCompletionSource();
        var launcher = new RHI.Platform.Linux.Launch.LinuxGameLauncher(TimeSpan.FromSeconds(3), Path.Combine(_root, "launch-logs"));

        await launcher.LaunchAsync(game, new GameLaunchOptions(Started: pid => started = pid, Exited: () => ended.TrySetResult()));

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(started > 0);
    }

    [Fact]
    public void AProfileWithoutTheToggle_KeepsTheShapeOlderBuildsWrote()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var state = store.Load();
        state.HdrProfiles["steam:1"] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope };
        state.HdrProfiles["steam:2"] = new LinuxHdrGameProfile { ResolutionToggle = true, Resolution = " 2560X1440 " };
        store.Save(state);

        var text = File.ReadAllText(store.LibraryPath);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "ResolutionToggle"));
        Assert.Equal("2560x1440", store.Load().HdrProfiles["steam:2"].Resolution);
        Assert.False(new LinuxHdrGameProfile { Resolution = "2560x1440" }.IsDefault());
        Assert.True(new LinuxHdrGameProfile().IsDefault());
    }

    // ---- through the view model --------------------------------------------------------------------------------

    [Fact]
    public async Task Play_SwitchesBeforeTheLaunch_AndBackWhenTheGameEnds_AndTheProfileIsSaved()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(launcher);
        var calls = new List<string>();
        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => Ok(args[0] == "-j" ? KScreenJson : ""));
        fx.Vm.Runtime.ResolutionToggle = true;
        fx.Vm.Runtime.SelectedResolution = fx.Vm.Runtime.ResolutionOptions.For("2560x1440");
        Assert.Equal("The display switches to 2560x1440 when Play starts the game and back when it closes.", fx.Vm.Runtime.ResolutionHint);
        var saved = fx.Library.Load().HdrProfiles.Values.Single();
        Assert.Equal((true, "2560x1440"), (saved.ResolutionToggle, saved.Resolution));

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var options = Assert.Single(launcher.Launches).Options!;
        Assert.Equal(["kscreen-doctor -j", "kscreen-doctor output.DP-1.mode.3"], calls);
        Assert.EndsWith("Display switched to 2560x1440; it goes back when the game closes.", fx.Vm.StatusText);
        options.Started!(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, fx.Vm.Resolution.Switcher!.Pending!.GamePid);
        options.Exited!();
        for (var i = 0; i < 200 && fx.Vm.Resolution.Switcher.Pending is not null; i++) await Task.Delay(10);
        Assert.Equal("kscreen-doctor output.DP-1.mode.1", calls[^1]);
        for (var i = 0; i < 200 && !fx.Vm.StatusText.StartsWith(UiLocalizationService.Text("Shell.ResRestored"), StringComparison.Ordinal); i++) await Task.Delay(10);
        Assert.StartsWith(UiLocalizationService.Text("Shell.ResRestored"), fx.Vm.StatusText);

        // Off again: the game launches without any switch; the chosen size is kept for next time.
        fx.Vm.Runtime.ResolutionToggle = false;
        Assert.Equal((false, "2560x1440"), (fx.Library.Load().HdrProfiles.Values.Single().ResolutionToggle, fx.Library.Load().HdrProfiles.Values.Single().Resolution));
        await fx.Vm.LaunchCommand.ExecuteAsync(null);
        Assert.Null(launcher.Launches[^1].Options);
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public async Task AFailedLaunch_PutsTheDisplayBackAtOnce()
    {
        var launcher = new RecordingLauncher { Fail = true };
        using var fx = await StartAsync(launcher);
        var calls = new List<string>();
        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => Ok(args[0] == "-j" ? KScreenJson : ""));
        fx.Vm.Runtime.ResolutionToggle = true;
        fx.Vm.Runtime.SelectedResolution = fx.Vm.Runtime.ResolutionOptions.For("2560x1440");

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Equal("kscreen-doctor output.DP-1.mode.1", calls[^1]);
        Assert.Null(fx.Vm.Resolution.Switcher!.Pending);
    }

    [Fact]
    public async Task ASwitchLeftBehind_IsPutBackWhenTheSwitcherArrives()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var calls = new List<string>();
        var first = Switcher(calls, (args, _) => Ok(args[0] == "-j" ? KScreenJson : ""));
        Assert.Equal(DisplaySwitchOutcome.Switched, (await first.SwitchAsync(KdePlan("2560x1440"))).Outcome);

        fx.Vm.Resolution.Switcher = Switcher(calls, (_, _) => Ok(""), isRunning: (_, _) => false);
        await fx.Vm.Resolution.Recovery;

        Assert.Equal("kscreen-doctor output.DP-1.mode.1", calls[^1]);
        Assert.StartsWith(UiLocalizationService.Text("Shell.ResRestored"), fx.Vm.StatusText); // a pending notice may follow
    }

    [Fact]
    public async Task WithoutASwitcher_NothingIsEverSwitched()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(launcher);
        fx.Vm.Runtime.ResolutionToggle = true;
        Assert.True(fx.Vm.Runtime.SelectedResolutionPlan.SwitchesAroundLaunch);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(launcher.Launches).Options?.Started);
    }

    [Fact]
    public void PortugueseUi_HasTheResolutionLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Mudar a resolução durante o jogo", UiLocalizationService.Text("Shell.ResToggle"));
            Assert.Equal("A tela não oferece esta resolução: 1280x720", UiLocalizationService.Present(DisplayModeSwitcher.NoModePrefix + "1280x720"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static ResolutionPlan KdePlan(string size)
        => new(true, true, ResolutionPlanReason.KScreenDoctor, DisplayResolution.Parse(size), DisplayTool.KScreenDoctor);

    private static ExternalProcessResult Ok(string stdout) => new(0, stdout, "");

    private DisplayModeSwitcher Switcher(List<string> calls, Func<IReadOnlyList<string>, CancellationToken, ExternalProcessResult> answer,
        Func<int, long?, bool>? isRunning = null)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "fakebin")).FullName;
        foreach (var name in new[] { DisplayModeSwitcher.KScreenDoctor, DisplayModeSwitcher.WlrRandr })
            File.WriteAllText(Path.Combine(bin, name), "#!/bin/sh\nexit 99\n"); // found on PATH, never run (the runner is fake)
        return new DisplayModeSwitcher(Path.Combine(_root, "state", "display-mode.json"), name => name == "PATH" ? bin : null,
            run: (tool, args, ct) =>
            {
                lock (calls) calls.Add(Path.GetFileName(tool) + " " + string.Join(" ", args));
                return Task.FromResult(answer(args, ct));
            },
            isRunning: isRunning ?? ((_, _) => false));
    }

    private async Task<ViewModelFixture> StartAsync(RecordingLauncher launcher)
    {
        var game = Game("Wine Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe");
        var hdr = new LinuxHdrLaunchProfileService(
            environment: name => name switch { "XDG_SESSION_TYPE" => "wayland", "XDG_CURRENT_DESKTOP" => "KDE", _ => null },
            commandExists: command => command == DisplayModeSwitcher.KScreenDoctor,
            nvidiaVersion: () => null);
        var fx = await ViewModelFixture.StartAsync([game], launcher: launcher, hdrProfiles: hdr);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == game.Name).Game;
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || !fx.Vm.StatusText.StartsWith("Ready", StringComparison.Ordinal)); i++)
            await Task.Delay(25);
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
            Runner = "Proton", CompatibilityTool = "GE-Proton11-3", CompatibilityLayerType = "proton", LaunchCommand = command
        };
    }

    private sealed class RecordingLauncher : IGameLaunchOptionsLauncher
    {
        public List<(string Game, GameLaunchOptions? Options)> Launches { get; } = [];
        public bool Fail { get; init; }

        public bool CanLaunch(DetectedGame game) => true;

        public Task LaunchAsync(DetectedGame game, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, null));
            return Fail ? throw new InvalidOperationException("no") : Task.CompletedTask;
        }

        public Task LaunchAsync(DetectedGame game, GameLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Launches.Add((game.Name, options));
            return Fail ? throw new InvalidOperationException("no") : Task.CompletedTask;
        }
    }
}
