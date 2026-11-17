using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Launch.LauncherOptions;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// w14/launcher-options (#318, a Linux addition): the Launch arguments row's "Add to Steam launch options" (Lutris arguments,
// Heroic game arguments) button. It writes the game's arguments into the launcher's own options, so the game gets them
// however it is started; Remove, Reset launch options, Reset Overrides and a cleared box take RHI's part out again, and an
// edit of the box or a display mode switch updates the launcher's copy. While the launcher runs nothing is written: the
// change waits until the launcher has closed (or the user stops waiting).
public sealed partial class LaunchArgumentsPresenter
{
    private LauncherOptionsService? _launcherOptions;
    private readonly Dictionary<string, DetectedGame> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private bool _watching;

    /// <summary>The launcher writer (tests hand in one on a fake home); built on first use, with its start-up recovery.</summary>
    internal LauncherOptionsService LauncherOptions
    {
        get
        {
            if (_launcherOptions is null)
            {
                _launcherOptions = new LauncherOptionsService(new LauncherOptionsStore(Path.Combine(_shell.LauncherOptionsConfigRoot, "launcher-options.json")),
                    Path.Combine(_shell.LauncherOptionsDataRoot, "launcher-options"));
                RecoverLauncherOptions(_launcherOptions);
            }
            return _launcherOptions;
        }
        set
        {
            _launcherOptions = value;
            RecoverLauncherOptions(value);
            LoadLauncher();
        }
    }

    /// <summary>Test seam: the wait between checks for a launcher that has closed.</summary>
    internal Func<CancellationToken, Task> WaitForLauncher { get; set; } = ct => Task.Delay(TimeSpan.FromSeconds(3), ct);

    /// <summary>The button beside Reset launch options ("Add to Steam launch options", or "Remove from …").</summary>
    [ObservableProperty] private bool showLauncherButton;
    [ObservableProperty] private string launcherButtonText = "";
    [ObservableProperty] private string launcherButtonTip = "";

    /// <summary>The second line under the box: the launcher's copy, or the wait for the launcher to close.</summary>
    [ObservableProperty] private string launcherCopyNote = "";

    /// <summary>The launcher line the last <see cref="Save"/> produced (appended to its status line).</summary>
    internal string LastLauncherLine { get; private set; } = "";

    private static string Key(DetectedGame game) => LinuxGameLibraryStore.GameKey(game);

    private static void RecoverLauncherOptions(LauncherOptionsService service)
    {
        try
        {
            if (service.Recover() is > 0 and var restored)
                RHI.Core.Services.AppLog.Warn("launcher-options", $"Start-up: put back {restored} launcher file(s) an interrupted RHI write had changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("launcher-options", "Launcher options recovery failed; tried again at the next start", ex);
        }
    }

    /// <summary>The game's launcher record, when RHI put arguments into the launcher's options.</summary>
    internal LauncherOptionsRecord? LauncherRecord(DetectedGame game) => LauncherOptions.RecordFor(Key(game));

    /// <summary>Whether the game's arguments are in its launcher's options (the button was used and not undone).</summary>
    internal bool InLauncher(DetectedGame game) => LauncherRecord(game) is not null;

    /// <summary>Whether the launcher holds the game's current arguments (then RHI's own launch leaves them to the launcher).</summary>
    internal bool InLauncherNow(DetectedGame game)
        => LauncherRecord(game) is { } record && string.Equals(record.Arguments, Saved(game), StringComparison.Ordinal);

    internal bool IsWaitingForLauncher(DetectedGame game) => _waiting.ContainsKey(Key(game));

    private static string LauncherName(LauncherKind kind) => kind switch
    {
        LauncherKind.Steam => "Steam",
        LauncherKind.Lutris => "Lutris",
        _ => "Heroic"
    };

    private static string Where(LauncherKind kind) => T(kind switch
    {
        LauncherKind.Steam => "LoArgs.WhereSteam",
        LauncherKind.Lutris => "LoArgs.WhereLutris",
        _ => "LoArgs.WhereHeroic"
    });

    /// <summary>The button and the launcher line for the selected game.</summary>
    private void LoadLauncher()
    {
        if (_game is not { } game || LauncherOptionsService.KindOf(game) is not { } kind)
        {
            (ShowLauncherButton, LauncherButtonText, LauncherButtonTip, LauncherCopyNote) = (false, "", "", "");
            return;
        }
        var record = LauncherRecord(game);
        var waiting = IsWaitingForLauncher(game);
        ShowLauncherButton = record is not null || Saved(game) is not null || waiting;
        if (waiting)
        {
            LauncherButtonText = T("LoArgs.StopWaiting");
            LauncherButtonTip = TF("LoArgs.StopWaitingTip", LauncherName(kind));
            LauncherCopyNote = TF("LoArgs.Waiting", LauncherName(kind));
        }
        else if (record is not null)
        {
            LauncherButtonText = TF("LoArgs.Remove", Where(record.Launcher));
            LauncherButtonTip = TF("LoArgs.RemoveTip", Where(record.Launcher));
            LauncherCopyNote = TF("LoArgs.InLauncher", Where(record.Launcher), record.Files.FirstOrDefault()?.Written ?? "");
        }
        else
        {
            LauncherButtonText = TF("LoArgs.Add", Where(kind));
            LauncherButtonTip = TF("LoArgs.AddTip", Where(kind), LauncherName(kind));
            LauncherCopyNote = "";
        }
    }

    /// <summary>The button: adds the arguments to the launcher's options, takes them out again, or stops waiting.</summary>
    [RelayCommand]
    public void ToggleLauncherOptions()
    {
        if (_game is not { } game) return;
        if (_waiting.Remove(Key(game)))
        {
            _shell.StatusText = TF("LoArgs.StoppedWaiting", game.DisplayName());
            LoadLauncher();
            return;
        }
        // Text typed but not yet saved (the click came before the box lost focus) is what the user means.
        if (!string.Equals((Text ?? "").Trim(), Saved(game) ?? "", StringComparison.Ordinal))
        {
            Commit();
            if (InLauncher(game)) return; // the save already updated the launcher copy
        }
        if (InLauncher(game))
        {
            _shell.StatusText = Line(game, LauncherOptions.Remove(Key(game)), removing: true);
        }
        else if (Saved(game) is { } arguments)
        {
            _shell.StatusText = Line(game, LauncherOptions.Apply(game, Key(game), arguments), removing: false);
        }
        else
        {
            _shell.StatusText = T("LoArgs.NothingToAdd");
        }
        Load();
    }

    /// <summary>
    /// After the game's arguments changed (the box, Reset, Reset Overrides, a display mode switch): the launcher's copy follows
    /// (updated, or RHI's part taken out when they were cleared). Only for a game whose arguments are in its launcher. Returns
    /// the line to add to the status, or "".
    /// </summary>
    internal string SyncLauncher(DetectedGame game)
    {
        if (LauncherRecord(game) is not { } record) return "";
        var saved = Saved(game);
        if (string.Equals(record.Arguments, saved, StringComparison.Ordinal)) return "";
        var result = saved is null ? LauncherOptions.Remove(Key(game)) : LauncherOptions.Apply(game, Key(game), saved);
        var line = Line(game, result, removing: saved is null);
        if (ReferenceEquals(game, _game)) LoadLauncher();
        return line;
    }

    // The status line for a launcher write; a refusal because the launcher runs queues the change for when it has closed.
    private string Line(DetectedGame game, LauncherOptionsResult result, bool removing)
    {
        var kind = result.Launcher ?? LauncherOptionsService.KindOf(game) ?? LauncherKind.Steam;
        switch (result.Outcome)
        {
            case LauncherOptionsOutcome.Written:
                return TF("LoArgs.Added", game.DisplayName(), Where(kind), result.Value ?? "");
            case LauncherOptionsOutcome.Removed:
                return result.KeptUserChange ? TF("LoArgs.RemovedKept", game.DisplayName(), Where(kind)) : TF("LoArgs.RemovedRestored", game.DisplayName(), Where(kind));
            case LauncherOptionsOutcome.Nothing:
                return "";
            case LauncherOptionsOutcome.LauncherRunning:
                _waiting[Key(game)] = game;
                if (ReferenceEquals(game, _game)) LoadLauncher();
                _ = WatchLaunchersAsync();
                return TF(removing ? "LoArgs.RunningRemove" : "LoArgs.RunningAdd", LauncherName(kind));
            case LauncherOptionsOutcome.NotSupported:
                return T(kind switch
                {
                    LauncherKind.Steam => "LoArgs.NoSteamFile",
                    LauncherKind.Lutris => "LoArgs.NoLutrisFile",
                    _ => "LoArgs.NoHeroicFile"
                });
            default:
                return TF("LoArgs.Failed", Where(kind), result.Detail ?? "");
        }
    }

    /// <summary>Applies the changes that waited for a launcher to close, once it has (one watcher at a time; UI thread).</summary>
    private async Task WatchLaunchersAsync()
    {
        if (_watching) return;
        _watching = true;
        try
        {
            while (_waiting.Count > 0)
            {
                await WaitForLauncher(CancellationToken.None);
                foreach (var (key, game) in _waiting.ToList())
                {
                    var kind = LauncherRecord(game)?.Launcher ?? LauncherOptionsService.KindOf(game);
                    if (kind is { } k && LauncherOptions.IsRunning(k)) continue;
                    _waiting.Remove(key);
                    var saved = Saved(game);
                    LauncherOptionsResult result;
                    if (saved is null) result = LauncherOptions.Remove(key);
                    else result = LauncherOptions.Apply(game, key, saved);
                    if (result.Outcome == LauncherOptionsOutcome.LauncherRunning) { _waiting[key] = game; continue; } // started again
                    var line = Line(game, result, removing: saved is null);
                    if (line.Length > 0) _shell.StatusText = TF("LoArgs.AfterClose", LauncherName(kind ?? LauncherKind.Steam), line);
                    if (ReferenceEquals(game, _game)) Load();
                }
            }
        }
        finally { _watching = false; }
    }
}
