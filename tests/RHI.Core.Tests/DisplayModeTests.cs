using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.DisplayModes;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #311 (upstream 2.9.3 Display modes, 2.9.4 end state; ported from upstream's <c>DisplayModeTests</c>): the store (modes,
/// a game's Custom mode), switching with real ini files journaled on the components' records, Off, the Keep step for
/// installs and "write to all", and the Linux launch settings (HDR mode, resolution switch, FPS limit, NVIDIA settings).
/// </summary>
public sealed class DisplayModeTests : IDisposable
{
    private const string Key = "steam:123";
    private const string Other = "steam:456";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-dm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Store ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreMonitorAndTv_SwappingNothing_FeatureOff()
    {
        var store = new DisplayModeStore();
        Assert.Equal(["Monitor", "TV"], store.Modes.Select(m => m.Name));
        Assert.Empty(store.ItemsFor(Key));
        Assert.Null(store.ActiveModeId(Key)); // new games start Off
        Assert.False(store.Enabled);
    }

    [Fact]
    public void Modes_AddRenameMoveDelete()
    {
        var store = new DisplayModeStore();
        var id = store.AddMode("Handheld");
        Assert.NotNull(id);
        Assert.Null(store.AddMode("handheld")); // duplicate, any case
        Assert.Equal(DisplayModeNameProblem.Taken, store.ValidateName("tv"));
        Assert.Null(store.ValidateName("TV", "tv")); // renaming to its own name
        Assert.Equal(DisplayModeNameProblem.Reserved, store.ValidateName("custom")); // a game's own mode is called Custom
        Assert.Equal(DisplayModeNameProblem.Empty, store.ValidateName("  "));
        Assert.Equal(DisplayModeNameProblem.TooLong, store.ValidateName(new string('x', DisplayModeStore.MaxNameLength + 1)));
        Assert.True(store.RenameMode(id!, "Deck"));
        Assert.True(store.MoveMode(id!, -1));
        Assert.Equal(["Monitor", "Deck", "TV"], store.Modes.Select(m => m.Name));
        Assert.False(store.MoveMode(store.Modes[0].Id, -1)); // already first

        store.SetActive(Key, id!);
        Assert.True(store.DeleteMode(id!));
        Assert.Null(store.ActiveModeId(Key)); // turned off
        Assert.Empty(store.Snapshot().Games); // nothing left to save
        Assert.True(store.DeleteMode("tv"));
        Assert.False(store.DeleteMode("monitor")); // one always stays
    }

    [Fact]
    public void MaxModes_IsEnforced()
    {
        var store = new DisplayModeStore();
        for (var i = 0; i < DisplayModeStore.MaxModes - 2; i++) Assert.NotNull(store.AddMode("Mode " + i));
        Assert.Null(store.AddMode("One too many"));
        Assert.Equal(DisplayModeStore.MaxModes, store.Modes.Count);
    }

    [Fact]
    public void Custom_OnlyForTheGameThatMadeIt()
    {
        var store = new DisplayModeStore();
        store.CreateCustom(Key);
        Assert.Equal(3, store.ModesFor(Key).Count);
        Assert.Equal(2, store.ModesFor(Other).Count); // no Custom for other games
        store.SetInCustom(Key, DisplayModeItemKeys.RdxPeakNits, true);
        store.SetCustomValue(Key, DisplayModeItemKeys.RdxPeakNits, "800");
        Assert.Equal("800", store.Effective(Key, DisplayModeStore.CustomId, DisplayModeItemKeys.RdxPeakNits));
        Assert.Equal([DisplayModeItemKeys.RdxPeakNits], store.ItemsFor(Key));
        Assert.Empty(store.ItemsFor(Other)); // only this game swaps its Custom items

        store.SetInCustom(Key, DisplayModeItemKeys.RdxPeakNits, false); // unticked: normal value, value kept for later
        Assert.Null(store.Effective(Key, DisplayModeStore.CustomId, DisplayModeItemKeys.RdxPeakNits));
        Assert.Equal("800", store.CustomValue(Key, DisplayModeItemKeys.RdxPeakNits));

        store.SetActive(Other, DisplayModeStore.CustomId); // no Custom mode: refused
        Assert.Null(store.ActiveModeId(Other));
        store.SetActive(Key, DisplayModeStore.CustomId);
        Assert.Equal(DisplayModeStore.CustomId, store.ActiveModeId(Key));
        store.DeleteCustom(Key);
        Assert.Null(store.ActiveModeId(Key)); // deleting the mode it is in turns it off
        Assert.False(store.HasCustom(Key));
    }

    [Fact]
    public void Items_EverythingAModeSetsPlusCustom()
    {
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "1000"); // set in Settings: every game swaps it
        store.SetModeDefault("monitor", DisplayModeItemKeys.DriverFpsCap, "141");
        Assert.Equal([DisplayModeItemKeys.DriverFpsCap, DisplayModeItemKeys.RdxPeakNits], store.ItemsFor(Other)); // in mode order
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, null);
        Assert.Equal([DisplayModeItemKeys.DriverFpsCap], store.ItemsFor(Other));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughTheFileStore()
    {
        var store = new DisplayModeStore { Enabled = true };
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "117");
        store.CreateCustom(Key);
        store.SetInCustom(Key, DisplayModeItemKeys.Hdr, true);
        store.SetCustomValue(Key, DisplayModeItemKeys.Hdr, LinuxHdrModes.Gamescope);
        store.SetActive(Key, "tv");
        store.SetOriginal(Key, DisplayModeItemKeys.UlTargetFps, "141");

        var file = new DisplayModeFileStore(Path.Combine(_root, "display-modes.json"));
        file.Save(store.Snapshot());
        var copy = new DisplayModeStore();
        copy.Load(file.Load(out var unreadable));

        Assert.False(unreadable);
        Assert.True(copy.Enabled);
        Assert.Equal("117", copy.ModeDefault("TV", DisplayModeItemKeys.UlTargetFps)); // ids are case-insensitive
        Assert.Equal("tv", copy.ActiveModeId(Key));
        Assert.Equal("141", copy.OriginalValue(Key, DisplayModeItemKeys.UlTargetFps));
        Assert.Equal(LinuxHdrModes.Gamescope, copy.Effective(Key, DisplayModeStore.CustomId, DisplayModeItemKeys.Hdr));
    }

    [Fact]
    public void Load_NullOrEmptyGivesDefaults_UnknownModeOrCustomIsOff()
    {
        var store = new DisplayModeStore();
        store.Load(new DisplayModeData { Modes = [] });
        Assert.Equal(2, store.Modes.Count);
        store.Load(new DisplayModeData
        {
            Modes = [new DisplayModeDefinition { Id = "custom", Name = "Custom" }, new DisplayModeDefinition { Id = "tv", Name = "TV" }],
            Games = { [Key] = new GameDisplayModeState { Mode = "gone" }, [Other] = new GameDisplayModeState { Mode = "custom" } },
        });
        Assert.Equal(["TV"], store.Modes.Select(m => m.Name)); // "custom" is never a global mode
        Assert.Null(store.ActiveModeId(Key));
        Assert.Null(store.ActiveModeId(Other)); // Custom without a Custom mode
        Assert.Empty(store.Snapshot().Games);
    }

    [Fact]
    public void FileStore_UnreadableFile_IsReported_AndNotOverwritten()
    {
        var path = Path.Combine(_root, "bad", "display-modes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var file = new DisplayModeFileStore(path);
        Assert.Null(file.Load(out var unreadable));
        Assert.True(unreadable);
        Assert.ThrowsAny<IOException>(() => file.Save(new DisplayModeData()));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void MoveGame_StateFollowsTheNewKey()
    {
        var store = new DisplayModeStore();
        store.SetActive(Key, "tv");
        Assert.True(store.MoveGame(Key, Other));
        Assert.Equal("tv", store.ActiveModeId(Other));
        Assert.Null(store.ActiveModeId(Key));
    }

    // ── Item values ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DisplayModeItemKeys.RdxPeakNits, "700", "700")]
    [InlineData(DisplayModeItemKeys.RdxPeakNits, "0", null)]        // no off for brightness
    [InlineData(DisplayModeItemKeys.RdxPeakNits, "10001", null)]
    [InlineData(DisplayModeItemKeys.UlTargetFps, "0", "0")]         // 0 = off
    [InlineData(DisplayModeItemKeys.UlTargetFps, "19", null)]
    [InlineData(DisplayModeItemKeys.DcFpsLimit, "117,5", "117.5")]  // decimals, either separator
    [InlineData(DisplayModeItemKeys.RsOverlayKey, "36, 0,1,0", "36,0,1,0")]
    [InlineData(DisplayModeItemKeys.RsOverlayKey, "36,0,2,0", null)]
    [InlineData(DisplayModeItemKeys.RsOverlayKey, "Home", null)]
    public void Normalize_ChecksRanges(string key, string text, string? expected)
        => Assert.Equal(expected, DisplayModeItems.Get(key)!.Normalize(text));

    [Fact]
    public void ItemKeys_AreUnique_AndUpstreamsWhereKept()
    {
        Assert.Equal(DisplayModeItems.All.Count, DisplayModeItems.All.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Upstream's keys (RHI-2.9.4 DisplayModeItemKeys) less RtxHdrPeak / HdrDisplay (no Linux equivalent); LaunchArgs since w14/layouts-c.
        Assert.Equal(["RdxPeakNits", "RdxGameNits", "RdxUiNits", "UlTargetFps", "DcFpsLimit", "DriverFpsCap", "MfgMode", "DlssSrPreset", "DlssSrScale",
            "DlssRrPreset", "DlssRrScale", "RsPreset", "RsOverlayKey", "RsScreenshotKey", "RsGamepadNav", "UlOsdKey", "Hdr", "Resolution", "LaunchArgs"],
            DisplayModeItems.All.Select(i => i.Key));
    }

    [Fact]
    public void RenoDxKeys_BothSpellings_ReadRenoDxsOwnAndWriteAllLineByLine()
    {
        // RenoDX's own key plus an older RHI write with a capital T (ReShade's ini is case-sensitive)
        var lines = new List<string> { "[renodx-preset1]", "toneMapGameNits=203", "toneMapPeakNits=1000", "ToneMapPeakNits=1050", "; comment", "[renodx-preset2]", "Other=1" };
        Assert.Equal("1000", RenoDxNitsIni.Read(lines, RenoDxNitsIni.PeakKey)); // RenoDX's value, not the stale capital-T one
        Assert.True(RenoDxNitsIni.Write(lines, RenoDxNitsIni.PeakKey, "700"));
        Assert.Equal(["[renodx-preset1]", "toneMapGameNits=203", "toneMapPeakNits=700", "ToneMapPeakNits=700", "; comment",
            "[renodx-preset2]", "Other=1", "toneMapPeakNits=700", "ToneMapPeakNits=700"], lines); // both spellings added to a section without it
        Assert.False(RenoDxNitsIni.Write(lines, RenoDxNitsIni.PeakKey, "700"));
    }

    [Fact]
    public void RenoDxKeys_NoPresetSection_CreatesPreset1()
    {
        var lines = new List<string> { "[GENERAL]", "PresetPath=x.ini" };
        RenoDxNitsIni.Write(lines, RenoDxNitsIni.PeakKey, "800");
        Assert.Equal(["[GENERAL]", "PresetPath=x.ini", "", "[renodx-preset1]", "toneMapPeakNits=800", "ToneMapPeakNits=800"], lines);
    }

    // ── Switching (real files) ────────────────────────────────────────────────

    [Fact]
    public async Task Switch_WritesTheNewModeAndNormalValuesBack()
    {
        var f = Fixture("switch", peak: 1000, fps: 141);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "117");
        var svc = f.Service(store);

        var r = await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal(2, r.Written.Count());
        Assert.Equal("700", f.Peak());
        Assert.Equal("117", f.Fps());
        Assert.Contains("toneMapPeakNits=700", File.ReadAllText(f.ReShadeIni).Split("[renodx-preset2]")[1]); // every preset
        // Coming from Off: the starting values are its values from before modes
        Assert.Equal("1000", store.OriginalValue(f.Key, DisplayModeItemKeys.RdxPeakNits));
        Assert.Equal("141", store.OriginalValue(f.Key, DisplayModeItemKeys.UlTargetFps));
        Assert.True(store.IsManaged(f.Key));

        // Changed in game while on the TV, then back to Monitor and to TV again
        f.SetPeak(650);
        await svc.SwitchAsync(f.Ctx(), "monitor"); // Monitor has no values: the game's normal values
        Assert.Equal("1000", f.Peak());
        Assert.Equal("141", f.Fps());
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("700", f.Peak()); // the mode's value again: in-game changes are not kept
        Assert.Equal("117", f.Fps());
        Assert.Contains("; keep me", File.ReadAllText(f.ReShadeIni)); // nothing else in the file changed
    }

    [Fact]
    public async Task Switch_ModeWithoutAValue_DoesNotInheritThePreviousModesValue()
    {
        var f = Fixture("inherit", peak: 1050, fps: 141);
        var store = new DisplayModeStore();
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("1050", f.Peak()); // nothing to write yet
        await svc.SwitchAsync(f.Ctx(), "monitor");

        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "2000");
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("2000", f.Peak());
        await svc.SwitchAsync(f.Ctx(), "monitor");
        Assert.Equal("1050", f.Peak());
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("2000", f.Peak());
    }

    [Fact]
    public async Task Switch_SkipsItemsTheGameCannotUse_AndOnlyRhisComponents()
    {
        var f = Fixture("skip", peak: 1000, fps: 141, displayCommander: false);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.DcFpsLimit, "117.5");
        store.SetModeDefault("tv", DisplayModeItemKeys.DriverFpsCap, "116");
        var svc = f.Service(store);
        var r = await svc.SwitchAsync(f.Ctx(fpsLimitOffered: false), "tv");

        Assert.Empty(r.Written);
        Assert.Equal(DisplayModeUnavailable.DisplayCommanderNotManaged, r.Items.Single(i => i.Item.Key == DisplayModeItemKeys.DcFpsLimit).Unavailable);
        Assert.Equal(DisplayModeUnavailable.FpsLimitNotOffered, r.Items.Single(i => i.Item.Key == DisplayModeItemKeys.DriverFpsCap).Unavailable);
        Assert.False(File.Exists(Path.Combine(f.Bin, "DisplayCommander.ini"))); // not created
    }

    [Fact]
    public async Task Switch_ReShadeKeys_WrittenToEveryReShadeIni_PresetToTheMainOne()
    {
        var f = Fixture("keys", peak: 1000, fps: 141);
        File.WriteAllText(Path.Combine(f.Bin, "ReShade2.ini"), "[INPUT]\nKeyOverlay=36,0,0,0\n");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RsOverlayKey, "45,0,1,0");
        store.SetModeDefault("tv", DisplayModeItemKeys.RsPreset, "TV.ini");
        store.SetModeDefault("tv", DisplayModeItemKeys.RsGamepadNav, "1");
        var r = await f.Service(store).SwitchAsync(f.Ctx(), "tv");

        Assert.Equal(3, r.Written.Count());
        Assert.Contains("KeyOverlay=45,0,1,0", File.ReadAllText(f.ReShadeIni));
        Assert.Contains("KeyOverlay=45,0,1,0", File.ReadAllText(Path.Combine(f.Bin, "ReShade2.ini")));
        Assert.Contains("GamepadNavigation=1", File.ReadAllText(Path.Combine(f.Bin, "ReShade2.ini")));
        Assert.Contains("PresetPath=TV.ini", File.ReadAllText(f.ReShadeIni));
        Assert.DoesNotContain("PresetPath", File.ReadAllText(Path.Combine(f.Bin, "ReShade2.ini")));
    }

    [Fact]
    public async Task Switch_DisplayCommanderAndReLimiterOff()
    {
        var f = Fixture("dc", peak: 1000, fps: 141);
        File.WriteAllText(Path.Combine(f.Bin, "DisplayCommander.ini"), "[DisplayCommander]\nfps_limit=60\n");
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.DcFpsLimit, "117.5");
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "0");
        await f.Service(store).SwitchAsync(f.Ctx(), "tv");
        Assert.Contains("fps_limit=117.5", File.ReadAllText(Path.Combine(f.Bin, "DisplayCommander.ini")));
        Assert.DoesNotContain("target_fps", File.ReadAllText(f.ReLimiterIni)); // 0 = off: the key goes
        Assert.Equal("0", f.Fps());
    }

    // ── Off ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TurnOff_WritesGlobalValuesElseTheValuesFromBeforeModes()
    {
        var f = Fixture("off", peak: 1000, fps: 141);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "117");
        var svc = f.Service(store);
        svc.GlobalValue = (_, item) => item == DisplayModeItemKeys.UlTargetFps ? "60" : null; // only the cap has a global setting
        await svc.SwitchAsync(f.Ctx(), "tv");
        f.SetPeak(650); // changed in game

        var off = await svc.TurnOffAsync(f.Ctx());
        Assert.True(off.TurnedOff);
        Assert.Equal("1000", f.Peak()); // no global: what it had before modes
        Assert.Equal("60", f.Fps());    // the global setting
        Assert.False(store.IsManaged(f.Key));
        Assert.Null(svc.Keep(f.Ctx(), DisplayModeItemKeys.ReShadeIniItems)); // global writes reach it again

        // Already off: nothing to do
        Assert.Empty((await svc.TurnOffAsync(f.Ctx())).Items);
    }

    // ── Keep (installs, "write to all games") ─────────────────────────────────

    [Fact]
    public async Task Keep_PutsModeValuesBackAfterAGlobalWrite()
    {
        var f = Fixture("keep", peak: 1000, fps: 141);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        var svc = f.Service(store);
        Assert.Null(svc.Keep(f.Ctx(), DisplayModeItemKeys.ReShadeIniItems)); // not in a mode: global writes go ahead
        await svc.SwitchAsync(f.Ctx(), "tv");

        var keep = svc.Keep(f.Ctx(), DisplayModeItemKeys.ReShadeIniItems);
        Assert.NotNull(keep);
        f.SetPeak(400); // e.g. "Write peak brightness to all games"
        Assert.Equal(1, await keep!(CancellationToken.None));
        Assert.Equal("700", f.Peak());

        // Items it does not swap are not kept
        Assert.Null(svc.Keep(f.Ctx(), DisplayModeItemKeys.ReLimiterItems));
    }

    [Fact]
    public async Task Keep_ReLimiterInstalledLater_GetsTheModesValueNotZero()
    {
        var f = Fixture("later", peak: 1000, fps: null);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.UlTargetFps, "118");
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), "tv");

        // ReLimiter installed while in TV: the install writes the global cap into a fresh relimiter.ini
        var keep = svc.Keep(f.Ctx(), DisplayModeItemKeys.ReLimiterItems);
        Assert.NotNull(keep);
        File.WriteAllText(f.ReLimiterIni, "[FrameLimiter]\ntarget_fps=323\n");
        await keep!(CancellationToken.None);
        Assert.Equal("118", f.Fps());
    }

    // ── Custom ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_CustomMode_ChangesOnlyWhatItTicks()
    {
        var f = Fixture("custom", peak: 1000, fps: 141);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        store.CreateCustom(f.Key);
        store.SetInCustom(f.Key, DisplayModeItemKeys.UlTargetFps, true);
        store.SetCustomValue(f.Key, DisplayModeItemKeys.UlTargetFps, "90");
        var svc = f.Service(store);
        await svc.SwitchAsync(f.Ctx(), "tv");
        Assert.Equal("141", f.Fps()); // TV does not set the cap: normal value
        await svc.SwitchAsync(f.Ctx(), DisplayModeStore.CustomId);
        Assert.Equal("90", f.Fps());
        Assert.Equal("1000", f.Peak()); // Custom does not tick peak: normal value, not TV's

        // An edit while in Custom is written at once
        Assert.Null(await svc.ApplyNowAsync(f.Ctx(), DisplayModeItems.Get(DisplayModeItemKeys.UlTargetFps)!, "75"));
        Assert.Equal("75", f.Fps());
    }

    // ── Launch settings (Linux mapping) ───────────────────────────────────────

    [Fact]
    public async Task LaunchSettings_HdrResolutionFpsAndNvidia_SwapAndComeBackOnOff()
    {
        var f = Fixture("launch", peak: 1000, fps: 141);
        var state = new LinuxGameLibraryState();
        state.HdrProfiles[f.Key] = new LinuxHdrGameProfile { EnableLegacyWsi = true, Resolution = "2560x1440" };
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.Hdr, LinuxHdrModes.Gamescope);
        store.SetModeDefault("tv", DisplayModeItemKeys.Resolution, "3840x2160");
        store.SetModeDefault("tv", DisplayModeItemKeys.DriverFpsCap, "116");
        store.SetModeDefault("tv", DisplayModeItemKeys.MfgMode, "fixed:1");
        store.SetModeDefault("tv", DisplayModeItemKeys.DlssSrScale, "50");
        store.SetModeDefault("tv", DisplayModeItemKeys.DlssSrPreset, "11");
        var svc = f.Service(store);

        var tv = f.Ctx(launch: DisplayModeLaunchValues.From(state, f.Key));
        await svc.SwitchAsync(tv, "tv");
        Assert.True(tv.Launch!.Changed);
        tv.Launch.ApplyTo(state, f.Key);
        var profile = state.HdrProfiles[f.Key];
        Assert.Equal(LinuxHdrModes.Gamescope, profile.Mode);
        Assert.True(profile.ResolutionToggle);
        Assert.Equal("3840x2160", profile.Resolution);
        Assert.True(profile.EnableLegacyWsi); // not a mode item: left as it was
        Assert.Equal(116u, state.FpsLimits![f.Key]);
        Assert.Equal(NvidiaFrameGenerationModes.Fixed, profile.Nvidia!.FrameGenerationMode);
        Assert.Equal(1u, profile.Nvidia.FrameCount);
        Assert.Equal(50u, profile.Nvidia.SuperResolutionRenderScale);
        Assert.Equal(11u, profile.Nvidia.SuperResolutionPreset);

        var off = f.Ctx(launch: DisplayModeLaunchValues.From(state, f.Key));
        await svc.TurnOffAsync(off);
        off.Launch!.ApplyTo(state, f.Key);
        profile = state.HdrProfiles[f.Key];
        Assert.Equal(LinuxHdrModes.Off, profile.Mode);
        Assert.False(profile.ResolutionToggle);
        Assert.Equal("2560x1440", profile.Resolution); // what it had before modes
        Assert.Null(profile.Nvidia);
        Assert.Null(state.FpsLimits); // Global again
    }

    [Fact]
    public async Task LaunchSettings_FpsLimitGlobalIsTheNormalValue()
    {
        var f = Fixture("fps", peak: 1000, fps: 141);
        var state = new LinuxGameLibraryState { FpsLimits = new(StringComparer.OrdinalIgnoreCase) { [f.Key] = 90 } };
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.DriverFpsCap, "0");
        var svc = f.Service(store);
        svc.GlobalValue = (_, item) => item == DisplayModeItemKeys.DriverFpsCap ? DisplayModeItems.FpsGlobal : null; // upstream: "global"
        var ctx = f.Ctx(launch: DisplayModeLaunchValues.From(state, f.Key));
        await svc.SwitchAsync(ctx, "tv");
        Assert.Equal(0u, ctx.Launch!.FpsLimit); // Off for this game
        await svc.TurnOffAsync(ctx);
        Assert.Null(ctx.Launch.FpsLimit); // back to Global (the global setting wins over its own 90, as upstream)
    }

    [Theory]
    [InlineData(DisplayModeItemKeys.Hdr, "HDR10")]
    [InlineData(DisplayModeItemKeys.Resolution, "huge")]
    [InlineData(DisplayModeItemKeys.Resolution, "Off:huge")]
    [InlineData(DisplayModeItemKeys.DriverFpsCap, "10")]
    [InlineData(DisplayModeItemKeys.MfgMode, "fixed:9")]
    [InlineData(DisplayModeItemKeys.DlssSrScale, "20")]
    public void LaunchSettings_RefuseValuesTheLaunchCannotTake(string item, string value)
    {
        var v = new DisplayModeLaunchValues();
        Assert.NotNull(DisplayModeLaunchSettings.Write(v, item, value));
        Assert.False(v.Changed);
    }

    // ── Journal ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_IsJournaledOnTheComponentsRecord_AndACrashBeforeTheSavePutsTheFileBack()
    {
        var f = Fixture("crash", peak: 1000, fps: 141);
        var (journal, _) = SimulatedDeath.NewJournal(Path.Combine(_root, "crash-journal"), f.Records);
        var before = File.ReadAllBytes(f.ReShadeIni);
        var store = new DisplayModeStore();
        store.SetModeDefault("tv", DisplayModeItemKeys.RdxPeakNits, "700");
        var svc = new DisplayModeService(store, new DisplayModeIo(new DisplayModeGameFiles(f.Records, journal, _ => null)));

        using (var death = new SimulatedDeath(journal))
        {
            f.Records.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => svc.SwitchAsync(f.Ctx(), "tv"));
            Assert.True(died);
            Assert.Equal("700", f.Peak());
            var report = await SimulatedDeath.RecoverAsync(Path.Combine(_root, "crash-journal"), Path.Combine(_root, "crash-records.json"));
            Assert.Equal(1, report!.RolledBack);
        }
        Assert.Equal(before, File.ReadAllBytes(f.ReShadeIni));
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private sealed class GameFixture(DetectedGame game, LinuxDeploymentRecordStore records)
    {
        public DetectedGame Game => game;
        public LinuxDeploymentRecordStore Records => records;
        public string Bin => game.BinaryDirectory!;
        public string Key => LinuxGameLibraryStore.GameKey(game);
        public string ReShadeIni => Path.Combine(Bin, "ReShade.ini");
        public string ReLimiterIni => Path.Combine(Bin, "relimiter.ini");

        public DisplayModeService Service(DisplayModeStore store)
            => new(store, new DisplayModeIo(new DisplayModeGameFiles(records, null, _ => null)));

        public DisplayModeContext Ctx(bool fpsLimitOffered = true, DisplayModeLaunchValues? launch = null) => new()
        {
            Game = game, Key = Key, NvidiaOffered = true, FpsLimitOffered = fpsLimitOffered, HdrOffered = true, ResolutionOffered = true,
            Launch = launch ?? new DisplayModeLaunchValues(),
        };

        public string? Peak() => RenoDxNitsIni.Read(IniLines.Read(ReShadeIni), RenoDxNitsIni.PeakKey);

        public void SetPeak(int nits)
        {
            var lines = IniLines.Read(ReShadeIni);
            RenoDxNitsIni.Write(lines, RenoDxNitsIni.PeakKey, nits.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllLines(ReShadeIni, lines);
        }

        public string? Fps() => File.Exists(ReLimiterIni)
            ? IniLines.Get(IniLines.Read(ReLimiterIni), "FrameLimiter", "target_fps") ?? "0"
            : null;
    }

    private GameFixture Fixture(string name, int peak, int? fps, bool displayCommander = true)
    {
        var bin = Path.Combine(_root, name, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame { Name = "DM " + name, InstallPath = Path.Combine(_root, name), BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Test" };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name + "-records.json"));
        var kinds = new List<ManagedComponentKind> { ManagedComponentKind.ReShade, ManagedComponentKind.RenoDX, ManagedComponentKind.ReLimiter };
        if (displayCommander) kinds.Add(ManagedComponentKind.DisplayCommander);
        foreach (var kind in kinds)
        {
            var file = Path.Combine(bin, kind + ".bin");
            File.WriteAllText(file, kind.ToString());
            records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
                BinaryDirectory = bin, Version = "1", Files = [new LinuxDeployedFile { Path = file }]
            });
        }
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"),
            $"; keep me\n[GENERAL]\nPresetPath=Default.ini\n[INPUT]\nKeyOverlay=36,0,0,0\n[renodx-preset1]\ntoneMapPeakNits={peak}\n[renodx-preset2]\ntoneMapPeakNits={peak}\n");
        if (fps is { } cap) File.WriteAllText(Path.Combine(bin, "relimiter.ini"), $"[FrameLimiter]\ntarget_fps={cap}\nosd_toggle_key=F10\n");
        return new GameFixture(game, records);
    }
}
