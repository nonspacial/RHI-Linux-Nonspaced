using System.Text.Json;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class HeroicGameDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-heroic-tests-" + Guid.NewGuid().ToString("N"));

    public HeroicGameDetectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LegendaryCandidates_IncludeNativeStandaloneConfig()
    {
        var home = "/home/tester";
        var config = "/home/tester/.config";

        var candidates = LinuxPaths.HeroicLegendaryRootCandidates(home, config).ToArray();

        Assert.Contains("/home/tester/.config/legendary", candidates);
        Assert.Contains("/home/tester/.config/heroic/legendaryConfig/legendary", candidates);
    }

    [Fact]
    public void LegendaryCandidates_IncludeBothFlatpakLayouts()
    {
        var home = "/home/tester";
        var config = "/home/tester/.config";

        var candidates = LinuxPaths.HeroicLegendaryRootCandidates(home, config).ToArray();

        Assert.Contains("/home/tester/.var/app/com.heroicgameslauncher.hgl/config/legendary", candidates);
        Assert.Contains("/home/tester/.var/app/com.heroicgameslauncher.hgl/config/heroic/legendaryConfig/legendary", candidates);
    }


    [Fact]
    public void HeroicMetadataCandidates_IncludeCurrentSideloadLibraryPath()
    {
        var root = "/home/tester/.config/heroic";

        var candidates = HeroicGameDetector.HeroicMetadataFileCandidatesForTests(root);

        Assert.Contains("/home/tester/.config/heroic/sideload_apps/library.json", candidates);
        Assert.Contains("/home/tester/.config/heroic/store/sideload_apps/library.json", candidates);
    }

    [Fact]
    public void SideloadLibraryEntry_UsesFolderNameAndHeroicAppId()
    {
        var gameDir = Path.Combine(_root, "World of Warcraft");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "World of Warcraft Launcher.exe");
        File.WriteAllText(exe, "MZ");

        var json = Path.Combine(_root, "library.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            games = new[]
            {
                new
                {
                    runner = "sideload",
                    app_name = "gThWioiWZWd4EuJcaf9g6T",
                    title = "World of Warcraft",
                    folder_name = gameDir,
                    is_installed = true,
                    install = new
                    {
                        executable = exe,
                        platform = "Windows",
                        is_dlc = false
                    }
                }
            }
        }));

        var games = HeroicGameDetector.ParseInstalledJsonForTests(json, "sideload");

        var game = Assert.Single(games);
        Assert.Equal("World of Warcraft", game.Name);
        Assert.Equal(Path.GetFullPath(gameDir), game.InstallPath);
        Assert.Equal("Heroic/Sideload", game.Source);
        Assert.Null(game.EpicAppName);
        Assert.Equal("gThWioiWZWd4EuJcaf9g6T", game.HeroicAppName);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Contains("appName=gThWioiWZWd4EuJcaf9g6T", game.LaunchCommand, StringComparison.Ordinal);
        Assert.Contains("runner=sideload", game.LaunchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void SideloadLibraryEntry_CanDeriveFolderFromAbsoluteExecutable()
    {
        var gameDir = Path.Combine(_root, "Custom Game");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "CustomGame.exe");
        File.WriteAllText(exe, "MZ");

        var json = Path.Combine(_root, "library.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            games = new[]
            {
                new
                {
                    runner = "sideload",
                    app_name = "custom-app",
                    title = "Custom Game",
                    is_installed = true,
                    install = new { executable = exe, platform = "Windows" }
                }
            }
        }));

        var games = HeroicGameDetector.ParseInstalledJsonForTests(json, "sideload");

        var game = Assert.Single(games);
        Assert.Equal(Path.GetFullPath(gameDir), game.InstallPath);
        Assert.Equal(exe, game.ExecutablePath);
    }


    [Fact]
    public void SideloadGameConfig_AttachesExactWinePrefixByHeroicAppId()
    {
        var prefix = Path.Combine(_root, "Prefixes", "World of Warcraft");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        var game = new RHI.Core.Models.DetectedGame
        {
            Name = "World of Warcraft",
            Source = "Heroic/Sideload",
            HeroicAppName = "gThWioiWZWd4EuJcaf9g6T"
        };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gThWioiWZWd4EuJcaf9g6T"] = new { winePrefix = prefix },
            ["version"] = "v0.1"
        });

        HeroicGameDetector.ApplyConfiguredPrefixJsonForTests(json, game);

        Assert.Equal(prefix, game.PrefixPath);
    }

    [Fact]
    public void LegendaryInstalledJson_ParsesEpicGameAndExecutable()
    {
        var gameDir = Path.Combine(_root, "Black Myth - Wukong");
        Directory.CreateDirectory(gameDir);
        var exe = Path.Combine(gameDir, "b1.exe");
        File.WriteAllText(exe, "MZ");

        var json = Path.Combine(_root, "installed.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["wukong-app"] = new
            {
                app_name = "wukong-app",
                executable = "b1.exe",
                install_path = gameDir,
                platform = "Windows",
                title = "Black Myth: Wukong",
                version = "1.0"
            }
        }));

        var games = HeroicGameDetector.ParseInstalledJsonForTests(json, "legendary");

        var game = Assert.Single(games);
        Assert.Equal("Black Myth: Wukong", game.Name);
        Assert.Equal(Path.GetFullPath(gameDir), game.InstallPath);
        Assert.Equal("Heroic/Epic", game.Source);
        Assert.Equal("wukong-app", game.EpicAppName);
        Assert.Equal(exe, game.ExecutablePath);
        Assert.Contains("runner=legendary", game.LaunchCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroicNestedLibraryEntry_UsesNestedInstallPath()
    {
        var gameDir = Path.Combine(_root, "Nested Game");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "Game.exe"), "MZ");

        var json = Path.Combine(_root, "library.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            games = new[]
            {
                new
                {
                    runner = "legendary",
                    app_name = "nested-app",
                    title = "Nested Game",
                    is_installed = true,
                    install = new
                    {
                        install_path = gameDir,
                        executable = "Game.exe",
                        platform = "Windows"
                    }
                }
            }
        }));

        var games = HeroicGameDetector.ParseInstalledJsonForTests(json, "legendary");

        var game = Assert.Single(games);
        Assert.Equal("Heroic/Epic", game.Source);
        Assert.Equal(Path.Combine(gameDir, "Game.exe"), game.ExecutablePath);
    }

    [Fact]
    public void HeroicNestedLibraryEntry_IgnoresExplicitlyUninstalledGame()
    {
        var gameDir = Path.Combine(_root, "Old Game");
        Directory.CreateDirectory(gameDir);

        var json = Path.Combine(_root, "library.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            games = new[]
            {
                new
                {
                    runner = "gog",
                    app_name = "old-game",
                    title = "Old Game",
                    is_installed = false,
                    install = new { install_path = gameDir }
                }
            }
        }));

        var games = HeroicGameDetector.ParseInstalledJsonForTests(json, "gog");

        Assert.Empty(games);
    }


    [Fact]
    public void HeroicGameConfig_ParsesActiveWineRunnerMetadata()
    {
        var prefix = Path.Combine(_root, "wine-prefix");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        var runner = Path.Combine(_root, "Wine-GE", "bin");
        Directory.CreateDirectory(runner);
        var wine = Path.Combine(runner, "wine");
        var wineserver = Path.Combine(runner, "wineserver");
        File.WriteAllText(wine, string.Empty);
        File.WriteAllText(wineserver, string.Empty);

        var game = new RHI.Core.Models.DetectedGame
        {
            Name = "Heroic Wine Game",
            Source = "Heroic/GOG",
            HeroicAppName = "heroic-wine"
        };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["heroic-wine"] = new
            {
                winePrefix = prefix,
                wineVersion = new { type = "wine", name = "Wine-GE-Proton8-26", bin = wine, wineserver }
            }
        });

        HeroicGameDetector.ApplyConfiguredPrefixJsonForTests(json, game);

        Assert.Equal(prefix, game.PrefixPath);
        Assert.Equal("wine", game.CompatibilityLayerType);
        Assert.Equal("Wine-GE-Proton8-26", game.CompatibilityTool);
        Assert.Equal(wine, game.WineBinaryPath);
        Assert.Equal(wineserver, game.WineServerPath);
        Assert.Equal("Heroic Wine", game.Runner);
    }

    [Fact]
    public void HeroicGameConfig_ParsesProtonAndUsesEmbeddedWineBinary()
    {
        var compatRoot = Path.Combine(_root, "GE-Proton10-20");
        var embedded = Path.Combine(compatRoot, "files", "bin");
        Directory.CreateDirectory(embedded);
        var proton = Path.Combine(compatRoot, "proton");
        var wine = Path.Combine(embedded, "wine");
        var wineserver = Path.Combine(embedded, "wineserver");
        File.WriteAllText(proton, string.Empty);
        File.WriteAllText(wine, string.Empty);
        File.WriteAllText(wineserver, string.Empty);

        var compatData = Path.Combine(_root, "heroic-proton-prefix");
        var pfx = Path.Combine(compatData, "pfx");
        Directory.CreateDirectory(Path.Combine(pfx, "drive_c"));

        var game = new RHI.Core.Models.DetectedGame
        {
            Name = "Heroic Proton Game",
            Source = "Heroic/Epic",
            HeroicAppName = "heroic-proton"
        };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["heroic-proton"] = new
            {
                winePrefix = compatData,
                wineVersion = new { type = "proton", name = "GE-Proton10-20", bin = proton }
            }
        });

        HeroicGameDetector.ApplyConfiguredPrefixJsonForTests(json, game);

        Assert.Equal(pfx, game.PrefixPath);
        Assert.Equal("proton", game.CompatibilityLayerType);
        Assert.Equal("GE-Proton10-20", game.CompatibilityTool);
        Assert.Equal(wine, game.WineBinaryPath);
        Assert.Equal(wineserver, game.WineServerPath);
        Assert.Equal("Heroic Proton", game.Runner);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
