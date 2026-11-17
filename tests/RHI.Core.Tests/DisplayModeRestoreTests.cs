using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review (platform): display modes put a game back exactly as it was before modes, or say they could not.
/// <list type="bullet">
/// <item>M3: Off forgets the values from before modes only once every one of them is back; else the game stays in its mode
/// and Off can be tried again. A mode deleted while a game could not be turned off leaves a pending restore.</item>
/// <item>m7: a file that cannot be read stops a switch; what it wrote goes back and nothing is recorded.</item>
/// <item>m8: a key or file the game did not have before modes is removed again by Off.</item>
/// <item>m9 / m10: no switch while display-modes.json is unreadable, or while another operation runs.</item>
/// <item>Nits: line endings and BOM kept, RenoDX nits not rounded, hand-edited presets checked on load, a launch edit made
/// during a switch kept.</item>
/// </list>
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DisplayModeRestoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-dmr-" + Guid.NewGuid().ToString("N"))).FullName;

    public DisplayModeRestoreTests() => UiLocalizationService.Apply("en-US");

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        IniLines.WriteFaultForTesting.Value = null;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── M3: Off only once everything is back ─────────────────────────────────

    [Fact]
    public async Task Off_ThatCannotWriteAValueBack_StaysInTheMode_KeepsTheOriginals_AndARetryFinishes()
    {
        var f = Fixture("off-fails");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "117");
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), "tv");
        IniLines.WriteFaultForTesting.Value = path => Path.GetFileName(path) == "ReShade.ini" ? new IOException("disk full") : null;

        var off = await svc.TurnOffAsync(f.Ctx());

        Assert.False(off.TurnedOff);
        Assert.Equal("tv", off.ModeId);
        Assert.Equal([DisplayModeItemKeys.RdxPeakNits], off.NotRestored.Select(i => i.Item.Key));
        Assert.Equal("tv", store.ActiveModeId(f.Key));
        Assert.Equal("1000", store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits)); // not forgotten
        Assert.Equal("700", f.Peak());
        Assert.Equal("141", f.Fps()); // what could go back went back

        IniLines.WriteFaultForTesting.Value = null;
        var retry = await svc.TurnOffAsync(f.Ctx());

        Assert.True(retry.TurnedOff);
        Assert.Equal("1000", f.Peak());
        Assert.False(store.IsManaged(f.Key));
        Assert.Null(store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits));
    }

    [Fact]
    public async Task Off_WhileTheGamesFolderCannotBeWritten_KeepsTheMode()
    {
        var f = Fixture("off-refused");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        string? refusal = null;
        var svc = new DisplayModeService(store, new DisplayModeIo(new DisplayModeGameFiles(f.Records, null, _ => refusal)));
        await svc.SwitchAsync(f.Ctx(), "tv");
        refusal = "the game is running";

        var off = await svc.TurnOffAsync(f.Ctx());

        Assert.False(off.TurnedOff);
        Assert.Equal("tv", store.ActiveModeId(f.Key));
        refusal = null;
        Assert.True((await svc.TurnOffAsync(f.Ctx())).TurnedOff);
        Assert.Equal("1000", f.Peak());
    }

    [Fact]
    public async Task DeletingAModeWhileAGameRuns_LeavesAPendingRestore_ThatOffWritesBackLater()
    {
        var probe = new Probe();
        using var fx = await ViewModelFixture.StartAsync([LaunchGame("Runner")], hdrProfiles: HdrService(), runningProbe: probe);
        var game = fx.Vm.Games.Single().Game;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        var tv = dm.AddMode("Big Screen")!;
        dm.SetModeValue(tv, DisplayModeItemKeys.DriverFpsCap, "60");
        await dm.SwitchAsync(game, tv);
        Assert.Equal(60u, fx.Library.Load().FpsLimits![LinuxGameLibraryStore.GameKey(game)]);
        probe.Running = game;

        var (deleted, turnedOff, pending) = await dm.DeleteModeAsync(tv);

        Assert.True(deleted);
        Assert.Equal(0, turnedOff);
        Assert.Equal(1, pending);
        Assert.Null(dm.ActiveModeId(game));
        Assert.True(dm.Store.HasRestorePending(LinuxGameLibraryStore.GameKey(game)));
        Assert.True(new DisplayModeFileStore(dm.FilePath).Load(out _)!.Games[LinuxGameLibraryStore.GameKey(game)].RestorePending); // saved

        probe.Running = null;
        var line = await dm.SwitchAsync(game, null); // Off in the game's menu

        Assert.StartsWith("Runner: display mode off", line, StringComparison.Ordinal);
        Assert.Null(fx.Library.Load().FpsLimits); // back to Global
        Assert.False(dm.Store.HasRestorePending(LinuxGameLibraryStore.GameKey(game)));
    }

    [Fact]
    public async Task APendingRestore_IsKeptWhenTheGameSwitchesToAnotherMode()
    {
        var f = Fixture("pending-switch");
        var store = new DisplayModeStore();
        var big = store.AddMode("Big")!;
        store.SetModeDefault(big, DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "500");
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), big);
        store.DeleteMode(big); // the game could not be turned off first

        await svc.SwitchAsync(f.Ctx(), "tv");

        Assert.Equal("500", f.Peak());
        Assert.Equal("1000", store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits)); // not the deleted mode's 700
        Assert.True((await svc.TurnOffAsync(f.Ctx())).TurnedOff);
        Assert.Equal("1000", f.Peak());
    }

    // ── m7: a read error stops the switch cleanly ────────────────────────────

    [Fact]
    public async Task AFileThatCannotBeRead_StopsTheSwitch_PutsBackWhatItWrote_AndRecordsNothing()
    {
        var f = Fixture("unreadable");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "117");
        var svc = f.Service(store);
        File.SetUnixFileMode(f.ReLimiterIni, UnixFileMode.None);

        var result = await svc.SwitchAsync(f.Ctx(), "tv");

        Assert.NotNull(result.Error);
        Assert.Null(result.ModeId);
        Assert.Empty(result.Written);
        Assert.Equal("1000", f.Peak()); // the brightness written first went back
        Assert.False(store.IsManaged(f.Key));
        Assert.Null(store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits));

        File.SetUnixFileMode(f.ReLimiterIni, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("1000", store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits)); // not the mode's 700
        Assert.Equal("700", f.Peak());
    }

    [Fact]
    public void Summary_NamesTheReadErrorAndTheValuesOffCouldNotPutBack()
    {
        var item = DisplayModeItems.Get(DisplayModeItemKeys.RdxPeakNits)!;
        var stopped = new DisplayModeService.SwitchResult("Game", null, "Off", []) { Error = "Access to the path '/x/relimiter.ini' is denied." };
        Assert.Equal("Game: the display mode was not changed: Access to the path '/x/relimiter.ini' is denied. What had been written was put back; nothing was recorded.",
            RHI.Desktop.ViewModels.DisplayModeText.Summary(stopped));
        var kept = new DisplayModeService.SwitchResult("Game", "tv", "TV", [new(item, false, "1000", DisplayModeUnavailable.None, "disk full", NotRestored: true)]) { OffRequested = true };
        Assert.StartsWith("Game: still in TV mode: ", RHI.Desktop.ViewModels.DisplayModeText.Summary(kept), StringComparison.Ordinal);
        Assert.EndsWith(" couldn't be put back. The values from before modes are kept; try Off again. (disk full)", RHI.Desktop.ViewModels.DisplayModeText.Summary(kept), StringComparison.Ordinal);

        UiLocalizationService.Apply("pt-BR");
        Assert.StartsWith("Game: continua no modo TV: ", RHI.Desktop.ViewModels.DisplayModeText.Summary(kept), StringComparison.Ordinal);
    }

    // ── m8: what was not there before modes goes again ───────────────────────

    [Fact]
    public async Task Off_RemovesKeysAndAFileTheGameDidNotHaveBeforeModes()
    {
        var f = Fixture("absent", reShadeIni: "[GENERAL]\nEffectSearchPaths=.\\\n[INPUT]\nKeyOverlay=36,0,0,0\n");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RsPreset, "TV.ini");
        store.SetModeDefault("tv", DisplayModeItemKeys.RsGamepadNav, "1");
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.DcFpsLimit, "117.5");
        var svc = f.Service(store);
        var before = File.ReadAllText(f.ReShadeIni);
        var dcIni = Path.Combine(f.Bin, "DisplayCommander.ini");

        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Contains("PresetPath=TV.ini", File.ReadAllText(f.ReShadeIni));
        Assert.Contains("GamepadNavigation=1", File.ReadAllText(f.ReShadeIni));
        Assert.True(File.Exists(dcIni));
        Assert.True(store.TryGetOriginal(f.Key, DisplayModeItemKeys.RsGamepadNav, out var nav));
        Assert.Null(nav); // "none" is recorded, not "0"

        var off = await svc.TurnOffAsync(f.Ctx());

        Assert.True(off.TurnedOff);
        var after = File.ReadAllText(f.ReShadeIni);
        Assert.DoesNotContain("PresetPath", after);
        Assert.DoesNotContain("GamepadNavigation", after);
        Assert.DoesNotContain("oneMapPeakNits", after);
        Assert.StartsWith(before.TrimEnd(), after, StringComparison.Ordinal); // only the empty preset header may stay
        Assert.False(File.Exists(dcIni)); // a mode made it; no record lists it
    }

    [Fact]
    public async Task Off_KeepsADisplayCommanderIniThatWasThere()
    {
        var f = Fixture("dc-kept");
        var dcIni = Path.Combine(f.Bin, "DisplayCommander.ini");
        File.WriteAllText(dcIni, "[DisplayCommander]\nother=1\n");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.DcFpsLimit, "117.5");
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), "tv");
        await svc.TurnOffAsync(f.Ctx());
        Assert.Equal("[DisplayCommander]\nother=1\n", File.ReadAllText(dcIni));
    }

    // ── m9 / m10 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSwitch_WhileDisplayModesJsonIsUnreadable()
    {
        using var fx = await ViewModelFixture.StartAsync([LaunchGame("Broken")], hdrProfiles: HdrService(), seed: library =>
        {
            var config = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(library.LibraryPath)!)!, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "display-modes.json"), "{ not json");
        });
        var game = fx.Vm.Games.Single().Game;
        var dm = fx.Vm.DisplayModes;
        Assert.True(File.Exists(dm.FilePath));
        dm.Store.Enabled = true; // as the session would have it (Enabled's own save is refused)

        var line = await dm.SwitchAsync(game, "tv");

        Assert.Equal($"Display modes can't switch games while {dm.FilePath} can't be read (it was set aside). Fix or delete the file.", line);
        Assert.Null(dm.ActiveModeId(game));
        Assert.Equal((0, 0, 0), await dm.SwitchAllAsync("tv"));
    }

    [Fact]
    public async Task NoSwitch_WhileAnotherOperationRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([LaunchGame("Busy")], hdrProfiles: HdrService());
        var game = fx.Vm.Games.Single().Game;
        var dm = fx.Vm.DisplayModes;
        dm.Enabled = true;
        dm.SetModeValue("tv", DisplayModeItemKeys.DriverFpsCap, "60");
        using (((RHI.Desktop.ViewModels.IPanelHost)fx.Vm).BeginOperation("install", cancellable: false))
        {
            Assert.Equal("Another RHI operation is already running.", await dm.SwitchAsync(game, "tv"));
            Assert.Null(dm.ActiveModeId(game));
        }
        Assert.StartsWith("Busy: TV mode.", await dm.SwitchAsync(game, "tv"), StringComparison.Ordinal);
    }

    // ── Nits ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchAndOff_KeepTheFilesLineEndingsAndBom_AndDoNotRoundRenoDxNits()
    {
        var text = "; keep me\r\n[GENERAL]\r\nPresetPath=Default.ini\r\n[renodx-preset1]\r\ntoneMapPeakNits=1000.5\r\n";
        var f = Fixture("crlf", reShadeIni: text, bom: true);
        var before = File.ReadAllBytes(f.ReShadeIni);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        var svc = f.Service(store);

        await svc.SwitchAsync(f.Ctx(), "tv");
        var during = File.ReadAllBytes(f.ReShadeIni);
        Assert.Equal([0xEF, 0xBB, 0xBF], during.Take(3));
        Assert.Contains("toneMapPeakNits=700\r\n", Encoding.UTF8.GetString(during));
        Assert.Equal("1000.5", store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits));

        await svc.TurnOffAsync(f.Ctx());
        Assert.Equal(before, File.ReadAllBytes(f.ReShadeIni));
    }

    [Theory]
    [InlineData("TV.ini", "TV.ini")]
    [InlineData("presets/Night.txt", "presets/Night.txt")]
    [InlineData("TV.ini\nKeyOverlay=1,0,0,0", null)]
    [InlineData("TV", null)]
    public void HandEditedPresets_AreCheckedOnLoad(string value, string? kept)
    {
        var data = DisplayModeStore.CreateDefaults();
        data.Modes[1].Values[DisplayModeItemKeys.RsPreset] = value;
        data.Games["g"] = new GameDisplayModeState { Custom = true, Items = [DisplayModeItemKeys.RsPreset] };
        data.Games["g"].Values[DisplayModeStore.CustomId] = new(StringComparer.OrdinalIgnoreCase) { [DisplayModeItemKeys.RsPreset] = value };
        var store = new DisplayModeStore();

        store.Load(data);

        Assert.Equal(kept, store.ModeDefault("tv", DisplayModeItemKeys.RsPreset));
        Assert.Equal(kept, store.CustomValue("g", DisplayModeItemKeys.RsPreset));
    }

    [Fact]
    public void ALaunchEditMadeDuringASwitch_IsNotOverwritten()
    {
        var state = new LinuxGameLibraryState();
        state.FpsLimits = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["g"] = 90 };
        var copy = DisplayModeLaunchValues.From(state, "g");
        Assert.Null(DisplayModeLaunchSettings.Write(copy, DisplayModeItemKeys.Hdr, LinuxHdrModes.Gamescope)); // the switch
        state.FpsLimits["g"] = 120; // the user, on the page, while the switch ran

        copy.ApplyTo(state, "g");

        Assert.Equal(LinuxHdrModes.Gamescope, state.HdrProfiles["g"].Mode);
        Assert.Equal(120u, state.FpsLimits!["g"]);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed class Probe : IGameRunningProbe
    {
        public DetectedGame? Running { get; set; }
        public bool IsRunning(DetectedGame game) => Running is { } running && running.Name == game.Name;
    }

    private static LinuxHdrLaunchProfileService HdrService()
        => new(environment: name => name == "XDG_SESSION_TYPE" ? "wayland" : name == "XDG_CURRENT_DESKTOP" ? "KDE" : null,
            commandExists: name => name == "kscreen-doctor", nvidiaVersion: () => null, nvidiaGpu: () => NvidiaGpuFacts.None);

    private DetectedGame LaunchGame(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Manual", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Proton", CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton", LaunchCommand = "umu-run " + name + ".exe"
        };
    }

    private sealed class GameFixture(DetectedGame game, LinuxDeploymentRecordStore records)
    {
        public LinuxDeploymentRecordStore Records => records;
        public string Bin => game.BinaryDirectory!;
        public string Key => LinuxGameLibraryStore.GameKey(game);
        public string ReShadeIni => Path.Combine(Bin, "ReShade.ini");
        public string ReLimiterIni => Path.Combine(Bin, "relimiter.ini");

        public DisplayModeService Service(DisplayModeStore store)
            => new(store, new DisplayModeIo(new DisplayModeGameFiles(records, null, _ => null)));

        public DisplayModeContext Ctx() => new() { Game = game, Key = Key, Launch = new DisplayModeLaunchValues() };

        public string? Peak() => RenoDxNitsIni.Read(IniLines.Read(ReShadeIni), RenoDxNitsIni.PeakKey);

        public string? Fps() => IniLines.Get(IniLines.Read(ReLimiterIni), "FrameLimiter", "target_fps") ?? "0";
    }

    private GameFixture Fixture(string name, string? reShadeIni = null, bool bom = false)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, name, "bin")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "DM " + name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Test" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        foreach (var kind in new[] { ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX, ManagedComponentKind.ReLimiter, ManagedComponentKind.DisplayCommander })
        {
            var file = Path.Combine(bin, kind + ".bin");
            File.WriteAllText(file, kind.ToString());
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
                BinaryDirectory = bin, Version = "1", Files = [new LinuxDeployedFile { Path = file }]
            });
        }
        var ini = reShadeIni ?? "[GENERAL]\nPresetPath=Default.ini\n[renodx-preset1]\ntoneMapPeakNits=1000\n";
        File.WriteAllBytes(Path.Combine(bin, "ReShade.ini"), bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ini)] : Encoding.UTF8.GetBytes(ini));
        File.WriteAllText(Path.Combine(bin, "relimiter.ini"), "[FrameLimiter]\ntarget_fps=141\nosd_toggle_key=F10\n");
        return new GameFixture(game, records);
    }
}
