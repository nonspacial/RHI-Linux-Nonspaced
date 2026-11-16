using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #283 (upstream 2.8.9 "Load ReShade as ASI", ReShade ⚙): ReShade is renamed to ReShade64.asi / ReShade32.asi for an ASI
/// loader, installs and updates keep that name, Off goes back to the name an install would pick; an .asi takes no Wine
/// override (the loader loads it), and the DLL-name dropdown is locked while it is on. Not offered for the Vulkan layer.
/// </summary>
public sealed partial class DllNameRenameTests
{
    [Fact]
    public async Task AsiMode_RenamesInstalledReShadeWithoutOverride_AndOffGoesBackToTheAutomaticName()
    {
        var fx = Fixture("asi-round-trip");
        var userRegBefore = File.ReadAllBytes(fx.UserReg);
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var dxgi = Path.Combine(fx.Bin, "dxgi.dll");
        var bytes = File.ReadAllBytes(dxgi);

        var on = await fx.Service.SetAsiModeAsync(fx.Game, true);

        Assert.True(on.Success, on.Message);
        var asi = Path.Combine(fx.Bin, "ReShade64.asi");
        Assert.Equal(bytes, File.ReadAllBytes(asi));
        Assert.False(File.Exists(dxgi));
        Assert.True(fx.Names.GetReShadeAsi(fx.Game));
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(asi, ProxyPayloadLocator.ReShadePayload(record)!.Path);
        Assert.Null(record.WineOverride); // the ASI loader loads it; Wine never looks for ReShade64.asi
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "ReShade64.asi", "Game.exe"));

        // The DLL-name dropdown is locked while ReShade is an .asi, and says where to turn it off.
        var (targets, refusal) = fx.Service.RenameOptions(fx.Game, ProxyDllNames.OfferedFor(false));
        Assert.Empty(targets);
        Assert.StartsWith(ReShadeDeploymentService.AsiModeLocksNamePrefix, refusal);
        Assert.StartsWith(ReShadeDeploymentService.AsiModeLocksNamePrefix, (await fx.Service.RenameProxyAsync(fx.Game, "winmm.dll")).Message);

        // An update keeps the .asi name.
        Assert.True((await fx.Service.UpdateAsync(fx.Game)).Success);
        Assert.True(File.Exists(asi));
        Assert.False(File.Exists(dxgi));

        var off = await fx.Service.SetAsiModeAsync(fx.Game, false);

        Assert.True(off.Success, off.Message);
        Assert.Equal(bytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(asi));
        Assert.False(fx.Names.GetReShadeAsi(fx.Game));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));

        Assert.True((await fx.Service.RemoveAsync(fx.Game)).Success);
        Assert.Equal(userRegBefore, File.ReadAllBytes(fx.UserReg));
    }

    [Fact]
    public async Task AsiMode_OffGoesBackToTheUsersChosenName()
    {
        var fx = Fixture("asi-off-choice");
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        Assert.True((await fx.Service.SetAsiModeAsync(fx.Game, true)).Success);

        Assert.True((await fx.Service.SetAsiModeAsync(fx.Game, false)).Success);

        Assert.True(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));
    }

    [Fact]
    public async Task AsiMode_BeforeInstall_SavesTheChoice_AndAFreshInstallUsesTheAsiName()
    {
        var fx = Fixture("asi-fresh");
        var userRegBefore = File.ReadAllBytes(fx.UserReg);

        var saved = await fx.Service.SetAsiModeAsync(fx.Game, true);

        Assert.True(saved.Success, saved.Message);
        Assert.Equal(ReShadeDeploymentService.AsiModeSavedPrefix + "ReShade64.asi", saved.Message);
        var install = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "ReShade64.asi")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!.WineOverride);
        Assert.Equal(userRegBefore, File.ReadAllBytes(fx.UserReg)); // no override for an .asi

        var off = await fx.Service.SetAsiModeAsync(fx.Game, false);
        Assert.True(off.Success, off.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
    }

    [Fact]
    public async Task AsiMode_OffBeforeInstall_SavesOnly()
    {
        var fx = Fixture("asi-off-only");
        Assert.True((await fx.Service.SetAsiModeAsync(fx.Game, true)).Success);
        var off = await fx.Service.SetAsiModeAsync(fx.Game, false);
        Assert.True(off.Success, off.Message);
        Assert.Equal(ReShadeDeploymentService.AsiModeOffSavedMessage, off.Message);
        Assert.False(fx.Names.GetReShadeAsi(fx.Game));
    }

    [Fact]
    public async Task AsiMode_On32BitGame_UsesReShade32Asi()
    {
        var fx = Fixture("asi-32");
        PeArchitectureDetectorTests.WritePe(fx.Game.ExecutablePath!, 0x014c);
        Assert.True((await fx.Service.SetAsiModeAsync(fx.Game, true)).Success);

        var install = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "ReShade32.asi")));
    }

    [Fact]
    public async Task AsiMode_RefusedForVulkan_AndWhenTheAsiNameIsTaken_ChangesNothing()
    {
        var vulkan = Fixture("asi-vulkan");
        vulkan.Game.GraphicsApi = GraphicsApi.Vulkan;
        var before = vulkan.Snapshot();
        var refused = await vulkan.Service.SetAsiModeAsync(vulkan.Game, true);
        Assert.False(refused.Success);
        Assert.Equal(ReShadeDeploymentService.AsiModeNotForVulkanMessage, refused.Message);
        Assert.False(vulkan.Names.GetReShadeAsi(vulkan.Game));
        vulkan.AssertUnchanged(before);

        var taken = Fixture("asi-taken");
        Assert.True((await taken.Service.InstallAsync(taken.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        File.WriteAllBytes(Path.Combine(taken.Bin, "reshade64.ASI"), [1, 2, 3]);
        before = taken.Snapshot();
        refused = await taken.Service.SetAsiModeAsync(taken.Game, true);
        Assert.False(refused.Success);
        Assert.StartsWith(ProxyRename.TargetTakenPrefix, refused.Message);
        Assert.False(taken.Names.GetReShadeAsi(taken.Game)); // a refused rename leaves the setting as it was
        taken.AssertUnchanged(before);
    }

    [Fact]
    public void AsiNames_NeedNoWineOverride_AndAreReShadePayloads()
    {
        Assert.Equal("ReShade64.asi", ProxyDllNames.AsiName(is32Bit: false));
        Assert.Equal("ReShade32.asi", ProxyDllNames.AsiName(is32Bit: true));
        Assert.False(ProxyDllNames.NeedsWineOverride("ReShade64.asi"));
        Assert.False(ProxyDllNames.NeedsWineOverride("ReShade32.asi"));
        Assert.True(ProxyDllNames.NeedsWineOverride("dxgi.dll"));
        Assert.True(ProxyPayloadLocator.IsReShadePayloadPath("/g/ReShade32.asi"));
    }
}
