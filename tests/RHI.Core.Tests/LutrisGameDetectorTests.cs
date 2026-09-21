using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class LutrisGameDetectorTests
{
    [Fact]
    public void WineSection_DetectsLutrisWineRunner()
    {
        var yaml = """
                   game:
                     exe: /games/Test/Game.exe
                     prefix: /games/Test/prefix
                   wine:
                     version: wine-ge-8-26-x86_64
                   """;

        var runtime = LutrisGameDetector.ParseWineRuntimeForTests(yaml);

        Assert.Equal("wine-ge-8-26-x86_64", runtime.Version);
        Assert.Equal("wine", runtime.LayerType);
    }

    [Fact]
    public void WineSection_DetectsLutrisProtonRunner()
    {
        var yaml = """
                   game:
                     exe: /games/Test/Game.exe
                     prefix: /games/Test/prefix
                   wine:
                     version: GE-Proton10-20
                   """;

        var runtime = LutrisGameDetector.ParseWineRuntimeForTests(yaml);

        Assert.Equal("GE-Proton10-20", runtime.Version);
        Assert.Equal("proton", runtime.LayerType);
    }
}
