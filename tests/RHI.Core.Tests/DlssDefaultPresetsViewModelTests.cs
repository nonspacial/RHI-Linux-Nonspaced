using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #215 through the real view model (owner decision: upstream's way): Settings → DLSS/Streamline holds three default
/// presets; Apply defaults (Quick Apply) and the DLSS batch copy them into a game's own NVIDIA settings, only where its
/// NVIDIA settings… dialog shows the row; Default copies nothing; Restore all clears them; nothing applies at launch from
/// the defaults. Launches go to a recording launcher; nothing is started.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DlssDefaultPresetsViewModelTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private static readonly NvidiaGpuFacts Rtx40 = new(true, false, "NVIDIA GeForce RTX 4090");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-dp-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Settings → DLSS/Streamline ---------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_ShowsThePresetsFromTheLists_SavesThem_AndOtherSavesKeepThem()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Settings Quest", ["nvngx_dlss.dll"])], Rtx40);
        var dialog = Dialog(fx);

        Assert.Equal(["DLSS preset", "Ray Reconstruction preset", "Frame Generation preset"],
            new[] { dialog.DefaultSrPreset, dialog.DefaultRrPreset, dialog.DefaultFgPreset }.Select(c => c.Label));
        Assert.Equal("Default", dialog.DefaultSrPreset.Items[0].Display); // Default first, as the NVIDIA settings
        Assert.Equal(DlssPresetCatalogue.Current.SuperResolution.Select(p => p.Value), dialog.DefaultSrPreset.Items.Select(i => i.Value!.Value));
        Assert.Equal(0u, dialog.DefaultSrPreset.Value);
        Assert.Contains("Apply defaults and Batch DLSS copy these presets", dialog.DefaultSrPreset.Tip, StringComparison.Ordinal);

        dialog.DefaultSrPreset.Selected = dialog.DefaultSrPreset.Items.For(11u);
        dialog.DefaultRrPreset.Selected = dialog.DefaultRrPreset.Items.For(DlssPresetCatalogue.Recommended);
        dialog.DefaultFgPreset.Selected = dialog.DefaultFgPreset.Items.For(1u);
        dialog.DefaultFgPreset.Selected = null; // the dropdown's items reset: not a choice
        dialog.SaveCommand.Execute(null);
        AssertSaved(fx, 11, DlssPresetCatalogue.Recommended, 1);
        Assert.Equal("Defaults · SR — · RR — · FG — · NR — · SL —\nDefault presets · DLSS K - TF1 · RR NVIDIA Recommended · FG A", fx.Vm.Dlss.DlssDefaultsSummary);

        // Opened again: filled from the settings. A save from another tab keeps them, and so does a save that
        // rebuilds the settings from the snapshot (the OptiScaler key's Apply to all).
        var again = Dialog(fx);
        Assert.Equal((11u, DlssPresetCatalogue.Recommended, 1u), (again.DefaultSrPreset.Value!.Value, again.DefaultRrPreset.Value!.Value, again.DefaultFgPreset.Value!.Value));
        again.ManageIni = !again.ManageIni;
        again.SaveCommand.Execute(null);
        AssertSaved(fx, 11, DlssPresetCatalogue.Recommended, 1);
        Assert.Null(fx.Vm.Settings.SaveOptiScalerMenuKey("auto"));
        AssertSaved(fx, 11, DlssPresetCatalogue.Recommended, 1);

        again.ResetCommand.Execute(null);
        Assert.Equal((0u, 0u, 0u), (again.DefaultSrPreset.Value!.Value, again.DefaultRrPreset.Value!.Value, again.DefaultFgPreset.Value!.Value));
        AssertSaved(fx, 0, 0, 0);
        Assert.DoesNotContain("Default presets", fx.Vm.Dlss.DlssDefaultsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_KeepsASavedPresetTheListsDoNotName()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Odd Quest", ["nvngx_dlss.dll"])], Rtx40);
        SetDefaults(fx, sr: 99);

        var dialog = Dialog(fx);
        Assert.Equal(99u, dialog.DefaultSrPreset.Value);
        Assert.Equal("99", dialog.DefaultSrPreset.Selected!.Display);
        dialog.SaveCommand.Execute(null);
        AssertSaved(fx, 99, 0, 0);
    }

    // ---- Quick Apply (upstream's Apply defaults) --------------------------------------------------------------------

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task QuickApply_SetsEachShownRow_SkipsTheRest_AndSaysWhich(string language)
    {
        using var fx = await StartAsync([Game("Copy Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"])], Rtx40, language: language);
        SetDefaults(fx, sr: 11, rr: 5, fg: 1);
        UiLocalizationService.Apply(language); // a Settings save adopts the saved language
        Assert.False(fx.Vm.Dlss.HasDlssVersionDefaults);
        Assert.True(fx.Vm.Dlss.CanQuickApplyDlssDefaults); // presets alone are enough (upstream hasDefaults)

        await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        var saved = SavedNvidia(fx, fx.Vm.SelectedGame!)!;
        Assert.Equal((11u, 0u, 1u), (saved.SuperResolutionPreset, saved.RayReconstructionPreset, saved.FrameGenerationPreset)); // no nvngx_dlssd.dll: no RR row
        var nv = fx.Vm.Dlss.Nvidia; // the NVIDIA settings… dialog shows what was copied
        Assert.Equal((11u, 1u), (nv.SelectedSuperResolutionPreset.Value, nv.SelectedFrameGenerationPreset.Value));
        // The shell may append its own notes (an offline manifest) after the line.
        Assert.StartsWith(language == "pt-BR"
                ? "Configurações NVIDIA de Copy Quest — definido: Preset do DLSS: K - TF1, Preset do Frame Generation: A. Não definido, porque as configurações NVIDIA deste jogo não oferecem: Preset do Ray Reconstruction."
                : "NVIDIA settings of Copy Quest — set: DLSS preset: K - TF1, Frame Generation preset: A. Not set, as this game's NVIDIA settings do not offer it: Ray Reconstruction preset.",
            fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickApply_OnAnRtx30_AndOverADlss1xFile_SetsNothing()
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Old Quest", ["nvngx_dlssg.dll"]);
        File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), PeVersionReaderTests.BuildVersionedPe("1.0.13.0", "1.0.13.0", size: 8192));
        using var fx = await StartAsync([game], new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 3080"));
        SetDefaults(fx, sr: 11, fg: 1);

        await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.Null(SavedNvidia(fx, fx.Vm.SelectedGame!));
        Assert.StartsWith("Not set, as this game's NVIDIA settings do not offer it: Frame Generation preset. Not set over a DLSS 1.x file: DLSS preset.", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickApply_ReplacesTheGamesOwnPreset_AndRestoreAllPutsDefaultBack()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Own Quest", ["nvngx_dlss.dll"])], Rtx40);
        var nv = fx.Vm.Dlss.Nvidia;
        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(12u);
        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(67u);
        SetDefaults(fx, sr: 11);

        await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        var saved = SavedNvidia(fx, fx.Vm.SelectedGame!)!;
        Assert.Equal((11u, 67u), (saved.SuperResolutionPreset, saved.SuperResolutionRenderScale)); // upstream sets it unconditionally
        Assert.Equal(11u, nv.SelectedSuperResolutionPreset.Value);
        Assert.True(fx.Vm.Dlss.CanRestoreAllDlss);

        await fx.Vm.Dlss.RestoreAllDlssCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.Null(SavedNvidia(fx, fx.Vm.SelectedGame!));
        Assert.Equal(DlssPresetCatalogue.Default, nv.SelectedSuperResolutionPreset.Value);
        Assert.StartsWith("NVIDIA settings set back to Default: Own Quest.", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickApply_IsNotOffered_WhileEveryDefaultIsDefault_AndADefaultDefaultWritesNothing()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Plain Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"])], Rtx40);
        Assert.False(fx.Vm.Dlss.CanQuickApplyDlssDefaults);

        SetDefaults(fx, fg: 2);
        Assert.True(fx.Vm.Dlss.CanQuickApplyDlssDefaults);
        await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        var saved = SavedNvidia(fx, fx.Vm.SelectedGame!)!;
        Assert.Equal((0u, 2u), (saved.SuperResolutionPreset, saved.FrameGenerationPreset)); // the Default SR default left the game's own choice
    }

    [Fact]
    public async Task QuickApply_WhileRepairAfterMoveRewritesTheLibrary_CopiesNothing_AndSaysWhy()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Repair Quest", ["nvngx_dlss.dll"])], Rtx40);
        SetDefaults(fx, sr: 11);
        fx.Vm.LibraryRepairRunning = true;
        try
        {
            await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

            Assert.Null(SavedNvidia(fx, fx.Vm.SelectedGame!));
            Assert.Equal(DlssPresetCatalogue.Default, fx.Vm.Dlss.Nvidia.SelectedSuperResolutionPreset.Value);
            Assert.StartsWith(UiLocalizationService.Text("Vm.WaitForRepairAfterMove"), fx.Vm.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("set:", fx.Vm.StatusText, StringComparison.Ordinal);

            var batch = await fx.Vm.Dlss.ApplyDlssBatchAsync([fx.Vm.SelectedGame!], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, null, null)).WaitAsync(HangGuard);
            Assert.Null(SavedNvidia(fx, fx.Vm.SelectedGame!));
            Assert.StartsWith(UiLocalizationService.Text("Vm.WaitForRepairAfterMove"), batch.Line, StringComparison.Ordinal);
        }
        finally { fx.Vm.LibraryRepairRunning = false; }
    }

    [Fact]
    public async Task NoLaunchFallback_AGameWhosePresetIsDefault_LaunchesWithoutTheGlobalOne()
    {
        UiLocalizationService.Apply("en-US");
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync([Game("Launch Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"])], Rtx40, launcher);
        SetDefaults(fx, sr: 11, fg: 1);
        var nv = fx.Vm.Dlss.Nvidia;
        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(67u); // something else chosen, so variables are passed

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var environment = Assert.Single(launcher.Launches).Options!.Environment!;
        Assert.Contains("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_SCALING_RATIO", environment.Keys);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("RENDER_PRESET", StringComparison.Ordinal));
    }

    // ---- the DLSS batch ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBatch_CopiesThePresetsIntoEveryTickedGame_AndRestoreSelectedClearsThem()
    {
        UiLocalizationService.Apply("en-US");
        var both = Game("Alpha Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"]);
        var srOnly = Game("Beta Quest", ["nvngx_dlss.dll"]);
        using var fx = await StartAsync([both, srOnly], Rtx40, select: "Alpha Quest");
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game; // not the selected game
        Assert.Equal(1, fx.Vm.Runtime.WriteNvidiaSettings([beta], (_, _) => new NvidiaGameSettings { SuperResolutionRenderScale = 72, NewestDlss = true }).Changed);

        var applied = await fx.Vm.Dlss.ApplyDlssBatchAsync([alpha, beta], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, 5, 1)).WaitAsync(HangGuard);

        Assert.Equal((11u, 0u, 1u), Presets(SavedNvidia(fx, alpha)!));
        var betaSaved = SavedNvidia(fx, beta)!;
        Assert.Equal((11u, 0u, 0u), Presets(betaSaved)); // no RR or FG file
        Assert.Equal((72u, true), (betaSaved.SuperResolutionRenderScale, betaSaved.NewestDlss)); // its other settings stay
        Assert.Equal(UiLocalizationService.Format("Dp.BatchPresets", 2, 2), applied.Line);
        Assert.Equal(11u, fx.Vm.Dlss.Nvidia.SelectedSuperResolutionPreset.Value); // the selected game's dialog follows
        Assert.Empty(applied.Result.Items); // no version chosen: no file touched

        // Upstream's mass restore: presets back to Default and the newest-DLSS override off; render scale stays.
        var restored = await fx.Vm.Dlss.RestoreDlssBatchAsync([alpha, beta]).WaitAsync(HangGuard);

        Assert.Null(SavedNvidia(fx, alpha));
        betaSaved = SavedNvidia(fx, beta)!;
        Assert.Equal((0u, 0u, 0u), Presets(betaSaved));
        Assert.Equal((72u, false), (betaSaved.SuperResolutionRenderScale, betaSaved.NewestDlss));
        Assert.EndsWith(UiLocalizationService.Format("Dp.BatchPresetsReset", 2), restored.Line, StringComparison.Ordinal);
        Assert.Equal(DlssPresetCatalogue.Default, fx.Vm.Dlss.Nvidia.SelectedSuperResolutionPreset.Value);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task TheBatchDialog_StartsItsPresetsFromSettings_AndHandsThemToTheBatch(string language)
    {
        using var fx = await StartAsync([Game("Gamma Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"])], Rtx40, language: language);
        SetDefaults(fx, sr: 11, fg: 2);
        UiLocalizationService.Apply(language); // a Settings save adopts the saved language
        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        var dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));

        Assert.Equal(UiLocalizationService.Text("Win.DoNotChange"), dialog.RayReconstructionPreset.Items[0].Display);
        Assert.Equal((11u, (uint?)null, 2u), (dialog.SuperResolutionPreset.Value!.Value, dialog.RayReconstructionPreset.Value, dialog.FrameGenerationPreset.Value!.Value));
        Assert.Equal(UiLocalizationService.Text("Nv.SrPreset"), dialog.SuperResolutionPreset.Label);

        dialog.Games.Single().IsChecked = true;
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(HangGuard); // no version chosen: the presets alone run

        Assert.Equal((11u, 0u, 2u), Presets(SavedNvidia(fx, fx.Vm.SelectedGame!)!));
        Assert.Equal(UiLocalizationService.Format("Dp.BatchPresets", 1, 0), dialog.Status);
        if (language == "pt-BR") Assert.StartsWith("Presets NVIDIA: definidos em 1 jogo(s)", dialog.Status, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static (uint, uint, uint) Presets(NvidiaGameSettings s) => (s.SuperResolutionPreset, s.RayReconstructionPreset, s.FrameGenerationPreset);

    private static SettingsDialogViewModel Dialog(ViewModelFixture fx) => new(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));

    private static void SetDefaults(ViewModelFixture fx, uint sr = 0, uint rr = 0, uint fg = 0)
    {
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.DefaultSrPreset = sr;
        settings.DefaultRrPreset = rr;
        settings.DefaultFgPreset = fg;
        fx.Vm.Settings.SaveLinuxSettings(settings);
        fx.Vm.Dlss.NotifyDlssDefaults();
    }

    private static void AssertSaved(ViewModelFixture fx, uint sr, uint rr, uint fg)
    {
        var saved = new LinuxSettingsStore(fx.Vm.Settings.GetLinuxSettingsSnapshot().SettingsPath).Load();
        Assert.Equal((sr, rr, fg), (saved.DefaultSrPreset, saved.DefaultRrPreset, saved.DefaultFgPreset));
        var live = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.Equal((sr, rr, fg), (live.DefaultSrPreset, live.DefaultRrPreset, live.DefaultFgPreset));
    }

    private static NvidiaGameSettings? SavedNvidia(ViewModelFixture fx, DetectedGame game)
        => fx.Library.Load().HdrProfiles.TryGetValue(LinuxGameLibraryStore.GameKey(game), out var profile) ? profile.Nvidia : null;

    /// <summary>Starts the view model over the games, selects one and waits for its analysis (the DLSS files).</summary>
    private static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, NvidiaGpuFacts gpu, RecordingLauncher? launcher = null,
        string? select = null, string language = "en-US")
    {
        var name = select ?? games[0].Name;
        var fx = await ViewModelFixture.StartAsync(games, uiLanguage: language, launcher: launcher ?? new RecordingLauncher(), hdrProfiles: Service(gpu));
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
        Assert.False(fx.Vm.IsSelectionProbeBusy);
        return fx;
    }

    private static LinuxHdrLaunchProfileService Service(NvidiaGpuFacts gpu)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => "wayland",
                "WAYLAND_DISPLAY" => "wayland-0",
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: _ => false,
            nvidiaVersion: () => null,
            nvidiaGpu: () => gpu);

    private DetectedGame Game(string name, string[] dlss)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        foreach (var file in dlss) File.WriteAllBytes(Path.Combine(dir, file), [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Manual", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Proton", CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton", LaunchCommand = "umu-run Quest.exe"
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
