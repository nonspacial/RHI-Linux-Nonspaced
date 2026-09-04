using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public class SteamGameDetectorTests
{
    [Theory]
    [InlineData("appmanifest_1903340.acf", 1903340)]
    [InlineData("appmanifest_1364780.acf", 1364780)]
    public void ExtractAppId_ValidName_ReturnsId(string name, int expected)
        => Assert.Equal(expected, SteamGameDetector.ExtractAppIdFromAcfFilename(name));

    [Theory]
    [InlineData("foo.acf")]
    [InlineData("appmanifest_nope.acf")]
    [InlineData("appmanifest_.acf")]
    public void ExtractAppId_InvalidName_ReturnsNull(string name)
        => Assert.Null(SteamGameDetector.ExtractAppIdFromAcfFilename(name));
}
