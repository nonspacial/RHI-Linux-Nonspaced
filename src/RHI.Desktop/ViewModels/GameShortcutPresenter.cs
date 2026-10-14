using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Desktop / applications-menu shortcuts for a library card (#162): created only from the card's right-click
/// "Create shortcut" choice (owner decision), and the other half — a shortcut's <c>--launch &lt;GameKey&gt;</c> arriving at
/// this RHI (its own command line or the single-instance hand-off) — started through the normal Play path.
/// </summary>
public sealed class GameShortcutPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly DesktopShortcutService _shortcuts;

    public GameShortcutPresenter(DesktopShortcutService shortcuts) => _shortcuts = shortcuts;

    /// <summary>Test seam: how long a shortcut launch waits for the selected game's analysis (default 2 minutes).</summary>
    internal TimeSpan AnalysisWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Creates the shortcut and returns the status line for it.</summary>
    public async Task<string> CreateAsync(DetectedGame game, ShortcutPlace place)
    {
        var result = await _shortcuts.CreateAsync(game, LinuxGameLibraryStore.GameKey(game), place);
        var where = T(place == ShortcutPlace.Desktop ? "Parity.ShortcutOnDesktop" : "Parity.ShortcutInMenu");
        return result.Outcome switch
        {
            ShortcutOutcome.Created => TF("Parity.ShortcutCreated", game.Name, where, result.FilePath),
            ShortcutOutcome.Updated => TF("Parity.ShortcutUpdated", game.Name, where, result.FilePath),
            ShortcutOutcome.NoDesktopFolder => T("Parity.ShortcutNoDesktopFolder"),
            ShortcutOutcome.ForeignFileInTheWay => TF("Parity.ShortcutForeignFile", result.FilePath),
            ShortcutOutcome.NoRhiCommand => T("Parity.ShortcutNoRhiCommand"),
            ShortcutOutcome.UnusableKey => TF("Parity.ShortcutUnusableKey", game.Name),
            _ => TF("Parity.ShortcutFailed", game.Name, UiLocalizationService.Present(result.Error))
        };
    }

    /// <summary>The library game a shortcut's key names: its current key, or the key of a card merged into it.</summary>
    public static DetectedGame? FindByKey(IEnumerable<DetectedGame> games, string gameKey)
    {
        var all = games.ToList();
        return all.FirstOrDefault(game => LinuxGameLibraryStore.GameKey(game) == gameKey)
               ?? all.FirstOrDefault(game => game.MergedAliases.Any(alias => LinuxGameLibraryStore.GameKey(alias) == gameKey));
    }

    /// <summary>
    /// Starts the game a shortcut names, through the Play path: waits for the library, selects the game, waits for its
    /// analysis (the HDR plan needs it) and runs Play. Every outcome is reported in the status line. UI thread.
    /// </summary>
    public async Task LaunchByKeyAsync(MainWindowViewModel vm, string gameKey)
    {
        await vm.Initialization;
        if (FindByKey(vm.AllLibraryGames, gameKey) is not { } game)
        {
            vm.ReportParityNotice(T("Parity.ShortcutGameNotFound"));
            return;
        }
        vm.SelectedGame = game;
        await Task.Yield();
        var waited = TimeSpan.Zero;
        while (vm.IsSelectionProbeBusy && waited < AnalysisWait)
        {
            await Task.Delay(100);
            waited += TimeSpan.FromMilliseconds(100);
        }
        if (!ReferenceEquals(vm.SelectedGame, game)) return; // the user picked another game meanwhile: theirs wins
        if (!vm.LaunchCommand.CanExecute(null))
        {
            vm.ReportParityNotice(TF("Parity.ShortcutCannotLaunch", game.Name, vm.LaunchToolTip));
            return;
        }
        await vm.LaunchCommand.ExecuteAsync(null);
    }
}
