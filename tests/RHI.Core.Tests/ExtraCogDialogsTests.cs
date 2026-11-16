using Avalonia.Controls;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297; upstream 2.9.0 "Settings save as soon as you change them … RTX HDR and Auto HDR keep their Apply
/// button because they use sliders"): the DX9 HDR, RE Framework and HSF ⚙s on the shared layout — the DX9 HDR preset is
/// written the moment it changes (no Apply), a refused write goes back, results show inside the ⚙, and HSF opens its ini
/// and helper log as upstream.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ExtraCogDialogsTests
{
    private static async Task<(ViewModelFixture Fixture, DetectedGame Game)> Dx9GameAsync(string name)
    {
        UiLocalizationService.Apply("en-US");
        var root = Path.Combine(Path.GetTempPath(), "rhi-cog-dx9-" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, name, "bin")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, name, "pfx")).FullName;
        PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var game = new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
            PrefixPath = prefix, Runner = "Proton", Source = "Test", GraphicsApi = GraphicsApi.DirectX9
        };
        var fx = await ViewModelFixture.StartAsync([game]);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(row => row.Game.Name == name).Game;
        return (fx, fx.Vm.SelectedGame!);
    }

    [Fact]
    public async Task Dx9Hdr_ThePresetIsWrittenTheMomentItChanges_WithoutAnApplyButton_AndARefusalGoesBack()
    {
        var (fx, game) = await Dx9GameAsync("Cog Dx9 Game");
        using var _ = fx;
        var bin = game.BinaryDirectory!;
        var conf = Path.Combine(bin, "dxvk.conf");
        File.WriteAllText(conf, DxvkHdrPresets.ConfFor(0));
        var d3d9 = Path.Combine(bin, "d3d9.dll");
        File.WriteAllBytes(d3d9, ReShadeChannelTests.DxvkHdrDll(0x8664));
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DxvkHdr,
            BinaryDirectory = bin, Version = "3.1-HDR-mod-v0.3.4",
            Files =
            [
                new LinuxDeployedFile { Path = d3d9, Sha256 = FileHash.Sha256Hex(d3d9) },
                new LinuxDeployedFile { Path = conf, Sha256 = FileHash.Sha256Hex(conf), AllowUserChanges = true }
            ]
        });

        // Avalonia 12 controls belong to the thread that made them: the ⚙ lives on one UI-like thread, as in the app.
        using var ui = new UiThreadStandIn();
        await ui.RunAsync(async () =>
        {
        var cog = DxvkHdrSettingsDialog.Build(fx.Vm.DxvkHdr, game.Name);
        cog.Prepare();
        Assert.DoesNotContain(CogDialogLayoutTests.All<Button>(cog.Body), b => b.Name == "DxvkHdrApply" || (string?)b.Content == "Apply");
        var preset = CogDialogLayoutTests.All<ComboBox>(cog.Body).Single(c => c.Name == "DxvkHdrPreset");
        var row = CogDialogLayoutTests.All<CogRow>(cog.Body).Single(r => r.Setting == preset);
        Assert.False(string.IsNullOrWhiteSpace(row.Help)); // the explanation sits under the setting
        Assert.Equal(0, preset.SelectedIndex);

        preset.SelectedIndex = 3;
        await WaitAsync(() => cog.StatusText.Length > 0);
        Assert.Equal(DxvkHdrPresets.ConfFor(3), File.ReadAllText(conf)); // written at once, journaled by the service
        Assert.Equal(3, preset.SelectedIndex);
        Assert.Equal(UiLocalizationService.Present(DxvkHdrDeploymentService.PresetSetMessage), cog.StatusText); // the result, inside the ⚙

        cog.ClearStatus();
        using (fx.Vm.Operations.TryBegin("test"))
        {
            preset.SelectedIndex = 1;
            await WaitAsync(() => cog.StatusText.Length > 0);
        }
        Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), cog.StatusText);
        Assert.Equal(3, preset.SelectedIndex); // refused: back to the preset the file holds
        Assert.Equal(DxvkHdrPresets.ConfFor(3), File.ReadAllText(conf));
        return true;
        });
    }

    [Fact]
    public async Task ReFramework_DeletesItsStorageFolder_AndSaysSoInsideTheCog()
    {
        var (fx, game) = await Dx9GameAsync("Cog Ref Game");
        using var _ = fx;
        var cog = ReFrameworkSettingsDialog.Build(fx.Vm.ReFrameworkStorage, game.Name);
        cog.Prepare();
        Assert.Equal("RE Framework Settings", cog.Title);
        var files = Assert.Single(cog.Body.Children.OfType<CogCard>());
        Assert.Equal("Files", files.Header);
        var delete = CogDialogLayoutTests.All<Button>(cog.Body).Single(b => b.Name == "RefStorageDelete");
        Assert.Contains("remove", delete.Classes); // red, as upstream's Danger button
        Assert.False(delete.IsVisible); // nothing to delete: hidden, and the line says why
    }

    [Fact]
    public async Task Hsf_OpensItsIniAndHelperLog_AsUpstream()
    {
        var (fx, game) = await Dx9GameAsync("Cog Hsf Game");
        using var _ = fx;
        var ini = Path.Combine(game.BinaryDirectory!, "HSF.ini");
        File.WriteAllText(ini, "[HSF]\n");
        var settings = new HsfState(true, "1.2", true, HsfRoute.None, false, false, ini);
        var cog = HsfSettingsDialog.Build(fx.Vm, new HsfPresenter(fx.Managed), settings, null);
        cog.Prepare();
        var open = CogDialogLayoutTests.All<Button>(cog.Body).Where(b => b.Classes.Contains("cogOpenFile")).ToArray();
        Assert.Equal(["Open HSF.ini", "Open helper log"], open.Select(b => (string)b.Content!));
        Assert.True(open[0].IsEnabled);
        Assert.Equal(Path.Combine(game.BinaryDirectory!, "HSF_streamline", "hsf_dlssg.log"), ToolTip.GetTip(open[1]));
        Assert.False(open[1].IsEnabled); // no log yet
        Assert.Equal(["This game", "DirectX 12", "Motion", "Files"], cog.Body.Children.OfType<CogCard>().Select(c => c.Header ?? ""));
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done(), "timed out");
    }
}
