using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #158 RTX Encore in the app: its row is hidden where it cannot apply, says it is not confirmed on Linux, other Neural
/// Rendering owners refuse over its runtime, and its ⚙ dialog saves the name and the Neural Rendering choice (refused
/// choices go back).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RtxEncoreAppTests
{
    private static DetectedGame EligibleGame(ViewModelFixture fx, string name, GraphicsApi api = GraphicsApi.DirectX12, bool dlss = true)
    {
        var bin = Path.Combine(fx.Root, "games", name, "bin");
        var prefix = Path.Combine(fx.Root, "games", name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        File.WriteAllBytes(Path.Combine(bin, "Game.exe"), RtxEncoreDeploymentServiceTests.Pe(0x11, 64 * 1024));
        if (dlss) File.WriteAllBytes(Path.Combine(bin, "nvngx_dlss.dll"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        return new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
            PrefixPath = prefix, Runner = "Wine", Source = "Test", GraphicsApi = api
        };
    }

    private static IReadOnlyList<ManagedComponent> Decorated(ViewModelFixture fx, DetectedGame game)
    {
        var files = Directory.EnumerateFiles(game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(game, files);
        fx.Managed.Decorate(game, components);
        return components;
    }

    private static ManagedComponent Row(IReadOnlyList<ManagedComponent> components, ManagedComponentKind kind) => components.Single(c => c.Kind == kind);

    private static void OwnRuntime(ViewModelFixture fx, DetectedGame game, ManagedComponentKind kind) => fx.Records.Upsert(new LinuxDeploymentRecord
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
        Version = "1.0", Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll"), Sha256 = "00" }]
    });

    [Fact]
    public async Task EligibleGame_ShowsTheRow_InstallableWithTheNotConfirmedNote()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var row = Row(Decorated(fx, EligibleGame(fx, "Eligible")), ManagedComponentKind.RtxEncore);

        Assert.Equal(ComponentAvailability.Available, row.Availability);
        Assert.True(row.ActionAllowed, row.Note);
        Assert.Equal(RtxEncoreDeploymentService.NotYetConfirmedNote, row.Note);
        Assert.True(ComponentRowViewModel.IsShown(row));
        Assert.True(new ComponentRowViewModel(row, null!).HasRtxEncoreSettings);
    }

    [Theory]
    [InlineData(GraphicsApi.DirectX9, true)]
    [InlineData(GraphicsApi.OpenGL, true)]
    [InlineData(GraphicsApi.DirectX12, false)]
    public async Task WhereItCannotApply_TheRowIsHidden(GraphicsApi api, bool dlss)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var row = Row(Decorated(fx, EligibleGame(fx, "Hidden", api, dlss)), ManagedComponentKind.RtxEncore);

        Assert.Equal(ComponentAvailability.Unsupported, row.Availability);
        Assert.False(ComponentRowViewModel.IsShown(row));
    }

    [Fact]
    public async Task RhisOwnInstall_StaysListedAndRemovable_EvenWhereItCouldNotBeInstalledNow()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = EligibleGame(fx, "Moved on", GraphicsApi.DirectX9);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RtxEncore,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0.0-rc.3", Metadata = new() { ["RtxEncore.Name"] = "version.dll" },
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "version.dll"), Sha256 = "00" }]
        });

        var row = Row(Decorated(fx, game), ManagedComponentKind.RtxEncore);

        Assert.True(ComponentRowViewModel.IsShown(row));
        Assert.True(row.ActionAllowed, row.Note);
        Assert.True(new ComponentRowViewModel(row, null!).HasRtxEncoreSettings);
    }

    [Fact]
    public async Task AnRtxEncoreRhiDidNotInstall_StaysListed_ProtectedAndWithoutSettings()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = EligibleGame(fx, "External", GraphicsApi.DirectX9);
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), RtxEncoreIdentityTests.RtxEncoreDll());
        var components = Decorated(fx, game);
        var row = Row(components, ManagedComponentKind.RtxEncore);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.False(row.ActionAllowed);
        Assert.True(ComponentRowViewModel.IsShown(row));
        Assert.False(new ComponentRowViewModel(row, null!).HasRtxEncoreSettings);
        Assert.NotEqual(ComponentAvailability.Installed, Row(components, ManagedComponentKind.ReShade).Availability);
    }

    [Fact]
    public async Task RtxEncoresRuntime_BlocksShortFuseAndFeeder_InTheRowAndAtInstall_BeforeReShadeIsInstalled()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = EligibleGame(fx, "Owned");
        OwnRuntime(fx, game, ManagedComponentKind.RtxEncore);
        var components = Decorated(fx, game);

        foreach (var kind in new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder })
        {
            var row = Row(components, kind);
            Assert.False(row.ActionAllowed);
            Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("RTX Encore"), row.Note);
            var install = await fx.Managed.InstallAsync(game, kind, []);
            Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("RTX Encore"), install.Message);
        }
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task AnotherOwner_BlocksRtxEncoreWithItsNeuralRenderingChoice()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = EligibleGame(fx, "Other owner");
        fx.Managed.ReShadeDllNames.SetRtxEncoreNeuralRendering(game, true);
        OwnRuntime(fx, game, ManagedComponentKind.ShortFuse);

        var row = Row(Decorated(fx, game), ManagedComponentKind.RtxEncore);

        Assert.False(row.ActionAllowed);
        Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("ShortFuse"), row.Note);
    }

    [Fact]
    public async Task Dialog_SavesTheName_AndTheNeuralRenderingChoice()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Alpha Quest");
        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.RtxEncore);

        Assert.Equal(RtxEncoreDeploymentService.DisplayName, dialog.Title);
        Assert.True(dialog.HasNeuralRendering);
        Assert.False(dialog.ShowsProxyAsText);
        Assert.Equal("version.dll", dialog.SelectedProxy);
        Assert.DoesNotContain("binkw64.dll", dialog.ProxyOptions);

        dialog.SelectedProxy = "winmm.dll";
        dialog.NeuralRenderingChosen = true;
        await dialog.PendingNeuralRendering!;

        Assert.Equal("winmm.dll", fx.Vm.RtxEncoreChosenName());
        Assert.True(fx.Vm.RtxEncoreNeuralRenderingChosen());
        Assert.Equal(UiLocalizationService.Present(RtxEncoreDeploymentService.SavedWithNeuralRenderingMessage), dialog.Message);
    }

    [Fact]
    public async Task Dialog_ARefusedNeuralRenderingChoice_GoesBack()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddAsync(fx, "Beta Quest");
        OwnRuntime(fx, game, ManagedComponentKind.ShortFuse);
        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.RtxEncore);

        dialog.NeuralRenderingChosen = true;
        await dialog.PendingNeuralRendering!;

        Assert.False(dialog.NeuralRenderingChosen);
        Assert.False(fx.Vm.RtxEncoreNeuralRenderingChosen());
        Assert.Equal(UiLocalizationService.Present(NeuralRenderingRuntimeOwnership.RefusalFor("ShortFuse")), dialog.Message);
        Assert.True(dialog.CanChangeNeuralRendering);
    }

    [Theory]
    [InlineData(ProxyComponentDialogKind.Dlssg2030)]
    [InlineData(ProxyComponentDialogKind.DlssEnabler)]
    [InlineData(ProxyComponentDialogKind.UniversalMfg)]
    public async Task OtherDialogs_HaveNoNeuralRenderingChoice(ProxyComponentDialogKind kind)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Gamma Quest");
        using var dialog = new ProxyComponentDialogViewModel(fx.Vm, kind);

        Assert.False(dialog.HasNeuralRendering);
        Assert.False(dialog.ShowsProxyAsText);
    }

    [Fact]
    public async Task Dialog_InPortuguese_IsTranslated()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddAsync(fx, "Delta Quest");
        UiLocalizationService.Apply("pt-BR");
        try
        {
            using var dialog = new ProxyComponentDialogViewModel(fx.Vm, ProxyComponentDialogKind.RtxEncore);
            Assert.Equal("Nome do arquivo", dialog.ProxyLabel);
            Assert.StartsWith("Ainda não confirmado no Linux", dialog.FooterNote);
            Assert.StartsWith("Instalar também", dialog.NeuralRenderingLabel);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static async Task<DetectedGame> AddAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        return fx.Vm.SelectedGame!;
    }
}
