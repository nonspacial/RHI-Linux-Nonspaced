using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// Launch with an HDR profile on a store hand-off, through the real view model (w2-launch review, minors 3 and 8):
/// the store client is started without launch options and the status line says where to enter the profile, or why
/// it is blocked; a blocked profile never stops a hand-off from starting. A direct launch with a blocked profile is
/// still refused (RHI would apply it there).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HdrHandoffLaunchViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Handoff_WithAnApplicableProfile_StartsTheStoreClientWithoutOptions_AndGivesItsSteps()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Steam Quest", "Steam", "steam -applaunch 1234"), launcher, gamescope: true);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(("Steam Quest", null), (launch.Game, launch.Options));
        Assert.StartsWith("Launching Steam Quest", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("Steam starts this game, so RHI does not apply the HDR profile on Launch", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("gamescope --hdr-enabled -- %command%", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handoff_WithABlockedProfile_StillStartsTheStoreClient_AndSaysWhyTheProfileIsNotUsed()
    {
        // Gamescope mode without gamescope on PATH used to refuse Launch ("HDR launch blocked") on a Steam game, which
        // then could not be started from RHI until HDR was set Off, though RHI applies nothing to a hand-off anyway.
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Steam Quest", "Steam", "gamemoderun steam -applaunch 1234"), launcher, gamescope: false);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Null(launch.Options);
        Assert.StartsWith("Launching Steam Quest", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("The launcher starts this game without the HDR profile, which is blocked:", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("gamescope executable was not found", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectLaunch_WithABlockedProfile_IsStillRefused()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Wine Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), launcher, gamescope: false);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Empty(launcher.Launches);
        Assert.StartsWith("HDR launch blocked:", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectLaunch_WithAnApplicableProfile_PassesTheProfileToTheLauncher()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Wine Quest", "Wine", "WINEPREFIX=/tmp/pfx wine Game.exe"), launcher, gamescope: true);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Equal("gamescope --hdr-enabled --", launch.Options?.CommandPrefix);
    }

    /// <summary>Starts the view model over one game, selects it, waits for its analysis and sets HDR to Gamescope.</summary>
    private static async Task<ViewModelFixture> StartAsync(DetectedGame game, RecordingLauncher launcher, bool gamescope)
    {
        var fx = await ViewModelFixture.StartAsync([game], launcher: launcher, hdrProfiles: Service(gamescope));
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == game.Name).Game;
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || !fx.Vm.StatusText.StartsWith("Ready", StringComparison.Ordinal)); i++)
            await Task.Delay(25);
        fx.Vm.Runtime.SelectedHdrMode = fx.Vm.Runtime.HdrModeOptions.ForDisplay(UiLocalizationService.Text("Ui.HdrGamescope"));
        Assert.True(fx.Vm.Runtime.SelectedHdrPlan.Enabled);
        return fx;
    }

    private static LinuxHdrLaunchProfileService Service(bool gamescope)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => "wayland",
                "WAYLAND_DISPLAY" => "wayland-0",
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: command => command == "gamescope" && gamescope,
            nvidiaVersion: () => null);

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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
