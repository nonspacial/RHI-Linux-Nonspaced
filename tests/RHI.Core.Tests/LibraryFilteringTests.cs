using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public class LibraryFilteringTests
{
    [Theory]
    [InlineData("Steam Linux Runtime 1.0 (scout)", "SteamLinuxRuntime", true)]
    [InlineData("Steam Linux Runtime 2.0 (soldier)", "SteamLinuxRuntime_soldier", true)]
    [InlineData("Steam Linux Runtime 3.0 (sniper)", "SteamLinuxRuntime_sniper", true)]
    [InlineData("Steamworks Common Redistributables", "Steamworks Shared", true)]
    [InlineData("Proton Experimental", "Proton - Experimental", true)]
    [InlineData("Street Fighter™ 6", "Street Fighter 6", false)]
    [InlineData("Terraria", "Terraria", false)]
    public void SteamToolFilter_ClassifiesConservatively(string name, string dir, bool expected)
        => Assert.Equal(expected, DetectionHelpers.IsKnownSteamTool(name, dir));

    [Theory]
    [InlineData("Windows Media Player", true)]
    [InlineData("Windows NT", true)]
    [InlineData("AntiCheatExpert", true)]
    [InlineData("Bome MIDI Translator Pro", true)]
    [InlineData("World of Warcraft", false)]
    [InlineData("Street Fighter 6", false)]
    public void WineDirectoryFilter_RemovesKnownSupportApps(string name, bool expected)
        => Assert.Equal(expected, DetectionHelpers.IsKnownNonGameWineDirectory(name));

    [Theory]
    [InlineData("/home/user/Games/Heroic/Prefixes/shared", true)]
    [InlineData("/home/user/.local/share/Steam/steamapps/compatdata/1364780/pfx", true)]
    [InlineData("/home/user/Games/World of Warcraft", false)]
    [InlineData("/home/user/.wine", false)]
    public void ManagedPrefixFilter_DoesNotRescanLauncherPrefixes(string path, bool expected)
        => Assert.Equal(expected, DetectionHelpers.IsLauncherManagedPrefix(path));
}
