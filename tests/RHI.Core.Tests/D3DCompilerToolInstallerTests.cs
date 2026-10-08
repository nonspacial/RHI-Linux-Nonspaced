using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class D3DCompilerToolInstallerTests
{
    [Fact]
    public void ProtontricksInvocation_IsArgumentListBasedAndTargetsSteamApp()
    {
        var psi = D3DCompilerToolInstaller.BuildProtontricksStartInfo(3826220);

        Assert.Equal("protontricks", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "3826220", "d3dcompiler_47" }, psi.ArgumentList.ToArray());
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }

    [Fact]
    public void WinetricksInvocation_ScopesSystemWineToSelectedPrefix()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "prefix with spaces");
        var psi = D3DCompilerToolInstaller.BuildWinetricksStartInfo(prefix);

        Assert.Equal("winetricks", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "-q", "d3dcompiler_47" }, psi.ArgumentList.ToArray());
        Assert.Equal(Path.GetFullPath(prefix), psi.Environment["WINEPREFIX"]);
    }

    [Fact]
    public void WinetricksInvocation_BindsLauncherManagedWineBinaryAndServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "runner with spaces");
        var prefix = Path.Combine(root, "prefix");
        var wine = Path.Combine(root, "files", "bin", "wine");
        var wineserver = Path.Combine(root, "files", "bin", "wineserver");

        var psi = D3DCompilerToolInstaller.BuildWinetricksStartInfo(prefix, wine, wineserver);

        Assert.Equal(Path.GetFullPath(prefix), psi.Environment["WINEPREFIX"]);
        Assert.Equal(Path.GetFullPath(wine), psi.Environment["WINE"]);
        Assert.Equal(Path.GetFullPath(wineserver), psi.Environment["WINESERVER"]);
        Assert.StartsWith(Path.GetDirectoryName(Path.GetFullPath(wine))!, psi.Environment["PATH"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherManagedPrefix_RecognizesHeroicAndLutrisButNotStandaloneWine()
    {
        Assert.True(D3DCompilerToolInstaller.IsLauncherManagedPrefix(new RHI.Core.Models.DetectedGame { Source = "Heroic/Epic" }));
        Assert.True(D3DCompilerToolInstaller.IsLauncherManagedPrefix(new RHI.Core.Models.DetectedGame { Source = "Lutris" }));
        Assert.False(D3DCompilerToolInstaller.IsLauncherManagedPrefix(new RHI.Core.Models.DetectedGame { Source = "Manual", Runner = "Wine" }));
    }
    [Fact]
    public void WinetricksEligibility_ManualWineGameWithInitializedPrefixIsFirstClass()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "rhi-manual-wine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefix);
        try
        {
            var manual = new RHI.Core.Models.DetectedGame
            {
                Source = "Manual",
                Runner = "Manual Wine",
                PrefixPath = prefix
            };
            var steam = new RHI.Core.Models.DetectedGame
            {
                Source = "Steam",
                Runner = "Proton",
                PrefixPath = prefix,
                SteamAppId = 1
            };
            var native = new RHI.Core.Models.DetectedGame
            {
                Source = "Manual",
                Runner = "Native Linux",
                PrefixPath = prefix
            };

            Assert.True(D3DCompilerToolInstaller.CanInstallWithWinetricks(manual));
            Assert.False(D3DCompilerToolInstaller.CanInstallWithWinetricks(steam));
            Assert.False(D3DCompilerToolInstaller.CanInstallWithWinetricks(native));
        }
        finally
        {
            Directory.Delete(prefix, true);
        }
    }

    // w2/vmsplit review finding 1: the helper tool writes into the prefix as soon as it starts, so a Cancel after that
    // point is refused and RHI waits for it; it used to report "cancelled" and leave the tool running behind an idle UI.
    [Fact]
    public async Task ToolRun_CancelAfterItStarted_WaitsForTheToolToFinish()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-d3dcompiler-cancel-" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        var marker = Path.Combine(root, "tool-finished");
        try
        {
            var exe = Path.Combine(bin, "Game.exe");
            PeArchitectureDetectorTests.WritePe(exe, 0x8664);
            var game = new RHI.Core.Models.DetectedGame { Name = "Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix };
            var tool = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            tool.ArgumentList.Add("-c");
            tool.ArgumentList.Add($"sleep 0.5; : > '{marker}'");
            var installer = new D3DCompilerToolInstaller(new RHI.Platform.Linux.Deployment.WineDllOverrideService());

            var run = await CancelAtWritePhase.RunAsync(ct => installer.RunAndVerifyAsync(game, tool, "fake-tool", applyOverride: false, ct), [bin]);

            Assert.True(File.Exists(marker), "RHI returned before the tool it started had finished.");
            Assert.DoesNotContain("cancelled", run.Result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
