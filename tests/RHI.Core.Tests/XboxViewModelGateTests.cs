using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>Refs #90: the view-model entry points that change an Xbox title show the write gate's refusal, in EN and PT-BR.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class XboxViewModelGateTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task ApplyingReShadeGlobalSettings_ToABusyXboxTitle_ShowsTheRefusal(string language)
    {
        using var tree = new DetectionTestTree();
        var dir = tree.Dir("payloads", "9VM");
        tree.File(JsonSerializer.Serialize(new { storeId = "9VM" }), "payloads", "9VM", XboxInstallerApp.OwnedMarker);
        tree.Exe("payloads", "9VM", "Game.exe");
        var ini = tree.File("[INPUT]\n", "payloads", "9VM", "ReShade.ini");
        var jobs = tree.File(JsonSerializer.Serialize(new { queue = new[] { new { kind = "Launch", titleId = "9VM" } } }), "state", "jobs.json");
        var package = new XboxPackageInfo("9VM", XboxWriteGate.InstallerApp, null, jobs);
        XboxWriteGate.Register(package, dir);
        var game = new DetectedGame
        {
            Name = "Xbox VM", Source = "Xbox", InstallPath = dir, BinaryDirectory = dir, ExecutablePath = Path.Combine(dir, "Game.exe"),
            Runner = XboxInstallerApp.DisplayName, XboxPackage = package
        };
        UiLocalizationService.Apply(language);
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game], language);
            fx.Vm.SelectedGame = Assert.Single(fx.Vm.Games, g => g.Game.Name == "Xbox VM").Game;

            var status = await fx.Vm.ReShadeProfile.ApplyGlobalReShadeSettingsToSelectedGameAsync(new(), fx.Vm.IniPresets.Service);

            Assert.Equal(UiLocalizationService.Present(XboxWriteGate.BusyMessage), status);
            if (language == "pt-BR") Assert.StartsWith("O app que instalou este jogo está trabalhando nele", status);
            Assert.Equal("[INPUT]\n", File.ReadAllText(ini));
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }
}
