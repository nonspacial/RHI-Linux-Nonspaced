using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #179 view model: the per-game Shaders / Add-ons choices (shown only where RHI installed ReShade; the add-on choice not
/// on the No Addons build), Select opening the game's own list, Global / Off saved and applied at once with one status
/// line, the global shader mode, and the Shaders/Add-ons dialog in its global and Select forms.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ShaderSetModesViewModelTests
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

    private static void GiveReShade(ViewModelFixture fx, DetectedGame game, string channel = "Stable", string packs = "[]")
        => fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Metadata = { ["ReShade.Channel"] = channel, ["ReShade.ShaderPacks"] = packs },
        });

    [Fact]
    public async Task Choices_AreHiddenWithoutRhisReShade_AndTheAddOnChoiceOnTheNoAddonsBuild()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        var profile = fx.Vm.ReShadeProfile;
        Assert.False(profile.ShowShaderMode);
        Assert.False(profile.ShowAddonMode);
        Assert.False(profile.ShowSetModes);

        GiveReShade(fx, game);
        profile.LoadSetModes(game);
        Assert.True(profile.ShowShaderMode);
        Assert.True(profile.ShowAddonMode);
        Assert.Equal(ShaderAddonMode.Select, profile.SelectedShaderMode!.Value); // migrated: its record's packs (none) as Select

        GiveReShade(fx, game, channel: "NoAddons");
        profile.LoadSetModes(game);
        Assert.True(profile.ShowShaderMode);
        Assert.False(profile.ShowAddonMode);
    }

    [Fact]
    public async Task Select_OpensTheGamesOwnList_AndClosingWithoutApplyingPutsTheSavedChoiceBack()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var profile = fx.Vm.ReShadeProfile;
        fx.Managed.ShaderSets!.SetShaderMode(game, ShaderAddonMode.Global, []);
        profile.LoadSetModes(game);
        var opened = new List<ShaderAddonSetPart>();
        profile.ShowSelectList = part => { opened.Add(part); return Task.CompletedTask; };

        profile.SelectedAddonMode = profile.AddonModeOptions.For(ShaderAddonMode.Select);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));
        profile.SelectedShaderMode = profile.ShaderModeOptions.For(ShaderAddonMode.Select);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([ShaderAddonSetPart.Addons, ShaderAddonSetPart.Shaders], opened);
        Assert.Equal(ShaderAddonMode.Global, profile.SelectedShaderMode!.Value);
        Assert.Equal(ShaderAddonMode.Global, profile.SelectedAddonMode!.Value);
        Assert.Equal(ShaderAddonMode.Global, fx.Managed.ShaderSets.ChoiceFor(game)!.ShaderMode);
    }

    [Fact]
    public async Task Off_IsSavedAndApplied_AndTheStatusLineCountsTheOutcome()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game); // no packs deployed: Off has nothing to take out
        var profile = fx.Vm.ReShadeProfile;
        profile.LoadSetModes(game);

        profile.SelectedShaderMode = profile.ShaderModeOptions.For(ShaderAddonMode.Off);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ShaderAddonMode.Off, fx.Managed.ShaderSets!.ChoiceFor(game)!.ShaderMode);
        // The line stays after the game is re-read (offline notices may follow it).
        Assert.StartsWith("Shader packs: " + UiLocalizationService.Format("Sets.SumUnchanged", 1) + ".", fx.Vm.StatusText);
    }

    [Fact]
    public async Task Global_ThatCannotBeApplied_StaysSaved_AndSaysTheGameKeptItsFiles()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var profile = fx.Vm.ReShadeProfile;
        fx.Managed.ShaderSets!.SetGlobalShaderPackIds(["CrosireMaster"]);
        profile.LoadSetModes(game);
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);

        profile.SelectedShaderMode = profile.ShaderModeOptions.For(ShaderAddonMode.Global);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ShaderAddonMode.Global, fx.Managed.ShaderSets.ChoiceFor(game)!.ShaderMode);
        Assert.StartsWith("Shader packs: " + UiLocalizationService.Format("Sets.SumFailed", 1, ""),
            fx.Vm.StatusText);
        Assert.Contains("Tunic (", fx.Vm.StatusText);
        CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
    }

    [Fact]
    public async Task AChoiceThatCannotBeSaved_PutsTheSavedOneBack_AndChangesNothing()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var profile = fx.Vm.ReShadeProfile;
        profile.LoadSetModes(game);
        var file = fx.Managed.ShaderSets!.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ unreadable");

        profile.SelectedShaderMode = profile.ShaderModeOptions.For(ShaderAddonMode.Off);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("{ unreadable", File.ReadAllText(file));
        Assert.False(profile.ShowShaderMode); // unreadable choices: nothing to show
    }

    [Fact]
    public async Task AChoiceWhileAnotherOperationRuns_IsRefused_AndTheSavedOneComesBack()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var profile = fx.Vm.ReShadeProfile;
        profile.LoadSetModes(game); // Select (migrated)
        using (fx.Vm.Operations.TryBegin("test"))
        {
            profile.SelectedShaderMode = profile.ShaderModeOptions.For(ShaderAddonMode.Off);
            await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(ShaderAddonMode.Select, profile.SelectedShaderMode!.Value);
        Assert.Equal(ShaderAddonMode.Select, fx.Managed.ShaderSets!.ChoiceFor(game)!.ShaderMode);
        Assert.False(fx.Managed.ShaderSets.ChoiceFor(game)!.Saved); // nothing was written
    }

    [Fact]
    public async Task GlobalShaderMode_IsSaved_AndOnlyGamesSetToGlobalAreReached()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var profile = fx.Vm.ReShadeProfile;
        profile.LoadSetModes(game); // the game is Select (migrated)

        profile.SelectedGlobalShaderMode = profile.GlobalShaderModeOptions.For(GlobalShaderMode.Custom);
        await profile.ModeChange!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(GlobalShaderMode.Custom, fx.Managed.ShaderSets!.ShaderMode);
        Assert.StartsWith("Shader packs: " + UiLocalizationService.Text("Sets.SumNone") + ".", fx.Vm.StatusText);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task Labels_FollowTheUiLanguage(string language)
    {
        var (fx, game) = await StartWithGameAsync(language);
        using var _ = fx;
        var profile = fx.Vm.ReShadeProfile;
        Assert.Equal(UiLocalizationService.Text("Sets.Select"), profile.ShaderModeOptions.For(ShaderAddonMode.Select).Display);
        Assert.Equal(language == "pt-BR" ? "Escolher" : "Select", profile.ShaderModeOptions.For(ShaderAddonMode.Select).Display);
        var line = ReShadeProfileViewModel.SummaryText(ShaderAddonSetPart.Addons,
            new ShaderAddonSetBatchResult(2, 0, 1, 1, 0, 0, false, [], ["Tunic"]));
        Assert.Equal(language == "pt-BR"
            ? "Add-ons do ReShade: 2 jogo(s) atualizado(s); 1 retido(s) pelo No Addons; 1 pulado(s) por estar(em) em execução: Tunic."
            : "ReShade add-ons: 2 game(s) updated; 1 held by No Addons; 1 skipped while running: Tunic.", line);
        UiLocalizationService.Apply("en-US");
    }

    [Fact]
    public async Task Dialog_GlobalForm_SavesTheGlobalLists_TheSelectFormMakesThatPartSelect()
    {
        var (fx, game) = await StartWithGameAsync();
        using var _ = fx;
        GiveReShade(fx, game);
        var snapshot = await fx.Vm.ReShadeProfile.GetGlobalShaderAddonSnapshotAsync();
        var global = ShaderAddonsDialogViewModel.ForGlobal(fx.Vm, snapshot, _ => (true, null), _ => Task.CompletedTask, ShaderAddonSetPart.Addons);
        Assert.Equal((UiLocalizationService.Text("Sets.GlobalDialogTitle"), UiLocalizationService.Text("Sets.ApplyGlobal"), 1, true),
            (global.WindowTitle, global.ApplyText, global.InitialTab, global.IsGlobal));
        global.TickShaders(["CrosireLegacy"]);
        await global.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(["CrosireLegacy"], fx.Managed.ShaderSets!.GlobalShaderPackIds);

        // Select from the game's Add-ons choice: the ticks become its own list even when they equal what it gets now.
        fx.Managed.ShaderSets.SetAddonMode(game, ShaderAddonMode.Global);
        var perGame = await fx.Vm.ReShadeProfile.GetShaderAddonManagerSnapshotAsync(selectPart: ShaderAddonSetPart.Addons);
        var select = ShaderAddonsDialogViewModel.For(fx.Vm, perGame!, _ => (true, null), _ => Task.CompletedTask, ShaderAddonSetPart.Addons);
        Assert.Equal((1, UiLocalizationService.Text("Win.ApplySelectedGame")), (select.InitialTab, select.ApplyText));
        await select.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(ShaderAddonMode.Select, fx.Managed.ShaderSets.ChoiceFor(game)!.AddonMode);
    }
}
