using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #200 DX9 HDR, correction round (reviews/w7-review-dxvk-hdr.md): the reviewer's probes adopted as tests, and one test
// per finding, each shown red without its fix.
public sealed partial class ReShadeChannelTests
{
    /// <summary>The DX9 HDR row as the app builds it: the probe over the game's files, then the service's decoration.</summary>
    private static ManagedComponent DecoratedDxvkRow(DxvkRig rig)
    {
        var files = Directory.EnumerateFiles(rig.Game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(rig.Game, files);
        var row = components.Single(component => component.Kind == ManagedComponentKind.DxvkHdr);
        rig.Dxvk.Decorate(rig.Game, components, rig.Dxvk.GetRecord(rig.Game));
        return row;
    }

    // Review major 1 (probe P3): the user deleted RHI's d3d9.dll. The row must still offer Remove (Installed, action
    // allowed), and Remove must take dxvk.conf, the per-exe override and the record away and put a moved ReShade back.
    [Fact]
    public async Task Review_DxvkHdr_D3d9DeletedByUser_RowStillOffersRemoval()
    {
        var rig = Rig("rv-d3d9-gone");
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.Delete(rig.D3d9);
        var row = new ManagedComponent { Kind = ManagedComponentKind.DxvkHdr, DisplayName = "DX9 HDR", Availability = ComponentAvailability.Available };
        rig.Dxvk.Decorate(rig.Game, [row], rig.Dxvk.GetRecord(rig.Game));
        var install = await rig.Dxvk.InstallAsync(rig.Game);
        Assert.True(row.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable || install.Success,
            $"row={row.Availability} actionAllowed={row.ActionAllowed}; Install answers: {install.Message}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DxvkHdr_D3d9DeletedByUser_RowOffersRemove_AndRemovalCleansTheRest(bool withReShade, bool noLongerDx9)
    {
        var rig = Rig($"dxvk-gone-{withReShade}-{noLongerDx9}");
        if (withReShade) await InstallDllReShadeAsync(rig);
        var proxy = withReShade ? File.ReadAllBytes(rig.D3d9) : null;
        var user = File.ReadAllBytes(rig.UserReg);
        var system = File.ReadAllBytes(rig.SystemReg);
        Assert.True((await rig.Dxvk.InstallAsync(rig.Game)).Success);
        File.Delete(rig.D3d9);
        if (noLongerDx9) rig.Game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        var row = DecoratedDxvkRow(rig);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(row.ManagedByRhi);
        Assert.True(row.ActionAllowed, row.Note);
        Assert.Equal(DxvkHdrDeploymentService.MissingD3d9Note, row.Note);

        var removed = await rig.Dxvk.RemoveAsync(rig.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Null(rig.Dxvk.GetRecord(rig.Game));
        Assert.False(File.Exists(rig.Conf));
        Assert.Equal(user, File.ReadAllBytes(rig.UserReg));
        Assert.Equal(system, File.ReadAllBytes(rig.SystemReg));
        if (withReShade)
        {
            Assert.Equal(proxy, File.ReadAllBytes(rig.D3d9));
            Assert.False(ReShadeVulkanLayer.IsLayerRecord(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)));
            Assert.False(File.Exists(Path.Combine(rig.Bin, "vulkan-1.dll")));
        }
        else Assert.False(File.Exists(rig.D3d9));
        // Gone with the record: the row is no longer shown on a game where DX9 HDR cannot apply.
        var after = DecoratedDxvkRow(rig);
        Assert.Equal(noLongerDx9 ? ComponentAvailability.Unsupported : ComponentAvailability.Available, after.Availability);
    }
}
