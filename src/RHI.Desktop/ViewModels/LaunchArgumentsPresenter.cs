using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/layouts-c (upstream 2.9.0+ per-game launch arguments): Game Overrides' Launch arguments box and Reset launch options,
/// for the selected game. The text is saved in the library file (<see cref="LinuxGameLibraryState.LaunchArguments"/>) when the
/// box loses focus or Enter is pressed (upstream saves on focus lost). The line under the box says how the arguments reach the
/// game on Linux (<see cref="LaunchArguments"/>): added by RHI, through Steam, or where to add them in the launcher that starts
/// the game. UI thread only.
/// </summary>
public sealed partial class LaunchArgumentsPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly MainWindowViewModel _shell;
    private DetectedGame? _game;

    /// <summary>Test seam: whether a program is on PATH (the Steam route).</summary>
    internal Func<string, bool> OnPath { get; set; } = LaunchArguments.SteamOnPath;

    internal LaunchArgumentsPresenter(MainWindowViewModel shell)
    {
        _shell = shell;
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainWindowViewModel.SelectedGame)) return;
            // Text typed for the game being left (the click that changed the game came before the box lost focus) is its own.
            if (_game is not null && !string.Equals((Text ?? "").Trim(), Saved(_game) ?? "", StringComparison.Ordinal)) Commit();
            Load();
        };
        Load();
    }

    /// <summary>The box's text (saved by <see cref="Commit"/>).</summary>
    [ObservableProperty] private string text = "";

    /// <summary>How the arguments reach the game, or where to add them (under the box).</summary>
    [ObservableProperty] private string note = "";

    /// <summary>RHI adds the arguments when it launches the selected game (the note is a hint, not a warning).</summary>
    [ObservableProperty] private bool applies;

    public bool HasGame => _game is not null;

    /// <summary>The box's tooltip (upstream's, with the Linux rule).</summary>
    public string Tip => T("LcArgs.Tip");

    /// <summary>The game's saved text, or null.</summary>
    internal string? Saved(DetectedGame game)
        => _shell.LibraryStateForUiLogic.LaunchArguments?.TryGetValue(LinuxGameLibraryStore.GameKey(game), out var text) == true ? text : null;

    /// <summary>The arguments a launch of <paramref name="game"/> gets (one entry each), empty when it has none.</summary>
    /// <remarks>#318: empty while the game's launcher holds them (its own options add them; RHI must not pass them twice).</remarks>
    internal IReadOnlyList<string> ArgumentsFor(DetectedGame game) => InLauncherNow(game) ? [] : LaunchArguments.Split(Saved(game));

    /// <summary>How the arguments reach <paramref name="game"/> when RHI launches it.</summary>
    internal LaunchArgumentsRoute RouteFor(DetectedGame game) => LaunchArguments.RouteFor(game, OnPath);

    /// <summary>Shows the selected game's text and note (selection, a save, a display mode switch).</summary>
    public void Load()
    {
        _game = _shell.SelectedGame;
        Text = _game is null ? "" : Saved(_game) ?? "";
        Note = _game is null ? "" : NoteFor(_game);
        Applies = _game is not null && (RouteFor(_game) is LaunchArgumentsRoute.Direct or LaunchArgumentsRoute.Steam || InLauncherNow(_game));
        OnPropertyChanged(nameof(HasGame));
        LoadLauncher(); // #318
    }

    /// <summary>The box lost focus or Enter was pressed: saves the text when it changed.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (_game is not { } game) return;
        var typed = Text ?? "";
        var current = Saved(game);
        var normalized = LaunchArguments.Normalize(typed);
        if (normalized is null && typed.Trim().Length > 0)
        {
            _shell.StatusText = typed.Contains('\0') ? T("LcArgs.ProblemNul") : TF("LcArgs.ProblemLength", LaunchArguments.MaxLength);
            Load();
            return;
        }
        if (string.Equals(normalized, current, StringComparison.Ordinal))
        {
            Text = current ?? "";
            return;
        }
        if (Save(game, normalized) is { } refused)
        {
            _shell.StatusText = refused;
            Load();
            return;
        }
        _shell.StatusText = (normalized is null
            ? TF("LcArgs.Cleared", game.DisplayName())
            : TF("LcArgs.Saved", game.DisplayName(), normalized) + (Applies ? "" : " " + NoteFor(game)))
            + (LastLauncherLine.Length > 0 ? " " + LastLauncherLine : ""); // #318: the launcher's copy followed
    }

    /// <summary>
    /// Game Overrides › Reset launch options (upstream: clears the launch executable and the arguments). Linux: the arguments,
    /// and the launch command typed in Edit game for a detected game (Linux's launch executable choice; the game then starts
    /// with its detected command again). A game added by hand keeps its command: it is how RHI starts it.
    /// </summary>
    [RelayCommand]
    public async Task ResetAsync()
    {
        if (_game is not { } game) return;
        var hadArgs = Saved(game) is not null;
        var inLauncher = InLauncher(game); // #318: Reset also takes RHI's part out of the launcher's options
        var hadCommand = !game.IsManuallyAdded && game.UserLaunchCommand is not null;
        if (!hadArgs && !hadCommand)
        {
            _shell.StatusText = TF("LcArgs.ResetNothing", game.DisplayName());
            return;
        }
        if (hadArgs && Save(game, null) is { } refused)
        {
            _shell.StatusText = refused;
            Load();
            return;
        }
        if (hadCommand && await _shell.ManualGames.ClearLaunchCommandAsync(game) is { } notCleared)
        {
            _shell.StatusText = notCleared;
            return;
        }
        var done = new List<string>();
        if (hadCommand) done.Add(T("LcArgs.ResetCommandPart"));
        if (hadArgs) done.Add(T("LcArgs.ResetArgsPart"));
        var line = TF("LcArgs.ResetDone", game.DisplayName(), string.Join(", ", done))
                   + (game.IsManuallyAdded && !string.IsNullOrWhiteSpace(game.LaunchCommand) ? " " + T("LcArgs.ResetManualKeepsCommand") : "")
                   + (inLauncher && hadArgs && LastLauncherLine.Length > 0 ? " " + LastLauncherLine : "");
        // A cleared command refreshed the library: the game's new "Ready — …" line carries the result instead of replacing it.
        if (hadCommand) ((ILibraryEditHost)_shell).RaiseNotice(line);
        else _shell.StatusText = line;
    }

    /// <summary>Saves (null = clears) the game's arguments; the localised refusal, or null.</summary>
    internal string? Save(DetectedGame game, string? arguments)
    {
        var library = (ILibraryEditHost)_shell;
        if (library.LibraryRepairRunning) return T("Vm.WaitForRepairAfterMove");
        var state = library.WorkingLibraryCopy();
        var map = state.LaunchArguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = LinuxGameLibraryStore.GameKey(game);
        if (LaunchArguments.Normalize(arguments) is { } text) map[key] = text;
        else map.Remove(key);
        state.LaunchArguments = map.Count == 0 ? null : map;
        if (library.SaveLibraryCopy(state) is { } refused) return refused;
        library.AdoptLibraryCopy(state);
        LastLauncherLine = SyncLauncher(game); // #318: the launcher's copy follows the saved arguments
        if (ReferenceEquals(game, _game)) Load();
        return null;
    }

    /// <summary>The line under the box for <paramref name="game"/>.</summary>
    internal string NoteFor(DetectedGame game) => RouteFor(game) switch
    {
        LaunchArgumentsRoute.Direct => T("LcArgs.RouteDirect"),
        LaunchArgumentsRoute.Steam => T("LcArgs.RouteSteam"),
        LaunchArgumentsRoute.SteamLinkOnly => T("LcArgs.RouteSteamLink"),
        LaunchArgumentsRoute.SteamShortcut => T("LcArgs.RouteSteamShortcut"),
        LaunchArgumentsRoute.UserCommand => T("LcArgs.RouteUserCommand"),
        LaunchArgumentsRoute.NoCommand => T("LcArgs.RouteNoCommand"),
        _ => LauncherNote(game)
    };

    private static string LauncherNote(DetectedGame game)
    {
        var entry = string.IsNullOrWhiteSpace(game.ParentLauncherName) ? game.DisplayName() : XboxCardText.AppName(game.ParentLauncherName)!;
        return LaunchHandoffClassifier.Classify(game) switch
        {
            LaunchHandoff.Heroic => TF("LcArgs.RouteHeroic", entry),
            LaunchHandoff.Lutris => TF("LcArgs.RouteLutris", entry),
            _ when !string.IsNullOrWhiteSpace(game.ParentLauncherName) => TF("LcArgs.RouteParentLauncher", entry),
            _ => T("LcArgs.RouteOtherLauncher")
        };
    }

    /// <summary>
    /// The launch's status line addition: the arguments RHI passes, or, when the game has arguments RHI cannot pass, where to
    /// add them. Empty when the game has none.
    /// </summary>
    internal string LaunchLine(DetectedGame game)
    {
        if (Saved(game) is not { } saved) return "";
        if (InLauncherNow(game) && LauncherRecord(game) is { } record) return TF("LoArgs.LaunchInLauncher", saved, Where(record.Launcher)); // #318
        return RouteFor(game) is LaunchArgumentsRoute.Direct or LaunchArgumentsRoute.Steam
            ? TF("LcArgs.LaunchWith", saved)
            : T("LcArgs.LaunchWithout") + " " + NoteFor(game);
    }
}
