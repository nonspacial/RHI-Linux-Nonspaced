using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// w14/layouts-c: upstream 2.9.0+ per-game launch arguments (Game Overrides and the Tabs Launch menu), and the library-wide
// update check behind the top bar's Update All count (UpdatesViewModel.CheckOnlyPass).
public partial class MainWindowViewModel
{
    private LaunchArgumentsPresenter? _launchArguments;

    /// <summary>Game Overrides' Launch arguments box and Reset launch options, for the selected game.</summary>
    public LaunchArgumentsPresenter LaunchArguments => _launchArguments ??= new LaunchArgumentsPresenter(this);

    /// <summary>The top bar's results also mark the game list's rows (upstream: the orange dot; Spotlight's covers read the same).</summary>
    private TopBarPresenter WithRowMarks(TopBarPresenter bar)
    {
        bar.UpdatesChanged += MarkRowUpdates;
        return bar;
    }

    /// <summary>Each row's update mark from the top bar's results (a game Update All covers with an update RHI knows of).</summary>
    internal void MarkRowUpdates()
    {
        if (_topBar is not { } bar) return;
        foreach (var game in _library.AllGames)
            _library.RowFor(game)?.SetHasUpdate(!game.IsHidden && bar.HasUpdate(LinuxGameLibraryStore.GameKey(game)));
    }

    /// <summary>
    /// Launch ▾'s first entry (upstream "Executable: name" or "auto-detect"): the program RHI starts, by file name; a launch
    /// command typed in Edit game, or a store hand-off, names no program of the game's, so it reads auto-detect.
    /// </summary>
    public string LaunchMenuExecutableText
    {
        get
        {
            var exe = SelectedGame is { UserLaunchCommand: null } game ? game.LaunchExecutablePath ?? game.ExecutablePath : null;
            return UiLocalizationService.Format("LcMenu.Executable",
                string.IsNullOrWhiteSpace(exe) ? UiLocalizationService.Text("LcMenu.ExecutableAuto") : Path.GetFileName(exe));
        }
    }

    // w14/launcher-options (#318): where the launcher-options record (config) and its journal and copies (data) live.
    internal string LauncherOptionsConfigRoot => _settingsStore.ConfigRoot;
    internal string LauncherOptionsDataRoot => _settingsStore.DataRoot;
}
