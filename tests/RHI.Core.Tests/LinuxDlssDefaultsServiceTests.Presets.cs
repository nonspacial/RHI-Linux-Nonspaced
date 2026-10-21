using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #215 below the view model: the three default presets in settings (round trip, Normalize), the one row rule Quick
/// Apply and the DLSS batch copy presets by (the NVIDIA settings… dialog's rows plus upstream's DLSS 1.x guard), the copy
/// itself (a chosen value replaces the game's own, Default copies nothing, every other setting stays), and the DLSS 1.x
/// guard of the version swap read from the DLL's version resource.
/// </summary>
public sealed partial class LinuxDlssDefaultsServiceTests
{
    private static readonly LinuxHdrHostSnapshot Rtx40 = new(true, "KDE", false, "610.57.04", false) { Gpu = new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 4090") };
    private static readonly LinuxHdrHostSnapshot Rtx30 = new(true, "KDE", false, "610.57.04", false) { Gpu = new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 3080") };

    // ---- settings ----------------------------------------------------------------------------------------------------

    [Fact]
    public void PresetDefaults_RoundTripThroughTheSettingsFile_AndAValueTheDriverWouldNotTakeBecomesDefault()
    {
        var root = Path.Combine(_root, "settings");
        var store = new LinuxSettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "cache"), Path.Combine(root, "data"));
        store.Save(new LinuxAppSettings { DefaultSrPreset = 11, DefaultRrPreset = DlssPresetCatalogue.Recommended, DefaultFgPreset = 2 });

        var loaded = store.Load();
        Assert.Equal((11u, 0x00FFFFFFu, 2u), (loaded.DefaultSrPreset, loaded.DefaultRrPreset, loaded.DefaultFgPreset));

        // A hand-edited file: values past the driver's range read as Default; one the lists do not name yet is kept (a
        // later manifest may add it; the dialog shows it by its number).
        File.WriteAllText(store.SettingsPath, """{ "DefaultSrPreset": 4294967295, "DefaultRrPreset": 16777216, "DefaultFgPreset": 7 }""");
        loaded = store.Load();
        Assert.Equal((0u, 0u, 7u), (loaded.DefaultSrPreset, loaded.DefaultRrPreset, loaded.DefaultFgPreset));
        Assert.Equal((0u, 0u, 0u), (new LinuxAppSettings().DefaultSrPreset, new LinuxAppSettings().DefaultRrPreset, new LinuxAppSettings().DefaultFgPreset));
    }

    [Fact]
    public void ADefaultDefault_CopiesNothing()
    {
        Assert.False(NvidiaPresetChoice.FromDefaults(0, 0, 0).HasAny);
        Assert.False(NvidiaPresetChoice.FromDefaults(0xFFFFFFFF, 0, 0).HasAny); // out of range = Default
        Assert.Equal(new NvidiaPresetChoice(11, null, 1), NvidiaPresetChoice.FromDefaults(11, 0, 1));

        var own = new NvidiaGameSettings { SuperResolutionPreset = 12, SuperResolutionRenderScale = 72 };
        var copy = NvidiaPresetCopy.Apply(own, NvidiaPresetChoice.FromDefaults(0, 0, 0), AllShown);
        Assert.Empty(copy.Set);
        Assert.Empty(copy.Skipped);
        Assert.Equal((12u, 72u), (copy.Settings!.SuperResolutionPreset, copy.Settings.SuperResolutionRenderScale));
    }

    // ---- the copy ----------------------------------------------------------------------------------------------------

    private static readonly NvidiaPresetRows AllShown = new(NvidiaPresetRowState.Shown, NvidiaPresetRowState.Shown, NvidiaPresetRowState.Shown);

    [Fact]
    public void ACopiedPreset_ReplacesTheGamesOwn_AndLeavesEveryOtherSettingAsItWas()
    {
        var own = new NvidiaGameSettings { SuperResolutionPreset = 12, RayReconstructionPreset = 5, SuperResolutionRenderScale = 72, NewestDlss = true };

        var copy = NvidiaPresetCopy.Apply(own, new NvidiaPresetChoice(11, null, 2), AllShown);

        Assert.Equal([NvidiaPresetSlot.SuperResolution, NvidiaPresetSlot.FrameGeneration], copy.Set);
        var s = copy.Settings!;
        Assert.Equal((11u, 5u, 2u), (s.SuperResolutionPreset, s.RayReconstructionPreset, s.FrameGenerationPreset)); // upstream: set unconditionally
        Assert.Equal((72u, true), (s.SuperResolutionRenderScale, s.NewestDlss));
        Assert.Equal(12u, own.SuperResolutionPreset); // the caller's copy is not changed
    }

    [Fact]
    public void ARowTheGameDoesNotShow_IsSkipped_WithItsReason()
    {
        var rows = new NvidiaPresetRows(NvidiaPresetRowState.Dlss1x, NvidiaPresetRowState.NotShown, NvidiaPresetRowState.Shown);

        var copy = NvidiaPresetCopy.Apply(null, new NvidiaPresetChoice(11, 4, 1), rows);

        Assert.Equal([NvidiaPresetSlot.FrameGeneration], copy.Set);
        Assert.Equal([(NvidiaPresetSlot.SuperResolution, NvidiaPresetRowState.Dlss1x), (NvidiaPresetSlot.RayReconstruction, NvidiaPresetRowState.NotShown)], copy.Skipped);
        Assert.Equal((0u, 0u, 1u), (copy.Settings!.SuperResolutionPreset, copy.Settings.RayReconstructionPreset, copy.Settings.FrameGenerationPreset));
    }

    [Fact]
    public void TheBatchsDefaultEntry_SetsAShownRowBackToDefault()
    {
        var copy = NvidiaPresetCopy.Apply(new NvidiaGameSettings { SuperResolutionPreset = 12 }, new NvidiaPresetChoice(DlssPresetCatalogue.Default, null, null), AllShown);

        Assert.Equal([NvidiaPresetSlot.SuperResolution], copy.Set);
        Assert.Null(copy.Settings); // nothing chosen any more: the launch profile drops the member
    }

    // ---- the row rule ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRows_AreTheDialogsRows_ForAnyGame()
    {
        var game = PresetGame("rows-dialog", sr: "", fg: "");
        var detection = Detection(game);

        Assert.Equal(new NvidiaPresetRows(NvidiaPresetRowState.Shown, NvidiaPresetRowState.NotShown, NvidiaPresetRowState.Shown),
            LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, detection, Rtx40)); // no nvngx_dlssd.dll: no RR row
        Assert.Equal(NvidiaPresetRowState.NotShown, LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, detection, Rtx30).FrameGeneration); // FG preset: RTX 40+
        Assert.Equal(NvidiaPresetRows.None, LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, detection, Rtx40 with { Gpu = NvidiaGpuFacts.None }));
        // The game's files not known yet (its analysis still runs): the dialog offers no row, so nothing is copied.
        Assert.Equal(NvidiaPresetRows.None, NvidiaLaunchSettings.Plan(game, null, Rtx40, families: null).PresetRows);

        var noFg = PresetGame("rows-no-fg", sr: "");
        Assert.Equal(NvidiaPresetRowState.NotShown, LinuxDlssDefaultsService.NvidiaPresetRowsFor(noFg, Detection(noFg), Rtx40).FrameGeneration);

        var native = PresetGame("rows-native", sr: "", fg: "");
        native.Runner = "Native Linux";
        Assert.Equal(NvidiaPresetRows.None, LinuxDlssDefaultsService.NvidiaPresetRowsFor(native, Detection(native), Rtx40));
    }

    [Fact]
    public void ADlss1xFile_TakesNoPreset_EvenWhereTheRowShows()
    {
        var game = PresetGame("rows-1x", sr: "1.0.13.0", rr: "1.0.0.0", fg: "");
        var rows = LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, Detection(game), Rtx40);
        Assert.Equal(new NvidiaPresetRows(NvidiaPresetRowState.Dlss1x, NvidiaPresetRowState.Dlss1x, NvidiaPresetRowState.Shown), rows);

        var modern = PresetGame("rows-310", sr: "310.4.0.0", rr: "310.4.0.0");
        Assert.Equal(new NvidiaPresetRows(NvidiaPresetRowState.Shown, NvidiaPresetRowState.Shown, NvidiaPresetRowState.NotShown),
            LinuxDlssDefaultsService.NvidiaPresetRowsFor(modern, Detection(modern), Rtx40));
    }

    [Fact]
    public void NoLaunchFallback_ADefaultPresetSetsNoVariable_WhateverTheGlobalDefaults()
    {
        // The plan never reads the global defaults: a game whose own presets are Default launches without a preset
        // variable even while Settings holds default presets (option 2 rejected by the owner).
        var settings = new LinuxAppSettings { DefaultSrPreset = 11, DefaultRrPreset = 5, DefaultFgPreset = 2 }.Normalize();
        var game = PresetGame("no-fallback", sr: "", rr: "", fg: "");
        var plan = NvidiaLaunchSettings.Plan(game, new NvidiaGameSettings { SuperResolutionRenderScale = 72 }, Rtx40, LinuxDlssDefaultsService.Families(Detection(game)));

        Assert.True(plan.Shown);
        Assert.Equal(11u, settings.DefaultSrPreset);
        Assert.DoesNotContain(plan.Environment.Keys, key => key.Contains("RENDER_PRESET", StringComparison.Ordinal));
        Assert.Contains("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_SCALING_RATIO", plan.Environment.Keys);
    }

    // ---- the version swap's DLSS 1.x guard ---------------------------------------------------------------------------

    [Fact]
    public async Task QuickApply_NeverSwapsADlss1xSuperResolutionFile()
    {
        var game = Game("quick-sr-1x");
        var target = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        var original = PeVersionReaderTests.BuildVersionedPe("1.0.13.0", "1.0.13.0", size: 70_000);
        await File.WriteAllBytesAsync(target, original);
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));

        var result = await service.ApplyDefaultsAsync(game, new LinuxDlssVersionDefaults(SuperResolution: "9.9.9"), [target]);

        var item = Assert.Single(result.Items);
        Assert.True(item.Skipped);
        Assert.Contains("1.x", item.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".original"));
    }

    // w10 review 6: one DLSS 1.x rule for auto-follow, Apply defaults, the batch and the preset rows — the version's first
    // number is 1, however the version is spelt; a version that only starts with "1" (10.x, 100.x) is not 1.x.
    [Theory]
    [InlineData("1.0.13.0", true)]
    [InlineData("1,0,13,0", true)]
    [InlineData("1", true)]
    [InlineData("1.7", true)]
    [InlineData("10.0.0.0", false)]
    [InlineData("100.1", false)]
    [InlineData("310.1.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheDlss1xRule_IsOneRule_ReadFromTheVersionsFirstNumber(string? version, bool expected)
    {
        Assert.Equal(expected, DlssAutoFollowService.IsDlss1x(version));
        var game = Game("rule-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        File.WriteAllBytes(file, string.IsNullOrEmpty(version) ? [0x4D, 0x5A] : PeVersionReaderTests.BuildVersionedPe(version, version, size: 8192));
        Assert.Equal(expected, DlssAutoFollowService.IsDlss1xFile(file));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>A Proton game started by its own command (the NVIDIA settings apply); a null version = no such file, "" = a bare stub.</summary>
    private DetectedGame PresetGame(string name, string? sr = null, string? rr = null, string? fg = null)
    {
        var game = Game(name);
        var exe = Path.Combine(game.InstallPath, "Quest.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        void Write(string file, string? version)
        {
            if (version is null) return;
            File.WriteAllBytes(Path.Combine(game.InstallPath, file), version.Length == 0 ? [0x4D, 0x5A] : PeVersionReaderTests.BuildVersionedPe(version, version, size: 8192));
        }
        Write("nvngx_dlss.dll", sr);
        Write("nvngx_dlssd.dll", rr);
        Write("nvngx_dlssg.dll", fg);
        game.Source = "Manual";
        game.ExecutablePath = exe;
        game.LaunchExecutablePath = exe;
        game.Runner = "Proton";
        game.CompatibilityTool = "GE-Proton10-25";
        game.CompatibilityLayerType = "proton";
        game.LaunchCommand = "umu-run Quest.exe";
        return game;
    }

    private DlssDetectionResult Detection(DetectedGame game)
        => NewService(new GateHandler(Manifest(), PayloadZip())).DetectTargets(game, Directory.GetFiles(game.InstallPath));
}
