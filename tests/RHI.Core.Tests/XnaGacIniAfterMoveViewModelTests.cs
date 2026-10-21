using System.Collections.Concurrent;
using System.ComponentModel;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #214 review 1 and 2: what Repair after move still leaves to the user (ReShade's XNA settings file in the prefix's .NET
/// assembly folder that RHI did not rewrite) reaches the app's line beside the repair's own text, in the UI language, and
/// goes to the log; an unreadable copy never turns a saved repair into "could not save". XNA ReShade is installed by a
/// stand-alone service (<see cref="XnaGacIniAfterMoveTests.InstalledAsync"/>), the library moved, then the app starts
/// with that ledger.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class XnaGacIniAfterMoveViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-xna-gac-move-vm-" + Guid.NewGuid().ToString("N"));

    public XnaGacIniAfterMoveViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task CopyTheUserChanged_TheAppLineSaysWhatToSetInWhichFile_AndTheLogHasIt(string language)
    {
        var installed = await XnaGacIniAfterMoveTests.InstalledAsync(_root, "changed-" + language);
        File.AppendAllText(installed.GacIni, "[OVERLAY]\nMine=1\n");
        var moved = installed.MoveLibrary();
        var newBase = XnaGacReShade.WindowsPathOf(moved.Game.PrefixPath!, moved.Game.BinaryDirectory!);

        var (lines, log) = await RepairInTheAppAsync(installed, moved, language);

        var note = XnaGacReShade.GacIniChangedAfterMove(moved.GacIni, newBase);
        AssertALineHas(lines, UiLocalizationService.PresentFor(language, note));
        Assert.Contains($"Terraria: {note}", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task WineRunningForThePrefix_TheAppLineSaysToCloseTheGame(string language)
    {
        var installed = await XnaGacIniAfterMoveTests.InstalledAsync(_root, "wine-" + language);
        var moved = installed.MoveLibrary();

        var (lines, log) = await RepairInTheAppAsync(installed, moved, language, wineRunning: _ => true);

        AssertALineHas(lines, UiLocalizationService.PresentFor(language, XnaGacReShade.GacIniWineRunningAfterMoveMessage));
        Assert.Contains(XnaGacReShade.GacIniWineRunningAfterMoveMessage, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyThatCannotBeRead_TheAppShowsTheRepairDone_AndWhatToDo_NotASaveFailure()
    {
        var installed = await XnaGacIniAfterMoveTests.InstalledAsync(_root, "unreadable");
        var moved = installed.MoveLibrary();
        File.SetUnixFileMode(moved.GacIni, UnixFileMode.None);
        try
        {
            if (CanRead(moved.GacIni)) return; // running as root: the mode does not stop a read

            var (lines, _) = await RepairInTheAppAsync(installed, moved, "en-US");

            var line = AssertALineHas(lines, XnaGacReShade.GacIniNotUpdatedAfterMovePrefix);
            Assert.Contains("Terraria: RHI's records and settings now point to the new folder (", line, StringComparison.Ordinal);
            var saveFailed = UiLocalizationService.Text("Win.CouldNotSave").Split('{')[0];
            Assert.DoesNotContain(lines, l => l.StartsWith(saveFailed, StringComparison.Ordinal));
        }
        finally { File.SetUnixFileMode(moved.GacIni, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public async Task NothingLeftToDo_TheAppLineIsTheRepairsOwn()
    {
        var installed = await XnaGacIniAfterMoveTests.InstalledAsync(_root, "rewritten");
        var moved = installed.MoveLibrary();

        var (lines, log) = await RepairInTheAppAsync(installed, moved, "en-US");

        var line = AssertALineHas(lines, "Terraria: RHI's records and settings now point to the new folder (");
        Assert.DoesNotContain("ReShade's settings file", line, StringComparison.Ordinal);
        Assert.DoesNotContain("repair-after-move", log, StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    // Starts the app on the stand-alone install's ledger with the moved game, runs Repair after move from the view model and
    // returns every status line it showed (PropertyChanged, so a later "Ready" line cannot hide one) and the log written.
    private async Task<(IReadOnlyList<string> Lines, string Log)> RepairInTheAppAsync(XnaGacIniAfterMoveTests.Fixture installed,
        XnaGacIniAfterMoveTests.Located moved, string language, Func<string, bool>? wineRunning = null)
    {
        using var fx = await ViewModelFixture.StartAsync([moved.Game], uiLanguage: language,
            seed: library => File.Copy(installed.LedgerPath, Path.Combine(Path.GetDirectoryName(library.LibraryPath)!, "deployments.json")),
            wineRunning: wineRunning);
        UiLocalizationService.Apply(language);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Terraria").Game;
        var lines = new ConcurrentQueue<string>();
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.StatusText)) lines.Enqueue(fx.Vm.StatusText);
        }
        var logPath = Path.Combine(_root, $"rhi-{Guid.NewGuid():N}.log");
        fx.Vm.PropertyChanged += OnChanged;
        try
        {
            using (AppLog.UseInThisFlow(new AppLog(logPath)))
                await fx.Vm.RepairAfterMoveCommand.ExecuteAsync(null);
        }
        finally
        {
            fx.Vm.PropertyChanged -= OnChanged;
            UiLocalizationService.Apply("en-US");
        }
        Assert.False(new DeploymentRelocation(fx.Records).NeedsRepair(moved.Game)); // the repair itself is done
        return (lines.ToArray(), File.Exists(logPath) ? File.ReadAllText(logPath) : "");
    }

    private static string AssertALineHas(IReadOnlyList<string> lines, string text)
    {
        var line = lines.FirstOrDefault(l => l.Contains(text, StringComparison.Ordinal));
        Assert.True(line is not null, $"no status line holds \"{text}\"; lines: {string.Join(" | ", lines)}");
        return line!;
    }

    private static bool CanRead(string path)
    {
        try { using var _ = File.OpenRead(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
