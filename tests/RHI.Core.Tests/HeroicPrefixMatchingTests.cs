using RHI.Core.Models;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class HeroicPrefixMatchingTests
{
    private static DetectedGame Game(string name, string? prefix = null, string runner = "Wine/Proton (Heroic)")
        => new() { Name = name, InstallPath = "/games/" + name, Source = "Heroic/Epic", Runner = runner, PrefixPath = prefix };

    private static string Prefix(DetectionTestTree tree, params string[] parts)
    {
        tree.Dir([.. parts, "drive_c"]);
        return tree.Path_(parts);
    }

    [Fact]
    public void LookAlikeFolders_AreNotAttached()
    {
        using var tree = new DetectionTestTree();
        Prefix(tree, "Prefixes", "Heads");
        Prefix(tree, "Prefixes", "Hades II");
        Prefix(tree, "Prefixes", "Bimbo");
        var hades = Game("Hades");
        var limbo = Game("Limbo");
        var tunic = Game("Tunic");

        HeroicGameDetector.AttachFallbackPrefixes([hades, limbo, tunic], [tree.Path_("Prefixes")]);

        Assert.All(new[] { hades, limbo, tunic }, game => Assert.Null(game.PrefixPath));
    }

    [Fact]
    public void ExactNormalisedName_IsAttached_AndAPfxSubfolderIsUsed()
    {
        using var tree = new DetectionTestTree();
        var hades = Prefix(tree, "Prefixes", "Hades");
        Prefix(tree, "Prefixes", "Hades II");
        var witcher = Prefix(tree, "Prefixes", "The Witcher 3 Wild Hunt", "pfx");
        var games = new[] { Game("HADES"), Game("The Witcher 3: Wild Hunt"), Game("Hades II") };

        HeroicGameDetector.AttachFallbackPrefixes(games, [tree.Path_("Prefixes")]);

        Assert.Equal(hades, games[0].PrefixPath);
        Assert.Equal(witcher, games[1].PrefixPath);
        Assert.Equal(tree.Path_("Prefixes", "Hades II"), games[2].PrefixPath);
    }

    [Fact]
    public void Ambiguity_AttachesNothing()
    {
        using var tree = new DetectionTestTree();
        Prefix(tree, "a", "Celeste");
        Prefix(tree, "b", "Celeste");
        var twinA = Game("Doom");
        var twinB = Game("DOOM");
        Prefix(tree, "a", "Doom");
        var celeste = Game("Celeste");

        HeroicGameDetector.AttachFallbackPrefixes([celeste, twinA, twinB], [tree.Path_("a"), tree.Path_("b")]);

        Assert.Null(celeste.PrefixPath); // two folders fit equally
        Assert.Null(twinA.PrefixPath);   // two games claim one folder equally
        Assert.Null(twinB.PrefixPath);
    }

    [Fact]
    public void NativeGames_AndFoldersAlreadyInUse_AreLeftAlone()
    {
        using var tree = new DetectionTestTree();
        var used = Prefix(tree, "Prefixes", "Stardew Valley");
        Prefix(tree, "Prefixes", "Celeste");
        var configured = Game("Something Else", prefix: used);
        var stardew = Game("Stardew Valley");
        var celeste = Game("Celeste", runner: "Native Linux");

        HeroicGameDetector.AttachFallbackPrefixes([configured, stardew, celeste], [tree.Path_("Prefixes")]);

        Assert.Equal(used, configured.PrefixPath);
        Assert.Null(stardew.PrefixPath);
        Assert.Null(celeste.PrefixPath);
    }

    [Theory]
    [InlineData("hades", "heads", 0.6)]
    [InlineData("hadesii", "hades", 5.0 / 7)]
    [InlineData("limbo", "bimbo", 0.8)]
    [InlineData("same", "same", 1.0)]
    public void Similarity_IsEditDistanceRatio(string a, string b, double expected)
        => Assert.Equal(expected, HeroicGameDetector.Similarity(a, b), 3);

    [Fact]
    public void LinuxPlatformRecord_IsNativeAndGetsNoConfiguredPrefix()
    {
        using var tree = new DetectionTestTree();
        var install = tree.Dir("games", "Celeste");
        var json = tree.File($$"""
            { "installed": [ { "title": "Celeste", "appName": "celeste1", "install_path": "{{install}}", "platform": "linux", "is_installed": true } ] }
            """, "installed.json");

        var game = Assert.Single(HeroicGameDetector.ParseInstalledJsonForTests(json, "gog"));
        Assert.Equal("Native Linux", game.Runner);

        HeroicGameDetector.ApplyConfiguredPrefixJsonForTests($$"""{ "celeste1": { "winePrefix": "{{tree.Dir("pfx")}}" } }""", game);
        Assert.Null(game.PrefixPath);
    }
}
