using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// #222 items 1–3 (w8 final re-review leftovers): the landed note was still dropped where a landed result was overwritten
// (a preset to all games with its packs), where ReShade's install applied the game's add-ons after it (their landed save
// was turned into a plain "add-ons applied"), and on the "shaders applied but add-ons failed" lines.
public sealed partial class LandedLineViewModelTests
{
    // A message ReShade's install really returns (it has its PT-BR presentation).
    private const string ReShadeInstalled =
        "ReShade 6.8.0 [Stable] applied as dxgi.dll; no RHI shader packs. Existing ReShade.ini customizations are preserved.";

    // A failure the Addon Manager really returns (PT-BR pair), for the "add-ons failed" lines.
    private const string AddonsFailed = ReShadeAddonManagerService.ReconcileFailedPrefix + "E/S negada";

    private static string Presented(string language, string message) => " " + UiLocalizationService.PresentFor(language, message);

    // ── item 1: preset to all games with its packs ─────────────────────────────────────────────────────────────

    // Two games; the first one's preset save lands, the second's goes through. The packs then fail on both, or go in
    // without landing: either way the line names the first game only.
    [Theory]
    [InlineData("en-US", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en-US", true)]
    [InlineData("pt-BR", true)]
    public async Task PresetToAllGames_WithPacks_TheLandedPresetKeepsItsGame_WhateverThePacksDo(string language, bool packsGoIn)
    {
        using var pair = await PresetPairAsync("preset-all-packs-" + language + (packsGoIn ? "-in" : "-fail"), language);
        pair.Fx.Managed.ReShadeInstallForTesting = (_, _, _) => Task.FromResult(packsGoIn
            ? ComponentOperationResult.Ok(ReShadeInstalled)
            : ComponentOperationResult.Fail(AddonsFailed));

        LandedLedgerWrite.Arm(pair.Fx.Records, LandedShape.AfterCache, at: 1); // the first game's preset save
        string line;
        try { line = await pair.Presets.InstallPresetToAllAsync(pair.Preset, withShaderPacks: true); }
        finally { LandedLedgerWrite.Disarm(pair.Fx.Records); }

        var expected = packsGoIn ? new IniBatchResult(2, 0, 0) { Landed = [pair.First.Name] } : new IniBatchResult(0, 0, 2) { Landed = [pair.First.Name] };
        Assert.Equal(IniPresetsPresenter.BatchSummary("Packs.ini", expected), line);
        Assert.EndsWith(Note(language, pair.First.Name), line, StringComparison.Ordinal);
        Assert.DoesNotContain(pair.Second.Name, line, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(pair.First.BinaryDirectory!, "Packs.ini")));
    }

    // ── item 2: ReShade's install applies the game's add-ons after it ───────────────────────────────────────────

    // Shaders/Add-ons Apply on a game where RHI does not manage ReShade yet: ReShade goes in (no landing), its install
    // applies the game's add-ons and their save lands; the later add-on step then has nothing left to do. The line says so.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ShadersAddonsApply_NoReShadeYet_TheAddOnsSaveAfterTheInstallLanded_TheLineSaysSo(string language)
    {
        using var rig = await RigAsync("sets-after-" + language, language);
        ReShadeInstallSucceeds(rig);
        AddonSelectionLandsOnce(rig);

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonChoicesAsync(
            new ShaderAddonSetSelection(ShaderAddonMode.Select, [], ShaderAddonMode.Select, ["custom-Alpha"]));

        Assert.True(result.Success, result.Message);
        Assert.Equal(UiLocalizationService.ShaderAddonAppliedFor(language, 0, 1, false)
                     + Presented(language, ReShadeInstalled + LinuxManagedComponentService.AddonSetAppliedNote + " " + DeploymentScope.Landed(AddonManager).Message),
            result.Message);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), result.Message, StringComparison.Ordinal);
    }

    // The explicit lists (a preset with its packs on the selected game): ReShade's install already applied the add-ons
    // (the Ok("") shortcut), and their landed save is still said — on the line and in the note handed to the caller.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ExplicitListsApply_TheAddOnsSaveAfterTheInstallLanded_TheShortcutKeepsTheNote(string language)
    {
        using var rig = await RigAsync("lists-after-" + language, language);
        ReShadeInstallSucceeds(rig);
        AddonSelectionLandsOnce(rig);
        rig.Fx.Vm.Session.Invalidate(rig.Game); // no analysis to say otherwise: Apply installs ReShade (the shortcut's case)
        var note = new System.Runtime.CompilerServices.StrongBox<string>("");

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonManagerAsync([], ["custom-Alpha"], note);

        var expected = Presented(language, ReShadeInstalled + LinuxManagedComponentService.AddonSetAppliedNote + " " + DeploymentScope.Landed(AddonManager).Message);
        Assert.True(result.Success, result.Message);
        Assert.Equal(UiLocalizationService.ShaderAddonAppliedFor(language, 0, 1, false) + expected, result.Message);
        Assert.Equal(expected, note.Value);
        Assert.Equal(1, rig.AddonCalls);
    }

    // The ReShade row and every other install that applies the game's add-ons after ReShade share that step: its result
    // is Landed and its message (which the row shows as it is) ends with the add-ons' note, in both languages.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ReShadeInstall_TheAddOnsSaveAfterItLanded_TheResultIsLanded_AndSaysSo(string language)
    {
        using var rig = await RigAsync("row-after-" + language, language);
        ReShadeInstallSucceeds(rig);
        AddonSelectionLandsOnce(rig);
        rig.Fx.Managed.ShaderSets!.SetAddonMode(rig.Game, ShaderAddonMode.Select, ["custom-Alpha"]);

        var result = await rig.Fx.Managed.ApplyReShadeAsync(rig.Game, rig.Fx.Managed.GetReShadeOptions(rig.Game));

        Assert.True(result.Success, result.Message);
        Assert.True(result.Landed);
        Assert.Equal(ReShadeInstalled + LinuxManagedComponentService.AddonSetAppliedNote + " " + DeploymentScope.Landed(AddonManager).Message, result.Message);
        Assert.EndsWith(Note(language, AddonManager), " " + UiLocalizationService.PresentFor(language, result.Message), StringComparison.Ordinal);
    }

    // ── item 3: "shaders applied but add-ons failed" ────────────────────────────────────────────────────────────

    // RHI's ReShade is installed: the pack change lands, the add-ons then fail. The line keeps the pack change's note.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ShadersAddonsApply_PacksLanded_TheAddOnsFail_TheLineKeepsThePacksNote(string language)
    {
        using var rig = await RigAsync("sets-sync-fail-" + language, language, shaderPacks: true);
        rig.Fx.Managed.AddonSelectionForTesting = (_, _, _) => Task.FromResult(ComponentOperationResult.Fail(AddonsFailed));

        var result = await rig.LandingResult(() => rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonChoicesAsync(
            new ShaderAddonSetSelection(ShaderAddonMode.Select, [], ShaderAddonMode.Select, ["custom-Alpha"])));

        Assert.False(result.Success);
        Assert.Equal(UiLocalizationService.Format("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.PresentFor(language, AddonsFailed))
                     + Note(language, "ReShade"), result.Message);
    }

    // No RHI ReShade yet: ReShade's install lands, the add-ons then fail (after the install and again on their own step).
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ShadersAddonsApply_ReShadeInstallLanded_TheAddOnsFail_TheLineKeepsTheInstallsNote(string language)
    {
        using var rig = await RigAsync("sets-install-fail-" + language, language);
        rig.ReShadeInstallLands(removeRecordFirst: true);
        rig.Fx.Managed.AddonSelectionForTesting = (_, _, _) => Task.FromResult(ComponentOperationResult.Fail(AddonsFailed));

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonChoicesAsync(
            new ShaderAddonSetSelection(ShaderAddonMode.Select, [], ShaderAddonMode.Select, ["custom-Alpha"]));

        Assert.False(result.Success);
        Assert.Equal(UiLocalizationService.Format("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.PresentFor(language, AddonsFailed))
                     + Presented(language, DeploymentScope.Landed("ReShade").Message + LinuxManagedComponentService.AddonSetNotAppliedPrefix + AddonsFailed),
            result.Message);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), result.Message, StringComparison.Ordinal);
    }

    // The explicit lists without the shader-set store: ReShade's install lands, the add-ons fail.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ExplicitListsApply_ReShadeInstallLanded_TheAddOnsFail_TheLineKeepsTheInstallsNote(string language)
    {
        using var rig = await RigAsync("lists-fail-" + language, language);
        rig.Fx.Managed.ShaderSets = null;
        rig.ReShadeInstallLands(removeRecordFirst: true);
        rig.Fx.Managed.AddonSelectionForTesting = (_, _, _) => Task.FromResult(ComponentOperationResult.Fail(AddonsFailed));

        var result = await rig.Fx.Vm.ReShadeProfile.ApplyShaderAddonManagerAsync([], ["custom-Alpha"]);

        Assert.False(result.Success);
        Assert.Equal(UiLocalizationService.Format("Vm.ShadersAppliedButAddOnsFailed", UiLocalizationService.PresentFor(language, AddonsFailed))
                     + Note(language, "ReShade"), result.Message);
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ReShade's own install (offline) goes in without landing: the game has RHI's ReShade afterwards.</summary>
    private static void ReShadeInstallSucceeds(Rig rig)
    {
        var record = rig.Fx.Records.Find(rig.Game, ManagedComponentKind.ReShade)!;
        rig.Fx.Records.Remove(rig.Game, ManagedComponentKind.ReShade);
        rig.Fx.Managed.ReShadeInstallForTesting = (_, _, _) =>
        {
            rig.Fx.Records.Upsert(record);
            return Task.FromResult(ComponentOperationResult.Ok(ReShadeInstalled));
        };
    }

    /// <summary>
    /// The Addon Manager's own apply (offline) as it really behaves: it records the selection, and its save lands and then
    /// reports an error; once recorded, the game's add-ons are up to date (no second apply).
    /// </summary>
    private static void AddonSelectionLandsOnce(Rig rig)
        => rig.Fx.Managed.AddonSelectionForTesting = (game, ids, _) =>
        {
            rig.AddonCalls++;
            rig.Fx.Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShadeAddonManager,
                BinaryDirectory = game.BinaryDirectory!, Files = [],
                Metadata = new(StringComparer.OrdinalIgnoreCase) { ["AddonManager.SelectedIds"] = System.Text.Json.JsonSerializer.Serialize(ids) }
            });
            return Task.FromResult(DeploymentScope.Landed(AddonManager));
        };

    private sealed class PresetPair(ViewModelFixture fx, DetectedGame first, DetectedGame second, IniPresetsPresenter presets, string preset) : IDisposable
    {
        public ViewModelFixture Fx { get; } = fx;
        public DetectedGame First { get; } = first;
        public DetectedGame Second { get; } = second;
        public IniPresetsPresenter Presets { get; } = presets;
        public string Preset { get; } = preset;
        public void Dispose() => Fx.Dispose();
    }

    // Two games with RHI's ReShade (each a native D3DCompiler_47 in its prefix, so packs can go in), the preset service
    // journaled on the fixture's ledger, and a preset whose effects need one pack.
    private async Task<PresetPair> PresetPairAsync(string name, string language)
    {
        DetectedGame Game(string letter)
        {
            var root = Path.Combine(_root, name, letter);
            var bin = Directory.CreateDirectory(Path.Combine(root, "Bin")).FullName;
            var exe = Path.Combine(bin, "Game.exe");
            PeArchitectureDetectorTests.WritePe(exe, 0x8664);
            File.WriteAllText(Path.Combine(bin, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n");
            File.WriteAllText(Path.Combine(bin, "dxgi.dll"), "MZ ReShade crosire");
            var prefix = Path.Combine(root, "pfx");
            var compiler = Path.Combine(prefix, "drive_c", "windows", "system32", "d3dcompiler_47.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(compiler)!);
            ReShadeChannelTests.WriteNativeD3DCompiler(compiler, 0x8664);
            return new DetectedGame
            {
                Name = $"Landed {letter} {name}", InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
                Runner = "Proton", Source = "Heroic", GraphicsApi = GraphicsApi.DirectX12
            };
        }
        var (a, b) = (Game("A"), Game("B"));
        var fx = await ViewModelFixture.StartAsync([a, b], language);
        UiLocalizationService.Apply(language);
        var cards = new[] { a, b }.Select(game => fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game).ToArray();
        foreach (var card in cards)
        {
            var dxgi = Path.Combine(card.BinaryDirectory!, "dxgi.dll");
            fx.Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(card), GameName = card.Name, Component = ManagedComponentKind.ReShade,
                BinaryDirectory = card.BinaryDirectory!, Version = "6.8.0",
                Files = [new LinuxDeployedFile { Path = dxgi, Sha256 = FileHash.Sha256Hex(dxgi) }]
            });
        }
        fx.Managed.ReShadePresets = new ReShadePresetService(fx.Records, SimulatedDeath.NewJournal(Path.Combine(_root, name, "journal"), fx.Records).Journal, _ => null);
        var preset = Path.Combine(_root, name, "Downloads", "Packs.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
        File.WriteAllText(preset, "Techniques=SMAA@SMAA.fx\n");
        var presets = fx.Vm.IniPresets;
        presets.ResolvePacksForTesting = _ => Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>, string?)>((["CrosireMaster"], [], null));
        return new PresetPair(fx, cards[0], cards[1], presets, preset);
    }
}
