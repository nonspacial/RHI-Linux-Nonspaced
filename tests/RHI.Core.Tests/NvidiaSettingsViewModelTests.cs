using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #211 through the real view model: the NVIDIA settings in the DLSS section are hidden where they cannot apply (no
/// NVIDIA GPU, native Linux, no DLSS file), frame generation only on an RTX 50; a choice is saved with the game's launch
/// profile (Default saves nothing), a direct launch passes the variables with HDR Off, and a store hand-off starts
/// without options and says where to paste them. Launches go to a recording launcher; nothing is started.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class NvidiaSettingsViewModelTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-nv-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ADlssGameOnAnNvidiaGpu_ShowsItsFeatures_AndFrameGenerationOnlyOnAnRtx50()
    {
        var game = Game("Dlss Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll", "nvngx_dlssg.dll"]);
        using (var fx = await StartAsync(game, Gpu(rtx50: false)))
        {
            var nv = fx.Vm.Dlss.Nvidia;
            Assert.Same(fx.Vm.Runtime.Nvidia, nv);
            Assert.True(nv.IsShown);
            Assert.True(nv.ShowSuperResolution);
            Assert.False(nv.ShowRayReconstruction); // no nvngx_dlssd.dll
            Assert.True(nv.ShowFrameGenerationPreset);
            Assert.False(nv.ShowFrameGeneration); // an RTX 4090 has no multi frame generation
            Assert.Equal("Nothing chosen: the game uses its own DLSS settings.", nv.Hint);
        }
        using (var fx = await StartAsync(Game("Dlss Quest 50", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll", "nvngx_dlssg.dll"]), Gpu(rtx50: true)))
            Assert.True(fx.Vm.Dlss.Nvidia.ShowFrameGeneration);
    }

    public static TheoryData<string> HiddenCases => new() { "no-nvidia", "native", "no-dlss" };

    [Theory]
    [MemberData(nameof(HiddenCases))]
    public async Task WhereTheSettingsCannotApply_TheSectionIsHidden(string why)
    {
        var game = why switch
        {
            "native" => Game("Native Quest", "Manual", "./quest.sh", dlss: ["nvngx_dlss.dll"], runner: "Native Linux"),
            "no-dlss" => Game("Plain Quest", "Manual", "umu-run Quest.exe", dlss: []),
            _ => Game("Amd Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"])
        };
        using var fx = await StartAsync(game, why == "no-nvidia" ? NvidiaGpuFacts.None : Gpu(rtx50: true));

        var nv = fx.Vm.Dlss.Nvidia;
        Assert.False(nv.IsShown);
        Assert.False(nv.ShowSuperResolution);
        Assert.False(nv.ShowFrameGeneration);
        Assert.Equal("", nv.Hint);
    }

    [Fact]
    public async Task AChoice_IsSavedWithTheLaunchProfile_AndDefaultSavesNothing()
    {
        var game = Game("Saved Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]);
        using var fx = await StartAsync(game, Gpu(rtx50: false));
        var nv = fx.Vm.Dlss.Nvidia;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);

        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(11);
        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(OptionLists.NvidiaCustom);
        nv.SuperResolutionCustomScale = 72;

        var saved = fx.Library.Load().HdrProfiles[key].Nvidia!;
        Assert.Equal((11u, 72u), (saved.SuperResolutionPreset, saved.SuperResolutionRenderScale));
        Assert.Equal(LinuxHdrModes.Off, fx.Library.Load().HdrProfiles[key].Mode);
        Assert.Equal("RHI applies these settings when it starts the game.", nv.Hint);

        // Shown again after selecting away and back: the custom scale reads as Custom with its number.
        fx.Vm.Runtime.LoadHdrProfile(fx.Vm.SelectedGame!);
        Assert.Equal(OptionLists.NvidiaCustom, nv.SelectedSuperResolutionScale.Value);
        Assert.Equal(72m, nv.SuperResolutionCustomScale);
        Assert.Equal("K - TF1", nv.SelectedSuperResolutionPreset.Display);

        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(DlssPresetCatalogue.Default);
        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(0);
        Assert.DoesNotContain(key, fx.Library.Load().HdrProfiles.Keys); // nothing chosen: no profile kept
    }

    [Fact]
    public async Task DirectLaunch_WithHdrOff_PassesTheNvidiaVariablesToTheLauncher()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Direct Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false), launcher);
        var nv = fx.Vm.Dlss.Nvidia;
        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(DlssPresetCatalogue.Recommended);
        nv.NewestDlss = true;

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.NotNull(launch.Options);
        Assert.Equal(new Dictionary<string, string>
        {
            ["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION"] = "0xffffff",
            ["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE"] = "1",
            ["PROTON_ENABLE_NGX_UPDATER"] = "1",
        }, launch.Options!.Environment!.OrderBy(p => p.Key).ToDictionary());
        Assert.Null(launch.Options.CommandPrefix);
        Assert.StartsWith("Launching with NVIDIA settings: Direct Quest", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SteamHandoff_StartsWithoutOptions_AndSaysWhereToPasteTheLine()
    {
        var launcher = new RecordingLauncher();
        using var fx = await StartAsync(Game("Steam Quest", "Steam", "steam -applaunch 1234", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false), launcher);
        var nv = fx.Vm.Dlss.Nvidia;
        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(11);

        Assert.True(nv.ShowLaunchOptions);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=11 %command%", nv.LaunchOptions);
        Assert.Contains("Steam starts this game, so RHI does not apply the NVIDIA settings on Launch", nv.Hint, StringComparison.Ordinal);

        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Null(launch.Options); // RHI applies nothing to a store hand-off
        Assert.Contains("paste: DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=11 %command%", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameGeneration_OnAnRtx50_KeepsOnlyTheModesOwnValues()
    {
        using var fx = await StartAsync(Game("Mfg Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlssg.dll"]), Gpu(rtx50: true));
        var nv = fx.Vm.Dlss.Nvidia;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);

        nv.SelectedFrameGenerationMode = nv.FrameGenerationModeOptions.For(NvidiaFrameGenerationModes.Fixed);
        nv.SelectedFixedFrameCount = nv.FixedFrameCountOptions.For(3);
        Assert.True(nv.ShowFixedFrameCount);
        Assert.True(nv.ShowFrameCount);
        Assert.False(nv.ShowTargetFrameRate);

        nv.SelectedFrameGenerationMode = nv.FrameGenerationModeOptions.For(NvidiaFrameGenerationModes.Dynamic);
        nv.SelectedTargetFrameRate = nv.TargetFrameRateOptions.For(OptionLists.NvidiaCustom);
        nv.CustomTargetFrameRate = 90;

        var saved = fx.Library.Load().HdrProfiles[key].Nvidia!;
        Assert.Equal(("Dynamic", 0u, 90u), (saved.FrameGenerationMode, saved.FrameCount, saved.DynamicTargetFrameRate));
        Assert.Equal(0u, nv.SelectedFixedFrameCount.Value);
        Assert.True(nv.ShowDynamicFrameCount);
        Assert.False(nv.ShowFixedFrameCount);
        Assert.True(nv.ShowCustomTargetFrameRate);
        Assert.Equal("90", fx.Vm.Runtime.SelectedHdrPlan.Environment["DXVK_NVAPI_DRS_NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE"]);
    }

    [Fact]
    public async Task ThePresetLists_FollowTheManifest_AndASavedPresetItNoLongerListsStaysSelected()
    {
        using var fx = await StartAsync(Game("Preset Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false));
        var nv = new NvidiaSettingsPresenter(fx.Vm.Runtime, () => DlssPresetCatalogue.Merge(DlssPresetCatalogue.BuiltIn,
            new UpstreamDlssPresetsDefinition([new("N - TF2", 14, false)], [], [new("C", 3, false)])));

        nv.Load(new NvidiaGameSettings { SuperResolutionPreset = 15 });

        Assert.Equal(["Default", "J - TF1", "K - TF1", "L - TF2", "M - TF2", "N - TF2", "15", "NVIDIA Recommended"],
            nv.SuperResolutionPresetOptions.Select(o => o.Display));
        Assert.Equal(15u, nv.SelectedSuperResolutionPreset.Value);
        Assert.Equal(["Default", "A", "B", "C", "NVIDIA Recommended"], nv.FrameGenerationPresetOptions.Select(o => o.Display));
    }

    [Fact]
    public async Task Labels_FollowTheUiLanguage()
    {
        using var fx = await StartAsync(Game("Pt Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false));
        var nv = fx.Vm.Dlss.Nvidia;
        try
        {
            UiLocalizationService.Apply("pt-BR");
            fx.Vm.Runtime.RefreshLocalized();
            Assert.Equal("Padrão", nv.SelectedSuperResolutionPreset.Display);
            Assert.Equal("Recomendado pela NVIDIA", nv.SuperResolutionPresetOptions.Last().Display);
            Assert.Equal("Nada escolhido: o jogo usa os próprios ajustes de DLSS.", nv.Hint);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
            fx.Vm.Runtime.RefreshLocalized();
        }
    }

    // ---- Restore all (finding 1: coordinator decision 4) -----------------------------------------------------------

    [Fact]
    public async Task RestoreAll_SetsTheNvidiaSettingsBackToDefault_BesideRhisDlssFiles()
    {
        var game = Game("Restore Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]);
        File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll.original"), [0x4D, 0x5A, 0x01]); // RHI swapped this one
        using var fx = await StartAsync(game, Gpu(rtx50: false));
        var nv = fx.Vm.Dlss.Nvidia;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);
        nv.SelectedSuperResolutionPreset = nv.SuperResolutionPresetOptions.For(11);
        nv.NewestDlss = true;
        Assert.NotNull(fx.Library.Load().HdrProfiles[key].Nvidia);
        Assert.True(fx.Vm.Dlss.DlssSrRestoreAvailable);

        await fx.Vm.Dlss.RestoreAllDlssCommand.ExecuteAsync(null);

        Assert.False(fx.Library.Load().HdrProfiles.TryGetValue(key, out var left) && left.Nvidia is not null);
        Assert.DoesNotContain(fx.Vm.Runtime.SelectedHdrPlan.Environment.Keys,
            k => k.StartsWith("DXVK_NVAPI_DRS_", StringComparison.Ordinal) || k == "PROTON_ENABLE_NGX_UPDATER");
        Assert.Equal(DlssPresetCatalogue.Default, nv.SelectedSuperResolutionPreset.Value);
        Assert.False(nv.NewestDlss);
        Assert.False(nv.HasChoices);
    }

    [Fact]
    public async Task RestoreAll_IsOfferedForNvidiaSettingsAlone_AndSaysSo()
    {
        using var fx = await StartAsync(Game("Only Nv Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false));
        var nv = fx.Vm.Dlss.Nvidia;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);
        Assert.False(fx.Vm.Dlss.CanRestoreAllDlss); // no RHI files, nothing chosen

        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(67u);
        Assert.True(fx.Vm.Dlss.CanRestoreAllDlss);
        Assert.True(fx.Vm.Dlss.RestoreAllDlssCommand.CanExecute(null));

        await fx.Vm.Dlss.RestoreAllDlssCommand.ExecuteAsync(null);

        Assert.DoesNotContain(key, fx.Library.Load().HdrProfiles.Keys);
        Assert.Equal(0u, nv.SelectedSuperResolutionScale.Value);
        Assert.StartsWith("NVIDIA settings set back to Default: Only Nv Quest.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.False(fx.Vm.Dlss.CanRestoreAllDlss);
    }

    // ---- a restored library file (finding 6, Refs #149) ----------------------------------------------------------

    [Fact]
    public async Task FirstNvidiaChangeAfterARestoredLibrary_ChangesOnlyThatSetting()
    {
        var game = Game("Restored Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]);
        using var fx = await StartAsync(game, Gpu(rtx50: false), seed: store => File.WriteAllText(store.LibraryPath, "{ not json"));
        var nv = fx.Vm.Dlss.Nvidia;
        var key = LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!);
        var restored = new LinuxGameLibraryState();
        restored.HdrProfiles[key] = new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 11, NewestDlss = true } };
        var staged = Path.Combine(_root, "restored.json");
        new LinuxGameLibraryStore(staged).Save(restored);
        File.Copy(staged, fx.Library.LibraryPath, overwrite: true);

        // The controls show the stand-in (nothing chosen); the user picks a render scale.
        nv.SelectedSuperResolutionScale = nv.SuperResolutionScaleOptions.For(67u);

        var saved = fx.Library.Load().HdrProfiles[key].Nvidia!;
        Assert.Equal((11u, true, 67u), (saved.SuperResolutionPreset, saved.NewestDlss, saved.SuperResolutionRenderScale));
        Assert.Equal(11u, nv.SelectedSuperResolutionPreset.Value); // the controls now show the restored choices
        Assert.True(nv.NewestDlss);
    }

    // ---- hide rules (finding 5) and the hand-off hint (finding 2) --------------------------------------------------

    [Fact]
    public async Task TheFrameGenerationPreset_IsHiddenOnAnRtx30()
    {
        var files = new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll" };
        using (var fx = await StartAsync(Game("Fg30 Quest", "Manual", "umu-run Quest.exe", dlss: files), new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 3080")))
        {
            Assert.True(fx.Vm.Dlss.Nvidia.IsShown);
            Assert.False(fx.Vm.Dlss.Nvidia.ShowFrameGenerationPreset);
        }
        using (var fx = await StartAsync(Game("Fg40 Quest", "Manual", "umu-run Quest.exe", dlss: files), Gpu(rtx50: false)))
            Assert.True(fx.Vm.Dlss.Nvidia.ShowFrameGenerationPreset);
    }

    [Fact]
    public void TheHandoffHint_ListsWhatTheCopyLineHolds_NotABlockedHdrModesVariables()
    {
        var game = Game("Heroic Quest", "Heroic", "xdg-open heroic://launch/legendary/abc", dlss: ["nvngx_dlss.dll"]);
        var x11 = new LinuxHdrHostSnapshot(false, "KDE", false, "610.57.04", true) { Gpu = Gpu(rtx50: false) };
        var plan = Service(Gpu(rtx50: false)).BuildPlan(game, new LinuxHdrGameProfile
        {
            Mode = LinuxHdrModes.NativeWayland, EnableLegacyWsi = true, Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 11 }
        }, x11, new NvidiaDlssFamilies(true, false, false));

        Assert.False(plan.CanApply); // native Wayland HDR on X11: blocked
        var hint = NvidiaLaunchText.ApplyInLauncher(plan, game);
        Assert.Contains("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=11", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE_HDR_WSI", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE_HDR_WSI", plan.LaunchOptions, StringComparison.Ordinal);
    }

    // ---- the one NVIDIA settings dialog (owner layout B; nits 8, 9) ------------------------------------------------

    [Fact]
    public async Task TheDialog_BindsTheGamesSettings_KeepsProvenanceInTooltips_AndCloseOnlyCloses()
    {
        using var fx = await StartAsync(Game("Dialog Quest", "Manual", "umu-run Quest.exe", dlss: ["nvngx_dlss.dll"]), Gpu(rtx50: false));
        var dialog = new NvidiaSettingsDialogViewModel(fx.Vm.Dlss.Nvidia);
        var closed = 0;
        dialog.CloseRequested += () => closed++;

        Assert.Same(fx.Vm.Dlss.Nvidia, dialog.Settings);
        Assert.Equal("NVIDIA settings", dialog.WindowTitle);
        Assert.Contains("Proton's NVIDIA layer", dialog.SrPresetTip, StringComparison.Ordinal);
        Assert.Contains("Proton 10 or newer", dialog.SrPresetTip, StringComparison.Ordinal);
        Assert.Contains("Some games may fail to start with it on", dialog.NewestDlssTip, StringComparison.Ordinal);
        Assert.DoesNotContain("Proton", UiLocalizationService.Text("Nv.DialogIntro"), StringComparison.Ordinal); // provenance only in tooltips
        Assert.DoesNotContain("disk", UiLocalizationService.Text("Nv.DialogIntro"), StringComparison.OrdinalIgnoreCase);

        dialog.CloseCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.False(fx.Vm.Dlss.Nvidia.HasChoices); // nothing saved by closing
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static NvidiaGpuFacts Gpu(bool rtx50) => new(true, rtx50, rtx50 ? "NVIDIA GeForce RTX 5090" : "NVIDIA GeForce RTX 4090");

    /// <summary>Starts the view model over one game, selects it and waits for its analysis (the DLSS files).</summary>
    private static async Task<ViewModelFixture> StartAsync(DetectedGame game, NvidiaGpuFacts gpu, RecordingLauncher? launcher = null,
        Action<LinuxGameLibraryStore>? seed = null)
    {
        var fx = await ViewModelFixture.StartAsync([game], launcher: launcher ?? new RecordingLauncher(), hdrProfiles: Service(gpu), seed: seed);
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == game.Name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
            if (!(fx.Vm.Session.Game?.Name == game.Name && fx.Vm.Session.Analysis is not null && !fx.Vm.IsSelectionProbeBusy))
                await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
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

    private DetectedGame Game(string name, string source, string command, string[] dlss, string runner = "Proton")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        foreach (var file in dlss) File.WriteAllBytes(Path.Combine(dir, file), [0x4D, 0x5A]);
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
