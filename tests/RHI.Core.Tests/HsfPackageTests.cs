using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #277: HSF's package rules, pinned on real data — the Capt-Hunter/HSF release list (2026-11-16), the HSF-0.9.8.8.zip
/// entry list and its HSF.ini — and upstream's D3D12 route, HSF.ini carry-over and Launchpad order.
/// </summary>
public sealed class HsfPackageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "hsf", name);

    private static List<string> Lines(string text) => IniLines.FromBytes(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void NewestRelease_IsTheFirstWithAnHsfZip_WithGitHubsDigest()
    {
        var release = HsfPackage.ParseNewestRelease(File.ReadAllText(Fixture("releases-2026-11-16.json")))!;

        Assert.Equal("0.9.8.8", release.Version);
        Assert.Equal("HSF-0.9.8.8.zip", release.AssetName);
        Assert.Equal("https://github.com/Capt-Hunter/HSF/releases/download/v0.9.8.8/HSF-0.9.8.8.zip", release.Url);
        Assert.Equal("sha256:6e512806f05d161b91dda7c0895e852512213d0ca6cd9ffb30708e53a561333f", release.Digest);
    }

    [Fact]
    public void NewestRelease_SkipsDraftsAndReleasesWithoutAZip_AndKeepsTheVersionToSafeCharacters()
    {
        const string json = """
            [ {"tag_name":"v1.0","draft":true,"assets":[{"name":"HSF-1.0.zip","browser_download_url":"https://github.com/x/1.zip"}]},
              {"tag_name":"v0.9.9","draft":false,"assets":[{"name":"HSF-0.9.9-patch-notes.png","browser_download_url":"https://github.com/x/p.png"}]},
              {"tag_name":"v0.9.8/../x","draft":false,"assets":[{"name":"HSF-0.9.8.zip","browser_download_url":"https://github.com/x/0.zip"}]} ]
            """;
        var release = HsfPackage.ParseNewestRelease(json)!;
        Assert.Equal("0.9.8..x", release.Version);
        Assert.Null(release.Digest);
    }

    [Fact]
    public void StagedPaths_OfThe0988Zip_PutEverythingInHsfsOwnFolder()
    {
        var staged = File.ReadAllLines(Fixture("HSF-0.9.8.8-entries.txt")).Where(l => l.Length > 0)
            .Select(entry => (entry, path: HsfPackage.StagedPathFor(entry))).ToArray();

        Assert.All(staged, s => Assert.NotNull(s.path)); // every entry of the real release is taken
        var map = staged.ToDictionary(s => s.entry, s => s.path);
        Assert.Equal("hsf.addon64", map["hsf.addon64"]);
        Assert.Equal("HSF.ini", map["HSF.ini"]);
        Assert.Equal("HSF.fx", map["reshade-shaders/Shaders/HSF.fx"]);
        Assert.Equal("HSF_streamline/LICENSE.txt", map["LICENSE.txt"]);
        Assert.Equal("HSF_streamline/README.txt", map["README.txt"]);
        Assert.Equal("HSF_streamline/licenses/Streamline-license.txt", map["licenses/Streamline-license.txt"]);
        Assert.Equal("HSF_streamline/hsf-dlssg-helper64.exe", map["HSF_streamline/hsf-dlssg-helper64.exe"]);
        foreach (var required in HsfPackage.RequiredFiles) Assert.Contains(required, map.Values);
    }

    [Theory]
    [InlineData("HSF_streamline/../../evil.dll")]
    [InlineData("../hsf.addon64x/HSF_streamline/x.dll")]
    [InlineData("/etc/HSF_streamline/x.dll")]
    [InlineData("HSF_streamline/helper.log")]
    [InlineData("sub/HSF.ini")]
    [InlineData("docs/readme.md")]
    public void StagedPaths_NeverClimbOutOrTakeLogsOrStrayFiles(string entry)
    {
        var path = HsfPackage.StagedPathFor(entry);
        Assert.True(path is null || !path.Contains(".."), path);
        if (entry is "HSF_streamline/helper.log" or "sub/HSF.ini" or "docs/readme.md" or "HSF_streamline/../../evil.dll" or "../hsf.addon64x/HSF_streamline/x.dll")
            Assert.Null(path);
    }

    [Theory]
    [InlineData(true, false, false, null, HsfRoute.BringIn)]
    [InlineData(true, false, true, null, HsfRoute.GameStreamline)]
    [InlineData(true, false, true, "1.5.3", HsfRoute.None)]   // Streamline 1.x: HSF leaves it alone
    [InlineData(true, true, false, null, HsfRoute.None)]      // 32-bit: no D3D12 routes
    [InlineData(false, false, false, null, HsfRoute.None)]    // not DX12
    public void PickRoute_FollowsHsf(bool dx12, bool is32, bool hasStreamline, string? version, HsfRoute expected)
        => Assert.Equal(expected, HsfPackage.PickRoute(dx12, is32, hasStreamline, version));

    [Fact]
    public void PickRoute_UnknownStreamline_IsNoGuess() => Assert.Equal(HsfRoute.None, HsfPackage.PickRoute(true, false, null, null));

    [Fact]
    public void PresetRoute_WritesTheKeyIntoTheRealCleanIni_OnlyWhenNoRouteIsSet()
    {
        var lines = Lines(File.ReadAllText(Fixture("HSF-0.9.8.8-HSF.ini")));
        Assert.True(HsfPackage.PresetRoute(lines, HsfRoute.BringIn));
        Assert.Equal("2", IniLines.Get(lines, "HSF", "SlBringIn"));
        Assert.False(HsfPackage.PresetRoute(lines, HsfRoute.GameStreamline)); // a route is set: never changed
        Assert.Null(IniLines.Get(lines, "HSF", "InGameDlssg"));

        var userOff = Lines("[HSF]\nInGameDlssg=0\n");
        Assert.False(HsfPackage.PresetRoute(userOff, HsfRoute.BringIn));
        Assert.Equal((HsfRoute.None, true), HsfPackage.ReadRoute(userOff));
        Assert.True(HsfPackage.SetRoute(userOff, HsfRoute.GameStreamline));
        Assert.Equal((HsfRoute.GameStreamline, false), HsfPackage.ReadRoute(userOff));
        Assert.True(HsfPackage.SetRoute(userOff, HsfRoute.None));
        Assert.Equal((HsfRoute.None, false), HsfPackage.ReadRoute(userOff));
    }

    [Fact]
    public void MergeIni_KeepsThePlayersSettings_DropsTheRetiredKeys_AndCarriesOtherSections()
    {
        var clean = File.ReadAllText(Fixture("HSF-0.9.8.8-HSF.ini"));
        const string old = "[HSF]\r\nMultiplier=4\r\nSlBringIn=2\r\nDx9ExRun=1\r\nPacing=3\r\n; comment\r\n[LaunchPadUndo]\r\nTechniques=A,B\r\n";

        var merged = HsfPackage.MergeIni(clean, old);

        Assert.StartsWith("; HSF settings, a clean start.", merged);
        Assert.Contains("Multiplier=4\r\n", merged);
        Assert.Contains("SlBringIn=2\r\n", merged);
        Assert.DoesNotContain("Dx9ExRun", merged);
        Assert.DoesNotContain("Pacing", merged);
        Assert.Contains("[LaunchPadUndo]\r\nTechniques=A,B\r\n", merged);
        Assert.Equal(clean, HsfPackage.MergeIni(clean, null));
    }

    [Fact]
    public void MoveTechniqueFirst_OnlyReorders_AndNeverAdds()
    {
        const string lp = HsfPackage.LaunchpadToken;
        var (text, changed) = HsfPackage.MoveTechniqueFirst("Techniques=HSF_Depth@HSF.fx," + lp + "\r\nTechniqueSorting=HSF_Depth@HSF.fx,Uplift@Uplift.fx," + lp + "\r\n", lp);
        Assert.True(changed);
        Assert.Equal("Techniques=" + lp + ",HSF_Depth@HSF.fx\r\nTechniqueSorting=" + lp + ",HSF_Depth@HSF.fx,Uplift@Uplift.fx\r\n", text);
        Assert.False(HsfPackage.MoveTechniqueFirst("Techniques=HSF_Depth@HSF.fx\n", lp).Changed);
        Assert.False(HsfPackage.MoveTechniqueFirst("Techniques=" + lp + ",X\n", lp).Changed);
    }

    [Fact]
    public void LaunchpadEntries_OnlyLaunchpadsFilesFromTheImmerseArchive()
    {
        Assert.Equal("Shaders/iMMERSE/MartysMods_LAUNCHPAD.fx", ImmerseLaunchpadStep.RelativeFor("iMMERSE-main/Shaders/MartysMods_LAUNCHPAD.fx"));
        Assert.Equal("Shaders/iMMERSE/MartysMods/mmx_global.fxh", ImmerseLaunchpadStep.RelativeFor("iMMERSE-main/Shaders/MartysMods/mmx_global.fxh"));
        Assert.Equal("Textures/iMMERSE/iMMERSE_bluenoise_opt.png", ImmerseLaunchpadStep.RelativeFor("iMMERSE-main/Textures/iMMERSE_bluenoise_opt.png"));
        Assert.Null(ImmerseLaunchpadStep.RelativeFor("iMMERSE-main/Shaders/MartysMods_MXAO.fx"));
        Assert.Null(ImmerseLaunchpadStep.RelativeFor("iMMERSE-main/Shaders/../MartysMods_LAUNCHPAD.fx"));
    }
}
