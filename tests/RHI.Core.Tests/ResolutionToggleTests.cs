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
        // Back by resolution and rounded refresh (kscreen-doctor's own mode name), never by the mode number: KScreen numbers
        // are list positions that can name another mode after a reboot or a monitor change.
        Assert.Equal(["output.DP-1.mode.3840x2160@144"], at144.RestoreArguments);

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
        var screen = new FakeKScreen();
        DisplayModeSwitcher? switcher = null;
        switcher = Switcher(calls, (args, _) =>
        {
            if (args.SequenceEqual(["output.DP-1.mode.3"])) Assert.True(File.Exists(switcher!.StatePath)); // recorded before the switch
            return screen.Answer(args);
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
        Assert.Equal(["kscreen-doctor -j", "kscreen-doctor output.DP-1.mode.3840x2160@144"], calls[^2..]);
        Assert.Equal("1", screen.CurrentId);
        Assert.Null(switcher.Pending);
        Assert.Equal(DisplayRestoreOutcome.NothingToRestore, (await switcher.RestoreAsync()).Outcome);
    }

    [Fact]
    public async Task Restore_WhenTheLaunchShellAlreadyPutTheModeBack_OnlyForgetsTheRecord()
    {
        var calls = new List<string>();
        var screen = new FakeKScreen();
        var switcher = Switcher(calls, (args, _) => screen.Answer(args));
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
        screen.CurrentId = "1"; // the launch shell ran the restore when the game's command ended

        Assert.Equal(DisplayRestoreOutcome.Restored, (await switcher.RestoreAsync()).Outcome);

        Assert.Equal("kscreen-doctor -j", calls[^1]); // looked, changed nothing
        Assert.Equal(3, calls.Count);
        Assert.Null(switcher.Pending);
    }

    [Fact]
    public async Task AFailedSwitch_GoesBackFirst_AndForgetsTheRecordOnlyWhenThatWorked_AndAFailedRestoreKeepsIt()
    {
        var failing = "output.DP-1.mode.3";
        var calls = new List<string>();
        var screen = new FakeKScreen();
        // The tool applies the mode, then reports an error.
        var switcher = Switcher(calls, (args, _) =>
        {
            var answer = screen.Answer(args);
            return args[0] == failing ? new ExternalProcessResult(1, "", "kscreen: backend refused\n") : answer;
        });

        var result = await switcher.SwitchAsync(KdePlan("2560x1440"));
        Assert.Equal(DisplaySwitchOutcome.Failed, result.Outcome);
        Assert.Equal(DisplayModeSwitcher.ToolFailedPrefix + "kscreen: backend refused", result.Detail);
        Assert.Equal("kscreen-doctor output.DP-1.mode.3840x2160@144", calls[^1]); // tried to go back
        Assert.Equal("1", screen.CurrentId);
        Assert.Null(switcher.Pending); // back where it was: nothing left to restore

        failing = "output.DP-1.mode.3840x2160@144";
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
        Assert.Equal(DisplayRestoreOutcome.Failed, (await switcher.RestoreAsync()).Outcome);
        Assert.NotNull(switcher.Pending); // tried again at the next start
    }

    [Fact]
    public async Task ASwitchThatTimesOut_AndCannotGoBack_KeepsTheRecordForTheNextStart()
    {
        var calls = new List<string>();
        var screen = new FakeKScreen();
        var switcher = Switcher(calls, (args, _) =>
        {
            if (args[0] == "-j") return screen.Answer(args);
            if (args[0] != "output.DP-1.mode.3") return new ExternalProcessResult(1, "", "kscreen: no backend\n");
            screen.Answer(args); // applied, then hung: the runner's time limit ends it
            throw new TimeoutException("kscreen-doctor timed out");
        });

        var result = await switcher.SwitchAsync(KdePlan("2560x1440"));

        Assert.Equal(DisplaySwitchOutcome.Failed, result.Outcome);
        Assert.Equal("kscreen-doctor output.DP-1.mode.3840x2160@144", calls[^1]); // tried to go back
        Assert.Equal("3", screen.CurrentId); // the display really is switched
        Assert.Equal("3840x2160@144", switcher.Pending!.PreviousMode); // so the record stays: the next start puts it back
        Assert.Equal(DisplaySwitchOutcome.EarlierSwitchPending, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
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
        var screen = new FakeKScreen();
        var switcher = Switcher(calls, (args, _) => screen.Answer(args), isRunning: (pid, _) => pid == 4242 && running-- > 0);
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
        switcher.AttachGame(4242); // no such process here: recorded without start ticks
        // After a reboot KScreen numbers the modes differently: the restore names the mode, so the right one comes back.
        screen.Renumber();
        var fresh = Switcher(calls, (args, _) => screen.Answer(args), isRunning: (pid, _) => pid == 4242 && running-- > 0);

        var result = await fresh.RecoverAsync(TimeSpan.FromMilliseconds(5));

        Assert.Equal(DisplayRestoreOutcome.Restored, result.Outcome);
        Assert.Equal(-1, running); // asked until the game had ended
        Assert.Equal("kscreen-doctor output.DP-1.mode.3840x2160@144", calls[^1]);
        Assert.Equal((3840, 2160), screen.CurrentSize);
        Assert.Null(fresh.Pending);
    }

    [Fact]
    public async Task FakeToolsOnATestPath_RunThroughTheRealProcessRunner()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var log = Path.Combine(_root, "calls.log");
        var tool = Path.Combine(bin, DisplayModeSwitcher.WlrRandr);
        // A stateful fake: --mode records the mode, --json answers with it as the current one.
        var screen = Directory.CreateDirectory(Path.Combine(_root, "screen")).FullName;
        File.WriteAllText(Path.Combine(screen, "current"), "2560x1440@165.000Hz");
        File.WriteAllText(Path.Combine(screen, "json-2560x1440@165.000Hz"), WlrJson);
        File.WriteAllText(Path.Combine(screen, "json-1920x1080@144.000Hz"), WlrJson
            .Replace("\"refresh\":165.0,\"current\":true", "\"refresh\":165.0,\"current\":false", StringComparison.Ordinal)
            .Replace("\"refresh\":144.0,\"current\":false", "\"refresh\":144.0,\"current\":true", StringComparison.Ordinal));
        File.WriteAllText(tool, $"#!/bin/sh\necho \"$*\" >> '{log}'\n" +
            $"if [ \"$1\" = --json ]; then cat \"{screen}/json-$(cat '{screen}/current')\"; exit 0; fi\n" +
            $"if [ \"$1\" = --output ] && [ \"$3\" = --mode ]; then printf %s \"$4\" > '{screen}/current'; fi\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var switcher = new DisplayModeSwitcher(Path.Combine(_root, "state", "display-mode.json"), name => name == "PATH" ? bin : null);
        var plan = new ResolutionPlan(true, true, ResolutionPlanReason.WlrRandr, new DisplayResolution(1920, 1080), DisplayTool.WlrRandr);

        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(plan)).Outcome);
        Assert.Equal(DisplayRestoreOutcome.Restored, (await switcher.RestoreAsync()).Outcome);

        Assert.Equal(["--json", "--output DP-2 --mode 1920x1080@144.000Hz", "--json", "--output DP-2 --mode 2560x1440@165.000Hz"], File.ReadAllLines(log));
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
        var screen = new FakeKScreen();
        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => screen.Answer(args));
        await fx.Vm.Resolution.DisplaySizesListed;
        calls.Clear();
        fx.Vm.Runtime.ResolutionToggle = true;
        fx.Vm.Runtime.SelectedResolution = fx.Vm.Runtime.ResolutionOptions.For("2560x1440");
        Assert.Equal("The display switches to 2560x1440 when Play starts the game and back when it closes.", fx.Vm.Runtime.ResolutionHint);
        var saved = fx.Library.Load().HdrProfiles.Values.Single();
        Assert.Equal((true, "2560x1440"), (saved.ResolutionToggle, saved.Resolution));

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var options = Assert.Single(launcher.Launches).Options!;
        Assert.Equal(["kscreen-doctor -j", "kscreen-doctor output.DP-1.mode.3"], calls);
        Assert.EndsWith("Display switched to 2560x1440; it goes back when the game closes.", fx.Vm.StatusText);
        // The launch shell carries the restore itself (absolute tool path, the recorded arguments), RHI running or not.
        Assert.Equal([Path.Combine(_root, "fakebin", DisplayModeSwitcher.KScreenDoctor), "output.DP-1.mode.3840x2160@144"], options.AfterExit);
        options.Started!(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, fx.Vm.Resolution.Switcher!.Pending!.GamePid);
        options.Exited!();
        for (var i = 0; i < 200 && fx.Vm.Resolution.Switcher.Pending is not null; i++) await Task.Delay(10);
        Assert.Equal("kscreen-doctor output.DP-1.mode.3840x2160@144", calls[^1]);
        for (var i = 0; i < 200 && !fx.Vm.StatusText.StartsWith(UiLocalizationService.Text("Shell.ResRestored"), StringComparison.Ordinal); i++) await Task.Delay(10);
        Assert.StartsWith(UiLocalizationService.Text("Shell.ResRestored"), fx.Vm.StatusText);

        // Off again: the game launches without any switch; the chosen size is kept for next time.
        fx.Vm.Runtime.ResolutionToggle = false;
        Assert.Equal((false, "2560x1440"), (fx.Library.Load().HdrProfiles.Values.Single().ResolutionToggle, fx.Library.Load().HdrProfiles.Values.Single().Resolution));
        await fx.Vm.LaunchCommand.ExecuteAsync(null);
        Assert.Null(launcher.Launches[^1].Options);
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public async Task AFailedLaunch_PutsTheDisplayBackAtOnce()
    {
        var launcher = new RecordingLauncher { Fail = true };
        using var fx = await StartAsync(launcher);
        var calls = new List<string>();
        var screen = new FakeKScreen();
        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => screen.Answer(args));
        fx.Vm.Runtime.ResolutionToggle = true;
        fx.Vm.Runtime.SelectedResolution = fx.Vm.Runtime.ResolutionOptions.For("2560x1440");

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Equal("kscreen-doctor output.DP-1.mode.3840x2160@144", calls[^1]);
        Assert.Null(fx.Vm.Resolution.Switcher!.Pending);
    }

    [Fact]
    public async Task ASwitchLeftBehind_IsPutBackWhenTheSwitcherArrives()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var calls = new List<string>();
        var screen = new FakeKScreen();
        var first = Switcher(calls, (args, _) => screen.Answer(args));
        Assert.Equal(DisplaySwitchOutcome.Switched, (await first.SwitchAsync(KdePlan("2560x1440"))).Outcome);

        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => screen.Answer(args), isRunning: (_, _) => false);
        await fx.Vm.Resolution.Recovery;
        await fx.Vm.Resolution.DisplaySizesListed;

        Assert.Contains("kscreen-doctor output.DP-1.mode.3840x2160@144", calls);
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
        Assert.Equal(OptionLists.GameResolution.Values, fx.Vm.Runtime.ResolutionOptions.Select(item => item.Value)); // nothing queried
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


    // ---- the launch shell puts the display back (RHI closed or not) --------------------------------------------

    [Fact]
    public async Task TheLaunchShell_RunsTheRestoreWhenTheGameEnds_WithoutRhisListeners_ArgvSafe_KeepingTheGamesStatus()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "shellbin")).FullName;
        var log = Path.Combine(_root, "restore.log");
        var tool = Path.Combine(bin, DisplayModeSwitcher.KScreenDoctor);
        // Each argument on its own line; noise on stderr must not reach the game's launch log.
        File.WriteAllText(tool, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done >> '{log}'\necho tool-noise >&2\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var pwned = Path.Combine(_root, "pwned");
        string[] restore = [tool, "output.DP-1.mode.3840x2160@144", $"'; touch '{pwned}'; '", $"$(touch {pwned}2)"];
        var game = Game("Quits", "Manual", "echo game-error >&2; exit 3");
        var launcher = new RHI.Platform.Linux.Launch.LinuxGameLauncher(TimeSpan.FromSeconds(5), Path.Combine(_root, "launch-logs"));

        var failure = await Assert.ThrowsAsync<GameLaunchFailedException>(() => launcher.LaunchAsync(game, new GameLaunchOptions(AfterExit: restore)));

        Assert.Equal(3, failure.ExitCode); // the game's status, not the restore's
        Assert.Equal("game-error", failure.Detail);
        Assert.Equal(restore[1..], File.ReadAllLines(log)); // ran once, every entry one argument, nothing expanded
        Assert.False(File.Exists(pwned));
        Assert.False(File.Exists(pwned + "2"));
    }

    [Fact]
    public async Task TheLaunchShell_RunsTheRestoreAfterACommandThatExecs()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "shellbin2")).FullName;
        var log = Path.Combine(_root, "restore2.log");
        var tool = Path.Combine(bin, DisplayModeSwitcher.WlrRandr);
        File.WriteAllText(tool, $"#!/bin/sh\necho \"$*\" >> '{log}'\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var game = Game("Execs", "Manual", "exec sleep 0.3");
        var launcher = new RHI.Platform.Linux.Launch.LinuxGameLauncher(TimeSpan.FromMilliseconds(50), Path.Combine(_root, "launch-logs2"));

        await launcher.LaunchAsync(game, new GameLaunchOptions(AfterExit: [tool, "--output", "DP-2", "--mode", "2560x1440@165.000Hz"]));

        Assert.False(File.Exists(log)); // the game still runs
        for (var i = 0; i < 500 && !File.Exists(log); i++) await Task.Delay(20);
        Assert.Equal(["--output DP-2 --mode 2560x1440@165.000Hz"], File.ReadAllLines(log)); // no RHI listener involved
    }

    // shell-launch re-review nits 1-2: the restore runs under the 15 s limit, through timeout named by its absolute path,
    // never a shell function (or another program) called timeout that the user's login shell puts first.
    [Fact]
    public async Task TheLaunchShell_RunsTheRestoreUnderTheFifteenSecondLimit_ThroughTimeoutByAbsolutePath()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "shellbin-timeout")).FullName;
        var log = Path.Combine(_root, "timeout.log");
        var fakeTimeout = Path.Combine(bin, "timeout");
        File.WriteAllText(fakeTimeout, $"#!/bin/sh\nprintf 'timeout %s\\n' \"$1\" >> '{log}'\nshift\nexec \"$@\"\n");
        var tool = Path.Combine(bin, DisplayModeSwitcher.KScreenDoctor);
        File.WriteAllText(tool, $"#!/bin/sh\nprintf 'restore %s\\n' \"$*\" >> '{log}'\n");
        foreach (var file in new[] { fakeTimeout, tool })
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var hijacked = Path.Combine(_root, "timeout-function-ran");
        // A login profile that defines a function named timeout, and a PATH whose first timeout is not coreutils'.
        var script = $"timeout() {{ touch '{hijacked}'; }}\nPATH='{bin}':\"$PATH\"\n" +
                     RHI.Platform.Linux.Launch.LinuxGameLauncher.WithAfterExit("exit 4", [tool, "output.DP-1.mode.3840x2160@144"], fakeTimeout);
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using var shell = System.Diagnostics.Process.Start(psi)!;
        await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(4, shell.ExitCode);
        Assert.Equal([$"timeout {RHI.Platform.Linux.Launch.LinuxGameLauncher.AfterExitTimeoutSeconds}", "restore output.DP-1.mode.3840x2160@144"], File.ReadAllLines(log));
        Assert.Equal(15, RHI.Platform.Linux.Launch.LinuxGameLauncher.AfterExitTimeoutSeconds);
        Assert.False(File.Exists(hijacked));

        // The launcher's own choice is an absolute coreutils path (or none), and the built script names it, not "timeout".
        var chosen = RHI.Platform.Linux.Launch.LinuxGameLauncher.TimeoutProgram;
        Assert.True(chosen is null || RHI.Platform.Linux.Launch.LinuxGameLauncher.TimeoutCandidates.Contains(chosen));
        var built = RHI.Platform.Linux.Launch.LinuxGameLauncher.WithAfterExit("true", ["/usr/bin/kscreen-doctor", "x"]);
        Assert.DoesNotContain("command -v timeout", built);
        if (chosen is not null) Assert.Contains($"'{chosen}' 15 '/usr/bin/kscreen-doctor' 'x'", built);
        Assert.Throws<ArgumentException>(() => RHI.Platform.Linux.Launch.LinuxGameLauncher.WithAfterExit("true", ["/usr/bin/kscreen-doctor"], "timeout"));
    }

    [Fact]
    public void TheBuiltCommand_CarriesTheRestoreAfterTheGameCommand_AndHandOffsAndUnsafeVectorsAreRefused()
    {
        var game = Game("Direct", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe");
        var psi = RHI.Platform.Linux.Launch.LinuxGameLauncher.BuildStartInfo(game,
            new GameLaunchOptions(AfterExit: ["/usr/bin/kscreen-doctor", "output.DP-1.mode.3840x2160@144"]));
        var script = psi.ArgumentList[^1];
        var at = script.IndexOf("WINEPREFIX=/tmp/pfx wine Game.exe", StringComparison.Ordinal);
        Assert.True(at >= 0 && script.IndexOf("'/usr/bin/kscreen-doctor' 'output.DP-1.mode.3840x2160@144'", StringComparison.Ordinal) > at, script);

        var launcher = new RHI.Platform.Linux.Launch.LinuxGameLauncher(TimeSpan.FromSeconds(1), Path.Combine(_root, "launch-logs3"));
        var steam = Game("Store", "Steam", "steam -applaunch 1");
        Assert.Equal(RHI.Platform.Linux.Launch.LinuxGameLauncher.HandoffProfileRefused,
            Assert.Throws<InvalidOperationException>(() => launcher.LaunchAsync(steam, new GameLaunchOptions(AfterExit: ["/usr/bin/true"])).GetAwaiter().GetResult()).Message);
        Assert.Throws<ArgumentException>(() => RHI.Platform.Linux.Launch.LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions(AfterExit: ["kscreen-doctor"])));
        Assert.Throws<ArgumentException>(() => RHI.Platform.Linux.Launch.LinuxGameLauncher.BuildStartInfo(game, new GameLaunchOptions(AfterExit: ["/bin/true", "a\0b"])));
    }

    // ---- records read back are checked, an unreadable one never disables the feature ----------------------------

    [Theory]
    [InlineData("KScreenDoctor", "DP-1", "[\"output.DP-1.mode.3840x2160@144\"]", true)]
    [InlineData("KScreenDoctor", "DP-1", "[\"output.DP-1.disable\"]", false)]
    [InlineData("KScreenDoctor", "DP-1", "[\"output.DP-1.mode.1\"]", false)]
    [InlineData("KScreenDoctor", "DP-1", "[\"output.HDMI-A-1.mode.1920x1080@60\"]", false)]
    [InlineData("KScreenDoctor", "DP-1", "[\"output.DP-1.mode.3840x2160@144\", \"output.DP-1.disable\"]", false)]
    [InlineData("KScreenDoctor", "DP.1", "[\"output.DP.1.mode.3840x2160@144\"]", false)]
    [InlineData("WlrRandr", "DP-2", "[\"--output\", \"DP-2\", \"--mode\", \"2560x1440@165.000Hz\"]", true)]
    [InlineData("WlrRandr", "DP-2", "[\"--output\", \"DP-2\", \"--off\"]", false)]
    [InlineData("WlrRandr", "DP-2", "[\"--output\", \"DP-2\", \"--mode\", \"2560x1440@165.000Hz; reboot\"]", false)]
    [InlineData("WlrRandr", "DP-2", "[\"--output\", \"DP-1\", \"--mode\", \"2560x1440@165.000Hz\"]", false)]
    [InlineData("None", "DP-2", "[\"--output\", \"DP-2\", \"--mode\", \"2560x1440@165.000Hz\"]", false)]
    public async Task ARecordReadBack_RunsOnlyInAShapeRhiWrites(string tool, string output, string arguments, bool valid)
    {
        var calls = new List<string>();
        var screen = new FakeKScreen { CurrentId = "3" }; // still at the game's size
        var switcher = Switcher(calls, (args, _) => args[0] switch
        {
            "--json" => Ok(WlrJson),
            "--output" => Ok(""),
            _ => screen.Answer(args)
        });
        Directory.CreateDirectory(Path.GetDirectoryName(switcher.StatePath)!);
        var text = $"{{\"Tool\":\"{tool}\",\"Output\":\"{output}\",\"RestoreArguments\":{arguments},\"PreviousMode\":\"3840x2160@144\"}}";
        File.WriteAllText(switcher.StatePath, text);

        var result = await switcher.RecoverAsync(TimeSpan.FromMilliseconds(5));

        if (valid)
        {
            Assert.Equal(DisplayRestoreOutcome.Restored, result.Outcome);
            Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(arguments)!.AsArray().Select(node => (string)node!), calls[^1].Split(' ')[1..]);
            return;
        }
        Assert.Equal(DisplayRestoreOutcome.RecordUnreadable, result.Outcome);
        Assert.Empty(calls); // nothing ran
        Assert.False(File.Exists(switcher.StatePath)); // set aside: the next Play switches again
        Assert.Equal(text, File.ReadAllText(result.SetAsideCopy!)); // the bytes are kept
        Assert.Null(switcher.RestoreCommand(System.Text.Json.JsonSerializer.Deserialize<DisplayModeState>(text,
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!));
        screen.CurrentId = "1";
        Assert.Equal(DisplaySwitchOutcome.Switched, (await switcher.SwitchAsync(KdePlan("2560x1440"))).Outcome);
    }

    [Fact]
    public async Task AnUnreadableRecord_IsSetAsideWithAPlainLine_AndPlayStillSwitches()
    {
        var calls = new List<string>();
        var screen = new FakeKScreen();
        var switcher = Switcher(calls, (args, _) => screen.Answer(args));
        Directory.CreateDirectory(Path.GetDirectoryName(switcher.StatePath)!);
        File.WriteAllText(switcher.StatePath, "{ not json");

        var result = await switcher.SwitchAsync(KdePlan("2560x1440"));

        Assert.Equal(DisplaySwitchOutcome.Switched, result.Outcome);
        Assert.Equal("{ not json", File.ReadAllText(result.SetAsideCopy!));
        var line = ResolutionText.Switch(result, KdePlan("2560x1440"))!;
        Assert.StartsWith("The saved display mode to go back to could not be read, so RHI did not use it (a copy is kept at " + result.SetAsideCopy, line);
        Assert.EndsWith("Display switched to 2560x1440; it goes back when the game closes.", line);
        Assert.Equal(DisplayRestoreOutcome.Restored, (await switcher.RestoreAsync()).Outcome);

        File.WriteAllText(switcher.StatePath, "[1,2]");
        var recovered = await switcher.RecoverAsync(TimeSpan.FromMilliseconds(5));
        Assert.Equal(DisplayRestoreOutcome.RecordUnreadable, recovered.Outcome);
        Assert.Contains(recovered.SetAsideCopy!, ResolutionText.Restore(recovered));
        Assert.Null(switcher.Pending);
        Assert.False(File.Exists(switcher.StatePath));
    }

    [Fact]
    public async Task AnUnreadableRecordThatCannotBeSetAside_SwitchesNothing_AndSaysWhichFileToFix()
    {
        var calls = new List<string>();
        var switcher = Switcher(calls, (args, _) => new FakeKScreen().Answer(args));
        var dir = Directory.CreateDirectory(Path.GetDirectoryName(switcher.StatePath)!).FullName;
        File.WriteAllText(switcher.StatePath, "{ not json");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await switcher.SwitchAsync(KdePlan("2560x1440"));

            Assert.Equal(DisplaySwitchOutcome.RecordUnreadable, result.Outcome);
            Assert.Empty(calls);
            Assert.Equal("The display was not switched: the saved display mode to go back to could not be read or set aside. Fix or delete "
                         + Path.GetFullPath(switcher.StatePath) + ".", ResolutionText.Switch(result, KdePlan("2560x1440")));
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Theory]
    [InlineData("DP-1; rm", "3")]
    [InlineData("DP.1", "3")]
    [InlineData("DP-1", "3.disable")]
    public async Task AToolAnswerOutsideTheAcceptedShapes_SwitchesNothing(string output, string modeId)
    {
        var calls = new List<string>();
        var json = KScreenJson.Replace("\"name\":\"DP-1\"", $"\"name\":\"{output}\"", StringComparison.Ordinal)
            .Replace("{\"id\":\"3\",", $"{{\"id\":\"{modeId}\",", StringComparison.Ordinal);
        var switcher = Switcher(calls, (args, _) => Ok(json));

        var result = await switcher.SwitchAsync(KdePlan("2560x1440"));

        Assert.Equal((DisplaySwitchOutcome.Failed, DisplayModeSwitcher.UnsafeAnswer), (result.Outcome, result.Detail));
        Assert.Equal(["kscreen-doctor -j"], calls);
        Assert.Null(switcher.Pending);
    }

    [Fact]
    public void TheRestoreCommand_NamesTheToolByAnAbsolutePath_NeverFromARelativePathEntry()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "relbin")).FullName;
        File.WriteAllText(Path.Combine(bin, DisplayModeSwitcher.KScreenDoctor), "#!/bin/sh\nexit 99\n");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), bin);
        Assert.False(Path.IsPathRooted(relative));
        var state = new DisplayModeState { Tool = DisplayTool.KScreenDoctor, Output = "DP-1", RestoreArguments = ["output.DP-1.mode.3840x2160@144"] };

        Assert.Null(new DisplayModeSwitcher(Path.Combine(_root, "r", "display-mode.json"), name => name == "PATH" ? relative : null).RestoreCommand(state));
        Assert.Equal([Path.Combine(bin, DisplayModeSwitcher.KScreenDoctor), "output.DP-1.mode.3840x2160@144"],
            new DisplayModeSwitcher(Path.Combine(_root, "r", "display-mode.json"), name => name == "PATH" ? relative + ":" + bin : null).RestoreCommand(state));
    }

    [Fact]
    public void AGameWithoutARecordedStartTime_IsNeverWaitedOn()
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc", "4242")).FullName;
        File.WriteAllText(Path.Combine(proc, "stat"), "4242 (game.exe) S 1 4242 4242 0 -1 4194560 0 0 0 0 0 0 0 0 20 0 1 0 987654 0 0");
        var root = Path.Combine(_root, "proc");

        Assert.True(DisplayModeSwitcher.IsProcessRunning(4242, 987654, root));
        Assert.False(DisplayModeSwitcher.IsProcessRunning(4242, 111, root)); // the pid was reused
        Assert.False(DisplayModeSwitcher.IsProcessRunning(4242, null, root)); // unknown start time: not taken for the game
        Assert.False(DisplayModeSwitcher.IsProcessRunning(4243, 987654, root));
    }

    // ---- the display's own sizes; the toggle only where it can apply --------------------------------------------

    [Fact]
    public async Task TheSizeList_IsTheDisplaysOwn_WithASavedSizeKept()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(launcher);
        fx.Vm.Runtime.ResolutionToggle = true;
        fx.Vm.Runtime.SelectedResolution = fx.Vm.Runtime.ResolutionOptions.For("5120x1440"); // saved, from the fixed list
        var calls = new List<string>();

        fx.Vm.Resolution.Switcher = Switcher(calls, (args, _) => new FakeKScreen().Answer(args));
        await fx.Vm.Resolution.DisplaySizesListed;

        Assert.Equal(["kscreen-doctor -j"], calls); // a query only
        Assert.Equal(["3840x2160", "2560x1440", "1920x1080", "5120x1440"], fx.Vm.Runtime.ResolutionOptions.Select(item => item.Value));
        Assert.Equal("5120x1440", fx.Vm.Runtime.SelectedResolution.Value);
        Assert.Equal("5120x1440", fx.Library.Load().HdrProfiles.Values.Single().Resolution);

        var wlr = await new DisplayModeSwitcher(Path.Combine(_root, "w", "display-mode.json"), name => name == "PATH" ? Path.Combine(_root, "fakebin") : null,
            run: (_, _, _) => Task.FromResult(Ok(WlrJson))).ListResolutionsAsync(DisplayTool.WlrRandr);
        Assert.Equal([new DisplayResolution(2560, 1440), new DisplayResolution(1920, 1080)], wlr);
        Assert.Equal(["2560x1440", "1920x1080"], OptionLists.GameResolutionFrom(wlr!.Select(size => size.ToString()), null).Values);
        Assert.Equal("1920x1080", OptionLists.GameResolutionFrom(["2560x1440", "1920x1080"], null).Fallback);
        Assert.Equal("2560x1440", OptionLists.GameResolutionFrom(["2560x1440", "1280x720"], null).Fallback);
    }

    [Fact]
    public void TheToggle_ShowsOnlyWhereAPlanCouldEverApply()
    {
        var direct = Game("Direct", "Wine", "wine Game.exe");
        var steam = Game("Store", "Steam", "steam -applaunch 1");
        var gnome = new LinuxHdrHostSnapshot(true, "GNOME", false, null, false);

        Assert.True(DisplayModeSwitcher.CanEverApply(direct, Kde));
        Assert.False(DisplayModeSwitcher.CanEverApply(steam, Kde)); // a hand-off is never switched, no gamescope to carry the size
        Assert.True(DisplayModeSwitcher.CanEverApply(steam, Kde with { GamescopeAvailable = true }));
        Assert.False(DisplayModeSwitcher.CanEverApply(direct, gnome));
        Assert.False(DisplayModeSwitcher.CanEverApply(direct, Kde with { KScreenDoctorAvailable = false }));
        Assert.True(DisplayModeSwitcher.CanEverApply(direct, gnome with { GamescopeAvailable = true }));
        Assert.True(DisplayModeSwitcher.CanEverApply(steam, LinuxHdrHostSnapshot.NotProbed));
    }

    [Fact]
    public async Task TheRuntimePanel_HidesTheToggleOnAHandoffCard()
    {
        var direct = Game("Wine Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe");
        var steam = Game("Store Quest", "Steam", "steam -applaunch 1");
        var hdr = new LinuxHdrLaunchProfileService(
            environment: name => name switch { "XDG_SESSION_TYPE" => "wayland", "XDG_CURRENT_DESKTOP" => "KDE", _ => null },
            commandExists: command => command == DisplayModeSwitcher.KScreenDoctor,
            nvidiaVersion: () => null);
        using var fx = await ViewModelFixture.StartAsync([direct, steam], launcher: new RecordingLauncher(), hdrProfiles: hdr);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == steam.Name).Game;
        for (var i = 0; i < 200 && fx.Vm.Runtime.ShowResolutionToggle; i++) await Task.Delay(25);
        Assert.False(fx.Vm.Runtime.ShowResolutionToggle);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == direct.Name).Game;
        for (var i = 0; i < 200 && !fx.Vm.Runtime.ShowResolutionToggle; i++) await Task.Delay(25);
        Assert.True(fx.Vm.Runtime.ShowResolutionToggle);
    }

    [Fact]
    public async Task TheAppComposition_WithoutADisplaySwitcher_NeverTouchesTheDisplay()
    {
        using var http = new HttpClient(new OfflineHandler());
        var wine = new WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        Assert.Null(services.DisplayModes);
        await Task.CompletedTask;
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A stateful kscreen-doctor answer over <see cref="KScreenJson"/>: <c>-j</c> reports DP-1's current mode,
    /// <c>output.DP-1.mode.X</c> selects by id or by kscreen-doctor's mode name (W x H @ rounded refresh), like the tool.
    /// </summary>
    private sealed class FakeKScreen
    {
        private static readonly (string Id, int W, int H, double R)[] Modes =
            [("1", 3840, 2160, 144.0), ("2", 2560, 1440, 60.0), ("3", 2560, 1440, 143.99), ("5", 2560, 1440, 165.0), ("4", 1920, 1080, 120.0)];
        private Dictionary<string, string> _ids = Modes.ToDictionary(m => m.Id, m => m.Id);

        public string CurrentId { get; set; } = "1";

        public (int, int) CurrentSize => Modes.Where(m => _ids[m.Id] == CurrentId).Select(m => (m.W, m.H)).Single();

        /// <summary>After a reboot: every mode gets another number.</summary>
        public void Renumber()
        {
            var original = Modes.Single(m => _ids[m.Id] == CurrentId).Id;
            _ids = Modes.ToDictionary(m => m.Id, m => (int.Parse(m.Id) + 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
            CurrentId = _ids[original];
        }

        public ExternalProcessResult Answer(IReadOnlyList<string> args)
        {
            if (args is ["-j"])
            {
                var json = KScreenJson.Replace("\"currentModeId\":\"1\"", $"\"currentModeId\":\"{CurrentId}\"", StringComparison.Ordinal);
                foreach (var mode in Modes) json = json.Replace($"{{\"id\":\"{mode.Id}\",", $"{{\"id\":\"#{_ids[mode.Id]}\",", StringComparison.Ordinal);
                return Ok(json.Replace("\"id\":\"#", "\"id\":\"", StringComparison.Ordinal));
            }
            if (args.Count == 1 && args[0].StartsWith("output.DP-1.mode.", StringComparison.Ordinal))
            {
                var query = args[0]["output.DP-1.mode.".Length..];
                var pick = Modes.FirstOrDefault(m => _ids[m.Id] == query
                    || $"{m.W}x{m.H}@{(int)Math.Round(m.R, MidpointRounding.AwayFromZero)}" == query);
                if (pick.Id is null) return new ExternalProcessResult(1, "", "kscreen-doctor: no such mode\n");
                CurrentId = _ids[pick.Id];
                return Ok("");
            }
            return new ExternalProcessResult(1, "", "kscreen-doctor: unexpected arguments\n");
        }
    }

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
