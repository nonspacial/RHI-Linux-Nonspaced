using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #311 (upstream 2.9.3 MainViewModel.DisplayModes, game page switch, game menu, Settings card): the presenter over the
/// shell — the feature switch and display-modes.json, the switch writing launch settings into the library file and the
/// page following, Off, Switch all games (hidden and running games skipped), the game menu's submenu, the global values
/// Off goes back to, and the Keep step installs and "write to all games" run.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DisplayModesPresenterTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-dmp-" + Guid.NewGuid().ToString("N"))).FullName;

    public DisplayModesPresenterTests() => UiLocalizationService.Apply("en-US");

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Off_SwitchRefused_MenuAndSwitchHidden_OnSavedInItsOwnFile()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service());
        var game = fx.Vm.Games.Single().Game;
        fx.Vm.SelectedGame = game;
        var dm = fx.Vm.DisplayModes;

        Assert.False(dm.Enabled);
        Assert.False(dm.ShowSwitch);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(fx.Vm.Games.Single()), i => i.Id == "display-mode");
        Assert.Equal("Turn display modes on in Settings › Display & HDR › Display modes first.", await dm.SwitchAsync(game, "tv"));

        dm.Enabled = true;
        Assert.True(dm.ShowSwitch);
        Assert.Equal(Path.Combine(fx.ConfigDir, "display-modes.json"), dm.FilePath);
        Assert.True(new DisplayModeFileStore(dm.FilePath).Load(out _)!.Enabled);
        Assert.Equal(["Monitor", "TV"], dm.SelectedButtons.Select(b => b.Name));
        Assert.All(dm.SelectedButtons, b => Assert.False(b.Active));
    }

    [Fact]
    public async Task Switch_WritesLaunchSettingsToTheLibrary_PageFollows_ActiveAgainTurnsOff()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service());
        var game = fx.Vm.Games.Single().Game;
        fx.Vm.SelectedGame = game;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.SetModeValue("tv", DisplayModeItemKeys.DriverFpsCap, "116");
        dm.SetModeValue("tv", DisplayModeItemKeys.Resolution, "3840x2160");

        var line = await dm.SwitchAsync(game, "tv");
        Assert.Equal("Direct Quest: TV mode. Set: FPS limit 116 FPS, Resolution on launch 3840x2160.", line);
        var key = LinuxGameLibraryStore.GameKey(game);
        var saved = fx.Library.Load();
        Assert.Equal(116u, saved.FpsLimits![key]);
        Assert.True(saved.HdrProfiles[key].ResolutionToggle);
        Assert.Equal("3840x2160", saved.HdrProfiles[key].Resolution);
        Assert.Equal("116", fx.Vm.Runtime.SelectedHdrPlan.Environment["DXVK_FRAME_RATE"]); // the launch takes it
        Assert.True(fx.Vm.Runtime.ResolutionToggle); // the page's resolution row follows
        Assert.Equal(116u, fx.Vm.FrameRateLimit.Selected!.Value); // and the FPS limit row
        var tv = dm.SelectedButtons.Single(b => b.Active);
        Assert.Equal("tv", tv.Id);
        Assert.Null(tv.Target); // the active mode again = Off
        Assert.Contains("Swaps: FPS limit, Resolution on launch.", tv.Tip, StringComparison.Ordinal);

        Assert.Equal("Direct Quest: display mode off, using global settings. Set: FPS limit Follow global, Resolution on launch Off.",
            await dm.SwitchAsync(game, tv.Target));
        saved = fx.Library.Load();
        Assert.Null(saved.FpsLimits);
        Assert.False(saved.HdrProfiles.ContainsKey(key)); // nothing left: the profile goes
        Assert.Null(dm.ActiveModeId(game));
    }

    [Fact]
    public async Task GameMenu_OffModesAndSettings_MarksTheActiveMode()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service());
        var row = fx.Vm.Games.Single();
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.OpenGameSettings = _ => Task.CompletedTask;
        var menu = fx.Vm.CardMenuFor(row);
        var collections = menu.ToList().FindIndex(i => i.Id == "add-to-collection");
        var entry = menu.Single(i => i.Id == "display-mode");
        Assert.Equal(collections + 1, menu.ToList().IndexOf(entry)); // under Add to collection, as upstream
        Assert.Equal(["✓ Off (global settings)", "Monitor", "TV", "Display mode settings..."], entry.Children!.Select(c => c.Label));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)entry.Children!.Single(c => c.Id == "display-mode-tv").Command).ExecuteAsync(null);
        Assert.StartsWith("Direct Quest: TV mode.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(["Off (global settings)", "Monitor", "✓ TV", "Display mode settings..."],
            fx.Vm.CardMenuFor(row).Single(i => i.Id == "display-mode").Children!.Select(c => c.Label));
    }

    [Fact]
    public async Task SwitchAll_SkipsHiddenAndRunningGames()
    {
        var probe = new Probe();
        using var fx = await ViewModelFixture.StartAsync([Game("Alpha"), Game("Beta"), Game("Gamma")], hdrProfiles: Service(), runningProbe: probe);
        var games = fx.Vm.Games.Select(r => r.Game).ToList();
        var alpha = games.Single(g => g.Name == "Alpha");
        var beta = games.Single(g => g.Name == "Beta");
        var gamma = games.Single(g => g.Name == "Gamma");
        fx.Vm.SelectedGame = beta;
        fx.Vm.ToggleHiddenCommand.Execute(null); // Beta hidden
        probe.Running = gamma;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.SetModeValue("tv", DisplayModeItemKeys.DriverFpsCap, "60");

        var (switched, running) = await dm.SwitchAllAsync("tv");
        Assert.Equal((1, 1), (switched, running));
        Assert.Equal("tv", dm.ActiveModeId(alpha));
        Assert.Null(dm.ActiveModeId(beta));
        Assert.Null(dm.ActiveModeId(gamma));
        Assert.Equal(60u, fx.Library.Load().FpsLimits![LinuxGameLibraryStore.GameKey(alpha)]);

        probe.Running = null;
        Assert.Equal((1, 0), await dm.SwitchAllAsync(null)); // Off: only games in a mode
        Assert.Null(dm.ActiveModeId(alpha));
        Assert.Null(fx.Library.Load().FpsLimits);
    }

    [Fact]
    public async Task Refused_WhileTheGameRuns()
    {
        var probe = new Probe();
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service(), runningProbe: probe);
        var game = fx.Vm.Games.Single().Game;
        probe.Running = game;
        fx.Vm.DisplayModes.Enabled = true;
        Assert.Equal("Close Direct Quest first: games write their settings when they close, which would undo the switch.",
            await fx.Vm.DisplayModes.SwitchAsync(game, "tv"));
        Assert.Null(fx.Vm.DisplayModes.ActiveModeId(game));
    }

    [Fact]
    public async Task GlobalValues_FollowSettings()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest"), Game("Red Dead Redemption 2")], hdrProfiles: Service());
        var settings = fx.Settings.Load();
        settings.PeakNits = 800;
        settings.PeakNitsEnabled = true;
        settings.ReLimiterTargetFps = 141;
        settings.OverlayHotkey = "45,0,0,0";
        fx.Vm.Settings.SaveLinuxSettings(settings);
        var dm = fx.Vm.DisplayModes;
        var quest = new DisplayModeContext { Game = fx.Vm.Games.First(r => r.Game.Name == "Direct Quest").Game, Key = "q" };
        var rdr2 = new DisplayModeContext { Game = fx.Vm.Games.First(r => r.Game.Name.StartsWith("Red")).Game, Key = "r" };
        Assert.Equal("800", dm.GlobalValue(quest, DisplayModeItemKeys.RdxPeakNits));
        Assert.Equal("141", dm.GlobalValue(quest, DisplayModeItemKeys.UlTargetFps));
        Assert.Equal("45,0,0,0", dm.GlobalValue(quest, DisplayModeItemKeys.RsOverlayKey));
        Assert.Null(dm.GlobalValue(rdr2, DisplayModeItemKeys.RsOverlayKey)); // RDR2 keeps its own overlay key
        Assert.Equal(DisplayModeItems.FpsGlobal, dm.GlobalValue(quest, DisplayModeItemKeys.DriverFpsCap));
        Assert.Null(dm.GlobalValue(quest, DisplayModeItemKeys.RdxGameNits)); // no global setting: the before-modes value

        settings.PeakNitsEnabled = false;
        fx.Vm.Settings.SaveLinuxSettings(settings);
        Assert.Null(dm.GlobalValue(quest, DisplayModeItemKeys.RdxPeakNits));
    }

    [Fact]
    public async Task InstallKeep_And_WriteToAll_KeepTheModesValues()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service());
        var game = fx.Vm.Games.Single().Game;
        foreach (var kind in new[] { ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX })
            fx.Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!, Version = "1"
            });
        var ini = Path.Combine(game.BinaryDirectory!, "ReShade.ini");
        File.WriteAllText(ini, "[INPUT]\nKeyOverlay=36,0,0,0\n[renodx-preset1]\ntoneMapPeakNits=1000\n");
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.SetModeValue("tv", DisplayModeItemKeys.RsOverlayKey, "45,0,1,0");
        Assert.Null(fx.Managed.DisplayModeKeeper!(game, ManagedComponentKind.ReShade)); // Off: an install writes what it writes
        await dm.SwitchAsync(game, "tv");
        Assert.Contains("KeyOverlay=45,0,1,0", File.ReadAllText(ini));

        // A ReShade update writes Settings' key into ReShade.ini: the mode's key comes back.
        var keep = fx.Managed.DisplayModeKeeper!(game, ManagedComponentKind.ReShade);
        Assert.NotNull(keep);
        File.WriteAllText(ini, "[INPUT]\nKeyOverlay=36,0,0,0\n[renodx-preset1]\ntoneMapPeakNits=1000\n");
        Assert.Equal(1, await keep!(CancellationToken.None));
        Assert.Contains("KeyOverlay=45,0,1,0", File.ReadAllText(ini));
        Assert.Null(fx.Managed.DisplayModeKeeper!(game, ManagedComponentKind.OptiScaler)); // writes none of these files

        // "Write to all games" (Settings) does the same.
        var line = await dm.KeepingAsync([game], DisplayModeItemKeys.GameFileItems, () =>
        {
            File.WriteAllText(ini, "[INPUT]\nKeyOverlay=36,0,0,0\n[renodx-preset1]\ntoneMapPeakNits=400\n");
            return Task.FromResult("written");
        });
        Assert.Equal("written", line);
        Assert.Contains("KeyOverlay=45,0,1,0", File.ReadAllText(ini));
        Assert.Contains("toneMapPeakNits=400", File.ReadAllText(ini)); // not swapped: the global write stays
    }

    [Fact]
    public async Task CustomMode_TickedValueIsWrittenWhileInIt_DeleteTurnsOff()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], hdrProfiles: Service());
        var game = fx.Vm.Games.Single().Game;
        fx.Vm.SelectedGame = game;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.CreateCustom(game);
        Assert.Equal(["Monitor", "TV", "Custom"], dm.SelectedButtons.Select(b => b.Name));
        await dm.SwitchAsync(game, DisplayModeStore.CustomId);
        var item = DisplayModeItems.Get(DisplayModeItemKeys.DriverFpsCap)!;
        Assert.Null(await dm.SetCustomItemAsync(game, item, true, "90"));
        Assert.Equal(90u, fx.Library.Load().FpsLimits![LinuxGameLibraryStore.GameKey(game)]); // written now

        var line = await dm.DeleteCustomAsync(game);
        Assert.StartsWith("Direct Quest: display mode off", line, StringComparison.Ordinal);
        Assert.Null(fx.Library.Load().FpsLimits);
        Assert.Equal(["Monitor", "TV"], dm.SelectedButtons.Select(b => b.Name));
    }

    [Fact]
    public async Task PortugueseUi_SummaryAndChoices()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Direct Quest")], uiLanguage: "pt-BR", hdrProfiles: Service());
        UiLocalizationService.Apply("pt-BR");
        var game = fx.Vm.Games.Single().Game;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.SetModeValue("tv", DisplayModeItemKeys.DriverFpsCap, "0");
        Assert.Equal("Direct Quest: modo TV. Definido: Limite de FPS Desligado.", await dm.SwitchAsync(game, "tv"));
        Assert.Equal("Seguir o global", DisplayModeText.Choices(DisplayModeItems.Get(DisplayModeItemKeys.DriverFpsCap)!)[0].Label);
    }

    [Fact]
    public void EveryItemGroupAndReason_HasEnglishAndPortugueseText()
    {
        var keys = DisplayModeItems.All.SelectMany(i => new[] { "Dm.Item." + i.Key, "Dm.Help." + i.Key })
            .Concat(DisplayModeItems.Groups.Select(g => "Dm.Group." + g))
            .Concat(Enum.GetNames<DisplayModeUnavailable>().Where(n => n != "None").Select(n => "Dm.Why." + n))
            .Concat(Enum.GetNames<DisplayModeNameProblem>().Select(n => "Dm.Name." + n))
            .Concat(["Dm.MenuButton", "Dm.MenuButtonTip", "Dm.CogTip", "Dm.MenuOff", "Dm.Label", "Dm.Off", "Dm.Custom"]);
        foreach (var language in new[] { "en-US", "pt-BR" })
        {
            UiLocalizationService.Apply(language);
            foreach (var key in keys) Assert.NotEqual(key, UiLocalizationService.Text(key));
        }
    }

    private static LinuxHdrLaunchProfileService Service()
        => new(environment: name => name == "XDG_SESSION_TYPE" ? "wayland" : name == "XDG_CURRENT_DESKTOP" ? "KDE" : null,
            commandExists: name => name == "kscreen-doctor", nvidiaVersion: () => null, nvidiaGpu: () => NvidiaGpuFacts.None);

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Manual", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Proton", CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton", LaunchCommand = "umu-run " + name.Replace(" ", "") + ".exe"
        };
    }

    private sealed class Probe : IGameRunningProbe
    {
        public DetectedGame? Running { get; set; }
        public bool IsRunning(DetectedGame game) => Running is { } running && running.Name == game.Name;
    }
}
