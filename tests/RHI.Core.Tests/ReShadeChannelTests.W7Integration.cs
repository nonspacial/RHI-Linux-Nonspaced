using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Wave 7 integration: where DX9 HDR (#200), ReShade for XNA games (#201), the in-place ReShade rename (#196) and the
/// shaders-only sync (#179) meet. Each lane's tree lacked the other's code, so these only exist on the merged tree.
/// </summary>
public sealed partial class ReShadeChannelTests
{
    private const string XnaGacWindows = @"C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_4.0.0.0__842cf8be1de50553";

    // A 32-bit DirectX 9 game the RHI manifest lists as XNA, with its prefix's GAC folder present.
    private DxvkRig XnaRig(string name)
    {
        var rig = Rig(name, is32: true);
        Directory.CreateDirectory(Path.Combine(rig.Game.PrefixPath!, "drive_c", "windows", "Microsoft.NET", "assembly", "GAC_32",
            "Microsoft.Xna.Framework.Graphics", "v4.0_4.0.0.0__842cf8be1de50553"));
        rig.Game.ManifestXnaGacFolder = XnaGacWindows;
        return rig;
    }

    // Review probe (w7/dxvk-hdr review major 2b): with RHI's GAC ReShade, DX9 HDR refuses (XNA loads d3d9.dll from the GAC
    // folder, never the game folder); nothing moves.
    [Fact]
    public async Task Review_Merged_XnaGacReShade_DxvkHdrInstall()
    {
        var rig = XnaRig("rv-xna-gac");
        var reshade = await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions());
        Assert.True(reshade.Success, reshade.Message);
        var record = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.True(XnaGacReShade.IsGacRecord(record));
        var gacD3d9 = Path.Combine(record.Metadata["ReShade.XnaGacFolder"], "d3d9.dll");
        var files = CancelAtWritePhase.Snapshot(rig.Bin, Path.GetDirectoryName(gacD3d9)!);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        var after = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        Assert.False(result.Success,
            $"DX9 HDR installed on an XNA GAC game: {result.Message}; ReShade layer={ReShadeVulkanLayer.IsLayerRecord(after)} gac={XnaGacReShade.IsGacRecord(after)} gacD3d9Exists={File.Exists(gacD3d9)}");
        Assert.Equal(DxvkHdrDeploymentService.XnaGameMessage, result.Message);
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(after));
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
        CancelAtWritePhase.AssertUnchanged(files, rig.Bin, Path.GetDirectoryName(gacD3d9)!);
    }

    // Review probe (w7/dxvk-hdr review major 2a): an XNA game without ReShade is not offered DX9 HDR, with the plain
    // reason, and the row is hidden (Unsupported) rather than offered.
    [Fact]
    public async Task Review_Merged_XnaGame_DxvkHdrOffered()
    {
        var rig = XnaRig("rv-xna-plain");
        var refusal = DxvkHdrDeploymentService.OfferRefusal(rig.Game);
        var result = await rig.Dxvk.InstallAsync(rig.Game);
        string later = "";
        if (result.Success)
        {
            var view = rig.ReShade.ViewForDxvkHdr(rig.Game);
            var reshade = await rig.ReShade.InstallAsync(view, new ReShadeInstallOptions { VulkanLayer = ReShadeVulkanLayer.IsVulkanTarget(view) });
            var rec = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade);
            later = $"; ReShade after: {reshade.Success} layer={ReShadeVulkanLayer.IsLayerRecord(rec)} gac={XnaGacReShade.IsGacRecord(rec)}";
        }
        Assert.True(refusal is not null && !result.Success, $"offer refusal={refusal ?? "none"}; install={result.Success} {result.Message}{later}");
        Assert.Equal(DxvkHdrDeploymentService.XnaGameMessage, refusal);
        Assert.False(ComponentProbe.IsDxvkHdrCandidate(rig.Game));
        Assert.False(File.Exists(rig.D3d9));
    }

    // The move refusal stands on its own: a GAC ReShade record on a game the manifest no longer lists as XNA (the list
    // changed since ReShade was installed) is still never moved to the Vulkan layer.
    [Fact]
    public async Task DxvkHdr_GacReShadeRecord_GameNoLongerListed_RefusesTheMove_AndChangesNothing()
    {
        var rig = XnaRig("xna-unlisted");
        Assert.True((await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions())).Success);
        var record = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        var gac = record.Metadata["ReShade.XnaGacFolder"];
        rig.Game.ManifestXnaGacFolder = null;
        var files = CancelAtWritePhase.Snapshot(rig.Bin, gac);
        var userBefore = File.ReadAllBytes(rig.UserReg);

        var result = await rig.Dxvk.InstallAsync(rig.Game);

        Assert.False(result.Success);
        Assert.Equal(ReShadeDeploymentService.DxvkHdrReShadeXnaGacMessage, result.Message);
        Assert.True(XnaGacReShade.IsGacRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        Assert.False(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        CancelAtWritePhase.AssertUnchanged(files, rig.Bin, gac);
        Assert.Equal(userBefore, File.ReadAllBytes(rig.UserReg));
    }

    // ReShade's row on a listed XNA game goes to the GAC path even when a DX9 HDR record is there (installed before the
    // manifest listed the game): the Vulkan view would put ReShade where XNA never loads it.
    [Fact]
    public async Task ViewForDxvkHdr_LeavesAListedXnaGameAsItIs()
    {
        var rig = Rig("xna-view", is32: true);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        Assert.Equal(GraphicsApi.Vulkan, rig.ReShade.ViewForDxvkHdr(rig.Game).EffectiveGraphicsApi); // unlisted: the layer view
        Directory.CreateDirectory(Path.Combine(rig.Game.PrefixPath!, "drive_c", "windows", "Microsoft.NET", "assembly", "GAC_32",
            "Microsoft.Xna.Framework.Graphics", "v4.0_4.0.0.0__842cf8be1de50553"));
        rig.Game.ManifestXnaGacFolder = XnaGacWindows;

        var view = rig.ReShade.ViewForDxvkHdr(rig.Game);

        Assert.Same(rig.Game, view);
        Assert.False(ReShadeVulkanLayer.IsVulkanTarget(view));
    }

    // #196 × #200: a ReShade DX9 HDR moved to its Vulkan layer is decided by another component — the rename refusal names
    // DX9 HDR in plain words (EN and PT-BR), the dropdown offers nothing, and Reset DLL names is not offered for it.
    [Fact]
    public async Task ReShadeMovedByDxvkHdr_RenameIsRefusedNamingDx9Hdr_AndResetIsNotOffered()
    {
        var rig = Rig("dxvk-rename");
        await InstallDllReShadeAsync(rig);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        Assert.True(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
        var managed = ManagedComponents(rig.Store, rig.ReShade);

        Assert.Equal(ReShadeDeploymentService.RenameMovedByDxvkHdrMessage, rig.ReShade.RenameBlocker(rig.Game));
        var (targets, refusal) = rig.ReShade.RenameOptions(rig.Game, ["dxgi.dll", "d3d9.dll"]);
        Assert.Empty(targets);
        Assert.Equal(ReShadeDeploymentService.RenameMovedByDxvkHdrMessage, refusal);
        var renamed = await rig.ReShade.RenameProxyAsync(rig.Game, "dxgi.dll");
        Assert.Equal(ReShadeDeploymentService.RenameMovedByDxvkHdrMessage, renamed.Message);
        Assert.False(managed.HasDllNamesToReset(rig.Game));
        Assert.Contains("DX9 HDR", ReShadeDeploymentService.RenameMovedByDxvkHdrMessage, StringComparison.Ordinal);
        var pt = UiLocalizationService.PresentFor("pt-BR", ReShadeDeploymentService.RenameMovedByDxvkHdrMessage);
        Assert.Contains("DX9 HDR", pt, StringComparison.Ordinal);
        Assert.NotEqual(ReShadeDeploymentService.RenameMovedByDxvkHdrMessage, pt);

        // Once DX9 HDR is removed ReShade is d3d9.dll again and the name is its own to change.
        Assert.True((await rig.Dxvk.RemoveAsync(rig.Game)).Success);
        Assert.Null(rig.ReShade.RenameBlocker(rig.Game));
    }

    // #179 × #201: XNA's ReShade reads the ReShade.ini copy in the prefix's .NET assembly folder, so a shaders-only change
    // must bring that copy along (game ini + BasePath), not only the game folder's ini.
    [Fact]
    public async Task ShaderSync_OnAnXnaGame_UpdatesTheGacReShadeIniCopy_AndKeepsItsBasePath()
    {
        var rig = XnaRig("xna-sync");
        SeedShaderCache(Path.Combine(_root, "xna-sync-cache"), "CrosireMaster");
        WriteNativeD3DCompiler(Path.Combine(rig.Game.PrefixPath!, "drive_c", "windows", "syswow64", "d3dcompiler_47.dll"), 0x014c);
        Assert.True((await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions())).Success);
        var record = rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!;
        var gacIni = Path.Combine(record.Metadata["ReShade.XnaGacFolder"], "ReShade.ini");
        Assert.DoesNotContain("CrosireMaster", File.ReadAllText(gacIni));

        var synced = await rig.ReShade.SyncShaderPacksAsync(rig.Game, ["CrosireMaster"]);

        Assert.True(synced.Success, synced.Message);
        var gameIni = File.ReadAllText(Path.Combine(rig.Bin, "ReShade.ini"));
        Assert.Contains("CrosireMaster", gameIni);
        var gac = File.ReadAllText(gacIni);
        Assert.Contains("CrosireMaster", gac);
        Assert.Contains("[INSTALL]\nBasePath=Z:" + rig.Bin.Replace('/', '\\'), gac);
        Assert.DoesNotContain("BasePath", gameIni);
        // The record knows the new copy, so removal still takes it as RHI's.
        Assert.Equal(FileHash.Sha256Hex(gacIni), rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!.Metadata["ReShade.XnaGacIniSha256"],
            StringComparer.OrdinalIgnoreCase);
        Assert.True((await rig.ReShade.RemoveAsync(rig.Game)).Success);
        Assert.False(File.Exists(gacIni));
    }
}
