using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179 layout B (owner): the game's Shaders/Add-ons dialog starts each tab with "Use the global set / Choose for this
/// game / None" (the list can be ticked only for Choose, shows the global set or nothing otherwise), Apply saves that
/// choice and brings the game to it without re-installing ReShade; Settings opens the same dialog for the global set.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ShaderSetChoicesDialogTests
{
    private static async Task<(ViewModelFixture Fx, DetectedGame Game)> StartWithGameAsync(string language = "en-US")
    {
        UiLocalizationService.Apply(language);
        var fx = await ViewModelFixture.StartAsync([], uiLanguage: language);
        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        return (fx, fx.Vm.SelectedGame!);
    }

    private static void GiveReShade(ViewModelFixture fx, DetectedGame game, string channel = "Stable", string packs = "[]", string version = "6.1.0")
        => fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = version,
            Metadata = { ["ReShade.Channel"] = channel, ["ReShade.ShaderPacks"] = packs },
        });

    private static ShaderAddonManagerUiSnapshot Catalog(IReadOnlyList<string> selected) => new(
        [
            new ReShadeShaderPackInfo("Lilium", "Lilium HDR Shaders", "", "Essential"),
            new ReShadeShaderPackInfo("CrosireMaster", "crosire reshade-shaders (master)", "", "Recommended"),
            new ReShadeShaderPackInfo("Prod80", "prod80", "", "Extra"),
        ],
        selected,
        [
            new ReShadeAddonCatalogItem("01", "Generic Depth", "", null, false, false, true, true),
            new ReShadeAddonCatalogItem("relimiter", "ReLimiter", "", null, false, true, true, true),
        ],
        [], "/tmp/custom-shaders", "/tmp/custom-addons");

    private static ShaderAddonsDialogViewModel Dialog(ViewModelFixture fx)
        => ShaderAddonsDialogViewModel.For(fx.Vm, Catalog(fx.Vm.ReShadeProfile.SetChoicesFor(fx.Vm.SelectedGame) is { } c
            ? c.ShaderMode == ShaderAddonMode.Select ? c.OwnShaders : c.ShaderMode == ShaderAddonMode.Global ? c.GlobalShaders : []
            : []), _ => (true, null), _ => Task.CompletedTask);

    private static string[] Ticked(ShaderAddonsDialogViewModel dialog) => dialog.TickedShaderIds().Order().ToArray();

    [Fact]
    public async Task TheChoiceRow_StartsOnTheSavedChoice_AndTheListFollowsIt()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        sets.SetGlobalShaderPackIds(["Lilium", "Prod80"]);
        sets.SetShaderMode(game, ShaderAddonMode.Select, ["CrosireMaster"]);
        var dialog = Dialog(fx);

        Assert.True(dialog.HasShaderChoice);
        Assert.True(dialog.HasAddonChoice);
        Assert.True(dialog.ShaderChoose);
        Assert.True(dialog.ShadersEditable);
        Assert.Equal(["CrosireMaster"], Ticked(dialog));
        Assert.Equal(UiLocalizationService.Text("Sets.ShadersChooseHint"), dialog.ShaderHint);

        dialog.ShaderUseGlobal = true; // the global set, shown read-only
        Assert.Equal(ShaderAddonMode.Global, dialog.ShaderMode);
        Assert.False(dialog.ShadersEditable);
        Assert.Equal(["Lilium", "Prod80"], Ticked(dialog));
        Assert.Equal(UiLocalizationService.Text("Sets.ShadersGlobalHint"), dialog.ShaderHint);

        dialog.ShaderNone = true; // nothing, read-only
        Assert.False(dialog.ShadersEditable);
        Assert.Empty(Ticked(dialog));

        dialog.ShaderChoose = true; // the game's own list comes back
        Assert.Equal(["CrosireMaster"], Ticked(dialog));
        Assert.Equal(ShaderAddonMode.Select, sets.ChoiceFor(game)!.ShaderMode); // nothing saved before Apply
    }

    [Fact]
    public async Task Choose_OnAGameThatNeverHadItsOwnList_StartsFromTheGlobalSet_AndKeepsTheTicksAcrossModes()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        sets.SetGlobalShaderPackIds(["Lilium"]);
        sets.SetShaderMode(game, ShaderAddonMode.Global, []);
        var dialog = Dialog(fx);
        Assert.True(dialog.ShaderUseGlobal);

        dialog.ShaderChoose = true;
        Assert.Equal(["Lilium"], Ticked(dialog));
        dialog.TickShaders(["Lilium", "Prod80"]);
        dialog.ShaderNone = true;
        dialog.ShaderChoose = true;

        Assert.Equal(["Lilium", "Prod80"], Ticked(dialog));
    }

    [Fact]
    public async Task Apply_SavesEachTabsChoice_AndChangesNothingButThePacksAndAddOns()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        sets.SetAddonMode(game, ShaderAddonMode.Global);
        var dialog = Dialog(fx);
        dialog.ShaderNone = true;
        dialog.AddonChoose = true;
        foreach (var row in dialog.Addons.Where(row => row.Id == "01")) row.IsChecked = true;
        var version = fx.Records.Find(game, ManagedComponentKind.ReShade)!.Version;

        await dialog.ApplyCommand.ExecuteAsync(null);

        var choice = sets.ChoiceFor(game)!;
        Assert.Equal((ShaderAddonMode.Off, ShaderAddonMode.Select), (choice.ShaderMode, choice.AddonMode));
        Assert.Equal(["01"], choice.AddonIds);
        Assert.Equal(version, fx.Records.Find(game, ManagedComponentKind.ReShade)!.Version); // ReShade itself never re-installed
    }

    [Fact]
    public async Task UseTheGlobalSet_ForgetsTheGamesOwnPackList_AsUpstream()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        sets.SetShaderMode(game, ShaderAddonMode.Select, ["CrosireMaster"]);
        var dialog = Dialog(fx);
        dialog.ShaderUseGlobal = true;

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(ShaderAddonMode.Global, sets.ChoiceFor(game)!.ShaderMode);
        Assert.Empty(sets.ChoiceFor(game)!.ShaderPackIds);
    }

    [Fact]
    public async Task TheAddOnChoice_IsHiddenOnTheNoAddonsBuild_AndApplySendsNoAddOnChoice()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game, channel: "NoAddons");
        var sets = fx.Managed.ShaderSets!;
        sets.SetAddonMode(game, ShaderAddonMode.Select, ["01"]);
        var dialog = Dialog(fx);

        Assert.True(dialog.HasShaderChoice);
        Assert.False(dialog.HasAddonChoice);
        Assert.True(dialog.AddonsEditable);
        await dialog.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(ShaderAddonMode.Select, sets.ChoiceFor(game)!.AddonMode); // held: left as it was
    }

    [Fact]
    public async Task ApplyWhileAnotherOperationRuns_IsRefused_AndSavesNothing()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        sets.SetShaderMode(game, ShaderAddonMode.Select, ["CrosireMaster"]);
        var before = File.ReadAllText(sets.FilePath);
        var dialog = Dialog(fx);
        dialog.ShaderNone = true;

        using (fx.Vm.Operations.TryBegin("test"))
            await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(before, File.ReadAllText(sets.FilePath));
        Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), dialog.Status);
    }

    [Fact]
    public async Task AChoiceThatCannotBeSaved_ChangesNothing_AndUnreadableChoicesShowNoChoiceRow()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var sets = fx.Managed.ShaderSets!;
        var dialog = Dialog(fx);
        Directory.CreateDirectory(Path.GetDirectoryName(sets.FilePath)!);
        File.WriteAllText(sets.FilePath, "{ unreadable");
        dialog.ShaderNone = true;

        await dialog.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("{ unreadable", File.ReadAllText(sets.FilePath));
        Assert.False(Dialog(fx).HasShaderChoice); // unreadable: no choice to show
    }

    // Review major 1 at the dialog: a game without RHI's ReShade opens on what it gets now; an untouched Apply keeps both
    // tabs on the global set (it used to save Select with the record's empty lists).
    [Fact]
    public async Task ANewGame_OpensOnTheGlobalSets_AndAnUntouchedApplyKeepsThem()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        var sets = fx.Managed.ShaderSets!;
        sets.SetGlobalShaderPackIds(["Lilium"]);
        sets.SetGlobalAddonIds(["01"]);
        var snapshot = (await fx.Vm.ReShadeProfile.GetShaderAddonManagerSnapshotAsync())!;
        Assert.Equal(["Lilium"], snapshot.SelectedShaderPackIds);
        Assert.Equal(["01"], snapshot.SelectedAddonIds);
        var dialog = ShaderAddonsDialogViewModel.For(fx.Vm, snapshot, _ => (true, null), _ => Task.CompletedTask);
        Assert.True(dialog.ShaderUseGlobal && dialog.AddonUseGlobal);

        await dialog.ApplyCommand.ExecuteAsync(null); // installs ReShade (the fixture is offline: it may fail; the choice must not change)

        var choice = sets.ChoiceFor(game)!;
        Assert.Equal((ShaderAddonMode.Global, ShaderAddonMode.Global), (choice.ShaderMode, choice.AddonMode));
    }

    // Review minor 4: a game the last global update did not reach (it was running) is shown as behind, and the global
    // dialog's Save brings it up to date even when the lists did not change.
    [Fact]
    public async Task ALaggingGame_IsShownBehind_AndTheGlobalSaveCatchesItUp()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game, packs: """["CrosireMaster"]""");
        var sets = fx.Managed.ShaderSets!;
        sets.SetShaderMode(game, ShaderAddonMode.Global, []); // global set empty: the game should have no packs
        Assert.True(Dialog(fx).ShadersBehind);
        fx.Vm.ReShadeProfile.RunningProbe = new NeverRunning();

        var global = ShaderAddonsDialogViewModel.ForGlobal(fx.Vm, await fx.Vm.ReShadeProfile.GetGlobalShaderAddonSnapshotAsync(),
            _ => (true, null), _ => Task.CompletedTask);
        await global.ApplyCommand.ExecuteAsync(null); // nothing changed in the lists

        // The fake record has no files to sync, so the sync fails and says so; what matters is that it was attempted.
        Assert.StartsWith(UiLocalizationService.Text("Win.ShaderPacks") + ": ", global.Status);
        Assert.DoesNotContain(UiLocalizationService.Text("Sets.SumNone"), global.Status);
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    [Fact]
    public async Task TheGlobalDialog_HasNoChoiceRow_SaysWhatTheListIs_AndSavesTheGlobalLists()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var snapshot = await fx.Vm.ReShadeProfile.GetGlobalShaderAddonSnapshotAsync();
        var global = ShaderAddonsDialogViewModel.ForGlobal(fx.Vm, snapshot, _ => (true, null), _ => Task.CompletedTask);

        Assert.Equal((false, false, true, true), (global.HasShaderChoice, global.HasAddonChoice, global.ShadersEditable, global.AddonsEditable));
        Assert.Equal((UiLocalizationService.Text("Sets.GlobalDialogTitle"), UiLocalizationService.Text("Sets.ApplyGlobal")), (global.WindowTitle, global.ApplyText));
        Assert.Equal(UiLocalizationService.Text("Sets.GlobalShadersHint"), global.ShaderHint);
        global.TickShaders(["CrosireLegacy"]);
        await global.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(["CrosireLegacy"], fx.Managed.ShaderSets!.GlobalShaderPackIds);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task Labels_FollowTheUiLanguage(string language)
    {
        var (fx, _) = await StartWithGameAsync(language);
        using var __ = fx;
        Assert.Equal(language == "pt-BR" ? "Escolher para este jogo" : "Choose for this game", UiLocalizationService.Text("Sets.Choose"));
        var line = ReShadeProfileViewModel.SummaryText(ShaderAddonSetPart.Addons,
            new ShaderAddonSetBatchResult(2, 0, 1, 1, 0, 0, false, [], ["Tunic"], Excluded: 1));
        Assert.Equal(language == "pt-BR"
            ? "Add-ons do ReShade: 2 jogo(s) atualizado(s); 1 retido(s) pelo No Addons; 1 pulado(s) por estar(em) em execução: Tunic; 1 fora do Atualizar tudo, sem alteração."
            : "ReShade add-ons: 2 game(s) updated; 1 held by No Addons; 1 skipped while running: Tunic; 1 left out of Update all, not changed.", line);
        UiLocalizationService.Apply("en-US");
    }
}
