using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>Tests that switch the process-wide UI language run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiLanguageCollection
{
    public const string Name = "UI language (process-wide)";
}

/// <summary>
/// UI-10 (#78): the Edit-game dialog closes on a structural success flag, never on the localised message, so it
/// closes in PT-BR too and stays open (showing why) when the edit or reset is refused.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class GameEditDialogResultTests
{
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task SaveEdit_ReportsSuccessStructurally_InEitherLanguage(string language)
    {
        UiLocalizationService.Apply(language);
        try
        {
            using var fx = await ViewModelFixture.StartAsync([], language);
            var game = fx.Game("Tunic");
            await AddAndSelectAsync(fx, game);

            var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(game.ExecutablePath!, game.InstallPath, game.BinaryDirectory!, null);

            Assert.True(saved, message);
            Assert.Contains(language == "pt-BR" ? "alvo de modificações salvo" : "modification target saved", message);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task SaveEdit_Refused_IsNotSuccess()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            using var fx = await ViewModelFixture.StartAsync([], "pt-BR");
            var game = fx.Game("Tunic");
            var outside = fx.Game("Elsewhere");
            await AddAndSelectAsync(fx, game);

            var (saved, message) = await fx.Vm.SaveSelectedGameEditAsync(outside.ExecutablePath!, game.InstallPath, game.BinaryDirectory!, null);

            Assert.False(saved);
            Assert.Contains("dentro da raiz do jogo", message);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public async Task ResetEdit_RefusedWhileComponentsAreManaged_IsNotSuccess_OtherwiseSucceeds()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Tunic");
        await AddAndSelectAsync(fx, game);

        var (reset, message) = await fx.Vm.ResetSelectedGameEditAsync();
        Assert.True(reset, message);

        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fx.Vm.SelectedGame!), GameName = game.Name,
            Component = ManagedComponentKind.ReShade, BinaryDirectory = game.BinaryDirectory!, Version = "6.0"
        });
        (reset, message) = await fx.Vm.ResetSelectedGameEditAsync();
        Assert.False(reset);
        Assert.Contains("Remove RHI-managed components", message);
    }

    private static async Task AddAndSelectAsync(ViewModelFixture fx, DetectedGame game)
    {
        var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        Assert.Equal(game.Name, fx.Vm.SelectedGame?.Name);
    }
}
