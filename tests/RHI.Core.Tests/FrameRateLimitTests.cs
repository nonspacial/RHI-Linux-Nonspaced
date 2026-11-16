using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Core.Services;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #310 (upstream 2.8.9 per-game FPS limit; owner decision 2026-11-16): the cap goes to DXVK_FRAME_RATE and
/// VKD3D_FRAME_RATE in the game's launch variables. Global follows Settings, Off drops it, Custom takes 20–1000, and
/// upstream's rule leaves Global uncapped in ReLimiter / Display Commander games.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class FrameRateLimitTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-fps-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- rules -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 0u, false, null)]   // Global, no global cap
    [InlineData(null, 144u, false, 144u)] // Global follows Settings
    [InlineData(null, 144u, true, null)]  // upstream: ReLimiter / Display Commander games get the cap off
    [InlineData(0u, 144u, false, null)]   // Off beats the global cap
    [InlineData(90u, 144u, false, 90u)]   // the game's own cap
    [InlineData(90u, 0u, true, 90u)]      // chosen for the game: applies even with a frame limiter (upstream lets you)
    [InlineData(19u, 0u, false, null)]    // outside 20–1000: none
    [InlineData(null, 1001u, false, null)]
    public void Effective(uint? perGame, uint global, bool limiter, uint? expected)
        => Assert.Equal(expected, FrameRateLimit.Effective(perGame, global, limiter));

    [Fact]
    public void Environment_SetsBothLayers()
    {
        Assert.Equal(new Dictionary<string, string> { ["DXVK_FRAME_RATE"] = "116", ["VKD3D_FRAME_RATE"] = "116" },
            FrameRateLimit.Environment(116).OrderBy(p => p.Key).ToDictionary());
        Assert.Empty(FrameRateLimit.Environment(null));
    }

    [Fact]
    public void Presets_AreUpstreams()
    {
        // Upstream DlssPresetService.FpsLimiterPresets at RHI-2.9.4 (without Off and Custom...).
        Assert.Equal([59u, 73u, 97u, 116u, 138u, 157u, 171u, 189u, 224u, 258u, 275u, 324u, 416u], FrameRateLimit.Presets.Select(p => p.Fps));
    }

    [Fact]
    public void Plan_HiddenForNativeGamesAndStoreClients()
    {
        Assert.Equal(FrameRateLimitReason.NativeLinux, FrameRateLimit.Plan(Game("Native", "Manual", "./game", "Native Linux"), 60).Reason);
        Assert.Equal(FrameRateLimitReason.Ready, FrameRateLimit.Plan(Game("Steamy", "Steam", "steam -applaunch 1234"), 60).Reason);
        Assert.True(FrameRateLimit.Plan(Game("Direct", "Manual", "umu-run Direct.exe"), 60).HasVariables);
    }

    [Fact]
    public void LaunchPlan_HdrOff_DirectLaunch_AppliesTheVariables_WithoutAnNvidiaGpu()
    {
        var plan = Service().BuildPlan(Game("Direct", "Manual", "umu-run Direct.exe"), null, Service().ProbeHost(), null, frameRateLimit: 116);
        Assert.True(plan.AppliesAtLaunch);
        Assert.False(plan.Nvidia.HasVariables);
        Assert.Equal("116", plan.Environment["DXVK_FRAME_RATE"]);
        Assert.Equal("116", plan.Environment["VKD3D_FRAME_RATE"]);
        Assert.Equal("DXVK_FRAME_RATE=116 VKD3D_FRAME_RATE=116 %command%", plan.LaunchOptions);

        var none = Service().BuildPlan(Game("Direct", "Manual", "umu-run Direct.exe"), null, Service().ProbeHost(), null, frameRateLimit: null);
        Assert.False(none.AppliesAtLaunch);
        Assert.Empty(none.Environment);
    }

    [Fact]
    public void LibraryFile_KeepsOffAndCaps_DropsTheRest_AndFollowsAnOlderKey()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "tunic")).FullName;
        var game = new DetectedGame { Name = "Tunic", Source = "Manual", InstallPath = dir };
        var state = new LinuxGameLibraryState
        {
            FpsLimits = new() { [LinuxGameLibraryStore.LegacyGameKey(game)] = 60, ["a"] = 0, ["b"] = 5, ["c"] = 2000 }
        }.Normalize();
        Assert.Equal(["a", LinuxGameLibraryStore.LegacyGameKey(game)], state.FpsLimits!.Keys.Order(StringComparer.Ordinal));

        Assert.True(state.AdoptOlderKeys(game));
        Assert.Equal(60u, state.FpsLimits[LinuxGameLibraryStore.GameKey(game)]);
        Assert.Equal(0, new LinuxGameLibraryState().Normalize().FpsLimits?.Count ?? 0);
    }

    [Fact]
    public void GlobalSetting_IsNormalised()
    {
        Assert.Equal(0u, new LinuxAppSettings { GlobalFpsLimit = 5 }.Normalize().GlobalFpsLimit);
        Assert.Equal(144u, new LinuxAppSettings { GlobalFpsLimit = 144 }.Normalize().GlobalFpsLimit);
    }

    // ---- the shell ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GameRow_GlobalOffPresetsAndCustom_SavedAndLaunched()
    {
        var launcher = new RecordingLauncher();
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest", "Manual", "umu-run DirectQuest.exe")], launcher: launcher, hdrProfiles: Service());
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        var fps = fx.Vm.Dlss.FrameRateLimit;
        Assert.True(fps.IsShown);
        Assert.Equal("Global (Off)", fps.Choices[0].Label);
        Assert.Equal(["Off", "59 FPS (60Hz VRR Cap)"], fps.Choices.Skip(1).Take(2).Select(c => c.Label));
        Assert.Equal("Custom…", fps.Choices[^1].Label);
        Assert.Same(fps.Choices[0], fps.Selected);

        fps.Selected = fps.Choices.Single(c => c.Value == 116);
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);
        Assert.Equal(116u, fx.Library.Load().FpsLimits![key]);
        Assert.StartsWith("FPS limit for Direct Quest: 116 FPS, applied the next time the game starts.", fx.Vm.StatusText);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal("116", launch.Options!.Environment!["DXVK_FRAME_RATE"]);
        Assert.Equal("116", launch.Options.Environment["VKD3D_FRAME_RATE"]);
        Assert.StartsWith("Launching Direct Quest with a 116 FPS limit", fx.Vm.StatusText, StringComparison.Ordinal);

        // Custom…: 20–1000, its own entry above Custom… once set.
        fps.Selected = fps.Choices[^1];
        Assert.True(fps.IsCustomEntry);
        fps.CustomText = "5000";
        fps.ApplyCustomCommand.Execute(null);
        Assert.Equal("The FPS limit has to be a number from 20 to 1000; nothing was changed.", fx.Vm.StatusText);
        Assert.Equal(116u, fx.Library.Load().FpsLimits![key]);
        fps.Selected = fps.Choices[^1];
        fps.CustomText = "90";
        fps.ApplyCustomCommand.Execute(null);
        Assert.Equal(90u, fx.Library.Load().FpsLimits![key]);
        Assert.Equal("90 FPS (Custom)", fps.Selected!.Label);

        fps.Selected = fps.Choices[0]; // back to Global: no entry kept
        Assert.Null(fx.Library.Load().FpsLimits);
    }

    [Fact]
    public async Task GlobalLimit_AppliesToGamesOnGlobal_ButNotWithReLimiter_AndOffDropsIt()
    {
        var launcher = new RecordingLauncher();
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest", "Manual", "umu-run DirectQuest.exe")], launcher: launcher, hdrProfiles: Service());
        var game = fx.Vm.Games.Single().Game;
        fx.Vm.SelectedGame = game;
        var settings = fx.Settings.Load();
        settings.GlobalFpsLimit = 138;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        var fps = fx.Vm.Dlss.FrameRateLimit;
        Assert.Equal("Global (138 FPS)", fps.Choices[0].Label);
        Assert.Equal("138", fx.Vm.Runtime.SelectedHdrPlan.Environment["VKD3D_FRAME_RATE"]);

        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReLimiter,
            BinaryDirectory = game.BinaryDirectory!, Version = "1"
        });
        Assert.False(fx.Vm.Runtime.SelectedHdrPlan.FrameRate.HasVariables);
        fps.Load();
        Assert.Contains("ReLimiter or Display Commander is installed for this game", fps.Tip, StringComparison.Ordinal);

        fps.Selected = fps.Choices.Single(c => c.Value == 0); // Off
        Assert.False(fx.Vm.Runtime.SelectedHdrPlan.FrameRate.HasVariables);
        Assert.Equal(0u, fx.Library.Load().FpsLimits![LinuxGameLibraryStore.GameKey(game)]);
    }

    [Fact]
    public async Task SteamHandoff_SaysWhereToPasteTheLine()
    {
        var launcher = new RecordingLauncher();
        using var fx = await ViewModelFixture.StartAsync([Game("Steam Quest", "Steam", "steam -applaunch 1234")], launcher: launcher, hdrProfiles: Service());
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        fx.Vm.Dlss.FrameRateLimit.Selected = fx.Vm.Dlss.FrameRateLimit.Choices.Single(c => c.Value == 59);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(launcher.Launches).Options);
        Assert.Contains("Steam starts this game, so RHI does not apply the FPS limit on Launch", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("paste: DXVK_FRAME_RATE=59 VKD3D_FRAME_RATE=59 %command%", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void PortugueseUi()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Global: 60 FPS", FrameRateLimitPresenter.ChoicesFor(60, null)[0].Label);
            Assert.Equal("Limite de FPS do jogo", UiLocalizationService.Text("Fps.Label"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static LinuxHdrLaunchProfileService Service()
        => new(environment: name => name == "XDG_SESSION_TYPE" ? "wayland" : null, commandExists: _ => false,
            nvidiaVersion: () => null, nvidiaGpu: () => NvidiaGpuFacts.None);

    private DetectedGame Game(string name, string source, string command, string runner = "Proton")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = source, InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = runner, CompatibilityTool = runner == "Native Linux" ? null : "GE-Proton10-25",
            CompatibilityLayerType = runner == "Native Linux" ? null : "proton", LaunchCommand = command,
            SteamAppId = source == "Steam" ? 1234 : null
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
