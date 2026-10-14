namespace RHI.Core.Tests;

/// <summary>#155: every DLSS flow takes its targets from <c>DetectTargets</c>, so the manifest folders reach Quick Apply too.</summary>
public sealed partial class LinuxDlssDefaultsServiceTests
{
    [Fact]
    public void Inspect_ManifestDlssFolderPastTheDepth8Walk_IsEligible()
    {
        var game = Game("sinking-city-2");
        game.DlssPathOverride = DlssPathOverrideTests.DlssRel;
        game.StreamlinePathOverride = DlssPathOverrideTests.StreamlineRel;
        var (dlss, _) = DlssPathOverrideTests.SinkingCityLayout(game.InstallPath);
        var service = NewService(new GateHandler(Manifest(), PayloadZip()));

        var candidate = service.Inspect(game, RHI.Core.Services.GameFileIndexer.Build(game.InstallPath, 8));

        Assert.True(candidate.Eligible, candidate.Reason);
        Assert.Equal([Path.Combine(dlss, "nvngx_dlss.dll")], candidate.Detection.DlssPaths);
    }
}
