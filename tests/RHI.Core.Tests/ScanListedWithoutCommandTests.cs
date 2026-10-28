using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w12 follow-up (merge review nit 1): games a detector listed without a launch command (Lutris prefixes whose store entries
/// each start another game, Wine prefixes whose runtime markers cannot be read) get their own plain line in the scan notice,
/// naming the control that fixes it, EN + PT-BR; they are never counted in the "N item(s) skipped" line. w12 final review
/// m1: the line counts only games of the final library still without a command (Edit game's commands applied), and is left
/// out at 0; with nothing else to say there is no notice at all.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ScanListedWithoutCommandTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "rhi-listed-a");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "rhi-listed-b");

    private static DetectedGame Listed(string path, string? command = null)
        => new() { Name = Path.GetFileName(path), Source = "Lutris", InstallPath = path, LaunchCommand = command };

    [Theory]
    [InlineData("en-US", "Lutris: 2 game(s) listed without a launch command — set one in Edit game", "skipped")]
    [InlineData("pt-BR", "Lutris: 2 jogo(s) listado(s) sem comando de execução — defina um em Editar jogo", "ignorado")]
    public void ListedWithoutACommand_HasItsOwnLine_NeverTheSkippedCount(string language, string line, string skippedWord)
    {
        var scan = new GameLibraryScanResult([], [new DetectorScanStatus("Lutris", 5, null, [], ListedWithoutLaunchCommand: [A, B])], []);
        UiLocalizationService.Apply(language);
        try
        {
            var problems = LibraryScanStatusText.Problems(scan, null, [Listed(A), Listed(B)]);

            Assert.NotNull(problems);
            Assert.Contains(line, problems, StringComparison.Ordinal);
            Assert.DoesNotContain(skippedWord, problems, StringComparison.Ordinal);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void ARealSkip_BesideListedGames_KeepsBothLines()
    {
        var scan = new GameLibraryScanResult([], [new DetectorScanStatus("Wine", 3, null, ["Wine prefix folder /x skipped"], ListedWithoutLaunchCommand: [A])], []);
        UiLocalizationService.Apply("en-US");

        var problems = LibraryScanStatusText.Problems(scan, null, [Listed(A)])!;

        Assert.Contains("Wine: 1 item(s) skipped", problems, StringComparison.Ordinal);
        Assert.Contains("Wine: 1 game(s) listed without a launch command — set one in Edit game", problems, StringComparison.Ordinal);
    }

    // m1: a game whose command the user set (or that left the library) is no longer counted; at 0 the line goes, and a notice
    // with nothing else in it goes too (never an empty "Scan problems:"); a real skip beside it keeps its own line.
    [Fact]
    public void OnlyGamesStillWithoutACommand_AreCounted_AndZeroLeavesNoLine()
    {
        UiLocalizationService.Apply("en-US");
        var listedOnly = new GameLibraryScanResult([], [new DetectorScanStatus("Lutris", 2, null, [], ListedWithoutLaunchCommand: [A, B])], []);
        var withSkip = new GameLibraryScanResult([], [new DetectorScanStatus("Lutris", 2, null, ["x skipped"], ListedWithoutLaunchCommand: [A, B])], []);

        Assert.Contains("Lutris: 1 game(s) listed without a launch command",
            LibraryScanStatusText.Problems(listedOnly, null, [Listed(A, "lutris lutris:rungame/a"), Listed(B)]), StringComparison.Ordinal);
        Assert.Contains("Lutris: 1 game(s) listed without a launch command",
            LibraryScanStatusText.Problems(listedOnly, null, [Listed(B)]), StringComparison.Ordinal);
        Assert.Null(LibraryScanStatusText.Problems(listedOnly, "/state/rhi.log", [Listed(A, "a"), Listed(B, "b")]));
        var skipOnly = LibraryScanStatusText.Problems(withSkip, null, [Listed(A, "a"), Listed(B, "b")])!;
        Assert.Contains("Lutris: 1 item(s) skipped", skipOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("listed without a launch command", skipOnly, StringComparison.Ordinal);
    }

    // The detector's paths are canonical (PathIdentity): a library game spelled with a trailing separator is the same game.
    [Fact]
    public void LibraryPaths_AreMatchedByIdentity()
    {
        UiLocalizationService.Apply("en-US");
        var scan = new GameLibraryScanResult([], [new DetectorScanStatus("Lutris", 1, null, [], ListedWithoutLaunchCommand: [PathIdentity.Canonicalize(A)])], []);

        Assert.Contains("Lutris: 1 game(s)", LibraryScanStatusText.Problems(scan, null, [Listed(A + Path.DirectorySeparatorChar)]), StringComparison.Ordinal);
        // A game whose install path Edit game re-pointed is still found by the folder it was detected in.
        var moved = Listed(B);
        moved.LibraryIdentityPath = A;
        Assert.Contains("Lutris: 1 game(s)", LibraryScanStatusText.Problems(scan, null, [moved]), StringComparison.Ordinal);
    }

    // A detector reports each path canonically (a folder reached through a symlink is its real folder) and once.
    [Fact]
    public async Task Diagnostics_CollectCanonicalPaths_EachOnce()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-listed-canon-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root, "real", "Game")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "real"));
            var viaLink = Path.Combine(root, "link", "Game");

            var (_, _, listed) = await DetectorDiagnostics.CollectScanAsync(() =>
            {
                DetectorDiagnostics.ListedWithoutLaunchCommand([viaLink, real], "test");
                return Task.FromResult(0);
            });

            Assert.Equal([PathIdentity.Canonicalize(real)], listed);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // m1 through the app: two games listed without a command; Edit game's command on one → "1 game(s)"; on both → no notice.
    [Theory]
    [InlineData(1, "Commandless: 1 game(s) listed without a launch command — set one in Edit game")]
    [InlineData(2, null)]
    public async Task EditGameCommands_AreAppliedBeforeTheLineCounts(int withCommand, string? expected)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-listed-vm-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            UiLocalizationService.Apply("en-US");
            var detector = new CommandlessDetector(Directory.CreateDirectory(Path.Combine(root, "Game One")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "Game Two")).FullName);
            using var fx = await ViewModelFixture.StartAsync([], detectors: [detector], seed: library =>
            {
                var state = library.Load();
                foreach (var game in detector.Games().Take(withCommand))
                    state.GameEdits[LinuxGameLibraryStore.GameKey(game)] = new LinuxGameEditOverride { LaunchCommand = "lutris lutris:rungame/" + game.Name };
                library.Save(state);
            });
            var lines = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lock (lines) lines.Add(fx.Vm.StatusText); };

            await fx.Vm.RefreshCommand.ExecuteAsync(null);

            string[] seen;
            lock (lines) seen = lines.ToArray();
            Assert.Equal(withCommand, fx.Vm.Games.Count(row => !string.IsNullOrWhiteSpace(row.Game.LaunchCommand)));
            if (expected is null)
            {
                Assert.DoesNotContain(seen, line => line.Contains("listed without a launch command", StringComparison.Ordinal));
                Assert.DoesNotContain(seen, line => line.Contains("Scan problems", StringComparison.Ordinal));
            }
            else Assert.Contains(seen, line => line.Contains(expected, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>A detector that lists two games without a launch command, as Lutris and Wine do (fresh games per scan).</summary>
    private sealed class CommandlessDetector(params string[] folders) : IGameDetector
    {
        public string Name => "Commandless";

        public IEnumerable<DetectedGame> Games() => folders.Select(folder => new DetectedGame
        {
            Name = Path.GetFileName(folder), Source = "Lutris", InstallPath = folder, Runner = "wine", RequiresExplicitPatchTarget = true
        });

        public Task<IReadOnlyList<DetectedGame>> DetectAsync(CancellationToken cancellationToken = default)
        {
            var games = Games().ToArray();
            DetectorDiagnostics.ListedWithoutLaunchCommand(games.Select(game => game.InstallPath).ToArray(), "test: listed without a launch command");
            return Task.FromResult<IReadOnlyList<DetectedGame>>(games);
        }
    }
}
