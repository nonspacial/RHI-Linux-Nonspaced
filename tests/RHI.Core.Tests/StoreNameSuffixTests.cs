using System.Text;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-b (#305): store platform suffixes stripped for name lookups (Spotlight artwork, PCGamingWiki data). Upstream
/// 2.9.3's StoreNameSuffixTests (b5c4ede, efac81a, 7548a8e), ported as they are.
/// </summary>
public class StoreNameSuffixTests
{
    [Theory]
    [InlineData("Banishers: Ghosts of New Eden - Windows", "Banishers: Ghosts of New Eden")]
    [InlineData("Some Game (Windows)", "Some Game")]
    [InlineData("Some Game (Windows 10)", "Some Game")]
    [InlineData("Some Game (Win10)", "Some Game")]
    [InlineData("Some Game (PC)", "Some Game")]
    [InlineData("Some Game [PC]", "Some Game")]
    [InlineData("Goat Simulator 3 PC", "Goat Simulator 3")]
    [InlineData("Some Game for Windows", "Some Game")]
    [InlineData("Some Game for Windows 10", "Some Game")]
    [InlineData("Some Game – PC", "Some Game")]
    [InlineData("Some Game Windows 10", "Some Game")]
    [InlineData("Some Game (Microsoft Store Edition)", "Some Game")]
    [InlineData("NBA 2K27 (Windows PC)", "NBA 2K27")]
    [InlineData("Dead by Daylight Windows", "Dead by Daylight")]
    [InlineData("SUPERHOT WINDOWS 10", "SUPERHOT")]
    [InlineData("Some Game Windows PC", "Some Game")]
    public void StripPlatform_RemovesTrailingPlatform(string name, string expected) =>
        Assert.Equal(expected, StoreNameSuffix.StripPlatform(name));

    [Theory]
    [InlineData("Banishers: Ghosts of New Eden")]
    [InlineData("Windows")]                         // nothing left: keep
    [InlineData("Microsoft Flight Simulator 2024")]
    [InlineData("Half-Life 2")]
    [InlineData("Windows 98 Simulator")]            // platform word not at the end
    [InlineData("PC Building Simulator")]
    [InlineData("Arc Pc")]                          // bare "PC" only in capitals
    [InlineData("")]
    [InlineData(null)]
    public void StripPlatform_LeavesOtherNames(string? name) =>
        Assert.Null(StoreNameSuffix.StripPlatform(name));

    [Theory]
    [InlineData("Hela: Of Mice & Magic Demo", "Hela: Of Mice & Magic")]
    [InlineData("Some Game (Demo)", "Some Game")]
    [InlineData("Some Game - Demo (PC)", "Some Game")]
    [InlineData("Some Game Playtest", "Some Game")]
    public void StripTrial_RemovesDemoAndPlaytest(string name, string expected) =>
        Assert.Equal(expected, StoreNameSuffix.StripTrial(name));

    [Theory]
    [InlineData("Demonologist")]
    [InlineData("Some Game")]
    public void StripTrial_LeavesOtherNames(string name) =>
        Assert.Null(StoreNameSuffix.StripTrial(name));

    [Theory]
    [InlineData("Dishonored Definitive Edition", "Dishonored")]
    [InlineData("NBA 2K26 Standard Edition", "NBA 2K26")]
    [InlineData("Some Game - Game of the Year Edition", "Some Game")]
    [InlineData("Some Game: Deluxe Edition", "Some Game")]
    public void StripEdition_RemovesEditionName(string name, string expected) =>
        Assert.Equal(expected, StoreNameSuffix.StripEdition(name));

    [Theory]
    [InlineData("Some Game")]
    [InlineData("Definitive Edition")]   // nothing left: keep
    [InlineData("Special Edition Racer")]
    public void StripEdition_LeavesOtherNames(string name) =>
        Assert.Null(StoreNameSuffix.StripEdition(name));

    [Fact]
    public void PcgwLookup_FindsMicrosoftStoreNames()
    {
        var titles = new[] { "Dishonored", "Mafia: Definitive Edition", "NBA 2K26", "Resident Evil 3 (2020)",
                             "Resident Evil 3: Nemesis", "Prey (2006)", "Prey (2017)", "Dead by Daylight" };
        var json = "{\"games\":{" + string.Join(",", titles.Select(t => $"\"{t}\":{{\"steam_appid\":0}}")) + "}}";
        var data = PcgwCentralData.Parse(Encoding.UTF8.GetBytes(json))!;

        Assert.Equal("Dishonored", data.TryLookup("Dishonored® Definitive Edition (PC)", null)?.PageTitle);
        Assert.Equal("Mafia: Definitive Edition", data.TryLookup("Mafia: Definitive Edition (Windows PC)", null)?.PageTitle);
        Assert.Equal("NBA 2K26", data.TryLookup("NBA 2K26 Standard Edition (Windows PC)", null)?.PageTitle);
        Assert.Equal("Resident Evil 3 (2020)", data.TryLookup("RESIDENT EVIL 3 for Windows", null)?.PageTitle);
        Assert.Equal("Dead by Daylight", data.TryLookup("Dead by Daylight Windows", null)?.PageTitle);
        Assert.Null(data.TryLookup("Prey (Windows)", null)?.PageTitle); // two years: no guess
    }
}
