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
                ? "Configurações NVIDIA de Copy Quest — definido(s): Preset do DLSS: K - TF1, Preset do Frame Generation: A. Não definido(s), porque as configurações NVIDIA deste jogo não oferecem: Preset do Ray Reconstruction."
                : "NVIDIA settings of Copy Quest — set: DLSS preset: K - TF1, Frame Generation preset: A. Not set, as this game's NVIDIA settings do not offer: Ray Reconstruction preset.",
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
        Assert.StartsWith("Not set, as this game's NVIDIA settings do not offer: Frame Generation preset. Not set over a DLSS 1.x file: DLSS preset.", fx.Vm.StatusText, StringComparison.Ordinal);
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
        var both = Game("Alpha Quest", ["nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll"]); // every row (w10 review 2: RR too)
        var srOnly = Game("Beta Quest", ["nvngx_dlss.dll"]);
        using var fx = await StartAsync([both, srOnly], Rtx40, select: "Alpha Quest");
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game; // not the selected game
        Assert.Equal(1, fx.Vm.Runtime.WriteNvidiaSettings([beta], (_, _) => new NvidiaGameSettings { SuperResolutionRenderScale = 72, NewestDlss = true }).Changed);

        var applied = await fx.Vm.Dlss.ApplyDlssBatchAsync([alpha, beta], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, 5, 1)).WaitAsync(HangGuard);

        Assert.Equal((11u, 5u, 1u), Presets(SavedNvidia(fx, alpha)!));
        var betaSaved = SavedNvidia(fx, beta)!;
        Assert.Equal((11u, 0u, 0u), Presets(betaSaved)); // no RR or FG file
        Assert.Equal((72u, true), (betaSaved.SuperResolutionRenderScale, betaSaved.NewestDlss)); // its other settings stay
        Assert.Equal("NVIDIA presets: set in 2 game(s). Not set in 1 game(s), as the game's NVIDIA settings do not offer the row.", applied.Line);
        Assert.Equal(11u, fx.Vm.Dlss.Nvidia.SelectedSuperResolutionPreset.Value); // the selected game's dialog follows
        Assert.Empty(applied.Result.Items); // no version chosen: no file touched

        // Upstream's mass restore: presets back to Default and the newest-DLSS override off; render scale stays.
        var restored = await fx.Vm.Dlss.RestoreDlssBatchAsync([alpha, beta]).WaitAsync(HangGuard);

        Assert.Null(SavedNvidia(fx, alpha)); // SR, RR and FG back to Default: nothing else was set
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
        Assert.Equal(language == "pt-BR" ? "Presets NVIDIA: definidos em 1 jogo(s)." : "NVIDIA presets: set in 1 game(s).", dialog.Status);
    }

    // w10 review 4 + 3: the batch line gives each skip its own reason (a DLSS 1.x file is not "the row is not offered"),
    // and a game without a confirmed patch target is offered no row (its files are never indexed, so they are unknown).
    [Fact]
    public async Task TheBatch_CountsEachSkipReason_AndAGameWithoutAConfirmedTargetIsOfferedNoRow()
    {
        UiLocalizationService.Apply("en-US");
        var old = Game("Old Quest", ["nvngx_dlssg.dll"]);
        File.WriteAllBytes(Path.Combine(old.InstallPath, "nvngx_dlss.dll"), PeVersionReaderTests.BuildVersionedPe("1.0.13.0", "1.0.13.0", size: 8192));
        using var fx = await StartAsync([old, Game("Ghost Quest", ["nvngx_dlss.dll", "nvngx_dlssg.dll"])], Rtx40, select: "Old Quest");
        var oldGame = fx.Vm.SelectedGame!;
        var ghost = fx.Vm.Games.Single(row => row.Game.Name == "Ghost Quest").Game.ShallowClone();
        ghost.GameRootNotAllowed = true; // no confirmed patch target
        Assert.False(ghost.HasConfirmedPatchTarget);

        var applied = await fx.Vm.Dlss.ApplyDlssBatchAsync([oldGame, ghost], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, null, 1)).WaitAsync(HangGuard);

        Assert.Equal((0u, 0u, 1u), Presets(SavedNvidia(fx, oldGame)!)); // FG set; SR not over the DLSS 1.x file
        Assert.Null(SavedNvidia(fx, ghost));
        Assert.Equal("NVIDIA presets: set in 1 game(s). Not set in 1 game(s), as the game's NVIDIA settings do not offer the row. "
            + "Not set over a DLSS 1.x file in 1 game(s).", applied.Line);
    }

    // w10 review 1 (Refs #149): a library file restored while RHI runs is adopted by the batch's NVIDIA write even when
    // the selected game is not ticked; the selected game's controls then show the restored profile, so its next change
    // keeps the restored NVIDIA settings instead of saving the empty stand-in over them.
    [Theory]
    [InlineData("apply")]
    [InlineData("restore")]
    public async Task ARestoredLibrary_BatchOnAnotherGame_ThenAnHdrChange_KeepsTheSelectedGamesRestoredProfile(string how)
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Alpha Quest", ["nvngx_dlss.dll"]), Game("Beta Quest", ["nvngx_dlss.dll"])], Rtx40,
            select: "Alpha Quest", seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        var restored = new LinuxGameLibraryState();
        restored.HdrProfiles[LinuxGameLibraryStore.GameKey(alpha)] = new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { SuperResolutionRenderScale = 72, SuperResolutionPreset = 12 } };
        restored.HdrProfiles[LinuxGameLibraryStore.GameKey(beta)] = new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 12 } };
        var staged = Path.Combine(_root, "restored.json");
        new LinuxGameLibraryStore(staged).Save(restored);
        File.Copy(staged, fx.Library.LibraryPath, overwrite: true); // the user restores the file

        if (how == "apply")
            await fx.Vm.Dlss.ApplyDlssBatchAsync([beta], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, null, null)).WaitAsync(HangGuard);
        else
            await fx.Vm.Dlss.RestoreDlssBatchAsync([beta]).WaitAsync(HangGuard);
        Assert.Equal(12u, fx.Vm.Dlss.Nvidia.SelectedSuperResolutionPreset.Value); // the controls show the restored profile

        fx.Vm.Runtime.HdrLegacyWsi = true;

        var saved = SavedNvidia(fx, alpha);
        Assert.NotNull(saved);
        Assert.Equal((72u, 12u), (saved!.SuperResolutionRenderScale, saved.SuperResolutionPreset));
        Assert.Equal(how == "apply" ? 11u : 0u, SavedNvidia(fx, beta)?.SuperResolutionPreset ?? 0u);
    }

    // Coordinator decision 3 (upstream's MassDlssDeployDialog order): game by game, that game's DLL swaps and then its
    // presets. A Cancel (the dialog's) accepted while the first game's DLLs are swapped lets that game finish — presets
    // included — and stops before the next, which keeps its own presets; the dialog's line names the games that got presets.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task TheBatch_GoesGameByGame_DllsThenPresets_AndCancelNamesTheGamesThatGotPresets(string language)
    {
        var alphaGame = Game("Alpha Quest", []);
        var betaGame = Game("Beta Quest", []);
        foreach (var game in new[] { alphaGame, betaGame }) File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), Enumerable.Repeat((byte)0x11, 70_000).ToArray());
        using var fx = await StartAsync([alphaGame, betaGame], Rtx40, select: "Alpha Quest", language: language, httpResponse: DlssDownloads);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.DefaultDlssVersion = "9.9.9";
        fx.Vm.Settings.SaveLinuxSettings(settings);
        SetDefaults(fx, sr: 11);
        UiLocalizationService.Apply(language); // a Settings save adopts the saved language
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        var dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        Assert.Equal(["Alpha Quest", "Beta Quest"], dialog.Games.Select(row => row.Name));
        foreach (var row in dialog.Games) row.IsChecked = true;
        var writes = 0;
        RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = _ => { if (++writes == 1) dialog.CloseCommand.Execute(null); }; // Cancel
        try
        {
            await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(HangGuard);
        }
        finally { RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = null; }

        Assert.Equal(1, writes); // the second game was never reached
        Assert.True(File.Exists(Path.Combine(alpha.InstallPath, "nvngx_dlss.dll.original")));
        Assert.Equal(11u, SavedNvidia(fx, alpha)?.SuperResolutionPreset);
        Assert.False(File.Exists(Path.Combine(beta.InstallPath, "nvngx_dlss.dll.original")));
        Assert.Null(SavedNvidia(fx, beta));
        Assert.Equal(UiLocalizationService.Text("Vm.OperationCancelled") + " "
            + (language == "pt-BR" ? "Presets NVIDIA definidos antes do cancelamento: Alpha Quest." : "NVIDIA presets set before the cancel: Alpha Quest."),
            dialog.Status);
    }

    // Restore selected, likewise game by game (upstream ExecuteRestoreAllAsync): that game's DLLs back, then its presets.
    [Fact]
    public async Task RestoreSelected_GoesGameByGame_AndCancelNamesTheGamesWhosePresetsWentBack()
    {
        UiLocalizationService.Apply("en-US");
        var alphaGame = Game("Alpha Quest", []);
        var betaGame = Game("Beta Quest", []);
        foreach (var game in new[] { alphaGame, betaGame })
        {
            File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), [0x4D, 0x5A, 0x01]);
            File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll.original"), [0x4D, 0x5A, 0x02]);
        }
        using var fx = await StartAsync([alphaGame, betaGame], Rtx40, select: "Alpha Quest");
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        Assert.Equal(2, fx.Vm.Runtime.WriteNvidiaSettings([alpha, beta], (_, _) => new NvidiaGameSettings { SuperResolutionPreset = 11, SuperResolutionRenderScale = 72 }).Changed);
        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        var dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        foreach (var row in dialog.Games) row.IsChecked = true;
        var writes = 0;
        RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = _ => { if (++writes == 1) dialog.CloseCommand.Execute(null); }; // Cancel
        try
        {
            await dialog.RestoreCommand.ExecuteAsync(null).WaitAsync(HangGuard);
        }
        finally { RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = null; }

        Assert.Equal(1, writes);
        Assert.False(File.Exists(Path.Combine(alpha.InstallPath, "nvngx_dlss.dll.original")));
        Assert.Equal((0u, 72u), (SavedNvidia(fx, alpha)!.SuperResolutionPreset, SavedNvidia(fx, alpha)!.SuperResolutionRenderScale));
        Assert.True(File.Exists(Path.Combine(beta.InstallPath, "nvngx_dlss.dll.original")));
        Assert.Equal(11u, SavedNvidia(fx, beta)!.SuperResolutionPreset);
        Assert.Equal("Operation cancelled. NVIDIA presets back to Default before the cancel: Alpha Quest.", dialog.Status);
    }

    // w10 follow-up D10 (decision 3, the order inside one game): a Cancel during the first game's download — before any
    // write — leaves that game without presets too, and the line names no game.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task TheBatch_CancelDuringTheFirstGamesDownload_LeavesThatGameWithoutPresets_AndNamesNoGame(string language)
    {
        var alphaGame = Game("Alpha Quest", []);
        var betaGame = Game("Beta Quest", []);
        foreach (var game in new[] { alphaGame, betaGame }) File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), Enumerable.Repeat((byte)0x11, 70_000).ToArray());
        DlssBatchDialogViewModel? dialog = null;
        var downloads = 0;
        HttpResponseMessage? Respond(HttpRequestMessage request)
        {
            if ((request.RequestUri?.AbsoluteUri ?? "").StartsWith("https://github.com/rhi-tests/dlss/", StringComparison.Ordinal)
                && Interlocked.Increment(ref downloads) == 1)
                dialog!.CloseCommand.Execute(null); // Cancel while the payload downloads
            return DlssDownloads(request);
        }
        using var fx = await StartAsync([alphaGame, betaGame], Rtx40, select: "Alpha Quest", language: language, httpResponse: Respond);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.DefaultDlssVersion = "9.9.9";
        fx.Vm.Settings.SaveLinuxSettings(settings);
        SetDefaults(fx, sr: 11);
        UiLocalizationService.Apply(language);
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        foreach (var row in dialog.Games) row.IsChecked = true;

        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.True(downloads >= 1, "the payload was never downloaded inside the batch");
        Assert.Null(SavedNvidia(fx, alpha));
        Assert.Null(SavedNvidia(fx, beta));
        Assert.False(File.Exists(Path.Combine(alpha.InstallPath, "nvngx_dlss.dll.original")));
        Assert.Equal(UiLocalizationService.Text("Vm.OperationCancelled"), dialog.Status);
    }

    // w10 follow-up D10: inside one game its DLL writes come first, its presets after — seen at the moment each write
    // phase begins (the first is the first game's): Apply has not set that game's presets yet, Restore has not reset them.
    [Theory]
    [InlineData("apply")]
    [InlineData("restore")]
    public async Task InsideOneGame_TheDllWritesComeBeforeItsPresets(string how)
    {
        UiLocalizationService.Apply("en-US");
        var alphaGame = Game("Alpha Quest", []);
        var betaGame = Game("Beta Quest", []);
        foreach (var game in new[] { alphaGame, betaGame })
        {
            File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), how == "apply" ? Enumerable.Repeat((byte)0x11, 70_000).ToArray() : [0x4D, 0x5A, 0x01]);
            if (how == "restore") File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll.original"), [0x4D, 0x5A, 0x02]);
        }
        using var fx = await StartAsync([alphaGame, betaGame], Rtx40, select: "Alpha Quest", httpResponse: DlssDownloads);
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        if (how == "apply")
        {
            var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
            settings.DefaultDlssVersion = "9.9.9";
            fx.Vm.Settings.SaveLinuxSettings(settings);
            SetDefaults(fx, sr: 11);
            UiLocalizationService.Apply("en-US");
        }
        else
            Assert.Equal(2, fx.Vm.Runtime.WriteNvidiaSettings([alpha, beta], (_, _) => new NvidiaGameSettings { SuperResolutionPreset = 11, SuperResolutionRenderScale = 72 }).Changed);
        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        var dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        foreach (var row in dialog.Games) row.IsChecked = true;
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(uint? Alpha, uint? Beta)>();
        RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = _ =>
            seen.Enqueue((SavedNvidia(fx, alpha)?.SuperResolutionPreset, SavedNvidia(fx, beta)?.SuperResolutionPreset));
        try
        {
            await (how == "apply" ? dialog.ApplyCommand : dialog.RestoreCommand).ExecuteAsync(null).WaitAsync(HangGuard);
        }
        finally { RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = null; }

        var phases = seen.ToArray();
        Assert.True(phases.Length >= 2, $"write phases seen: {phases.Length}");
        // The first game's DLL write: its presets not set yet (Apply) / not reset yet (Restore); the other game untouched.
        Assert.Equal(how == "apply" ? ((uint?)null, (uint?)null) : (11u, 11u), phases[0]);
        // The last game's DLL write: the first game is done (its presets after its DLLs), this one's presets still to come.
        Assert.Equal(how == "apply" ? (11u, (uint?)null) : (0u, 11u), phases[^1]);
        Assert.Equal(how == "apply" ? 11u : 0u, SavedNvidia(fx, beta)?.SuperResolutionPreset ?? 0u);
    }

    // w10 follow-up D11: a presets-only batch (no version chosen) stops between games on Cancel: the loop's own check is
    // the only stop there — the game that was reached keeps its presets, the next gets none, and the line names the first.
    [Fact]
    public async Task APresetsOnlyBatch_StopsBetweenGames_OnCancel()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await StartAsync([Game("Alpha Quest", ["nvngx_dlss.dll"]), Game("Beta Quest", ["nvngx_dlss.dll"])], Rtx40, select: "Alpha Quest");
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        using var cancel = new CancellationTokenSource();
        fx.Vm.Dlss.BeforeBatchPresetsForTesting = game => { if (game.Name == "Alpha Quest") cancel.Cancel(); };

        var stopped = await Assert.ThrowsAsync<DlssBatchCancelledException>(() =>
            fx.Vm.Dlss.ApplyDlssBatchAsync([alpha, beta], new LinuxDlssVersionDefaults(), new NvidiaPresetChoice(11, null, null), cancel.Token).WaitAsync(HangGuard));

        Assert.Equal(11u, SavedNvidia(fx, alpha)?.SuperResolutionPreset); // reached: runs to the end once begun
        Assert.Null(SavedNvidia(fx, beta));
        Assert.Equal("Operation cancelled. NVIDIA presets set before the cancel: Alpha Quest.", stopped.Line);
    }

    // w10 follow-up D14: a failure (not Cancel) partway through a batch still names the games that already got their
    // presets set (Apply) or reset (Restore selected), in the dialog's line, EN + PT-BR.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task AFailurePartwayThrough_NamesTheGamesThatAlreadyGotTheirPresets(string language)
    {
        var alphaGame = Game("Alpha Quest", []);
        var betaGame = Game("Beta Quest", []);
        foreach (var game in new[] { alphaGame, betaGame })
        {
            File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), [0x4D, 0x5A, 0x01]);
            File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll.original"), [0x4D, 0x5A, 0x02]);
        }
        using var fx = await StartAsync([alphaGame, betaGame], Rtx40, select: "Alpha Quest", language: language);
        SetDefaults(fx, sr: 11);
        UiLocalizationService.Apply(language);
        var alpha = fx.Vm.SelectedGame!;
        var beta = fx.Vm.Games.Single(row => row.Game.Name == "Beta Quest").Game;
        fx.Vm.Dlss.BeforeBatchPresetsForTesting = game => { if (game.Name == "Beta Quest") throw new IOException("Simulated failure."); };
        var failed = UiLocalizationService.Format("Win.BatchOperationFailed", UiLocalizationService.Present("Simulated failure."));
        var pt = language == "pt-BR";

        var candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        var dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        foreach (var row in dialog.Games) row.IsChecked = true;
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(HangGuard); // no version chosen: the presets alone

        Assert.Equal(11u, SavedNvidia(fx, alpha)?.SuperResolutionPreset);
        Assert.Null(SavedNvidia(fx, beta));
        Assert.Equal(failed + " " + (pt ? "Presets NVIDIA definidos antes da falha: Alpha Quest." : "NVIDIA presets set before the failure: Alpha Quest."), dialog.Status);

        Assert.Equal(1, fx.Vm.Runtime.WriteNvidiaSettings([beta], (_, _) => new NvidiaGameSettings { SuperResolutionPreset = 11 }).Changed);
        candidates = await fx.Vm.Dlss.GetDlssBatchCandidatesAsync().WaitAsync(HangGuard);
        dialog = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, DlssBatchDialogViewModel.Eligible(candidates!));
        foreach (var row in dialog.Games) row.IsChecked = true;
        await dialog.RestoreCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.Null(SavedNvidia(fx, alpha)); // back to Default: nothing else was set
        Assert.Equal(11u, SavedNvidia(fx, beta)?.SuperResolutionPreset);
        Assert.Equal(failed + " " + (pt ? "Presets NVIDIA de volta ao padrão antes da falha: Alpha Quest." : "NVIDIA presets back to Default before the failure: Alpha Quest."), dialog.Status);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The DLSS manifest (SR 9.9.9) and its payload, served on GitHub hosts; anything else is offline.</summary>
    private static HttpResponseMessage? DlssDownloads(HttpRequestMessage request)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? "";
        if (uri == DlssManagerService.ManifestUrl)
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "dlss": [{ "version": "9.9.9", "url": "https://github.com/rhi-tests/dlss/releases/download/test/dlss.zip" }] }""")
            };
        if (!uri.StartsWith("https://github.com/rhi-tests/dlss/", StringComparison.Ordinal)) return null;
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("nvngx_dlss.dll");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(Enumerable.Repeat((byte)0x81, 80_000).ToArray());
        }
        return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
    }

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
        string? select = null, string language = "en-US", Action<LinuxGameLibraryStore>? seed = null,
        Func<HttpRequestMessage, HttpResponseMessage?>? httpResponse = null)
    {
        var name = select ?? games[0].Name;
        var fx = await ViewModelFixture.StartAsync(games, uiLanguage: language, seed: seed, httpResponse: httpResponse,
            launcher: launcher ?? new RecordingLauncher(), hdrProfiles: Service(gpu));
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
