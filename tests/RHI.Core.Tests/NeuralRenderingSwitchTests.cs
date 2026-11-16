using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class NeuralRenderingSwitchTests
{
    [Fact]
    public void RecommendationContract_IsPublicForDesktopAssembly()
    {
        var method = typeof(LinuxManagedComponentService).GetMethod(
            nameof(LinuxManagedComponentService.RecommendedNeuralRenderingMethod),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
    }

    [Fact]
    public void ShortFuseSwitch_CleansFeederAndBridgeOnly()
    {
        var conflicts = LinuxManagedComponentService.NeuralRenderingConflictsFor(ManagedComponentKind.ShortFuse);
        Assert.Equal(new[] { ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge, ManagedComponentKind.GitcUplift }, conflicts.ToArray());
        Assert.DoesNotContain(ManagedComponentKind.ReShade, conflicts);
    }

    [Fact]
    public void FeederSwitch_CleansShortFuseAndBridgeOnly()
    {
        var conflicts = LinuxManagedComponentService.NeuralRenderingConflictsFor(ManagedComponentKind.Dlss5Feeder);
        Assert.Equal(new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Dx11Bridge, ManagedComponentKind.GitcUplift }, conflicts.ToArray());
    }

    [Fact]
    public void BridgeSwitch_CleansShortFuseAndFeederOnly()
    {
        var conflicts = LinuxManagedComponentService.NeuralRenderingConflictsFor(ManagedComponentKind.Dlss5Dx11Bridge);
        Assert.Equal(new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.GitcUplift }, conflicts.ToArray());
    }

    [Fact]
    public void UpliftSwitch_CleansTheThreeOtherMethods()
    {
        // w14/nr (#274): one Neural Rendering method at a time; installing Uplift removes RHI's other method first (upstream).
        var conflicts = LinuxManagedComponentService.NeuralRenderingConflictsFor(ManagedComponentKind.GitcUplift);
        Assert.Equal(new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge }, conflicts.ToArray());
    }

    [Fact]
    public void NonNeuralRenderingComponent_HasNoMethodSwitchCleanup()
    {
        Assert.Empty(LinuxManagedComponentService.NeuralRenderingConflictsFor(ManagedComponentKind.ReShade));
    }

    [Fact]
    public void Recommendation_NativeDlssDx12DefaultsToShortFuse()
    {
        var game = new DetectedGame { ForcedArchitecture = PeArchitecture.X64, GraphicsApi = GraphicsApi.DirectX12 };
        Assert.Equal(ManagedComponentKind.ShortFuse, LinuxManagedComponentService.RecommendedNeuralRenderingMethod(game, hasNativeDlss: true));
    }

    [Fact]
    public void Recommendation_NativeDlssDx11DefaultsToBridge()
    {
        var game = new DetectedGame { ForcedArchitecture = PeArchitecture.X64, GraphicsApi = GraphicsApi.DirectX11 };
        Assert.Equal(ManagedComponentKind.Dlss5Dx11Bridge, LinuxManagedComponentService.RecommendedNeuralRenderingMethod(game, hasNativeDlss: true));
    }

    // w14/nr (#274, upstream 18088a3): GITC Uplift only where DLSS5 Feeder was the default and all of Uplift works.
    [Theory]
    [InlineData(GraphicsApi.DirectX11)]
    [InlineData(GraphicsApi.DirectX12)]
    [InlineData(GraphicsApi.Vulkan)]
    public void Recommendation_NoNativeDlss64BitDx11Dx12Vulkan_IsGitcUplift(GraphicsApi api)
    {
        var game = new DetectedGame { ForcedArchitecture = PeArchitecture.X64, GraphicsApi = api };
        Assert.Equal(ManagedComponentKind.GitcUplift, LinuxManagedComponentService.RecommendedNeuralRenderingMethod(game, hasNativeDlss: false));
    }

    [Theory]
    [InlineData(PeArchitecture.X64, GraphicsApi.OpenGL)]
    [InlineData(PeArchitecture.X64, GraphicsApi.DirectX9)]
    [InlineData(PeArchitecture.X86, GraphicsApi.DirectX11)]
    public void Recommendation_NoNativeDlssElsewhere_StaysFeeder(PeArchitecture architecture, GraphicsApi api)
    {
        var game = new DetectedGame { ForcedArchitecture = architecture, GraphicsApi = api };
        Assert.Equal(ManagedComponentKind.Dlss5Feeder, LinuxManagedComponentService.RecommendedNeuralRenderingMethod(game, hasNativeDlss: false));
    }

    [Fact]
    public void Recommendation_Forced32BitDefaultsToFeederBeforeApi()
    {
        var game = new DetectedGame { ForcedArchitecture = PeArchitecture.X86, GraphicsApi = GraphicsApi.DirectX12 };
        Assert.Equal(ManagedComponentKind.Dlss5Feeder, LinuxManagedComponentService.RecommendedNeuralRenderingMethod(game, hasNativeDlss: true));
    }
    [Fact]
    public void NativeDlssInference_ShortFuseCreatedDlssRemainsNoNative()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.ShortFuse,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = "/game/nvngx_dlss.dll",
                    ExistedBeforeInstall = false
                }
            ]
        };

        var hasNative = LinuxManagedComponentService.InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow: true, shortFuseRecord: record, feederRecord: null);

        Assert.False(hasNative);
    }

    [Fact]
    public void NativeDlssInference_ShortFuseReplacedNativeDlssRemainsNative()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.ShortFuse,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = "/game/nvngx_dlss.dll",
                    ExistedBeforeInstall = true
                }
            ]
        };

        var hasNative = LinuxManagedComponentService.InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow: true, shortFuseRecord: record, feederRecord: null);

        Assert.True(hasNative);
    }

    [Fact]
    public void NativeDlssInference_FeederCreatedDlssRemainsNoNative()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.Dlss5Feeder,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = "/game/nvngx_dlss.dll",
                    ExistedBeforeInstall = false
                }
            ]
        };

        var hasNative = LinuxManagedComponentService.InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow: true, shortFuseRecord: null, feederRecord: record);

        Assert.False(hasNative);
    }

    [Fact]
    public void NativeDlssInference_UntrackedDlssDuringManagedMethodFailsSafeAsNative()
    {
        var record = new LinuxDeploymentRecord
        {
            Component = ManagedComponentKind.ShortFuse,
            Files =
            [
                new LinuxDeployedFile
                {
                    Path = "/game/renodx-dlss.addon64",
                    ExistedBeforeInstall = false
                }
            ]
        };

        var hasNative = LinuxManagedComponentService.InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow: true, shortFuseRecord: record, feederRecord: null);

        Assert.True(hasNative);
    }

}
