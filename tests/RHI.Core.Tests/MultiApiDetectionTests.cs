using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

/// <summary>Executables that reference several renderers (Refs #53).</summary>
public sealed class MultiApiDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-multiapi-" + Guid.NewGuid().ToString("N"));

    public MultiApiDetectionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExecutableReferencingAllFiveApis_IsNotReportedAtHighConfidence()
    {
        var exe = Write("Borderlands3.exe", "MZ d3d12.dll d3d11.dll d3d9.dll vulkan-1.dll opengl32.dll");

        var found = GraphicsApiDetector.Detect(_root, exe, GameFileIndexer.Build(_root));

        Assert.NotEqual(GraphicsApiConfidence.High, found.Confidence);
        Assert.Equal(GraphicsApiConfidence.Low, found.Confidence);
        Assert.Equal(5, found.Candidates.Count);
        Assert.Equal(GraphicsApi.DirectX12, found.Primary); // ReShade keeps a usable proxy until the user overrides
    }

    [Fact]
    public void PrefixSettings_DecideBetweenReferencedRenderers()
    {
        var gameRoot = Path.Combine(_root, "Red Dead Redemption 2");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "RDR2.exe");
        File.WriteAllText(exe, "MZ d3d12.dll d3d11.dll d3d9.dll vulkan-1.dll");
        var prefix = Path.Combine(_root, "pfx");
        var settings = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "Rockstar Games", "Red Dead Redemption 2", "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "system.xml"), "<rage__fwuiSystemSettingsCollection>\n  <graphics>\n    <API>kSettingAPI_Vulkan</API>\n  </graphics>\n</rage__fwuiSystemSettingsCollection>\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal(GraphicsApi.Vulkan, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains("system.xml", found.Evidence);
        Assert.Equal(4, found.Candidates.Count);
    }

    [Fact]
    public void PrefixSettingsOfAnotherGame_AreIgnored()
    {
        var gameRoot = Path.Combine(_root, "Some Game");
        Directory.CreateDirectory(gameRoot);
        var exe = Path.Combine(gameRoot, "SomeGame.exe");
        File.WriteAllText(exe, "MZ d3d12.dll d3d11.dll");
        var settings = Path.Combine(_root, "pfx", "drive_c", "users", "u", "Documents", "Rockstar Games", "Red Dead Redemption 2", "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "system.xml"), "<API>kSettingAPI_Vulkan</API>\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), Path.Combine(_root, "pfx"));

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
    }

    [Fact]
    public void ImportTable_UsesUpstreamsRule_DelayLoadsDoNotOutrankAnImportedDx11()
    {
        // UE4 shape: d3d11 imported, d3d12 delay-loaded and only used with -dx12.
        var exe = WritePe("UE4Game.exe", ["kernel32.dll", "dxgi.dll", "d3d11.dll"], ["d3d12.dll"]);

        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.Equal(GraphicsApiConfidence.High, found.Confidence);
        Assert.Contains(GraphicsApi.DirectX12, found.Candidates);
        Assert.StartsWith("Executable imports:", found.Evidence);
    }

    [Fact]
    public void ImportTable_DxgiOnlyMeansDx12_AndTwoImportedRenderersLowerConfidence()
    {
        var dxgiOnly = GraphicsApiDetector.Detect(_root, WritePe("Dx12Game.exe", ["dxgi.dll"], []));
        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.High), (dxgiOnly.Primary, dxgiOnly.Confidence));

        var dual = GraphicsApiDetector.Detect(_root, WritePe("DualGame.exe", ["d3d11.dll", "vulkan-1.dll"], []));
        Assert.Equal(GraphicsApi.Vulkan, dual.Primary); // upstream priority: DX12 > Vulkan > DX11
        Assert.Equal(GraphicsApiConfidence.Medium, dual.Confidence);
        Assert.Equal([GraphicsApi.Vulkan, GraphicsApi.DirectX11], dual.Candidates);
    }

    [Fact]
    public void ImportTable_DelayLoadCountsWhenNoDx11PlusImport()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("DelayGame.exe", ["d3d9.dll"], ["d3d12.dll"]));

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Contains("delay-loads", found.Evidence);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    /// <summary>Minimal PE32+ with one section holding the import and delay-load descriptors and names.</summary>
    private string WritePe(string name, string[] imports, string[] delayImports)
    {
        const int sectionRva = 0x1000, sectionRaw = 0x200, optional = 0x58, optionalSize = 240;
        var data = new List<byte>();
        var importDescriptors = (imports.Length + 1) * 20;
        var delayDescriptors = (delayImports.Length + 1) * 32;
        var namesStart = importDescriptors + delayDescriptors;
        var names = new List<byte>();
        var importNameRvas = imports.Select(dll => AddName(dll)).ToArray();
        var delayNameRvas = delayImports.Select(dll => AddName(dll)).ToArray();
        int AddName(string dll)
        {
            var rva = sectionRva + namesStart + names.Count;
            names.AddRange(Encoding.ASCII.GetBytes(dll));
            names.Add(0);
            return rva;
        }
        var section = new byte[namesStart + names.Count];
        for (var i = 0; i < imports.Length; i++) BitConverter.GetBytes(importNameRvas[i]).CopyTo(section, i * 20 + 12);
        for (var i = 0; i < delayImports.Length; i++)
        {
            BitConverter.GetBytes(1).CopyTo(section, importDescriptors + i * 32); // RVA-based attributes
            BitConverter.GetBytes(delayNameRvas[i]).CopyTo(section, importDescriptors + i * 32 + 4);
        }
        names.CopyTo(section, namesStart);

        var file = new byte[sectionRaw + section.Length];
        file[0] = (byte)'M'; file[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(file, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, 0x40);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(file, 0x44);
        BitConverter.GetBytes((ushort)1).CopyTo(file, 0x46);
        BitConverter.GetBytes((ushort)optionalSize).CopyTo(file, 0x54);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(file, optional);
        BitConverter.GetBytes(sectionRva).CopyTo(file, optional + 112 + 8);          // import directory
        BitConverter.GetBytes(importDescriptors).CopyTo(file, optional + 112 + 12);
        if (delayImports.Length > 0)
        {
            BitConverter.GetBytes(sectionRva + importDescriptors).CopyTo(file, optional + 112 + 13 * 8);
            BitConverter.GetBytes(delayDescriptors).CopyTo(file, optional + 112 + 13 * 8 + 4);
        }
        var header = optional + optionalSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(file, header);
        BitConverter.GetBytes(section.Length).CopyTo(file, header + 8);
        BitConverter.GetBytes(sectionRva).CopyTo(file, header + 12);
        BitConverter.GetBytes(section.Length).CopyTo(file, header + 16);
        BitConverter.GetBytes(sectionRaw).CopyTo(file, header + 20);
        section.CopyTo(file, sectionRaw);

        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
