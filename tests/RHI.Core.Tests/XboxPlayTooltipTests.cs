using RHI.Core.Services;
using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>Refs #90 (owner decision B): an Xbox card whose owning app RHI cannot open says where to start the game, in both languages.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class XboxPlayTooltipTests
{
    [Theory]
    [InlineData("en-US", "Unavailable: start this game from the app that installed it.")]
    [InlineData("pt-BR", "Indisponível: inicie este jogo pelo app que o instalou.")]
    public async Task XboxCardWithoutAnOpenCommand_PlayIsDisabled_AndTheTooltipNamesTheOwningApp(string language, string start)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-xbox-tip-{Guid.NewGuid():N}");
        var dir = Directory.CreateDirectory(Path.Combine(root, "9ABC")).FullName;
        var exe = Path.Combine(dir, "Game.exe");
        File.WriteAllBytes(exe, [0x13, 0x37, 0, 0]);
        var game = new DetectedGame
        {
            Name = "Xbox Game", InstallPath = dir, ExecutablePath = exe, Source = "Xbox", Runner = XboxInstallerApp.DisplayName,
            XboxPackage = new XboxPackageInfo("9ABC", XboxInstallerApp.DisplayName, null, null)
        };
        try
        {
            UiLocalizationService.Apply(language);
            using var fx = await ViewModelFixture.StartAsync([game], language);
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == "Xbox Game").Game;
            for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);

            Assert.False(fx.Vm.CanLaunch);
            Assert.StartsWith(start, fx.Vm.LaunchToolTip);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
