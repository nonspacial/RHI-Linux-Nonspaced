using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 view-model pins (upstream 2.8.8 <c>713c222</c>, <c>d61a696</c>, <c>f170aa6</c>/<c>865f784</c>): the DLSS section
/// re-reads the game from disk after a swap (upstream showed the version from before the merge), the DLSS batch's result
/// line, and the Streamline dropdown showing the manifest's <c>2.14.16.0-v2</c> label as it is.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class Upstream288PinsViewModelTests
{
    private const string DlssUrl = "https://github.com/rhi-tests/dlss/releases/download/b/dlss.zip";
    private const string Version = "310.5.2";

    private static readonly string Manifest = $$"""
        {
          "dlss": [{ "version": "{{Version}}", "url": "{{DlssUrl}}" }], "dlssd": [], "dlssg": [], "dlssnr": [],
          "streamline": [{ "version": "2.14.16.0-v2", "url": "https://github.com/rhi-tests/dlss/releases/download/v2/streamline.zip" },
                         { "version": "2.14.1.0", "url": "https://github.com/rhi-tests/dlss/releases/download/old/streamline.zip" }]
        }
        """;

    // item 1: after a swap the section shows what is on disk now (RHI applied, Restore offered), not the state from
    // before the swap; the swap's own line stays on screen.
    [Fact]
    public async Task Pin288_DlssSwap_ReReadsTheGameFromDisk()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer());
        var game = fx.Game("Pin288 Swap");
        var dll = Path.Combine(game.InstallPath, "nvngx_dlss.dll");
        File.WriteAllBytes(dll, DlssAutoFollowServiceTests.Pe("310.1.0"));
        await AddAndAnalyseAsync(fx, game);
        var selected = fx.Vm.SelectedGame!;
        var before = fx.Vm.Dlss.DlssSrDeploymentState;
        Assert.False(fx.Vm.Dlss.DlssSrRestoreAvailable);
        Assert.Equal(Version, fx.Vm.Dlss.SelectedDlssSrVersion);

        await fx.Vm.Dlss.SwapDlssComponentCommand.ExecuteAsync("SR");

        Assert.Equal(Version + ".0", DlssAutoFollowService.PeFileVersion(dll));
        Assert.True(fx.Vm.Dlss.DlssSrRestoreAvailable);
        Assert.NotEqual(before, fx.Vm.Dlss.DlssSrDeploymentState);
        Assert.Contains(dll + TransactionalFileDeploymentService.BackupExtension, fx.Vm.Session.CachedFiles(selected)!);
        Assert.StartsWith("Super resolution", fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // item 2 (the "lists it" half): the DLSS batch's result line names the game that failed, as upstream's summary does
    // ("Failed: 1 game(s) • <game>: <reason>"). Today the line only counts it ("… · 1 failed.").
    [Fact(Skip = "#249: the DLSS batch line counts a failed game but does not name it (w12-pins progress file, Decision needed)")]
    public async Task Pin288_DlssBatch_ResultLineNamesTheFailedGame()
    {
        var payloads = 0;
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: request =>
            request.RequestUri!.AbsoluteUri == DlssUrl && Interlocked.Increment(ref payloads) == 1 ? null : Answer()(request));
        var failing = fx.Game("Pin288 Failing");
        File.WriteAllBytes(Path.Combine(failing.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
        var good = fx.Game("Pin288 Good");
        File.WriteAllBytes(Path.Combine(good.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
        await AddAndAnalyseAsync(fx, failing);
        await AddAndAnalyseAsync(fx, good);
        var games = fx.Vm.Games.Select(row => row.Game).OrderBy(g => g.Name == "Pin288 Good" ? 1 : 0).ToArray();

        var outcome = await fx.Vm.Dlss.ApplyDlssBatchAsync(games, new LinuxDlssVersionDefaults(Version));

        Assert.Equal((1, 1), (outcome.Result.FailedCount, outcome.Result.ChangedCount));
        Assert.Contains("Pin288 Failing", outcome.Line, StringComparison.Ordinal);
    }

    // item 12: the Streamline dropdown lists the manifest's labels as they are (2.14.16.0-v2, not 2.14.16.0) and
    // starts on the newest.
    [Fact]
    public async Task Pin288_StreamlineDropdown_ShowsTheV2Label()
    {
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer());

        Assert.Equal(["2.14.16.0-v2", "2.14.1.0"], fx.Vm.Dlss.StreamlineVersions);
        Assert.Equal("2.14.16.0-v2", fx.Vm.Dlss.SelectedStreamlineVersion);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage?> Answer() => request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == DlssManagerService.ManifestUrl)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") };
        if (url != DlssUrl) return null;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("nvngx_dlss.dll");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(DlssAutoFollowServiceTests.Pe(Version));
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
    };

    private static async Task AddAndAnalyseAsync(ViewModelFixture fx, DetectedGame game)
    {
        var added = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added.Added, added.Message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(fx.Vm.Session.Analysis);
    }
}
