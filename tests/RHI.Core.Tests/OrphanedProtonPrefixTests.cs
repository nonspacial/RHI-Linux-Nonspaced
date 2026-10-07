using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class OrphanedProtonPrefixTests
{
    private static string Prefix(DetectionTestTree tree, string fontsLine)
    {
        var prefix = tree.Dir("prefix");
        tree.Dir("prefix", "drive_c");
        tree.File($"GE-Proton9-1\n{fontsLine}\n", "prefix", "config_info");
        return prefix;
    }

    private static string FakeWine(DetectionTestTree tree, params string[] parts)
    {
        var path = tree.File("#!/bin/sh\n", parts);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void ConfigInfoPointingOutsideAProtonBuild_IsNotTreatedAsProtonsWine()
    {
        using var tree = new DetectionTestTree();
        // Like config_info line 2 = /usr/share/fonts/ resolving to /usr/bin/wine.
        FakeWine(tree, "usr", "bin", "wine");
        var prefix = Prefix(tree, tree.Path_("usr", "share", "fonts") + "/");

        var runtime = WinePrefixDetector.PrefixRuntime.Read(prefix, _ => null);

        Assert.True(runtime.IsProton);
        Assert.Null(runtime.WineBinary);
    }

    [Theory]
    [InlineData("files", "proton")]
    [InlineData("dist", "version")]
    public void RealProtonBuild_IsUsed(string distFolder, string marker)
    {
        using var tree = new DetectionTestTree();
        var wine = FakeWine(tree, "GE-Proton9-1", distFolder, "bin", "wine");
        tree.File("", "GE-Proton9-1", distFolder, "bin", "wineserver");
        tree.File("", "GE-Proton9-1", marker);
        var prefix = Prefix(tree, tree.Path_("GE-Proton9-1", distFolder, "share", "fonts") + "/");

        var runtime = WinePrefixDetector.PrefixRuntime.Read(prefix, _ => null);

        Assert.Equal(wine, runtime.WineBinary);
        Assert.NotNull(runtime.WineServer);
    }

    [Fact]
    public void TheHostWine_IsNeverTakenForProtons()
    {
        using var tree = new DetectionTestTree();
        var wine = FakeWine(tree, "GE-Proton9-1", "files", "bin", "wine");
        tree.File("", "GE-Proton9-1", "proton");
        var prefix = Prefix(tree, tree.Path_("GE-Proton9-1", "files", "share", "fonts"));

        var runtime = WinePrefixDetector.PrefixRuntime.Read(prefix, name => name == "wine" ? wine : null);

        Assert.Null(runtime.WineBinary);
    }

    [Fact]
    public async Task Winetricks_IsRefusedOnAPrefixWhoseProtonIsGone()
    {
        using var tree = new DetectionTestTree();
        var orphan = new DetectedGame
        {
            Name = "Battle.net", Source = "Lutris", InstallPath = tree.Dir("game"), PrefixPath = tree.Dir("pfx", "drive_c")[..^"/drive_c".Length],
            CompatibilityLayerType = "proton", CompatibilityTool = "GE-Proton10-29", WineBinaryPath = null,
            ExecutablePath = tree.Exe("game", "Game.exe")
        };
        var plainWine = new DetectedGame
        {
            Name = "Old", Source = "Wine", InstallPath = orphan.InstallPath, PrefixPath = orphan.PrefixPath, CompatibilityLayerType = null
        };

        Assert.True(D3DCompilerToolInstaller.IsOrphanedProtonPrefix(orphan));
        Assert.False(D3DCompilerToolInstaller.CanInstallWithWinetricks(orphan));
        Assert.True(D3DCompilerToolInstaller.CanInstallWithWinetricks(plainWine));

        var result = await new D3DCompilerToolInstaller(new WineDllOverrideService()).InstallWithWinetricksAsync(orphan);
        Assert.False(result.Success);
        Assert.EndsWith("Missing Proton: GE-Proton10-29", result.Message);
        var portuguese = UiLocalizationService.PresentFor("pt-BR", result.Message);
        Assert.EndsWith("Proton ausente: GE-Proton10-29", portuguese);
    }
}
