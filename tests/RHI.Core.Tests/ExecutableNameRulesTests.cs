using RHI.Platform.Linux.Detection;

namespace RHI.Core.Tests;

public sealed class ExecutableNameRulesTests
{
    [Theory]
    [InlineData("Beacon")]
    [InlineData("PeaceKeeper")]
    [InlineData("CrashBandicoot")]
    [InlineData("CrashBandicootNSaneTrilogy")]
    [InlineData("Reporter")]
    [InlineData("TheReporter-Win64-Shipping")]
    [InlineData("Observer")]
    [InlineData("InstallationZero")]
    [InlineData("Setupper")]
    public void GameNames_ThatContainSupportWords_AreKept(string stem)
        => Assert.False(DetectionHelpers.IsSupportExecutableName(stem));

    [Theory]
    [InlineData("unins000")]
    [InlineData("Uninstall")]
    [InlineData("UnityCrashHandler64")]
    [InlineData("CrashReportClient")]
    [InlineData("crashpad_handler")]
    [InlineData("setup")]
    [InlineData("UE4PrereqSetup_x64")]
    [InlineData("DXSETUP")]
    [InlineData("vc_redist.x64")]
    [InlineData("vcredist_x86")]
    [InlineData("EasyAntiCheat_EOS_Setup")]
    [InlineData("EasyAntiCheat")]
    [InlineData("eac_launcher")]
    [InlineData("BEService_x64")]
    [InlineData("busybox")]
    [InlineData("gamelaunchhelper")]
    [InlineData("GameLaunchHelper")]
    [InlineData("EADesktopInstaller")]
    public void SupportExecutables_AreIgnored(string stem)
        => Assert.True(DetectionHelpers.IsSupportExecutableName(stem));

    [Fact]
    public void NameWords_SplitCamelCaseAcronymsAndDigits()
        => Assert.Equal(["be", "service", "x", "64", "unity", "crash", "handler", "64"],
            DetectionHelpers.NameWords("BEService_x64 UnityCrashHandler64"));

    [Theory]
    [InlineData("Beacon.exe")]
    [InlineData("CrashBandicoot.exe")]
    [InlineData("Reporter.exe")]
    public void FindLikelyExecutable_PicksTheGame_NotTheCrashHandler(string gameExe)
    {
        using var tree = new DetectionTestTree();
        var root = tree.Dir("Game");
        var game = tree.Exe("Game", gameExe);
        tree.Exe("Game", "UnityCrashHandler64.exe");
        tree.Exe("Game", "unins000.exe");

        Assert.Equal(game, DetectionHelpers.FindLikelyExecutable(root));
    }
}
