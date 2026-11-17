using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Launch.LauncherOptions;

namespace RHI.Core.Tests;

/// <summary>
/// #318 through the real view model: the Launch arguments row's "Add to Steam launch options" button on a fake Steam home,
/// RHI's own launch leaving the arguments to Steam once they are there, the box and a display mode switch updating Steam's
/// copy, Reset launch options putting the original back, and a running Steam making the change wait until it has closed.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LauncherOptionsViewModelTests : IDisposable
{
    private const int AppId = 4000001;
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-lopt-vm-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeProbe _probe = new();
    private readonly byte[] _original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "launcher-options", "steam-localconfig.vdf"));
    private readonly string _localconfig;

    public LauncherOptionsViewModelTests()
    {
        UiLocalizationService.Apply("en-US");
        _localconfig = Path.Combine(SteamRoot, "userdata", "10000001", "config", "localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(_localconfig)!);
        File.WriteAllBytes(_localconfig, _original);
    }

    private string SteamRoot => Path.Combine(_root, "home", ".local", "share", "Steam");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string? SteamOptions() => SteamLocalConfig.Read(File.ReadAllBytes(_localconfig), AppId).LaunchOptions;

    [Fact]
    public async Task Button_AddsTheArgumentsToSteam_AndRhisOwnLaunchLeavesThemToSteam()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(launcher);
        var args = fx.Vm.LaunchArguments;
        Assert.False(args.ShowLauncherButton); // nothing to add yet

        args.Text = "-dx11";
        args.Commit();
        Assert.True(args.ShowLauncherButton);
        Assert.Equal("Add to Steam launch options", args.LauncherButtonText);

        args.ToggleLauncherOptionsCommand.Execute(null);

        Assert.Equal("gamemoderun %command% -dx11", SteamOptions());
        Assert.Equal("Steam Quest: launch arguments are in Steam launch options: gamemoderun %command% -dx11", fx.Vm.StatusText);
        Assert.Equal("Remove from Steam launch options", args.LauncherButtonText);
        Assert.Equal("Also in Steam launch options: gamemoderun %command% -dx11. The game gets them however it is started.", args.LauncherCopyNote);
        Assert.True(args.Applies);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);
        var launch = Assert.Single(launcher.Launches);
        Assert.Null(launch.Options?.Arguments); // not passed twice (steam -applaunch would add them on top of Steam's own)
        Assert.Contains("Launch arguments: -dx11 (from Steam launch options).", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingTheBox_UpdatesSteamsCopy_AndClearingItTakesRhisPartOut()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        args.Text = "-dx11";
        args.Commit();
        args.ToggleLauncherOptions();

        args.Text = "-dx12 -windowed";
        args.Commit();
        Assert.Equal("gamemoderun %command% -dx12 -windowed", SteamOptions());
        Assert.EndsWith("launch arguments are in Steam launch options: gamemoderun %command% -dx12 -windowed", fx.Vm.StatusText, StringComparison.Ordinal);

        args.Text = "";
        args.Commit();
        Assert.Equal(_original, File.ReadAllBytes(_localconfig));
        Assert.False(args.ShowLauncherButton);
    }

    [Fact]
    public async Task ResetLaunchOptions_PutsSteamsOriginalOptionsBack()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        args.Text = "-dx11";
        args.Commit();
        args.ToggleLauncherOptions();

        await args.ResetCommand.ExecuteAsync(null);

        Assert.Equal(_original, File.ReadAllBytes(_localconfig));
        Assert.Null(args.LauncherRecord(fx.Vm.SelectedGame!));
        Assert.Equal("Steam Quest: launch options reset (launch arguments). Steam Quest: RHI's launch arguments are out of Steam launch options; your options from before are back.",
            fx.Vm.StatusText);
    }

    [Fact]
    public async Task RemoveButton_AfterTheUserChangedSteamsOptions_KeepsTheirChange()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        args.Text = "-dx11";
        args.Commit();
        args.ToggleLauncherOptions();
        File.WriteAllBytes(_localconfig, SteamLocalConfig.Write(File.ReadAllBytes(_localconfig), AppId, "mangohud %command% -dx11"));

        args.ToggleLauncherOptions();

        Assert.Equal("mangohud %command%", SteamOptions());
        Assert.Equal("Steam Quest: RHI's launch arguments are out of Steam launch options; the options you changed since are kept.", fx.Vm.StatusText);
        Assert.Equal("Add to Steam launch options", args.LauncherButtonText);
    }

    [Fact]
    public async Task WhileSteamRuns_TheChangeWaits_AndIsMadeOnceSteamHasClosed()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        var tick = new TaskCompletionSource();
        args.WaitForLauncher = _ => tick.Task;
        args.Text = "-dx11";
        args.Commit();
        _probe.Running.Add(LauncherKind.Steam);

        args.ToggleLauncherOptions();

        Assert.Equal(_original, File.ReadAllBytes(_localconfig));
        Assert.StartsWith("Steam is running, so RHI changed nothing yet", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal("Stop waiting", args.LauncherButtonText);
        Assert.Equal("Steam is running: RHI makes the change once Steam has closed.", args.LauncherCopyNote);

        _probe.Running.Clear();
        tick.SetResult();
        for (var i = 0; i < 100 && SteamOptions() != "gamemoderun %command% -dx11"; i++) await Task.Delay(20);

        Assert.Equal("gamemoderun %command% -dx11", SteamOptions());
        Assert.Equal("Steam has closed. Steam Quest: launch arguments are in Steam launch options: gamemoderun %command% -dx11", fx.Vm.StatusText);
        Assert.Equal("Remove from Steam launch options", args.LauncherButtonText);
    }

    [Fact]
    public async Task StopWaiting_DropsTheChange()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        var tick = new TaskCompletionSource();
        args.WaitForLauncher = _ => tick.Task;
        args.Text = "-dx11";
        args.Commit();
        _probe.Running.Add(LauncherKind.Steam);
        args.ToggleLauncherOptions();

        args.ToggleLauncherOptions(); // Stop waiting
        _probe.Running.Clear();
        tick.SetResult();
        await Task.Delay(50);

        Assert.Equal(_original, File.ReadAllBytes(_localconfig));
        Assert.Equal("Add to Steam launch options", args.LauncherButtonText);
    }

    [Fact]
    public async Task ADisplayModeSwitch_UpdatesSteamsCopy_OnceTheButtonWasUsed()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        var args = fx.Vm.LaunchArguments;
        args.Text = "-dx11";
        args.Commit();
        args.ToggleLauncherOptions();
        var modes = fx.Vm.DisplayModes;
        modes.Enabled = true;
        var tv = modes.Store.Modes.Single(m => m.Name == "TV").Id;
        modes.SetModeValue(tv, DisplayModeItemKeys.LaunchArgs, "-fullscreen");
        var game = fx.Vm.SelectedGame!;
        Assert.True(modes.Context(game).LaunchArgsOffered);

        await modes.SwitchAsync(game, tv);
        Assert.Equal("gamemoderun %command% -fullscreen", SteamOptions());

        await modes.SwitchAsync(game, null); // Off: the game's own arguments come back
        Assert.Equal("gamemoderun %command% -dx11", SteamOptions());
    }

    [Fact]
    public async Task ADisplayModeSwitch_LeavesSteamAlone_WhenTheButtonWasNotUsed()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        fx.Vm.LaunchArguments.Text = "-dx11";
        fx.Vm.LaunchArguments.Commit();
        var modes = fx.Vm.DisplayModes;
        modes.Enabled = true;
        var tv = modes.Store.Modes.Single(m => m.Name == "TV").Id;
        modes.SetModeValue(tv, DisplayModeItemKeys.LaunchArgs, "-fullscreen");

        await modes.SwitchAsync(fx.Vm.SelectedGame!, tv);

        Assert.Equal(_original, File.ReadAllBytes(_localconfig));
    }

    [Fact]
    public async Task LaunchMenu_OffersTheSameAction()
    {
        using var fx = await StartAsync(new RecordingLauncher());
        fx.Vm.LaunchArguments.Text = "-dx11";
        fx.Vm.LaunchArguments.Commit();
        Assert.True(fx.Vm.LaunchArguments.ShowLauncherButton); // the Tabs Launch ▾ menu shows the entry while the row does
        Assert.Equal("Add to Steam launch options", fx.Vm.LaunchArguments.LauncherButtonText);
    }

    [Fact]
    public void Strings_ExistInBothLanguages()
    {
        var keys = LayoutsCUiStrings.Entries.Where(e => e.Key.StartsWith("LoArgs.", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count >= 24);
        foreach (var row in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.EnUs), row.Key);
            Assert.False(string.IsNullOrWhiteSpace(row.PtBr), row.Key);
            Assert.NotEqual(row.EnUs, row.PtBr);
        }
    }

    private async Task<ViewModelFixture> StartAsync(RecordingLauncher launcher)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "Steam Quest")).FullName;
        var exe = Path.Combine(dir, "SteamQuest.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = "Steam Quest", Source = "Steam", SteamAppId = AppId, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            LaunchExecutablePath = exe, Runner = "Proton", LaunchCommand = $"steam -applaunch {AppId}"
        };
        var fx = await ViewModelFixture.StartAsync([game], launcher: launcher);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == game.Name).Game;
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        fx.Vm.LaunchArguments.LauncherOptions = new LauncherOptionsService(
            new LauncherOptionsStore(Path.Combine(fx.ConfigDir, "launcher-options.json")), Path.Combine(_root, "state"),
            new LauncherOptionsLocator(() => [SteamRoot], () => [], () => []), _probe);
        return fx;
    }

    private sealed class FakeProbe : ILauncherRunningProbe
    {
        public HashSet<LauncherKind> Running { get; } = [];
        public bool IsRunning(LauncherKind kind) => Running.Contains(kind);
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
