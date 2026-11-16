using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>
/// #294 (upstream 2.9.2 <c>93d0fcb</c>, 2.9.3 <c>870f4a9</c>, <c>2984022</c>): helper libraries and exes are never API
/// evidence, a GOG uninstaller is never the game exe, and crash reporters are never the game.
/// </summary>
public sealed class DetectionFixes294Tests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-det294-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    private string[] Index() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories);

    // OpenMW (OpenGL) ships avutil, Qt6Gui and SDL2, which name D3D11/D3D12 for video decoding and windowing.
    [Theory]
    [InlineData("avutil-58.dll")]
    [InlineData("avcodec-60.dll")]
    [InlineData("swscale-7.dll")]
    [InlineData("Qt6Gui.dll")]
    [InlineData("Qt5Core.dll")]
    [InlineData("SDL2.dll")]
    [InlineData("SDL3.dll")]
    [InlineData("libcef.dll")]
    [InlineData("libEGL.dll")]
    [InlineData("libGLESv2.dll")]
    [InlineData("d3dcompiler_47.dll")]
    [InlineData("gitc-uplift-helper64.exe")]
    [InlineData("host64/dlss5-feed-host64.exe")]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("CrashReportClient.exe")]
    public void HelperBinaries_AreNotNearbyApiEvidence(string helper)
    {
        var exe = Write("openmw.exe", "MZ....no renderer import....");
        Write(helper, "MZ....d3d12.dll....D3D12CreateDevice....dxgi.dll....");

        var found = GraphicsApiDetector.Detect(_root, exe, Index());

        Assert.NotEqual(GraphicsApi.DirectX12, found.Primary);
        Assert.DoesNotContain(GraphicsApi.DirectX12, found.Candidates);
    }

    [Fact]
    public void AnEngineLibrary_StillCountsAsNearbyEvidence()
    {
        var exe = Write("Game.exe", "MZ....no renderer import....");
        Write("GameEngine.dll", "MZ....d3d12.dll....D3D12CreateDevice....");

        Assert.Equal(GraphicsApi.DirectX12, GraphicsApiDetector.Detect(_root, exe, Index()).Primary);
    }

    // Ghost Song (GOG): unins000.exe (32-bit, ~3 MB) is bigger than the Unity game's exe (~650 KB).
    [Fact]
    public void GogUninstaller_IsNeverTheGameExe_EvenWhenItIsTheBiggest()
    {
        var uninstaller = Path.Combine(_root, "unins000.exe");
        PeArchitectureDetectorTests.WritePe(uninstaller, 0x014C, size: 3_000_000);
        var game = Path.Combine(_root, "Ghost Song.exe");
        PeArchitectureDetectorTests.WritePe(game, 0x8664, size: 650_000);
        PeArchitectureDetectorTests.WritePe(Path.Combine(_root, "UnityCrashHandler64.exe"), 0x8664);

        Assert.Equal(game, DetectionHelpers.FindLikelyExecutable(_root));
    }

    // Phantom Blade Zero and other Unreal games from Steam or Epic: the crash reporter is never the game, so it is
    // never the exe RHI launches, installs beside or watches (the running probe matches the chosen exe's path exactly).
    [Fact]
    public void UnrealCrashReporter_IsNeverTheGameExe()
    {
        var reporter = Path.Combine(_root, "Engine", "Binaries", "Win64", "CrashReportClient.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(reporter)!);
        PeArchitectureDetectorTests.WritePe(reporter, 0x8664, size: 4_000_000);
        var game = Path.Combine(_root, "PBZ", "Binaries", "Win64", "PBZ-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        PeArchitectureDetectorTests.WritePe(game, 0x8664, size: 900_000);

        Assert.Equal(game, DetectionHelpers.FindLikelyExecutable(_root));
    }
}
