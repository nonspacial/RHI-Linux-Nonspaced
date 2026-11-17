namespace RHI.Desktop.ViewModels;

// Upstream 2.9.4 Game settings cog (DetailLayoutHost.GameSettings / .Header): a ⚙ beside the game's title holding settings
// that apply to this game only. Linux holds Block Steam updates (#290, upstream 2.9.4's row). Remove from RHI (#293) is in
// the Management section (w14/layouts-a, #304), as upstream.
public partial class MainWindowViewModel
{
    /// <summary>The ⚙ shows when a row applies: Block Steam updates (a Steam game RHI found the manifest of) or Display
    /// modes (while they are on).</summary>
    public bool ShowGameSettings => SelectedGame is not null && (CanBlockSteamUpdates || GameSettingsHasDisplayModesRow);

    /// <summary>
    /// w14/display-modes (#311, upstream 2.9.3 <c>AddDisplayModeRow</c>): the Display modes row ("Choose…" opens the game's
    /// display mode settings), for every game while display modes are on, and in a ⚙ that shows anyway (Block Steam updates).
    /// </summary>
    public bool GameSettingsHasDisplayModesRow => SelectedGame is not null && (DisplayModes.Enabled || CanBlockSteamUpdates);

    /// <summary>The Block Steam updates row is offered for the selected game.</summary>
    public bool GameSettingsHasSteamRow => CanBlockSteamUpdates;


    /// <summary>The ⚙'s tooltip and the panel's title (upstream: "Game settings").</summary>
    public string GameSettingsTitle => T("Gs.Title");

    /// <summary>
    /// The Block Steam updates switch asks for <paramref name="blocked"/>; the switch then shows what the manifest really is
    /// (<see cref="SteamUpdatesBlocked"/>), so a refused change (Steam updating the game) springs back.
    /// </summary>
    internal Task SetSteamUpdatesBlockedAsync(bool blocked)
        => blocked != SteamUpdatesBlocked ? ToggleSteamUpdatesCommand.ExecuteAsync(null) : Task.CompletedTask;

    private void NotifyGameSettings()
    {
        OnPropertyChanged(nameof(ShowGameSettings));
        OnPropertyChanged(nameof(GameSettingsHasSteamRow));
        OnPropertyChanged(nameof(GameSettingsHasDisplayModesRow));
    }
}
