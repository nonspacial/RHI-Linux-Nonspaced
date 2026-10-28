using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 view-model pins (upstream 2.8.8 <c>713c222</c>, <c>d61a696</c>, <c>f170aa6</c>/<c>865f784</c>): the DLSS section
/// re-reads the game from disk after a swap (upstream showed the version from before the merge), the DLSS result lines
/// naming each failed game with its reason (batch apply/restore, Apply defaults, Restore all), and the Streamline dropdown showing the manifest's <c>2.14.16.0-v2</c> label as it is.
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

    // item 2 (the "lists it" half, fixed in this wave): the DLSS batch's result line names the game that failed with its
    // reason, as upstream's summary does ("Failed: 1 game(s) • <game>: <reason>"); the game that worked is not named.
    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Pin288_DlssBatch_ResultLineNamesTheFailedGame(string language)
    {
        var payloads = 0;
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, httpResponse: request =>
            request.RequestUri!.AbsoluteUri == DlssUrl && Interlocked.Increment(ref payloads) == 1 ? null : Answer()(request));
        try
        {
            UiLocalizationService.Apply(language);
            var failing = fx.Game("Pin288 Failing");
            File.WriteAllBytes(Path.Combine(failing.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
            var good = fx.Game("Pin288 Good");
            File.WriteAllBytes(Path.Combine(good.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
            await AddAndAnalyseAsync(fx, failing);
            await AddAndAnalyseAsync(fx, good);
            var games = fx.Vm.Games.Select(row => row.Game).OrderBy(g => g.Name == "Pin288 Good" ? 1 : 0).ToArray();

            var outcome = await fx.Vm.Dlss.ApplyDlssBatchAsync(games, new LinuxDlssVersionDefaults(Version));

            Assert.Equal((1, 1), (outcome.Result.FailedCount, outcome.Result.ChangedCount));
            Assert.StartsWith(Counts(language, changed: 1, failed: 1), outcome.Line, StringComparison.Ordinal);
            AssertNamesOnlyTheFailedGame(language, outcome.Line, "Pin288 Failing", "Pin288 Good");
            Assert.Equal(outcome.Line, fx.Vm.StatusText);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // The line names exactly what FailedCount counts: never a skipped or deferred item (even one marked unsuccessful),
    // each game once with its reasons in order, the count in games.
    [Fact]
    public void Pin288_FailedGamesLine_NamesOnlyTheFailedItems_OnceAGame()
    {
        UiLocalizationService.Apply("en-US");
        const LinuxDlssBatchComponent sr = LinuxDlssBatchComponent.SuperResolution, rr = LinuxDlssBatchComponent.RayReconstruction;
        var result = new LinuxDlssBatchResult([
            new("Pin288 Changed", sr, true, true, false, false, "Applied."),
            new("Pin288 Skipped", sr, false, false, false, true, "Skipped."),
            new("Pin288 Deferred", sr, false, false, true, false, "Deferred."),
            new("Pin288 Failed", sr, false, false, false, false, "Could not apply SR."),
            new("Pin288 Failed", rr, false, false, false, false, "Could not apply RR."),
        ]);

        Assert.Equal(2, result.FailedCount);
        Assert.Equal("Failed: 1 game(s) • Pin288 Failed: Could not apply SR; Could not apply RR.", DlssPanelViewModel.FailedGamesLine(result));
        Assert.Equal("", DlssPanelViewModel.FailedGamesLine(new LinuxDlssBatchResult([result.Items[0], result.Items[1], result.Items[2]])));
    }

    // The other flows that show the same result line: the batch's Restore, the single-game Apply defaults and Restore all.
    // A step that fails because the game folder became read-only after the analysis (the download itself worked) is
    // named with its reason; in the batch, the game that worked is not.
    public static TheoryData<string, string> ReadOnlyFlows => new()
    {
        { "batch-restore", "en-US" }, { "batch-restore", "pt-BR" },
        { "apply-defaults", "en-US" }, { "apply-defaults", "pt-BR" },
        { "restore-all", "en-US" }, { "restore-all", "pt-BR" },
    };

    [PermissionDenialTheory]
    [MemberData(nameof(ReadOnlyFlows))]
    public async Task Pin288_DlssResultLine_NamesTheFailedGame_InEveryFlow(string flow, string language)
    {
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, httpResponse: Answer());
        var locked = fx.Game("Pin288 Locked");
        var open = flow == "batch-restore" ? fx.Game("Pin288 Open") : null;
        try
        {
            foreach (var game in open is null ? new[] { locked } : new[] { locked, open })
            {
                if (flow == "apply-defaults") File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe("310.1.0"));
                else SeedBackup(game);
                await AddAndAnalyseAsync(fx, game);
            }
            if (flow == "apply-defaults")
            {
                var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
                settings.DefaultDlssVersion = Version;
                fx.Vm.Settings.SaveLinuxSettings(settings);
            }
            UiLocalizationService.Apply(language); // a Settings save adopts the saved language
            PermissionDenial.MakeReadOnly(locked.InstallPath);
            var lines = CollectStatus(fx);

            string line;
            if (flow == "batch-restore")
            {
                var games = fx.Vm.Games.Select(row => row.Game).OrderBy(g => g.Name == "Pin288 Open" ? 1 : 0).ToArray();
                var outcome = await fx.Vm.Dlss.RestoreDlssBatchAsync(games).WaitAsync(HangGuard);
                Assert.Equal((1, 1), (outcome.Result.FailedCount, outcome.Result.ChangedCount));
                line = outcome.Line;
            }
            else
            {
                await (flow == "apply-defaults" ? fx.Vm.Dlss.QuickApplyDlssDefaultsCommand : fx.Vm.Dlss.RestoreAllDlssCommand)
                    .ExecuteAsync(null).WaitAsync(HangGuard);
                line = LineWith(lines, FailedPrefix(language, 1));
            }

            Assert.StartsWith(Counts(language, changed: open is null ? 0 : 1, failed: 1), line, StringComparison.Ordinal);
            AssertNamesOnlyTheFailedGame(language, line, "Pin288 Locked", open is null ? null : "Pin288 Open");
        }
        finally
        {
            PermissionDenial.MakeWritable(locked.InstallPath);
            UiLocalizationService.Apply("en-US");
        }
    }

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // The counts in the UI language (#249: they were shown in English in PT-BR), literal wording.
    private static string Counts(string language, int changed, int failed) => language == "pt-BR"
        ? $"DLSS/Streamline: {changed} alterado(s) · 0 já atualizado(s) · 0 ignorado(s) · 0 adiado(s) · {failed} com falha."
        : $"DLSS/Streamline: {changed} changed · 0 current · 0 skipped · 0 deferred · {failed} failed.";

    [Theory]
    [MemberData(nameof(Languages))]
    public void Pin288_CountsLine_IsInTheUiLanguage_EvenWithNoTargets(string language)
    {
        try
        {
            UiLocalizationService.Apply(language);
            Assert.Equal(language == "pt-BR" ? "Nenhum alvo de DLSS/Streamline elegível foi encontrado." : "No eligible DLSS/Streamline targets were found.",
                DlssPanelViewModel.CountsLine(new LinuxDlssBatchResult([])));
            Assert.Equal(Counts(language, changed: 1, failed: 0), DlssPanelViewModel.CountsLine(new LinuxDlssBatchResult([
                new("Pin288 Counts", LinuxDlssBatchComponent.SuperResolution, true, true, false, false, "Applied.")])));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    // Literal wording (not the table row), so a reworded or dropped row fails here.
    private static string FailedPrefix(string language, int count)
        => language == "pt-BR" ? $"Falha em {count} jogo(s) • " : $"Failed: {count} game(s) • ";

    // The failed part comes after the counts: the game named with a reason after it, no English fixed text left anywhere
    // in a Portuguese line, and the game that worked not named.
    private static void AssertNamesOnlyTheFailedGame(string language, string line, string failed, string? fine)
    {
        var at = line.IndexOf(FailedPrefix(language, 1), StringComparison.Ordinal);
        Assert.True(at >= 0, line);
        var part = line[at..];
        Assert.StartsWith(FailedPrefix(language, 1) + failed + ": ", part, StringComparison.Ordinal);
        Assert.True(part.Length > (FailedPrefix(language, 1) + failed + ": ").Length + 2, "no reason after the name: " + line);
        if (fine is not null) Assert.DoesNotContain(fine, line, StringComparison.Ordinal);
        if (language == "pt-BR") // the whole line: the counts as well as the failed part
        {
            var words = System.Text.RegularExpressions.Regex.Replace(line.Replace(failed, " ", StringComparison.Ordinal), "'[^']*'", "''"); // paths
            var english = UiLocalizationService.EnglishMarkersIn(words)
                .Where(word => !word.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}): {line}");
        }
    }

    private static string LineWith(IEnumerable<string> lines, string fragment)
    {
        var found = lines.Where(line => line.Contains(fragment, StringComparison.Ordinal)).ToArray();
        Assert.True(found.Length == 1, $"{found.Length} line(s) with '{fragment}' in: {string.Join(" | ", lines)}");
        return found[0];
    }

    private static System.Collections.Concurrent.ConcurrentQueue<string> CollectStatus(ViewModelFixture fx)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText) && fx.Vm.StatusText is { Length: > 0 } text && !lines.Contains(text))
                lines.Enqueue(text);
        };
        return lines;
    }

    // A game RHI swapped earlier: today's DLL plus the true original beside it as .original.
    private static void SeedBackup(DetectedGame game)
    {
        File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll"), DlssAutoFollowServiceTests.Pe(Version));
        File.WriteAllBytes(Path.Combine(game.InstallPath, "nvngx_dlss.dll" + TransactionalFileDeploymentService.BackupExtension),
            DlssAutoFollowServiceTests.Pe("310.1.0"));
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
