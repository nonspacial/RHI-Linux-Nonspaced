using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class GraphicsApiDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-api-tests-" + Guid.NewGuid().ToString("N"));

    public GraphicsApiDetectionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Detect_UsesExecutableD3d12Signature()
    {
        var exe = WriteBinary("Game.exe", "MZ....dxgi.dll....D3D12.DLL....");
        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains(GraphicsApi.DirectX12, found.Candidates);
        Assert.Contains("d3d12.dll", found.Evidence.ToLowerInvariant());
    }

    [Fact]
    public void Detect_D3D12CoreAgilityMarkerWinsBeforeMisleadingDx11ExecutableSignature()
    {
        var exe = WriteBinary("AgilityGame.exe", "MZ....d3d11.dll....");
        File.WriteAllBytes(Path.Combine(_root, "D3D12Core.dll"), [0x4D, 0x5A, 0x90, 0x00]);

        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains("D3D12Core.dll", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_D3D12CoreAgilityMarkerInRuntimeSubfolderWinsBeforeMisleadingDx11ExecutableSignature()
    {
        var exe = WriteBinary("AgilitySubfolderGame.exe", "MZ....d3d11.dll....");
        var agilityDir = Path.Combine(_root, "D3D12");
        Directory.CreateDirectory(agilityDir);
        File.WriteAllBytes(Path.Combine(agilityDir, "D3D12Core.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains("D3D12/D3D12Core.dll", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_PrefersDx12WhenExecutableContainsDx11AndDx12()
    {
        var exe = WriteBinary("DualApi.exe", "MZ....d3d11.dll....d3d12.dll....");
        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        // Strings cannot tell which renderer runs: both stay candidates and the pick is not High (Refs #53).
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains(GraphicsApi.DirectX11, found.Candidates);
        Assert.Contains(GraphicsApi.DirectX12, found.Candidates);
    }

    [Fact]
    public void Detect_FindsUtf16VulkanSignature()
    {
        var exe = Path.Combine(_root, "VulkanGame.exe");
        File.WriteAllBytes(exe, Encoding.Unicode.GetBytes("header vulkan-1.dll trailer"));
        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.Vulkan, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
    }

    [Fact]
    public void Detect_IgnoresReShadeProxyAsApiProof()
    {
        var exe = WriteBinary("UnknownGame.exe", "MZ....no renderer import....");
        File.WriteAllText(Path.Combine(_root, "dxgi.dll"), "d3d12.dll d3d11.dll reshade");
        File.WriteAllText(Path.Combine(_root, "ReShade64.dll"), "d3d12.dll d3d11.dll");

        var found = GraphicsApiDetector.Detect(_root, exe);
        Assert.Equal(GraphicsApi.Unknown, found.Primary);
    }

    [Fact]
    public void FindLikelyExecutable_PrefersShippingBinaryOverLauncher()
    {
        var launcher = Path.Combine(_root, "GameLauncher.exe");
        File.WriteAllBytes(launcher, new byte[2_000_000]);
        var shippingDir = Path.Combine(_root, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(shippingDir);
        var shipping = Path.Combine(shippingDir, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(shipping, new byte[500_000]);

        var found = DetectionHelpers.FindLikelyExecutable(_root);
        Assert.Equal(shipping, found);
    }

    [Fact]
    public void ApplyRuntimeMetadata_PopulatesBinaryDirectoryAndEvidence()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "Test.exe");
        File.WriteAllText(exe, "d3d11.dll");
        var game = new DetectedGame { Name = "Test", InstallPath = _root, ExecutablePath = exe };

        DetectionHelpers.ApplyRuntimeMetadata(game);

        Assert.Equal(bin, game.BinaryDirectory);
        Assert.Equal(GraphicsApi.DirectX11, game.GraphicsApi);
        Assert.Equal(GraphicsApiConfidence.High, game.GraphicsApiConfidence);
        Assert.False(string.IsNullOrWhiteSpace(game.GraphicsApiEvidence));
    }


    [Fact]
    public void Detect_UsesReEngineCapabilityConfigWhenExeLoadsRendererDynamically()
    {
        var exe = WriteBinary("StreetFighter6.exe", "MZ....dynamic renderer loader....");
        var config = Path.Combine(_root, "config.ini");
        File.WriteAllText(config, "[Render]\nCapability=DirectX12\n");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains("config.ini", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_UsesUnrealDefaultGraphicsRhiConfig()
    {
        var exe = WriteBinary("Game.exe", "MZ....dynamic renderer loader....");
        var configDir = Path.Combine(_root, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var config = Path.Combine(configDir, "Engine.ini");
        File.WriteAllText(config, "[/Script/WindowsTargetPlatform.WindowsTargetSettings]\nDefaultGraphicsRHI=DefaultGraphicsRHI_DX12\n");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
    }

    [Fact]
    public void Detect_DoesNotTrustAmbiguousConfigOptionList()
    {
        var exe = WriteBinary("Game.exe", "MZ....dynamic renderer loader....");
        var config = Path.Combine(_root, "graphics.ini");
        File.WriteAllText(config, "GraphicsApiOptions=DX11,DX12\n");
        var index = GameFileIndexer.Build(_root);

        var found = GraphicsApiDetector.Detect(_root, exe, index);

        Assert.Equal(GraphicsApi.Unknown, found.Primary);
    }

    [Fact]
    public void Detect_UnityBootConfigUsesExplicitRendererBeforeStaticCapabilityStrings()
    {
        var exe = WriteBinary("UnityGame.exe", "MZ....d3d11.dll....d3d12.dll....");
        var data = Path.Combine(_root, "UnityGame_Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "boot.config"), "gfx-device-type=17\n");

        var found = GraphicsApiDetector.Detect(_root, exe, GameFileIndexer.Build(_root));

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains("boot.config", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_LegacyUnityDataWithoutBootConfigDefaultsToDx11OnWindows()
    {
        var exe = WriteBinary("OldUnity.exe", "MZ....renderer selected dynamically....");
        Directory.CreateDirectory(Path.Combine(_root, "OldUnity_Data"));

        var found = GraphicsApiDetector.Detect(_root, exe, GameFileIndexer.Build(_root));

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.Contains("Unity", found.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_UsesStrongD3d12SymbolWithoutDllName()
    {
        var exe = WriteBinary("DynamicGame.exe", "MZ....D3D12CreateDevice....");
        var found = GraphicsApiDetector.Detect(_root, exe, GameFileIndexer.Build(_root));

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
    }

    private string WriteBinary(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
