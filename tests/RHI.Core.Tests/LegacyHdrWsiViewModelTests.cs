using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #250 through the real view model: a Legacy HDR WSI tick saved earlier (the owner's Metro Exodus EE profile) is left out
/// of the launch line RHI shows for pasting into Steam or into the app that installed an Xbox game, and out of a game RHI
/// starts itself, on a host whose NVIDIA driver does not need the layer — while the saved profile and
/// <c>game-library.json</c> stay exactly as they were. Launches go to a recording launcher; nothing is started.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LegacyHdrWsiViewModelTests : IDisposable
{
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    internal const string Modern = "615.78.08";
    internal const string Older = "580.95.05";
    private const string Variable = "ENABLE_HDR_WSI";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hw-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("xbox")]
    public async Task OnAModernHost_ASavedTick_IsLeftOutOfTheCopiedLaunchText(string store)
    {
        var game = Game(store == "steam" ? "Metro Quest" : "Control Quest", store);
        var launcher = new RecordingLauncher();
        byte[]? seeded = null;
        using var fx = await StartAsync([game], Modern, launcher, s => seeded = Seed(s, game, LinuxHdrModes.Gamescope));
        var runtime = fx.Vm.Runtime;

        Assert.True(runtime.HdrLegacyWsi); // the saved tick is still loaded (hidden, not cleared)
        Assert.True(runtime.CanCopyHdrLaunchOptions);
        Assert.Equal("gamescope --hdr-enabled -- %command%", runtime.GetSelectedHdrLaunchOptions());
        Assert.DoesNotContain(Variable, runtime.HdrLaunchOptions, StringComparison.Ordinal);
        Assert.DoesNotContain(Variable, runtime.HdrProfileHint, StringComparison.Ordinal);
        Assert.DoesNotContain(Variable, HdrLaunchText.Copied(runtime.SelectedHdrPlan, fx.Vm.SelectedGame), StringComparison.Ordinal);

        if (store == "steam")
        {
            // Play hands the game to Steam unwrapped and says what to paste: that line is clean too.
            await fx.Vm.LaunchCommand.ExecuteAsync(null);
            var launch = Assert.Single(launcher.Launches);
            Assert.Null(launch.Options);
            Assert.Contains("paste: gamescope --hdr-enabled -- %command%", fx.Vm.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain(Variable, fx.Vm.StatusText, StringComparison.Ordinal);
        }

        // Loading, planning, copying and launching never rewrote the library; the saved tick is kept.
        Assert.Equal(seeded, File.ReadAllBytes(fx.Library.LibraryPath));
        Assert.True(fx.Library.Load().HdrProfiles[LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!)].EnableLegacyWsi);
    }

    [Fact]
    public async Task OnAnOlderDriver_ASavedTick_IsStillInTheCopiedLaunchText()
    {
        var game = Game("Metro Quest", "steam");
        using var fx = await StartAsync([game], Older, new RecordingLauncher(), store => Seed(store, game, LinuxHdrModes.Gamescope));

        Assert.Equal("ENABLE_HDR_WSI=1 gamescope --hdr-enabled -- %command%", fx.Vm.Runtime.GetSelectedHdrLaunchOptions());
    }

    [Theory]
    [InlineData(Modern, false)]
    [InlineData(Older, true)]
    [InlineData(null, false)]
    public async Task ADirectStart_GetsTheVariable_OnlyWhereTheHostNeedsIt(string? driver, bool used)
    {
        var game = Game("Digging Quest", "direct");
        var launcher = new RecordingLauncher();
        byte[]? seeded = null;
        using var fx = await StartAsync([game], driver, launcher, store => seeded = Seed(store, game, LinuxHdrModes.Gamescope));

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.NotNull(launch.Options);
        Assert.Equal("gamescope --hdr-enabled --", launch.Options!.CommandPrefix);
        Assert.Equal(used, launch.Options!.Environment?.ContainsKey(Variable) == true);
        Assert.Equal(seeded, File.ReadAllBytes(fx.Library.LibraryPath));
    }

    [Fact]
    public async Task AnUnrelatedHdrChange_SavedFromThePanel_KeepsTheHiddenTick()
    {
        var game = Game("Metro Quest", "steam");
        using var fx = await StartAsync([game], Modern, new RecordingLauncher(), store => Seed(store, game, LinuxHdrModes.Gamescope));
        var runtime = fx.Vm.Runtime;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);

        runtime.SelectedHdrMode = runtime.HdrModeOptions.For(LinuxHdrModes.NativeWayland);
        var saved = fx.Library.Load().HdrProfiles[key];
        Assert.Equal(LinuxHdrModes.NativeWayland, saved.Mode);
        Assert.True(saved.EnableLegacyWsi);

        // HDR Off keeps it too (the profile is not default while the tick is saved), and so does the way back.
        runtime.SelectedHdrMode = runtime.HdrModeOptions.For(LinuxHdrModes.Off);
        Assert.True(fx.Library.Load().HdrProfiles[key].EnableLegacyWsi);
        runtime.SelectedHdrMode = runtime.HdrModeOptions.For(LinuxHdrModes.Gamescope);
        saved = fx.Library.Load().HdrProfiles[key];
        Assert.True(saved.EnableLegacyWsi);
        Assert.Equal("gamescope --hdr-enabled -- %command%", runtime.GetSelectedHdrLaunchOptions());
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Saves a profile with the tick on for <paramref name="game"/>; returns the file's bytes.</summary>
    internal static byte[] Seed(LinuxGameLibraryStore store, DetectedGame game, string mode)
    {
        var state = new LinuxGameLibraryState();
        state.HdrProfiles[LinuxGameLibraryStore.GameKey(game)] = new LinuxHdrGameProfile { Mode = mode, EnableLegacyWsi = true };
        store.Save(state);
        return File.ReadAllBytes(store.LibraryPath);
    }

    /// <summary>Starts the view model over the games, selects the first and waits for its analysis.</summary>
    internal static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, string? driver, RecordingLauncher launcher,
        Action<LinuxGameLibraryStore>? seed = null, string language = "en-US", Func<string?>? nvidiaVersion = null)
    {
        var fx = await ViewModelFixture.StartAsync(games, uiLanguage: language, seed: seed, launcher: launcher,
            hdrProfiles: Service(nvidiaVersion ?? (() => driver)));
        await SelectAsync(fx, games[0].Name);
        return fx;
    }

    /// <summary>Selects the card and waits for its analysis (signals, no status-line poll).</summary>
    internal static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
            if (!(fx.Vm.Session.Game?.Name == name && fx.Vm.Session.Analysis is not null))
                await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(HangGuard);
    }

    internal static LinuxHdrLaunchProfileService Service(Func<string?> nvidiaVersion)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => "wayland",
                "WAYLAND_DISPLAY" => "wayland-0",
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: command => command == "gamescope",
            nvidiaVersion: nvidiaVersion,
            nvidiaGpu: () => NvidiaGpuFacts.None);

    internal DetectedGame Game(string name, string launch)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = name, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Proton", CompatibilityTool = "GE-Proton11-3", CompatibilityLayerType = "proton"
        };
        switch (launch)
        {
            case "steam":
                game.Source = "Steam";
                game.SteamAppId = 1449560;
                game.LaunchCommand = "steam -applaunch 1449560";
                break;
            case "xbox":
                // Started by the app that installed it (its own start script); RHI only shows the line to paste there.
                game.Source = "Xbox";
                game.LaunchCommand = "'/apps/installer/start.sh'";
                game.XboxPackage = new XboxPackageInfo("9WSI", "Installer", game.LaunchCommand, null);
                break;
            default:
                game.Source = "Manual";
                game.LaunchCommand = $"umu-run '{exe}'";
                break;
        }
        return game;
    }

    internal sealed class RecordingLauncher : IGameLaunchOptionsLauncher
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
