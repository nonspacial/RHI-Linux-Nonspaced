using System.ComponentModel;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #251 through the real view model (owner decision: shown unless the data says No, "safe, not sorry"): the HDR launch
/// section shows for Yes and Unknown, hides for No, and shows again for No with an HDR mod RHI installed (RenoDX). A hidden
/// section's saved profile stays in <c>game-library.json</c> untouched but is not applied at a direct launch and is not
/// offered as launch text. Why it shows is only in the label's 2 s tooltip, EN + PT-BR. HTTP fakes only.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HdrSectionViewModelTests : IDisposable
{
    private readonly LegacyHdrWsiViewModelTests _games = new();
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-hdr-section-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        _games.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("true", true, "HDR: listed as supported by PCGamingWiki.")]
    [InlineData("hackable", true, "HDR: PCGamingWiki lists it as possible with a fix.")]
    [InlineData("unknown", true, "HDR: not listed on PCGamingWiki, shown in case it applies.")]
    [InlineData("false", false, null)]
    public async Task TheSection_FollowsPcgwsAnswer(string hdr, bool shown, string? tip)
    {
        var game = _games.Game("Digging Quest", "direct");
        using var fx = await StartAsync(game, Wiki(game, hdr));

        Assert.Equal(shown, fx.Vm.Runtime.ShowHdrSection);
        Assert.Equal(tip, fx.Vm.Runtime.HdrSectionTip); // none while hidden
    }

    [Fact]
    public async Task OfflineOrNoService_ShowsTheSection()
    {
        var game = _games.Game("Digging Quest", "direct");
        var offline = Wiki(game, "false");
        offline.Fail = "offline";
        using (var fx = await StartAsync(game, offline))
        {
            Assert.True(fx.Vm.Runtime.ShowHdrSection);
            Assert.Equal("HDR: support could not be checked yet, shown in case it applies.", fx.Vm.Runtime.HdrSectionTip);
        }
        using (var fx = await LegacyHdrWsiViewModelTests.StartAsync([_games.Game("Plain Quest", "direct")], null, new LegacyHdrWsiViewModelTests.RecordingLauncher()))
            Assert.True(fx.Vm.Runtime.ShowHdrSection); // no resolver wired: nothing known, shown
    }

    [Fact]
    public async Task ANoGame_WithRenoDxInstalledByRhi_ShowsTheSection_AndSaysWhy()
    {
        var game = _games.Game("Digging Quest", "direct");
        using var fx = await StartAsync(game, Wiki(game, "false"));
        Assert.False(fx.Vm.Runtime.ShowHdrSection);

        InstallRenoDx(fx, fx.Vm.SelectedGame!);
        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
        await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);

        Assert.True(fx.Vm.Runtime.ShowHdrSection);
        Assert.StartsWith("HDR: an HDR mod is installed (RenoDX", fx.Vm.Runtime.HdrSectionTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHiddenSection_KeepsTheSavedProfile_ButADirectStartGetsNoHdr()
    {
        var game = _games.Game("Digging Quest", "direct");
        var launcher = new LegacyHdrWsiViewModelTests.RecordingLauncher();
        byte[]? seeded = null;
        using var fx = await StartAsync(game, Wiki(game, "false"), launcher, s => seeded = LegacyHdrWsiViewModelTests.Seed(s, game, LinuxHdrModes.Gamescope));
        var runtime = fx.Vm.Runtime;
        Assert.False(runtime.ShowHdrSection);

        Assert.Equal(LinuxHdrModes.Gamescope, runtime.SelectedHdrMode.Value); // the saved choice is still loaded
        Assert.False(runtime.SelectedHdrPlan.Enabled);
        Assert.False(runtime.ShowHdrLaunchProfile);
        Assert.Equal("%command%", runtime.GetSelectedHdrLaunchOptions());
        await fx.Vm.LaunchCommand.ExecuteAsync(null);

        var launch = Assert.Single(launcher.Launches);
        Assert.Null(launch.Options); // started without a wrapper or HDR variables
        Assert.Equal(seeded, File.ReadAllBytes(fx.Library.LibraryPath));
        Assert.Equal(LinuxHdrModes.Gamescope, fx.Library.Load().HdrProfiles[LinuxGameLibraryStore.GameKey(fx.Vm.SelectedGame!)].Mode);

        // The evidence changes (an HDR mod installed): the kept profile applies again.
        InstallRenoDx(fx, fx.Vm.SelectedGame!);
        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
        await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);
        Assert.True(runtime.ShowHdrSection);
        Assert.Equal("gamescope --hdr-enabled -- %command%", runtime.GetSelectedHdrLaunchOptions());
    }

    [Fact]
    public async Task TheAnswerArrivingLater_IsRaisedForTheView()
    {
        var game = _games.Game("Digging Quest", "direct");
        var wiki = Wiki(game, "false");
        using var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], null, new LegacyHdrWsiViewModelTests.RecordingLauncher());
        var runtime = fx.Vm.Runtime;
        var raised = 0;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(runtime.ShowHdrSection)) Interlocked.Increment(ref raised);
        }
        runtime.PropertyChanged += OnChanged;
        try
        {
            runtime.HdrSupport = Service(wiki);
            Assert.NotNull(runtime.HdrSupportLookup);
            await runtime.HdrSupportLookup!.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
            Assert.False(runtime.ShowHdrSection);
            Assert.True(raised >= 2, $"raised {raised}"); // at the start (shown, not known yet) and when the answer landed
        }
        finally { runtime.PropertyChanged -= OnChanged; }
    }

    [Fact]
    public async Task AFullRefresh_AsksPcgwAgain()
    {
        var game = _games.Game("Digging Quest", "direct");
        var wiki = Wiki(game, "false");
        using var fx = await StartAsync(game, wiki);
        var asked = wiki.Requests.Count;

        await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!); // a plain re-analysis does not
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        Assert.Null(fx.Vm.Runtime.HdrSupportLookup);
        Assert.Equal(asked, wiki.Requests.Count);

        wiki.Pages["Digging Quest"] = HdrSupportServiceTests.Wikitext("true");
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        if (fx.Vm.Runtime.HdrSupportLookup is { } lookup) await lookup.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        Assert.True(wiki.Requests.Count > asked);
        Assert.True(fx.Vm.Runtime.ShowHdrSection);
    }

    [Theory]
    [InlineData("en-US", "HDR: listed as supported by PCGamingWiki.", "HDR: an HDR mod is installed (")]
    [InlineData("pt-BR", "HDR: listado como compatível pelo PCGamingWiki.", "HDR: um mod de HDR está instalado (")]
    public async Task TheTooltip_IsInTheUiLanguage(string language, string listed, string mod)
    {
        UiLocalizationService.Apply(language);
        try
        {
            var yes = _games.Game("Control Quest", "direct");
            using (var fx = await StartAsync(yes, Wiki(yes, "true"), language: language))
                Assert.Equal(listed, fx.Vm.Runtime.HdrSectionTip);
            var no = _games.Game("Digging Quest", "direct");
            using (var fx = await StartAsync(no, Wiki(no, "false"), language: language))
            {
                InstallRenoDx(fx, fx.Vm.SelectedGame!);
                await fx.Vm.Session.ReprobeAsync(fx.Vm.SelectedGame!);
                await fx.WhenSelectionProbeIdleAsync(LegacyHdrWsiViewModelTests.HangGuard);
                Assert.StartsWith(mod, fx.Vm.Runtime.HdrSectionTip, StringComparison.Ordinal);
            }
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<ViewModelFixture> StartAsync(DetectedGame game, HdrSupportServiceTests.FakeWiki wiki,
        LegacyHdrWsiViewModelTests.RecordingLauncher? launcher = null, Action<LinuxGameLibraryStore>? seed = null, string language = "en-US")
    {
        var fx = await LegacyHdrWsiViewModelTests.StartAsync([game], LegacyHdrWsiViewModelTests.Modern, launcher ?? new LegacyHdrWsiViewModelTests.RecordingLauncher(), seed, language);
        fx.Vm.Runtime.HdrSupport = Service(wiki);
        if (fx.Vm.Runtime.HdrSupportLookup is { } lookup) await lookup.WaitAsync(LegacyHdrWsiViewModelTests.HangGuard);
        return fx;
    }

    private HdrSupportService Service(HdrSupportServiceTests.FakeWiki wiki)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        return new HdrSupportService(new PcGamingWikiGraphicsApiService(new HttpClient(wiki), Path.Combine(dir, "pcgw-api.json"), minRequestGap: TimeSpan.Zero),
            cachePath: Path.Combine(dir, "pcgw_hdr_cache.json"), overridesJson: """{"games":{}}""");
    }

    private static HdrSupportServiceTests.FakeWiki Wiki(DetectedGame game, string hdr)
        => new() { Search = { [game.Name] = game.Name.Replace(' ', '_') }, Pages = { [game.Name] = HdrSupportServiceTests.Wikitext(hdr) } };

    /// <summary>RenoDX as RHI installs it: its record and its add-on on disk (a row reads Installed only with both).</summary>
    private static void InstallRenoDx(ViewModelFixture fx, DetectedGame game)
    {
        var bin = game.BinaryDirectory!;
        var addon = Path.Combine(bin, "renodx-diggingquest.addon64");
        File.WriteAllBytes(addon, [0x4D, 0x5A, 0x90, 0x00]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = bin, Version = "1", Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        });
    }
}
