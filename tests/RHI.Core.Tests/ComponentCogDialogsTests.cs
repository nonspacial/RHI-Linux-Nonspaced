using Avalonia.Controls;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297; upstream 2.9.0 "Component settings (⚙)"): the ReShade, ReLimiter, Display Commander and Luma ⚙s on
/// the shared layout — each opens every ini (and log) its component uses, as upstream; Luma on a game that is not Unreal
/// Engine says it has nothing to set (and has a ⚙ for that), and results show inside the ⚙.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ComponentCogDialogsTests
{
    private static async Task<(ViewModelFixture Fixture, DetectedGame Game)> StartAsync(string name, string? engine = null)
    {
        UiLocalizationService.Apply("en-US");
        var root = Path.Combine(Path.GetTempPath(), $"rhi-cog-dialogs-{Guid.NewGuid():N}");
        var bin = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, Runner = "Proton", Source = "Heroic",
            GraphicsApi = GraphicsApi.DirectX12, EngineHint = engine
        };
        var fx = await ViewModelFixture.StartAsync([game]);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        return (fx, fx.Vm.SelectedGame!);
    }

    private static Button[] OpenButtons(CogDialog cog)
        => CogDialogLayoutTests.All<Button>(cog.Prepare()).Where(b => b.Classes.Contains("cogOpenFile")).ToArray();

    [Fact]
    public async Task ReShadeAndReLimiterAndDisplayCommander_EachOpenTheirIniAndLog_AsUpstream()
    {
        var (fx, game) = await StartAsync("Open Files Game");
        using var _ = fx;
        var bin = game.BinaryDirectory!;
        File.WriteAllText(Path.Combine(bin, "reshade.ini"), "[GENERAL]\n"); // any case, as ReShade writes it
        File.WriteAllText(Path.Combine(bin, "relimiter.ini"), "[FrameLimiter]\n");
        File.WriteAllText(Path.Combine(bin, "relimiter_20261101.log"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(bin, "relimiter_20261101.log"), DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(Path.Combine(bin, "relimiter_20261115.log"), "new");

        var reshade = OpenButtons(ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.ReShade, "ReShade", _ => null));
        Assert.Equal(["Open ReShade.ini", "Open ReShade.log"], reshade.Select(b => (string)b.Content!));
        Assert.Equal(Path.Combine(bin, "reshade.ini"), ToolTip.GetTip(reshade[0]));
        Assert.True(reshade[0].IsEnabled);
        Assert.False(reshade[1].IsEnabled); // no ReShade.log yet

        var relimiter = OpenButtons(ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.ReLimiter, "ReLimiter", _ => null));
        Assert.Equal(["Open relimiter.ini", "Open ReLimiter log"], relimiter.Select(b => (string)b.Content!));
        Assert.Equal(Path.Combine(bin, "relimiter_20261115.log"), ToolTip.GetTip(relimiter[1]));
        Assert.All(relimiter, b => Assert.True(b.IsEnabled));

        var dc = OpenButtons(ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.DisplayCommander, "Display Commander", _ => null));
        Assert.Equal("Open DisplayCommander.ini", Assert.Single(dc).Content);
        Assert.False(dc[0].IsEnabled);

        // Every one sits in upstream's Files card, beside Deploy.
        var cog = ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.ReShade, "ReShade", _ => null);
        cog.Prepare();
        var files = cog.Body.Children.OfType<CogCard>().Single(c => c.Header == "Files");
        Assert.Contains(CogDialogLayoutTests.All<Button>(files), b => b.Name == "IniDeploy");
        Assert.Equal(2, CogDialogLayoutTests.All<Button>(files).Count(b => b.Classes.Contains("cogOpenFile")));
        Assert.Equal("ReShade Settings", cog.Title);
    }

    [Fact]
    public async Task Luma_OnAGameThatIsNotUnreal_HasAGear_ThatSaysThereIsNothingToSet()
    {
        var (fx, _) = await StartAsync("Luma Unity Game", engine: "Unity");
        using var __ = fx;
        var luma = new ManagedComponent
        {
            Kind = ManagedComponentKind.LumaFramework, DisplayName = "Luma", Availability = ComponentAvailability.Installed, ManagedByRhi = true
        };
        Assert.True(IniPresetsPresenter.HasIniSettings(luma)); // upstream: Luma's ⚙ is there on every game

        var cog = ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.LumaFramework, "Luma", _ => null);
        cog.Prepare();
        var empty = Assert.Single(CogDialogLayoutTests.All<TextBlock>(cog.Body), t => t.Classes.Contains("cogEmpty"));
        Assert.Equal("Luma's settings are for Unreal Engine games. This game has nothing to set here.", empty.Text);
        Assert.Empty(CogDialogLayoutTests.All<ComboBox>(cog.Body));
        Assert.Empty(CogDialogLayoutTests.All<Button>(cog.Body));
    }

    [Fact]
    public async Task Luma_OnAnUnrealGame_ShowsItsUnrealEngineCard_NotTheEmptyLine()
    {
        var (fx, game) = await StartAsync("Luma Unreal Game", engine: "Unreal Engine 5");
        using var _ = fx;
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[GENERAL]\n");

        var cog = ComponentIniDialog.Build(fx.Vm, ManagedComponentKind.LumaFramework, "Luma", _ => null);
        cog.Prepare();
        Assert.DoesNotContain(CogDialogLayoutTests.All<TextBlock>(cog.Body), t => t.Classes.Contains("cogEmpty"));
        var card = Assert.Single(cog.Body.Children.OfType<CogCard>());
        Assert.Equal("Unreal Engine", card.Header);
        var row = Assert.Single(card.Children.OfType<CogRow>(), r => r.Label == "HDR on first boot");
        Assert.False(string.IsNullOrWhiteSpace(row.Help)); // the explanation is under the setting
    }
}
