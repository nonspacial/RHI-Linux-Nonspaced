using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

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

    internal static string Manifest(int appId, string name, string installDir, string stateFlags = "4")
        => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"{stateFlags}\"\n\t\"installdir\"\t\t\"{installDir}\"\n}}\n";

    [Fact]
    public void SymlinkedRootsAndLibraryAliases_AreWalkedOnce()
    {
        using var tree = new DetectionTestTree();
        var home = tree.Dir("home");
        var steam = tree.Dir("home", ".local", "share", "Steam");
        tree.Link(steam, "home", ".steam", "steam");
        var extra = tree.Dir("disk", "SteamLibrary");
        tree.Link(extra, "home", "lib-alias");
        tree.File(Manifest(10, "Main Game", "Main"), "home", ".local", "share", "Steam", "steamapps", "appmanifest_10.acf");
        tree.Exe("home", ".local", "share", "Steam", "steamapps", "common", "Main", "Main.exe");
        tree.File(Manifest(20, "Extra Game", "Extra"), "disk", "SteamLibrary", "steamapps", "appmanifest_20.acf");
        tree.Exe("disk", "SteamLibrary", "steamapps", "common", "Extra", "Extra.exe");
        // The vdf lists the root's own library by the symlinked spelling and the extra library twice.
        tree.File($$"""
                  "libraryfolders"
                  {
                    "0" { "path" "{{Path.Combine(home, ".steam", "steam")}}" }
                    "1" { "path" "{{extra}}" }
                    "2" { "path" "{{Path.Combine(home, "lib-alias")}}" }
                  }
                  """, "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");

        var games = SteamGameDetector.DetectIn(
            LinuxPaths.SteamRootCandidates(home, Path.Combine(home, ".local", "share")), CancellationToken.None);

        Assert.Equal(["Extra Game", "Main Game"], games.Select(g => g.Name).Order(StringComparer.Ordinal));
        // The first spelling seen is kept, so existing library keys do not move.
        Assert.Equal(Path.Combine(steam, "steamapps", "common", "Main"), games.Single(g => g.SteamAppId == 10).InstallPath);
        Assert.Equal(Path.Combine(extra, "steamapps", "common", "Extra"), games.Single(g => g.SteamAppId == 20).InstallPath);
    }

    [Fact]
    public void DistinctExisting_KeepsFirstSpellingAndDropsMissing()
    {
        using var tree = new DetectionTestTree();
        var real = tree.Dir("real");
        var alias = tree.Link(real, "alias");
        var other = tree.Dir("other");

        Assert.Equal([alias, other], LinuxPaths.DistinctExisting([alias, real + "/", tree.Path_("missing"), other, real]));
    }
}
