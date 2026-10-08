using System.Diagnostics;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #24: the prefix tools (winetricks/protontricks) run Wine, whose wineserver rewrites user.reg when it exits a few seconds
// later. RHI refuses to start while the game runs, and sets its override only once that wineserver has gone.
public sealed class D3DCompilerToolRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-d3dtool-reg-" + Guid.NewGuid().ToString("N"));

    public D3DCompilerToolRegistryTests() => Directory.CreateDirectory(_root);

    private DetectedGame Game()
    {
        var bin = Path.Combine(_root, "game");
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        // What a successful d3dcompiler_47 verb leaves: Microsoft's compiler in system32.
        var compiler = Path.Combine(prefix, "drive_c", "windows", "system32", "d3dcompiler_47.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(compiler)!);
        PeArchitectureDetectorTests.WritePe(compiler, 0x8664, 1_100_000);
        using (var stream = new FileStream(compiler, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 512;
            stream.Write(System.Text.Encoding.ASCII.GetBytes("Microsoft Corporation Microsoft (R) HLSL Shader Compiler 10.1 D3DCompiler_47"));
        }
        return new DetectedGame
        {
            Name = "Tool Test", InstallPath = _root, Source = "Manual", Runner = "Wine",
            PrefixPath = prefix, ExecutablePath = exe, BinaryDirectory = bin
        };
    }

    private static ProcessStartInfo StandInTool() => new("/bin/sh")
    {
        ArgumentList = { "-c", "exit 0" },
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static string? Override(DetectedGame game)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), "d3dcompiler_47", "Game.exe");

    [Fact]
    public async Task WhileTheGameRuns_TheToolIsNotStarted()
    {
        var game = Game();
        var installer = new D3DCompilerToolInstaller(new WineDllOverrideService(new WineRegistryEditor(_ => true)));

        var result = await installer.InstallWithWinetricksAsync(game);

        // Without the check this tried to start winetricks (missing here: "was not found or could not be started").
        Assert.False(result.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + game.PrefixPath, result.Message);
    }

    [Fact]
    public async Task TheToolsWineserverLingering_IsWaitedFor_ThenTheOverrideIsSet()
    {
        var game = Game();
        var probes = 0;
        // Idle before the tool; the tool's wineserver is still there for the next three looks, then exits.
        var editor = new WineRegistryEditor(_ => Interlocked.Increment(ref probes) is > 1 and <= 4);
        var installer = new D3DCompilerToolInstaller(new WineDllOverrideService(editor)) { WineServerExitWait = TimeSpan.FromSeconds(30) };

        var result = await installer.RunAndVerifyAsync(game, StandInTool(), "winetricks", applyOverride: true, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(probes > 4);
        Assert.Equal("native,builtin", Override(game));
    }

    [Fact]
    public async Task TheToolsWineserverNotGoingAway_LeavesTheRegistryAloneAndSaysSo()
    {
        var game = Game();
        var probes = 0;
        var editor = new WineRegistryEditor(_ => Interlocked.Increment(ref probes) > 1);
        var installer = new D3DCompilerToolInstaller(new WineDllOverrideService(editor)) { WineServerExitWait = TimeSpan.FromMilliseconds(600) };

        var result = await installer.RunAndVerifyAsync(game, StandInTool(), "winetricks", applyOverride: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(D3DCompilerToolInstaller.OverrideNotSetWineRunningPrefix + game.PrefixPath, result.Message);
        Assert.Null(Override(game));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
