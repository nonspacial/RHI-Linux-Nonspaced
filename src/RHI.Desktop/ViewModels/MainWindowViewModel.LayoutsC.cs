using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

// w14/layouts-c: upstream 2.9.0+ per-game launch arguments (Game Overrides and the Tabs Launch menu), and the library-wide
// update check behind the top bar's Update All count (UpdatesViewModel.CheckOnlyPass).
public partial class MainWindowViewModel
{
    private LaunchArgumentsPresenter? _launchArguments;

    /// <summary>Game Overrides' Launch arguments box and Reset launch options, for the selected game.</summary>
    public LaunchArgumentsPresenter LaunchArguments => _launchArguments ??= new LaunchArgumentsPresenter(this);

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
}
