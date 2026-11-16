namespace RHI.Desktop.ViewModels;

// Upstream 2.9.4 Game settings cog (DetailLayoutHost.GameSettings / .Header): a ⚙ beside the game's title holding settings
// that apply to this game only. Linux holds Block Steam updates (#290, upstream 2.9.4's row) and, until the Management
// section is ported, Remove from RHI (#293; upstream's Classic+ keeps it in Management).
public partial class MainWindowViewModel
{
    /// <summary>The ⚙ shows when a row applies: Block Steam updates (a Steam game RHI found the manifest of) or Remove
    /// from RHI (a detected game; a game added by hand keeps Remove manual game in the header).</summary>
    public bool ShowGameSettings => SelectedGame is { } game && (CanBlockSteamUpdates || !game.IsManuallyAdded || GameSettingsHasDisplayModesRow);

    /// <summary>
    /// w14/display-modes (#311, upstream 2.9.3 <c>AddDisplayModeRow</c>): the Display modes row ("Choose…" opens the game's
    /// display mode settings), for every game while display modes are on (a game added by hand has no other row).
    /// </summary>
    public bool GameSettingsHasDisplayModesRow => SelectedGame is not null && (DisplayModes.Enabled || !SelectedGame.IsManuallyAdded);

    /// <summary>The Block Steam updates row is offered for the selected game.</summary>
    public bool GameSettingsHasSteamRow => CanBlockSteamUpdates;

    /// <summary>The Remove from RHI row is offered for the selected game (enabled while no operation runs).</summary>
    public bool GameSettingsHasRemoveRow => SelectedGame is { IsManuallyAdded: false };

    /// <summary>The ⚙'s tooltip and the panel's title (upstream: "Game settings").</summary>
    public string GameSettingsTitle => T("Gs.Title");

    /// <summary>
    /// The Block Steam updates switch asks for <paramref name="blocked"/>; the switch then shows what the manifest really is
    /// (<see cref="SteamUpdatesBlocked"/>), so a refused change (Steam updating the game) springs back.
    /// </summary>
    internal void SetSteamUpdatesBlocked(bool blocked)
    {
        if (blocked != SteamUpdatesBlocked) ToggleSteamUpdatesCommand.Execute(null);
    }

    private void NotifyGameSettings()
    {
        OnPropertyChanged(nameof(ShowGameSettings));
        OnPropertyChanged(nameof(GameSettingsHasSteamRow));
        OnPropertyChanged(nameof(GameSettingsHasRemoveRow));
        OnPropertyChanged(nameof(GameSettingsHasDisplayModesRow));
    }
}
