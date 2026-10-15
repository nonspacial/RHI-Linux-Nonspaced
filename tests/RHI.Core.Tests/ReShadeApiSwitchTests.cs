using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #92 (upstream PAR-D07): changing the graphics-API override to or from Vulkan while ReShade is installed is refused until
/// ReShade is removed (the dropdown goes back to the saved choice and the status line says why); changes within DirectX
/// stay free.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ReShadeApiSwitchTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task ApiOverride_ToVulkan_WithADllReShade_IsRefusedAndReverted(string language)
    {
        var (fx, game) = await StartAsync("Dll ReShade Game", language, layer: false);
        using var _ = fx;
        try
        {
            if (language == "pt-BR") UiLocalizationService.Apply("pt-BR");
            var lines = StatusLines(fx);
            fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.For(GraphicsApi.Vulkan);

            Assert.Null(game.UserGraphicsApiOverride);
            Assert.Null(fx.Vm.Runtime.SelectedGraphicsApiOverride!.Value);
            // Read the lines the change wrote (the selection analysis' "Ready" line may follow it); a pending one-shot
            // notice (the offline fixture's DLSS manifest) may ride on the line, so the refusal leads it.
            Assert.Contains(lines, line => line.StartsWith(UiLocalizationService.Present(ReShadeVulkanLayer.ApiSwitchDllInstalledMessage), StringComparison.Ordinal));
            if (language == "pt-BR") Assert.Contains(lines, line => line.StartsWith("O ReShade está instalado", StringComparison.Ordinal));

            // Within DirectX nothing is refused.
            fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.For(GraphicsApi.DirectX11);
            Assert.Equal(GraphicsApi.DirectX11, game.UserGraphicsApiOverride);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    [Fact]
    public async Task ApiOverride_AwayFromVulkan_WithTheVulkanLayer_IsRefused()
    {
        var (fx, game) = await StartAsync("Layer Game", "en-US", layer: true);
        using var _ = fx;

        var lines = StatusLines(fx);
        fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.For(GraphicsApi.DirectX12);

        Assert.Null(game.UserGraphicsApiOverride);
        Assert.Contains(lines, line => line.StartsWith(ReShadeVulkanLayer.ApiSwitchLayerInstalledMessage, StringComparison.Ordinal));

        // Saying Vulkan explicitly keeps it Vulkan: allowed.
        fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.For(GraphicsApi.Vulkan);
        Assert.Equal(GraphicsApi.Vulkan, game.UserGraphicsApiOverride);
    }

    private static System.Collections.Concurrent.ConcurrentQueue<string> StatusLines(ViewModelFixture fx)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText);
        };
        return lines;
    }

    private static async Task<(ViewModelFixture Fixture, DetectedGame Game)> StartAsync(string name, string language, bool layer)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-api-switch-{Guid.NewGuid():N}");
        var bin = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix, Runner = "Proton", Source = "Heroic",
            GraphicsApi = layer ? GraphicsApi.Vulkan : GraphicsApi.DirectX12,
            GraphicsApis = [layer ? GraphicsApi.Vulkan : GraphicsApi.DirectX12],
            GraphicsApiConfidence = GraphicsApiConfidence.High
        };
        var fx = await ViewModelFixture.StartAsync([game], language);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        var selected = fx.Vm.SelectedGame!;
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(selected), GameName = name, Component = ManagedComponentKind.ReShade, BinaryDirectory = bin,
            Files = [new LinuxDeployedFile { Path = Path.Combine(bin, layer ? "vulkan-1.dll" : "dxgi.dll"), Sha256 = "00" }]
        };
        if (layer) record.Metadata[ReShadeVulkanLayer.MetaPath] = ReShadeVulkanLayer.PathValue;
        fx.Records.Upsert(record);
        return (fx, selected);
    }
}
