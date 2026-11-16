using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using Xunit;

namespace RHI.Core.Tests;

/// <summary>
/// w14/nr (#275): DLSS5 Feeder 2.0 (upstream RHI 2.9.3, cdff33f). New installs use the motion vectors built into
/// DLSS5_Feed.fx (no LumeniteFX download), Feeder is added to the game's own preset instead of replacing it, the provider
/// goes in that preset, and removal never deletes a preset that was there before Feeder.
/// </summary>
public sealed partial class Dlss5FeederLifecycleAuditTests
{
    // Feeder 2.0.0's DLSS5_Feed.fx has the built-in estimator (Install-DLSS5Feeder.ps1 looks for "float4 BaseLevel(").
    private static readonly string Feeder2Fx = "// DLSS5 Feeder 2.0.0\nfloat4 BaseLevel(float2 uv) { return 0; }\n" + new string('f', 1024);

    private const string UsersPreset =
        "Techniques=SMAA@SMAA.fx,Vibrance@Vibrance.fx\r\n" +
        "TechniqueSorting=SMAA@SMAA.fx,Vibrance@Vibrance.fx\r\n" +
        "\r\n[SMAA.fx]\r\nEdgeDetectionThreshold=0.050000\r\n" +
        "\r\n[Vibrance.fx]\r\nVibrance=0.150000\r\n";

    [Fact]
    public async Task Feeder2_NewInstall_UsesTheBuiltInMotionVectors_AndNeverDownloadsLumeniteFx()
    {
        using var f = new FeederFixture(feederFx: Feeder2Fx);

        var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

        Assert.True(result.Success, result.Message);
        f.AssertInstalled(Dlss5FeederConsumer.RenoDxDlss5, expectedEarlyLoad: "0", lumenite: false);
        Assert.DoesNotContain(f.LumeniteUrl, f.Requests);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal("5", record.Metadata[Dlss5FeederFullDeploymentService.MetaMvProvider]);
        var preset = File.ReadAllText(f.UserPresetPath);
        Assert.Equal(5, FeederPreset.ReadProvider(preset));
        Assert.Equal("DLSS5_Feed@DLSS5_Feed.fx", ReShadePresetText.GetKey(preset, "", "Techniques"));
        // The motion-vector choice is in the preset, never in ReShade.ini (Feeder does not read it there).
        Assert.DoesNotContain("DLSS5_MV_PROVIDER", File.ReadAllText(f.IniPath));

        var remove = await f.Service.RemoveAsync(f.Game);
        Assert.True(remove.Success, remove.Message);
        f.AssertClean(); // the preset RHI created goes with Feeder
    }

    [Fact]
    public async Task Feeder2_IsAddedToTheUsersPreset_AndRemovalPutsThePresetBackByteForByte()
    {
        using var f = new FeederFixture(feederFx: Feeder2Fx, userPreset: UsersPreset);
        var presetBefore = File.ReadAllBytes(f.UserPresetPath);

        var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

        Assert.True(result.Success, result.Message);
        var preset = File.ReadAllText(f.UserPresetPath);
        Assert.Equal("SMAA@SMAA.fx,Vibrance@Vibrance.fx,DLSS5_Feed@DLSS5_Feed.fx", ReShadePresetText.GetKey(preset, "", "Techniques"));
        Assert.Equal("0.150000", ReShadePresetText.GetKey(preset, "Vibrance.fx", "Vibrance"));
        Assert.Equal(5, FeederPreset.ReadProvider(preset));
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.DoesNotContain(record.Files, file => file.Path == f.UserPresetPath); // never one of Feeder's files
        Assert.Equal("0", record.Metadata[Dlss5FeederFullDeploymentService.MetaPresetCreated]);

        var remove = await f.Service.RemoveAsync(f.Game);

        Assert.True(remove.Success, remove.Message);
        f.AssertClean(keepUserPreset: true);
        Assert.Equal(presetBefore, File.ReadAllBytes(f.UserPresetPath));
    }

    [Fact]
    public async Task Feeder2_RemovalKeepsTheUsersOwnPresetEdits_AndThePreset()
    {
        using var f = new FeederFixture(feederFx: Feeder2Fx, userPreset: UsersPreset);
        Assert.True((await f.Install(Dlss5FeederConsumer.RenoDxDlss5)).Success);
        // The user tuned SMAA in the overlay after the install.
        var edited = ReShadePresetText.SetKey(File.ReadAllText(f.UserPresetPath), "SMAA.fx", "EdgeDetectionThreshold", "0.100000");
        File.WriteAllText(f.UserPresetPath, edited);

        var remove = await f.Service.RemoveAsync(f.Game);

        Assert.True(remove.Success, remove.Message);
        var after = File.ReadAllText(f.UserPresetPath);
        Assert.Equal("0.100000", ReShadePresetText.GetKey(after, "SMAA.fx", "EdgeDetectionThreshold"));
        Assert.Equal("SMAA@SMAA.fx,Vibrance@Vibrance.fx", ReShadePresetText.GetKey(after, "", "Techniques"));
        Assert.Null(FeederPreset.ReadProvider(after));
    }

    [Fact]
    public async Task Feeder2_APresetThatAlreadyUsesLumeniteFx_KeepsIt()
    {
        var preset = UsersPreset + "\r\n[DLSS5_Feed.fx]\r\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3\r\n";
        using var f = new FeederFixture(feederFx: Feeder2Fx, userPreset: preset);

        var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

        Assert.True(result.Success, result.Message);
        f.AssertInstalled(Dlss5FeederConsumer.RenoDxDlss5, expectedEarlyLoad: "0", lumenite: true);
        Assert.Contains(f.LumeniteUrl, f.Requests);
        Assert.Equal(3, FeederPreset.ReadProvider(File.ReadAllText(f.UserPresetPath)));
        Assert.StartsWith("SMAA@SMAA.fx,Vibrance@Vibrance.fx,Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx",
            ReShadePresetText.GetKey(File.ReadAllText(f.UserPresetPath), "", "Techniques"));
    }

    [Fact]
    public async Task Feeder2_AFailedInstall_PutsTheUsersPresetBack()
    {
        using var f = new FeederFixture(feederFx: Feeder2Fx, userPreset: UsersPreset);
        var presetBefore = File.ReadAllBytes(f.UserPresetPath);
        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(f.Records, f.RecordsPath))
            result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);

        Assert.False(result.Success);
        f.AssertClean(keepUserPreset: true);
        Assert.Equal(presetBefore, File.ReadAllBytes(f.UserPresetPath));
    }

    [Fact]
    public async Task Feeder2_32Bit_TheAddonAndItsHostComeFromTheSameRelease()
    {
        // 2.0.0 add-ons and hosts refuse another release's; Linux takes both from the one release zip (install and swap).
        using var f = new FeederFixture(x86: true, feederFx: Feeder2Fx);
        var result = await f.Install(Dlss5FeederConsumer.RenoDxDlss5);
        Assert.True(result.Success, result.Message);
        var record = f.Records.Find(f.Game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Contains(record.Files, file => file.Path.EndsWith("dlss5-feed.addon32", StringComparison.Ordinal));
        Assert.Contains(record.Files, file => file.Path.EndsWith(Path.Combine("host64", "dlss5-feed-host64.exe"), StringComparison.Ordinal));
        Assert.Equal(1, f.Requests.Count(url => url == "https://github.com/rhi-tests/feeder.zip")); // one archive for both
    }
}

/// <summary>w14/nr (#275): the Feeder row says which motion vectors the game uses, from its preset (Feeder's Alt+C menu may switch them).</summary>
public sealed class FeederMotionVectorsLabelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-feeder-mv-" + Guid.NewGuid().ToString("N"));

    public FeederMotionVectorsLabelTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LinuxDeploymentRecord Record(string? presetPath, string? recordedProvider)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Feeder.FullStack"] = "1" };
        if (presetPath is not null) metadata[Dlss5FeederFullDeploymentService.MetaPresetPath] = presetPath;
        if (recordedProvider is not null) metadata[Dlss5FeederFullDeploymentService.MetaMvProvider] = recordedProvider;
        return new LinuxDeploymentRecord { GameKey = "k", GameName = "g", Component = ManagedComponentKind.Dlss5Feeder, BinaryDirectory = _root, Metadata = metadata };
    }

    [Fact]
    public void ReadsTheProviderFromThePreset_SoASwitchInFeedersMenuShows()
    {
        var preset = Path.Combine(_root, "ReShadePreset.ini");
        File.WriteAllText(preset, FeederPreset.Merge(null, 5));
        Assert.Equal(Dlss5FeederFullDeploymentService.BuiltInMotionVectorsLabel,
            Dlss5FeederFullDeploymentService.MotionVectorsLabel(Record(preset, "5")));

        File.WriteAllText(preset, ReShadePresetText.SetKey(File.ReadAllText(preset), "DLSS5_Feed.fx", "PreprocessorDefinitions", "DLSS5_MV_PROVIDER=3"));
        Assert.Equal(Dlss5FeederFullDeploymentService.LumeniteMotionVectorsLabel,
            Dlss5FeederFullDeploymentService.MotionVectorsLabel(Record(preset, "5")));
    }

    [Fact]
    public void AnInstallFromBeforeFeeder2Support_ReadsRhisOwnPreset_AndIsLumenite()
    {
        File.WriteAllText(Path.Combine(_root, "RHI-DLSS5-Feeder.ini"),
            "Techniques=Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx\r\n\r\n[DLSS5_Feed.fx]\r\nPreprocessorDefinitions=DLSS5_MV_PROVIDER=3\r\n");
        Assert.Equal(Dlss5FeederFullDeploymentService.LumeniteMotionVectorsLabel,
            Dlss5FeederFullDeploymentService.MotionVectorsLabel(Record(null, null)));
    }

    [Fact]
    public void WithoutAReadablePreset_TheRecordedChoiceIsShown()
    {
        Assert.Equal(Dlss5FeederFullDeploymentService.BuiltInMotionVectorsLabel,
            Dlss5FeederFullDeploymentService.MotionVectorsLabel(Record(Path.Combine(_root, "gone.ini"), "5")));
    }
}
