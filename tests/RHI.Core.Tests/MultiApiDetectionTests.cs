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
    public void ImportTable_DelayLoadedDx12_KeepsTheImportedDx11_AtMedium()
    {
        // UE4 shape: d3d11 imported, d3d12 delay-loaded and only used with -dx12. Two renderers are in
        // play, so the imports guess (upstream's rule) and leave the decision open to config files.
        var exe = WritePe("UE4Game.exe", ["kernel32.dll", "dxgi.dll", "d3d11.dll"], ["d3d12.dll"]);

        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains(GraphicsApi.DirectX12, found.Candidates);
        Assert.StartsWith("Executable imports:", found.Evidence);
    }

    [Fact]
    public void ImportTable_OneImportedRendererAndNoOtherReference_IsHigh()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("Dx11Game.exe", ["kernel32.dll", "dxgi.dll", "d3d11.dll"], []));

        Assert.Equal((GraphicsApi.DirectX11, GraphicsApiConfidence.High), (found.Primary, found.Confidence));
        Assert.Equal([GraphicsApi.DirectX11], found.Candidates);
    }

    [Fact]
    public void ImportTable_OneImportedRendererButAnotherReferenced_IsMedium()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("Dx11Game.exe", ["dxgi.dll", "d3d11.dll"], [], "vulkan-1.dll vkCreateInstance"));

        Assert.Equal((GraphicsApi.DirectX11, GraphicsApiConfidence.Medium), (found.Primary, found.Confidence));
        Assert.Equal([GraphicsApi.DirectX11, GraphicsApi.Vulkan], found.Candidates);
    }

    [Fact]
    public void ImportTable_DxgiOnlyIsAnInferredDx12_AndTwoImportedRenderersStayDirectXFirst()
    {
        var dxgiOnly = GraphicsApiDetector.Detect(_root, WritePe("Dx12Game.exe", ["dxgi.dll"], []));
        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (dxgiOnly.Primary, dxgiOnly.Confidence));

        // No ReShade Vulkan proxy on Linux: DirectX-first keeps installs working (Robbie's #53 decision).
        var dual = GraphicsApiDetector.Detect(_root, WritePe("DualGame.exe", ["d3d11.dll", "vulkan-1.dll"], []));
        Assert.Equal(GraphicsApi.DirectX11, dual.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, dual.Confidence);
        Assert.Equal([GraphicsApi.DirectX11, GraphicsApi.Vulkan], dual.Candidates);
    }

    [Fact]
    public void ImportTable_DelayLoadCountsWhenNoDx11PlusImport()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("DelayGame.exe", ["d3d9.dll"], ["d3d12.dll"]));

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.Contains("delay-loads", found.Evidence);
    }

    [Fact]
    public void Rdr2_RealImportTable_PrefixSystemXmlSelectsVulkan_ElseDx12Medium()
    {
        // RDR2.exe imports d3d9.dll beside its FSR2 DX12/Vulkan back ends and NvLowLatencyVk, no dxgi.dll, and
        // names its renderers only past the first 32 MiB. A lone d3d9 import must not decide (it gave DX9 High).
        var (gameRoot, exe, prefix) = Rdr2Layout();

        var withoutSettings = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);
        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (withoutSettings.Primary, withoutSettings.Confidence));
        Assert.Equal([GraphicsApi.DirectX12, GraphicsApi.DirectX11, GraphicsApi.DirectX9, GraphicsApi.Vulkan], withoutSettings.Candidates);
        var noPrefix = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot));
        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (noPrefix.Primary, noPrefix.Confidence));
        // The evidence names the helper that implies DX12, never a d3d12.dll import RDR2 does not have (final
        // detect-deploy review, nit 4).
        Assert.StartsWith("Executable imports: ffx_fsr2_api_dx12", noPrefix.Evidence);
        Assert.Contains(", a helper for d3d12.dll (renderer helper imports", noPrefix.Evidence);
        Assert.DoesNotContain("Executable imports: d3d12.dll", noPrefix.Evidence);

        var settings = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "Rockstar Games", "Red Dead Redemption 2", "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "system.xml"), Rdr2SystemXml);

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal(GraphicsApi.Vulkan, found.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, found.Confidence);
        Assert.Contains("system.xml", found.Evidence);
        Assert.Equal(4, found.Candidates.Count);
    }

    [Fact]
    public void LoneImport_ContradictedByARendererNamePast32MiB_IsMedium()
    {
        // The scan covers the whole executable, not a 32 MiB prefix.
        var exe = WritePe("LateNames.exe", ["kernel32.dll", "d3d9.dll"], [], "d3d12.dll D3D12CreateDevice", extraOffset: 40L * 1024 * 1024);

        var found = GraphicsApiDetector.Detect(_root, exe);

        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (found.Primary, found.Confidence));
        Assert.Equal([GraphicsApi.DirectX12, GraphicsApi.DirectX9], found.Candidates);
    }

    [Fact]
    public void LoneImport_WithNothingElseInTheFile_StaysHigh()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("Dx9Game.exe", ["kernel32.dll", "d3d9.dll", "d3dx9_43.dll"], [], "", extraOffset: 40L * 1024 * 1024));

        Assert.Equal((GraphicsApi.DirectX9, GraphicsApiConfidence.High), (found.Primary, found.Confidence));
    }

    [Theory]
    [InlineData("ffx_fsr2_api_dx12_x64.dll", GraphicsApi.DirectX12)]
    [InlineData("ffx_fsr2_api_vk_x64.dll", GraphicsApi.Vulkan)]
    [InlineData("NvLowLatencyVk.dll", GraphicsApi.Vulkan)]
    [InlineData("ffx_fsr2_api_dx11_x64.dll", GraphicsApi.DirectX11)]
    [InlineData("ffx_fsr2_api_x64.dll", GraphicsApi.Unknown)]
    [InlineData("amd_ags_x64.dll", GraphicsApi.Unknown)]
    [InlineData("dxgi.dll", GraphicsApi.Unknown)]
    [InlineData("d3d12.dll", GraphicsApi.Unknown)]
    [InlineData("d3dx9_43.dll", GraphicsApi.Unknown)]
    public void HelperApi_ReadsTheRendererFromVendorBackEndNames(string dll, GraphicsApi api)
        => Assert.Equal(api, GraphicsApiDetector.HelperApi(dll));

    [Fact]
    public void HelperImports_ContradictALoneImport_AndRaiseItOnlyBelowAnImportedDx11()
    {
        var dx9 = GraphicsApiDetector.Detect(_root, WritePe("Helper9.exe", ["d3d9.dll", "ffx_fsr2_api_dx12_x64.dll"], []));
        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (dx9.Primary, dx9.Confidence));
        Assert.Contains("renderer helper imports", dx9.Evidence);

        var vk = GraphicsApiDetector.Detect(_root, WritePe("HelperVk.exe", ["d3d9.dll", "NvLowLatencyVk.dll"], []));
        Assert.Equal((GraphicsApi.DirectX9, GraphicsApiConfidence.Medium), (vk.Primary, vk.Confidence)); // DirectX first, never Vulkan
        Assert.Equal([GraphicsApi.DirectX9, GraphicsApi.Vulkan], vk.Candidates);

        // Upstream order: an imported DX11 is not outranked by a DX12 that is only a helper or delay-load.
        var dx11 = GraphicsApiDetector.Detect(_root, WritePe("Helper11.exe", ["dxgi.dll", "d3d11.dll", "ffx_fsr2_api_dx12_x64.dll"], []));
        Assert.Equal((GraphicsApi.DirectX11, GraphicsApiConfidence.Medium), (dx11.Primary, dx11.Confidence));
    }

    // Final detect-deploy review, minor 2 (surviving mutant G8): game-folder configs skip run-record folders too, not
    // only the prefix walk. The log sorts ahead of the real setting (shallower), so only the skip lets the setting win.
    [Theory]
    [InlineData("Logs")]
    [InlineData("BenchmarkData")]
    [InlineData("Crashes")]
    public void GameFolderConfigs_InARunRecordFolder_AreNotEvidence(string folder)
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Run Records")).FullName;
        var exe = WritePe(Path.Combine("Run Records", "RunRecords.exe"), ["kernel32.dll", "d3d11.dll", "vulkan-1.dll"], []);
        Directory.CreateDirectory(Path.Combine(gameRoot, folder));
        File.WriteAllText(Path.Combine(gameRoot, folder, "render.ini"), "[Video]\nRenderer=Vulkan\n");
        var config = Directory.CreateDirectory(Path.Combine(gameRoot, "Config", "Game")).FullName;
        File.WriteAllText(Path.Combine(config, "settings.ini"), "[Video]\nRenderer=DX11\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot));

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.StartsWith("Config selection: settings.ini", found.Evidence);
    }

    [Fact]
    public void Dxgi_BesideALoneDx9Import_IsMediumDx12()
    {
        var found = GraphicsApiDetector.Detect(_root, WritePe("Dxgi9.exe", ["dxgi.dll", "d3d9.dll"], []));

        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (found.Primary, found.Confidence));
    }

    [Fact]
    public void PrefixSettings_InASymlinkedFolder_AreNotRead()
    {
        var (gameRoot, exe, prefix) = Rdr2Layout();
        var elsewhere = Path.Combine(_root, "elsewhere", "Settings");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "system.xml"), Rdr2SystemXml);
        var rockstar = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "Rockstar Games");
        Directory.CreateDirectory(rockstar);
        Directory.CreateSymbolicLink(Path.Combine(rockstar, "Red Dead Redemption 2"), Path.Combine(_root, "elsewhere"));
        var documents = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents");
        Directory.CreateSymbolicLink(Path.Combine(documents, "Red Dead Redemption 2 copy"), Path.Combine(_root, "elsewhere"));

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal(GraphicsApi.DirectX12, found.Primary);
        Assert.DoesNotContain("system.xml", found.Evidence);
    }

    [Fact]
    public void PrefixSettings_GenericExeName_DoesNotClaimALaunchersSettings()
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Game")).FullName;
        var exe = WritePe(Path.Combine("Game", "Launcher.exe"), ["d3d11.dll", "vulkan-1.dll"], []);
        var prefix = Path.Combine(_root, "pfx");
        var launcher = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Local", "Ubisoft Game Launcher");
        Directory.CreateDirectory(launcher);
        File.WriteAllText(Path.Combine(launcher, "settings.ini"), "Renderer=Vulkan\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.DoesNotContain("Prefix settings", found.Evidence);
    }

    [Fact]
    public void PrefixSettings_UnrealGameUserSettingsThreeLevelsDown_WinOverABenchmarkLog()
    {
        // Borderlands 3's real prefix: the benchmark log says D3D11 (the API of that run), the user's
        // setting at Saved/Config/WindowsNoEditor/GameUserSettings.ini says DX12.
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Borderlands 3")).FullName;
        var exe = WritePe(Path.Combine("Borderlands 3", "Borderlands3.exe"),
            ["KERNEL32.dll", "dxgi.dll", "d3d9.dll", "d3d11.dll", "OPENGL32.dll"], ["GFSDK_Aftermath_Lib.x64.dll", "d3d12.dll"]);
        var prefix = Path.Combine(_root, "pfx");
        var saved = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "My Games", "Borderlands 3", "Saved");
        Directory.CreateDirectory(Path.Combine(saved, "BenchmarkData"));
        File.WriteAllText(Path.Combine(saved, "BenchmarkData", "BenchmarkData_2025-11-17_01-27-20.txt"), "GraphicsAPI:                        D3D11\r\n");
        Directory.CreateDirectory(Path.Combine(saved, "Logs"));
        File.WriteAllText(Path.Combine(saved, "Logs", "render.ini"), "Renderer=DX11\n");

        var benchmarkOnly = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);
        Assert.Equal((GraphicsApi.DirectX11, GraphicsApiConfidence.Medium), (benchmarkOnly.Primary, benchmarkOnly.Confidence));
        Assert.StartsWith("Executable imports:", benchmarkOnly.Evidence);

        var config = Directory.CreateDirectory(Path.Combine(saved, "Config", "WindowsNoEditor")).FullName;
        File.WriteAllText(Path.Combine(config, "Compat.ini"), "[ScalabilitySettings]\n");
        File.WriteAllText(Path.Combine(config, "GameUserSettings.ini"), "[/Script/OakGame.OakGameUserSettings]\r\nPreferredGraphicsAPI=DX12\r\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal((GraphicsApi.DirectX12, GraphicsApiConfidence.Medium), (found.Primary, found.Confidence));
        Assert.StartsWith("Prefix settings: My Games/Borderlands 3/Saved/Config/WindowsNoEditor/GameUserSettings.ini", found.Evidence);
    }

    [Fact]
    public void PrefixSettings_IniOutranksText_InTheSameFolder()
    {
        var (gameRoot, exe, prefix) = Rdr2Layout();
        var settings = Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "Rockstar Games", "Red Dead Redemption 2", "Settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "a-notes.txt"), "Renderer=DX12\n");
        File.WriteAllText(Path.Combine(settings, "system.xml"), Rdr2SystemXml);

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot), prefix);

        Assert.Equal(GraphicsApi.Vulkan, found.Primary);
    }

    [Theory]
    [InlineData("Launcher", "")]
    [InlineData("Shipping", "")]
    [InlineData("Hogwarts Legacy-Win64-Shipping", "hogwartslegacy")]
    [InlineData("Cyberpunk2077", "cyberpunk2077")]
    public void GameNameToken_DropsGenericNamesAndBuildSuffixes(string name, string token)
        => Assert.Equal(token, GraphicsApiDetector.GameNameToken(name));

    [Fact]
    public void DualImports_ExplicitDx11ConfigWins()
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Dual Game")).FullName;
        var exe = WritePe(Path.Combine("Dual Game", "DualGame.exe"), ["kernel32.dll", "d3d11.dll", "vulkan-1.dll"], []);
        File.WriteAllText(Path.Combine(gameRoot, "settings.ini"), "[Video]\nRenderer=DX11\n");

        var found = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot));

        Assert.Equal(GraphicsApi.DirectX11, found.Primary);
        Assert.StartsWith("Config selection: settings.ini", found.Evidence);
        Assert.Contains(GraphicsApi.Vulkan, found.Candidates);
    }

    [Fact]
    public void DualImports_VulkanConfigSelectsVulkan_ButARendererTheExeCannotUseIsIgnored()
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Dual Game")).FullName;
        var exe = WritePe(Path.Combine("Dual Game", "DualGame.exe"), ["d3d11.dll", "vulkan-1.dll"], []);
        var config = Path.Combine(gameRoot, "settings.ini");

        File.WriteAllText(config, "Renderer=OpenGL\n");
        var ignored = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot));
        Assert.Equal(GraphicsApi.DirectX11, ignored.Primary);
        Assert.StartsWith("Executable imports:", ignored.Evidence);

        File.WriteAllText(config, "Renderer=Vulkan\n");
        var vulkan = GraphicsApiDetector.Detect(gameRoot, exe, GameFileIndexer.Build(gameRoot));
        Assert.Equal(GraphicsApi.Vulkan, vulkan.Primary);
        Assert.Equal(GraphicsApiConfidence.Medium, vulkan.Confidence);
    }

    private const string Rdr2SystemXml =
        "<rage__fwuiSystemSettingsCollection>\n  <graphics>\n    <API>kSettingAPI_Vulkan</API>\n  </graphics>\n</rage__fwuiSystemSettingsCollection>\n";

    /// <summary>The real RDR2.exe import table (Steam build, 89.6 MB; read with PeImportReader), no delay-loads.</summary>
    private static readonly string[] Rdr2Imports =
    [
        "KERNEL32.dll", "USER32.dll", "IPHLPAPI.DLL", "DSOUND.dll", "WINMM.dll", "MF.dll", "MFPlat.DLL", "MFReadWrite.dll",
        "PROPSYS.dll", "WS2_32.dll", "COMCTL32.dll", "CRYPT32.dll", "WINTRUST.dll", "IMM32.dll", "DINPUT8.dll",
        "XINPUT9_1_0.dll", "oo2core_5_win64.dll", "amd_ags_x64.dll", "CFGMGR32.dll", "WTSAPI32.dll", "d3d9.dll",
        "NvLowLatencyVk.dll", "ffx_fsr2_api_x64.dll", "ffx_fsr2_api_dx12_x64.dll", "ffx_fsr2_api_vk_x64.dll", "bcrypt.dll",
        "RPCRT4.dll", "VERSION.dll", "bink2w64.dll", "GDI32.dll", "ADVAPI32.dll", "SHELL32.dll", "ole32.dll", "OLEAUT32.dll"
    ];

    private (string GameRoot, string Exe, string Prefix) Rdr2Layout()
    {
        var gameRoot = Directory.CreateDirectory(Path.Combine(_root, "Red Dead Redemption 2")).FullName;
        var exe = WritePe(Path.Combine("Red Dead Redemption 2", "RDR2.exe"), Rdr2Imports, [],
            "d3d12.dll D3D12CreateDevice d3d11.dll d3d9.dll vulkan-1.dll vkCreateInstance", extraOffset: 60L * 1024 * 1024);
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents"));
        return (gameRoot, exe, prefix);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    /// <summary>Minimal PE32+ with one section holding the import and delay-load descriptors and names.</summary>
    /// <param name="extraOffset">When set, the extra strings start at this file offset (sparse hole before them).</param>
    private string WritePe(string name, string[] imports, string[] delayImports, string extraStrings = "", long extraOffset = 0)
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

        var extra = Encoding.ASCII.GetBytes(extraStrings);
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
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write(file);
        // strings outside the import table (LoadLibrary names), optionally far into the file
        if (extraOffset > 0)
        {
            stream.SetLength(extraOffset);
            stream.Seek(extraOffset, SeekOrigin.Begin);
            stream.Write(extra);
            stream.Write(new byte[16]);
        }
        else stream.Write(extra);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
