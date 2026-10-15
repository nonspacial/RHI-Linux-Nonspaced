using System.Diagnostics;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// A standalone Wine prefix whose runtime markers cannot be read (config_info unreadable or a dangling link, beside a
/// readable <c>version</c> naming a Proton build) is runtime-unknown: RHI cannot tell a Proton-built prefix from a Wine
/// one. Host Wine would run wineboot over a Proton prefix, so no Wine of any kind may run in it — neither a launch,
/// nor the "Install D3DCompiler via Winetricks" button, nor the automatic D3DCompiler step of a ReShade shader-pack or
/// DLSS5 Feeder install (w2-launch re-review, major 1). Play stays visible but disabled with the reason and the way
/// out (Edit game → launch command) in its ~2 s tooltip (minor 2).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RuntimeUnknownPrefixTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-runtime-unknown-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("dangling")]
    [InlineData("unreadable")]
    public async Task RuntimeUnknownPrefix_NoWineRunningRoute_EverStartsAHelperTool(string marker)
    {
        var (game, prefix) = await ScanRuntimeUnknownGameAsync(marker);
        var started = new List<ProcessStartInfo>();
        var tools = new D3DCompilerToolInstaller(new WineDllOverrideService())
        {
            RunTool = (startInfo, _, _) =>
            {
                started.Add(startInfo);
                return Task.FromResult(new ExternalProcessResult(1, "", "stand-in tool"));
            }
        };

        Assert.True(D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game));
        Assert.False(D3DCompilerToolInstaller.CanInstallWithWinetricks(game));
        Assert.True(game.HasConfirmedPatchTarget); // still moddable: only the Wine-running routes are refused

        var expected = D3DCompilerToolInstaller.RuntimeUnknownMessagePrefix + prefix;
        // The "Install D3DCompiler via Winetricks" button, directly and through the managed service it is wired to.
        Assert.Equal(expected, (await tools.InstallWithWinetricksAsync(game)).Message);
        var managed = Managed(tools);
        Assert.Equal(expected, (await managed.InstallD3DCompilerWithWinetricksAsync(game)).Message);
        // The automatic dependency step (ReShade shader packs, DLSS5 Feeder) refuses before the component installer.
        Assert.Equal(expected, (await tools.InstallDependencyWithWinetricksAsync(game)).Message);
        var shaderPacks = await managed.ApplyReShadeAsync(game, new ReShadeInstallOptions { ShaderPackIds = ["standard"] });
        Assert.False(shaderPacks.Success);
        Assert.EndsWith(expected, shaderPacks.Message, StringComparison.Ordinal);

        Assert.Empty(started);
        Assert.EndsWith("Prefixo: " + prefix, UiLocalizationService.PresentFor("pt-BR", expected), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task RuntimeUnknownCard_PlayIsDisabled_AndItsTooltipSaysToSetALaunchCommandInEditGame(string language)
    {
        var (game, _) = await ScanRuntimeUnknownGameAsync("dangling");
        var plain = PlainGameWithoutCommand();
        UiLocalizationService.Apply(language);
        try
        {
            using var fx = await ViewModelFixture.StartAsync([game, plain], language);

            await SelectAsync(fx, game.Name);
            Assert.False(fx.Vm.CanLaunch);
            Assert.False(fx.Vm.LaunchCommand.CanExecute(null));
            Assert.Equal(UiLocalizationService.Text("Vm.LaunchRuntimeUnknownSetCommandInEdit"), fx.Vm.LaunchToolTip);
            Assert.False(fx.Vm.ReShadeProfile.CanInstallD3DCompilerWithWinetricks);
            Assert.Equal(UiLocalizationService.Text("Vm.WinetricksUnavailableRuntimeUnknown"), fx.Vm.ReShadeProfile.D3DCompilerToolHint);

            await SelectAsync(fx, plain.Name);
            Assert.False(fx.Vm.CanLaunch);
            Assert.Equal(UiLocalizationService.Text("Vm.NoLaunchCommandSetOneInEdit"), fx.Vm.LaunchToolTip);
            Assert.Contains(language == "pt-BR" ? "Editar jogo" : "Edit game", fx.Vm.LaunchToolTip, StringComparison.Ordinal);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    private async Task<(DetectedGame Game, string Prefix)> ScanRuntimeUnknownGameAsync(string marker)
    {
        var home = Path.Combine(_root, "home-" + marker);
        var prefix = Path.Combine(home, "Games", "proton-built");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "windows", "system32"));
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "");
        File.WriteAllText(Path.Combine(prefix, "version"), "GE-Proton10-32\n");
        var exe = Path.Combine(prefix, "drive_c", "Program Files", "Game", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var configInfo = Path.Combine(prefix, "config_info");
        if (marker == "dangling")
            File.CreateSymbolicLink(configInfo, Path.Combine(_root, "gone", "config_info"));
        else
        {
            File.WriteAllText(configInfo, "GE-Proton10-32\n/opt/ge/files/share/fonts/\n");
            File.SetUnixFileMode(configInfo, UnixFileMode.None);
        }

        var scan = await new GameLibraryScanner([new WinePrefixDetector(home, [])]).ScanWithReportAsync(null);
        var game = Assert.Single(scan.Games);
        Assert.Null(game.LaunchCommand);
        Assert.Null(game.WineBinaryPath);
        return (game, prefix);
    }

    private DetectedGame PlainGameWithoutCommand()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "No Command Quest")).FullName;
        var exe = Path.Combine(dir, "Quest.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = "No Command Quest", Source = "Manual", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            Runner = "Manual Wine", LaunchCommand = null
        };
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        for (var i = 0; i < 200 && fx.Vm.IsSelectionProbeBusy; i++)
            await Task.Delay(25);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
    }

    private LinuxManagedComponentService Managed(D3DCompilerToolInstaller tools)
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "deployments.json"));
        // The refused routes reach only the record store and the D3DCompiler installer: a null service here would turn
        // a route that got past its refusal into a NullReferenceException instead of the refusal asserted above.
        return new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: tools,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "config_info", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try { if (new FileInfo(file).LinkTarget is null) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (IOException) { }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
