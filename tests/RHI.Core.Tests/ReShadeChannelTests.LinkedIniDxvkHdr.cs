using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w8 integration (#213 leftover, DX9 HDR): moving RHI's ReShade to its Vulkan layer writes a ReShade.ini only when the game
// has none. The audit's narrow case — a ReShade.ini that is a link to a shared ~/.config/ReShade file not made yet — is not
// reachable: .NET counts a dangling link as an existing file, so the move writes nothing there and the link and the shared
// folder are left exactly as they were, on success, a failed save and a crash. (The write now tracks the link-rule target
// anyway, the one rule every ReShade.ini writer follows.)
public sealed partial class ReShadeChannelTests
{
    [Theory]
    [InlineData("ledger-blocked")]
    [InlineData("crash")]
    public async Task DxvkHdrMove_DanglingLinkedReShadeIni_FailsOrDies_TheLinkStays_AndNoSharedFileIsMade(string failure)
    {
        var rig = Rig("dxvk-dangling-" + failure, journaled: true); // sets the fake 7-Zip in this test's flow
        await InstallWithoutIniAsync(rig);
        var ini = Path.Combine(rig.Bin, "ReShade.ini");
        Assert.False(File.Exists(ini));
        var shared = Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.ConfigHome, "ReShade", Guid.NewGuid().ToString("N"), "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.CreateSymbolicLink(ini, shared); // the user's link; the shared file is made by the first write
        try
        {
            await RenoDxJournalTests.FailAsync(new RenoDxJournalTests.JournalParts(rig.Store, rig.Journal!, rig.JournalDirectory!, rig.Ledger),
                failure, 1, RHI.Core.Deployment.JournalRecoveryOutcome.RolledBack, () => rig.Dxvk.InstallAsync(rig.Game), () =>
                {
                    Assert.Equal(shared, new FileInfo(ini).LinkTarget); // still the link
                    Assert.False(File.Exists(shared), "no shared file was made");
                    Assert.Equal(rig.D3d9, ProxyPayloadLocator.ReShadePayload(new LinuxDeploymentRecordStore(rig.Ledger)
                        .Find(rig.Game, ManagedComponentKind.ReShade))!.Path); // ReShade is back at d3d9.dll
                });
        }
        finally { Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true); }
    }

    [Fact]
    public async Task DxvkHdrMove_DanglingLinkedReShadeIni_CountsAsAReShadeIni_TheMoveWritesNothingThere()
    {
        var rig = Rig("dxvk-dangling-ok", journaled: true);
        await InstallWithoutIniAsync(rig);
        var ini = Path.Combine(rig.Bin, "ReShade.ini");
        var shared = Path.Combine(RHI.Platform.Linux.Paths.LinuxPaths.ConfigHome, "ReShade", Guid.NewGuid().ToString("N"), "ReShade.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.CreateSymbolicLink(ini, shared);
        try
        {
            var installed = await rig.Dxvk.InstallAsync(rig.Game);

            Assert.True(installed.Success, installed.Message);
            Assert.Equal(shared, new FileInfo(ini).LinkTarget);
            Assert.False(File.Exists(shared)); // nothing written through the link
            Assert.DoesNotContain(rig.Store.Find(rig.Game, ManagedComponentKind.ReShade)!.Files, f => Path.GetFileName(f.Path) == "ReShade.ini");
        }
        finally { Directory.Delete(Path.GetDirectoryName(shared)!, recursive: true); }
    }

    // RHI's ReShade at d3d9.dll with "Manage ReShade.ini" off: the game has no ReShade.ini of RHI's.
    private static async Task InstallWithoutIniAsync(DxvkRig rig)
    {
        var result = await rig.ReShade.InstallAsync(rig.Game, new ReShadeInstallOptions { ManageIni = false });
        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(Path.Combine(rig.Bin, "ReShade.ini")));
    }
}
