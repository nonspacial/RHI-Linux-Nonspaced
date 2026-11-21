using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class NexusReShadeSearchPathsTests
{
    [Fact]
    public void NexusShaderPath_UsesTheStablePayloadPathInsteadOfTheMaterializedLaunchChain()
    {
        var binary = Path.Combine(Path.GetTempPath(), "rhi nexus payload", "bin");
        var game = new DetectedGame
        {
            Runner = "Nexus Launcher",
            PrefixPath = Path.Combine(Path.GetTempPath(), "rhi nexus prefix"),
            BinaryDirectory = binary
        };

        var paths = NexusReShadeSearchPaths.ForGame(game, [@".\reshade-shaders\RHI\Lilium\Shaders\**"]);

        var expected = "Z:" + Path.GetFullPath(Path.Combine(binary, "reshade-shaders", "RHI", "Lilium", "Shaders", "**")).Replace('/', '\\');
        Assert.Equal(expected, Assert.Single(paths));
    }

    [Fact]
    public void NonNexusShaderPath_StaysRelative()
    {
        var game = new DetectedGame { Runner = "Proton", BinaryDirectory = Path.GetTempPath() };
        var paths = NexusReShadeSearchPaths.ForGame(game, [@".\reshade-shaders\RHI\Lilium\Shaders\**"]);

        Assert.Equal(@".\reshade-shaders\RHI\Lilium\Shaders\**", Assert.Single(paths));
    }
}
