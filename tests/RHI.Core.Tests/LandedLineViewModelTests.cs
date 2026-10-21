using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #204 (w8 follow-up C11): an operation whose commit write landed and then reported an error is finished as committed
/// (<see cref="DeploymentScope.Landed"/>). Where the UI words the success line itself — Luma ⚙ HDR and TAA, a game's peak
/// brightness, the RenoDX ⚙ Engine.ini rows, Settings' per-game Apply, Apply to all games and the preset batch — that line
/// keeps what changed and adds the operation's own note (the existing EN + PT-BR text), so the user learns the save
/// complained although the change is in place. The services run journaled on the fixture's ledger, as in the app.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class LandedLineViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-landed-lines-" + Guid.NewGuid().ToString("N"));

    public LandedLineViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task LumaHdr_LandedSave_TheLineSaysWhatWasSet_AndThatTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("luma-hdr-" + language, language);

        var line = await rig.Landing(() => rig.Fx.Vm.IniPresets.SetLumaHdrAsync(LumaHdrMode.Off));

        Assert.Equal(UiLocalizationService.Format("Ini.LumaHdrSet", OptionLists.LumaHdrMode.Display(LumaHdrMode.Off)) + Note(language, "Luma"), line);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task LumaTaa_LandedSave_TheLineSaysWhatWasSet_AndThatTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("luma-taa-" + language, language);

        var line = await rig.Landing(() => rig.Fx.Vm.IniPresets.SetLumaTaaAsync(on: true));

        Assert.Equal(UiLocalizationService.Text("Ini.LumaTaaOn") + Note(language, "Luma"), line);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task GamePeakNits_LandedSave_TheLineSaysWhatWasSet_AndThatTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("peak-" + language, language);

        var line = await rig.Landing(() => rig.Fx.Vm.IniPresets.SetGameNitsAsync(900));

        Assert.Equal(UiLocalizationService.Format("Ini.PeakGameSet", 900) + Note(language, "RenoDX"), line);
        Assert.Equal(900, rig.Fx.Vm.IniPresets.GameNits());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task RenoDxEngineIniRow_LandedSave_TheLineSaysWhatChanged_AndThatTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("renodx-row-" + language, language);
        var lines = new List<string>();
        var row = Assert.Single(rig.Fx.Vm.RenoDxEngineIni.RowsFor(lines.Add));
        Assert.Equal(1, row.SelectedIndex);

        await rig.Landing(async () =>
        {
            row.SelectedIndex = 0;
            await row.Pending.WaitAsync(TimeSpan.FromSeconds(60));
            return "";
        });

        Assert.Equal([UiLocalizationService.Text("Rdx.LutOff") + Note(language, "RenoDX")], lines);
        Assert.Equal(0, row.SelectedIndex);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task PerGameApply_LandedSave_TheLineNamesTheFile_AndSaysTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("apply-" + language, language);
        await rig.AnalysedAsync();

        var line = await rig.Landing(() => rig.Fx.Vm.ReShadeProfile.ApplyGlobalReShadeSettingsToSelectedGameAsync(Settings(rig), rig.Fx.Vm.IniPresets.Service));

        Assert.Equal(UiLocalizationService.Format("Vm.ReShadeCaptureHotkeyDefaultsAppliedAt", rig.ReShadeIni) + Note(language, "ReShade.ini"), line);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ApplyToAllGames_LandedSave_TheLineNamesTheGame_AndSaysTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("apply-all-" + language, language);

        var line = await rig.Landing(() => rig.Fx.Vm.IniPresets.ApplyReShadeSettingsToAllAsync(Settings(rig)));

        Assert.EndsWith(Note(language, rig.Game.Name), line, StringComparison.Ordinal);
        Assert.NotEqual(Note(language, rig.Game.Name).TrimStart(), line);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task PresetToAllGames_LandedSave_TheLineNamesTheGame_AndSaysTheSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("preset-all-" + language, language);
        var preset = Path.Combine(rig.Root, "Downloads", "Landed.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
        File.WriteAllText(preset, "Techniques=SMAA@SMAA.fx\n");

        var line = await rig.Landing(() => rig.Fx.Vm.IniPresets.InstallPresetToAllAsync(preset, withShaderPacks: false));

        Assert.Equal(IniPresetsPresenter.BatchSummary("Landed.ini", new IniBatchResult(1, 0, 0)) + Note(language, rig.Game.Name), line);
        Assert.True(File.Exists(Path.Combine(rig.Bin, "Landed.ini")));
    }

    // Unit level: the batch counts a landed game as updated and names it; a normal result adds nothing.
    [Fact]
    public async Task TheBatch_NamesTheLandedGames_AndCountsThemAsUpdated()
    {
        DetectedGame Named(string name) => new() { Name = name, InstallPath = _root, BinaryDirectory = _root };

        var result = await UserIniService.RunBatchAsync([Named("Kept"), Named("Plain")],
            (game, _) => Task.FromResult(game.Name == "Kept" ? DeploymentScope.Landed("ReShade.ini") : ComponentOperationResult.Ok("done")),
            "test", CancellationToken.None);

        Assert.Equal(new IniBatchResult(2, 0, 0) { Landed = ["Kept"] }, result);
        Assert.Equal(UiLocalizationService.Format("Ini.BatchDone", "x", 2), IniPresetsPresenter.BatchSummary("x", new IniBatchResult(2, 0, 0)));
    }

    // ── w8 final review 1: Shaders/Add-ons Apply, a preset with its packs, Save global set, Update All ─────────────────

    // RHI's ReShade is installed: the packs change (journaled, landed) and so do the add-ons (landed): both notes.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ShadersAddonsApply_PacksAndAddOnsLanded_TheLineSaysWhatWasApplied_AndThatEachSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("sets-sync-" + language, language, shaderPacks: true);
        rig.AddonSelectionLands();

        var result = await rig.LandingResult(() => rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonChoicesAsync(
            new ShaderAddonSetSelection(ShaderAddonMode.Select, [], ShaderAddonMode.Select, ["custom-Alpha"])));

        Assert.True(result.Success, result.Message);
        Assert.Equal(UiLocalizationService.ShaderAddonAppliedFor(language, 0, 1, false) + Note(language, "ReShade") + Note(language, AddonManager),
            result.Message);
        Assert.Empty(ReShadeDeploymentService.RecordedShaderPacks(rig.Fx.Records.Find(rig.Game, ManagedComponentKind.ReShade)));
    }

    // No RHI ReShade yet: Apply installs it (landed), then the game's add-ons (landed).
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ShadersAddonsApply_ReShadeInstallLanded_TheLineSaysWhatWasApplied_AndThatEachSaveReportedAnError(string language)
    {
        using var rig = await RigAsync("sets-install-" + language, language);
        rig.ReShadeInstallLands(removeRecordFirst: true);
        rig.AddonSelectionLands();

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonChoicesAsync(
            new ShaderAddonSetSelection(ShaderAddonMode.Select, [], ShaderAddonMode.Select, ["custom-Alpha"]));

        // ReShade's install applies a game's add-ons after it (its own note), as upstream; Apply then brings them up to date.
        var install = " " + UiLocalizationService.PresentFor(language, DeploymentScope.Landed("ReShade").Message + LinuxManagedComponentService.AddonSetAppliedNote);
        Assert.True(result.Success, result.Message);
        Assert.Equal(UiLocalizationService.ShaderAddonAppliedFor(language, 0, 1, false) + install + Note(language, AddonManager), result.Message);
    }

    // The explicit lists (the dialog without the shader-set store; a preset with its packs on the selected game): the line,
    // and the note a preset adds to its own line.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ExplicitListsApply_ReShadeInstallAndAddOnsLanded_TheLineSaysSo_AndHandsTheNoteOn(string language)
    {
        using var rig = await RigAsync("lists-" + language, language);
        rig.Fx.Managed.ShaderSets = null;
        rig.ReShadeInstallLands(removeRecordFirst: true);
        rig.AddonSelectionLands();
        var note = new System.Runtime.CompilerServices.StrongBox<string>("");

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonManagerAsync([], ["custom-Alpha"], note);

        var notes = Note(language, "ReShade") + Note(language, AddonManager);
        Assert.True(result.Success, result.Message);
        Assert.Equal(UiLocalizationService.ShaderAddonAppliedFor(language, 0, 1, false) + notes, result.Message);
        Assert.Equal(notes, note.Value);
    }

    // The same lists with the shader-set store and RHI's ReShade (the packs sync, landed): the note is handed on too.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ExplicitListsApply_PackSyncLanded_HandsTheNoteOn(string language)
    {
        using var rig = await RigAsync("lists-sync-" + language, language, shaderPacks: true);
        var note = new System.Runtime.CompilerServices.StrongBox<string>("");

        var result = await rig.LandingResult(() => rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonManagerAsync([], [], note));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Note(language, "ReShade"), note.Value);
        Assert.EndsWith(note.Value, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task PresetWithItsPacks_OnAnotherGame_ReShadeInstallLanded_TheLineSaysSo(string language)
    {
        using var rig = await RigAsync("preset-packs-" + language, language);
        rig.ReShadeInstallLands();
        var (presets, preset) = rig.Preset();
        rig.Fx.Vm.SelectedGame = null; // the preset goes to a game that is not the selected one

        var line = await presets.InstallPresetAsync(preset, withShaderPacks: true, rig.Game);

        Assert.True(line.EndsWith(" " + UiLocalizationService.Format("Ini.PresetPacksApplied", "CrosireMaster") + Note(language, "ReShade"), StringComparison.Ordinal), line);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task PresetWithItsPacks_OnTheSelectedGame_ReShadeInstallLanded_TheLineSaysSo(string language)
    {
        using var rig = await RigAsync("preset-packs-sel-" + language, language);
        rig.Fx.Managed.ShaderSets = null; // the explicit lists: Apply reinstalls ReShade with the preset's packs
        rig.ReShadeInstallLands();
        var (presets, preset) = rig.Preset();

        var line = await presets.InstallPresetAsync(preset, withShaderPacks: true);

        Assert.True(line.EndsWith(" " + UiLocalizationService.Format("Ini.PresetPacksApplied", "CrosireMaster") + Note(language, "ReShade"), StringComparison.Ordinal), line);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task SaveGlobalSet_AGamesPackChangeLanded_TheLineNamesTheGame(string language)
    {
        using var rig = await RigAsync("sets-global-" + language, language, shaderPacks: true);
        rig.Fx.Managed.ShaderSets!.SetShaderMode(rig.Game, ShaderAddonMode.Global);
        rig.Fx.Managed.ShaderSets.SetAddonMode(rig.Game, ShaderAddonMode.Global);

        var result = await rig.LandingResult(() => rig.Fx.Vm.ReShadeProfile.ApplyGlobalSetsAsync([], []));

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(UiLocalizationService.Text("Win.ShaderPacks") + ": " + UiLocalizationService.Format("Sets.SumChanged", 1) + "."
                          + Note(language, rig.Game.Name) + " ", result.Message, StringComparison.Ordinal);
        Assert.Empty(ReShadeDeploymentService.RecordedShaderPacks(rig.Fx.Records.Find(rig.Game, ManagedComponentKind.ReShade)));
    }

    // Unit level: the batch line names the landed games; none adds nothing.
    [Fact]
    public void SetBatchSummary_NamesTheLandedGames()
    {
        var plain = new ShaderAddonSetBatchResult(2, 0, 0, 0, 0, 0, false, [], []);

        Assert.Equal(ReShadeProfileViewModel.SummaryText(ShaderAddonSetPart.Shaders, plain) + Note("en-US", "A, B"),
            ReShadeProfileViewModel.SummaryText(ShaderAddonSetPart.Shaders, plain with { Landed = ["A", "B"] }));
        Assert.DoesNotContain(DeploymentScope.LandedChangePrefix, ReShadeProfileViewModel.SummaryText(ShaderAddonSetPart.Shaders, plain with { Landed = [] }));
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private const string AddonManager = "ReShade Addon Manager";

    private static string Note(string language, string component)
        => " " + UiLocalizationService.PresentFor(language, DeploymentScope.Landed(component).Message);

    private static LinuxAppSettings Settings(Rig rig) => new() { ScreenshotPath = Path.Combine(rig.Root, "shots") };

    private sealed class Rig(ViewModelFixture fx, DetectedGame game, string root, string bin) : IDisposable
    {
        public ViewModelFixture Fx { get; } = fx;
        public DetectedGame Game { get; } = game;
        public string Root { get; } = root;
        public string Bin { get; } = bin;
        public string ReShadeIni => Path.Combine(Bin, "ReShade.ini");

        /// <summary>Runs <paramref name="action"/> with the operation's first ledger save landing and then throwing.</summary>
        public async Task<string> Landing(Func<Task<string>> action)
        {
            LandedLedgerWrite.Arm(Fx.Records, LandedShape.AfterCache, at: 1);
            try { return await action(); }
            finally { LandedLedgerWrite.Disarm(Fx.Records); }
        }

        /// <summary>As <see cref="Landing"/>, for an action that returns the operation's result.</summary>
        public async Task<ComponentOperationResult> LandingResult(Func<Task<ComponentOperationResult>> action)
        {
            LandedLedgerWrite.Arm(Fx.Records, LandedShape.AfterCache, at: 1);
            try { return await action(); }
            finally { LandedLedgerWrite.Disarm(Fx.Records); }
        }

        /// <summary>
        /// ReShade's own install (offline here) reports a commit write that landed and then threw. With
        /// <paramref name="removeRecordFirst"/> the game has no RHI ReShade before, and the install records it.
        /// </summary>
        public void ReShadeInstallLands(bool removeRecordFirst = false)
        {
            var record = Fx.Records.Find(Game, ManagedComponentKind.ReShade)!;
            if (removeRecordFirst) Fx.Records.Remove(Game, ManagedComponentKind.ReShade);
            Fx.Managed.ReShadeInstallForTesting = (_, _, _) =>
            {
                Fx.Records.Upsert(record);
                return Task.FromResult(DeploymentScope.Landed("ReShade"));
            };
        }

        /// <summary>The Addon Manager's own apply (offline here) reports a commit write that landed and then threw.</summary>
        public void AddonSelectionLands()
            => Fx.Managed.AddonSelectionForTesting = (_, _, _) => Task.FromResult(DeploymentScope.Landed("ReShade Addon Manager"));

        /// <summary>
        /// A preset whose effects need one pack, and the presenter that installs it (the pack list is given; the real one
        /// reads every pack's files).
        /// </summary>
        public (IniPresetsPresenter Presets, string Path) Preset()
        {
            var preset = System.IO.Path.Combine(Root, "Downloads", "Packs.ini");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(preset)!);
            File.WriteAllText(preset, "Techniques=SMAA@SMAA.fx\n");
            // Packs need a native D3DCompiler_47 in the prefix (what the Winetricks verb leaves).
            var compiler = System.IO.Path.Combine(Game.PrefixPath!, "drive_c", "windows", "system32", "d3dcompiler_47.dll");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(compiler)!);
            ReShadeChannelTests.WriteNativeD3DCompiler(compiler, 0x8664);
            var presets = Fx.Vm.IniPresets;
            presets.ResolvePacksForTesting = _ => Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>, string?)>((["CrosireMaster"], [], null));
            return (presets, preset);
        }

        /// <summary>Waits for the selected game's analysis (the per-game Apply reads it).</summary>
        public async Task AnalysedAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == Game.Name) done.TrySetResult(); }
            Fx.Vm.Session.Completed += OnCompleted;
            try
            {
                Fx.Vm.Session.Invalidate(Fx.Vm.SelectedGame!);
                Fx.Vm.SelectedGame = null;
                Fx.Vm.SelectedGame = Fx.Vm.Games.Single(g => g.Game.Name == Game.Name).Game;
                if (!Fx.Vm.Session.TryGetCached(Fx.Vm.SelectedGame!, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally { Fx.Vm.Session.Completed -= OnCompleted; }
            await Fx.WhenSelectionProbeIdleAsync(TimeSpan.FromMinutes(2));
        }

        public void Dispose() => Fx.Dispose();
    }

    // One Unreal game with RHI's ReShade, Luma and RenoDX records (each service checks only its own), a ReShade.ini with
    // Luma's and RenoDX's sections, and an Engine.ini in the prefix holding RHI's RenoDX colour-grading line. The ⚙ services
    // are journaled on the fixture's ledger, as the app's composition sets them; with shaderPacks, ReShade is journaled too
    // and its record holds a deployed pack.
    private async Task<Rig> RigAsync(string name, string language, bool shaderPacks = false)
    {
        var root = Path.Combine(_root, name);
        var bin = Directory.CreateDirectory(Path.Combine(root, "Proj", "Binaries", "Win64")).FullName;
        var exe = Path.Combine(bin, "Proj-Win64-Shipping.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n\n[Luma]\nEnableHDR=1\nDisplayMode=1\n\n[renodx-preset1]\nToneMapPeakNits=400\n");
        var dxgi = Path.Combine(bin, "dxgi.dll");
        File.WriteAllText(dxgi, "MZ ReShade crosire");
        var addon = Path.Combine(bin, "renodx-landed.addon64");
        PeArchitectureDetectorTests.WritePe(addon, 0x8664, 96 * 1024);
        var prefix = Path.Combine(root, "pfx");
        var engineIni = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Proj", "Saved", "Config", "Windows", "Engine.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(engineIni)!);
        File.WriteAllText(engineIni, "[SystemSettings]\nUserKeep=1\n");
        var game = new DetectedGame
        {
            Name = "Landed " + name, InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix, Runner = "Proton",
            Source = "Heroic", GraphicsApi = GraphicsApi.DirectX12, EngineHint = "Unreal Engine 5", EngineIniProjectOverride = "Proj"
        };
        var fx = await ViewModelFixture.StartAsync([game], language, journaledReShade: shaderPacks);
        UiLocalizationService.Apply(language);
        var card = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
        var key = LinuxDeploymentRecordStore.GameKey(card);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = key, GameName = card.Name, Component = ManagedComponentKind.ReShade, BinaryDirectory = bin, Version = "6.8.0",
            Files = [new LinuxDeployedFile { Path = dxgi, Sha256 = FileHash.Sha256Hex(dxgi) }],
            // w8 final review 1: RHI's packs as deployed, so taking them out is a real (journaled) shader-pack change
            Metadata = shaderPacks
                ? new(StringComparer.OrdinalIgnoreCase) { ["ReShade.ShaderPacks"] = "[\"CrosireMaster\"]" }
                : new(StringComparer.OrdinalIgnoreCase)
        });
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = key, GameName = card.Name, Component = ManagedComponentKind.LumaFramework, BinaryDirectory = bin, Files = []
        });
        var renodx = new LinuxDeploymentRecord
        {
            GameKey = key, GameName = card.Name, Component = ManagedComponentKind.RenoDX, BinaryDirectory = bin, Version = "Lut",
            SourceUrl = RenoDxCatalogService.GenericUnrealUrl,
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        };
        RenoDxEngineIni.Write(renodx, RenoDxEngineIni.Apply(engineIni,
            new Dictionary<string, IReadOnlyList<(string Section, string Key, string Value)>> { [RenoDxEngineIni.LutGroup] = RenoDxEngineIni.LutKeys },
            new HashSet<string>(), null));
        fx.Records.Upsert(renodx);
        var journal = SimulatedDeath.NewJournal(Path.Combine(root, "journal"), fx.Records).Journal;
        fx.Managed.LumaSettings = new LumaSettingsService(fx.Records, journal, _ => null);
        fx.Managed.RenoDxPeakNits = new RenoDxPeakNits(fx.Records, journal, _ => null);
        fx.Managed.ReShadePresets = new ReShadePresetService(fx.Records, journal, _ => null);
        fx.Managed.UserIni = new UserIniService(new UserIniTemplates(Path.Combine(root, "inis")), fx.Records, _ => null, journal);
        fx.Vm.SelectedGame = card;
        return new Rig(fx, card, root, bin);
    }
}
