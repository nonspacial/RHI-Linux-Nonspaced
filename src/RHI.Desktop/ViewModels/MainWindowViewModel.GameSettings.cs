namespace RHI.Desktop.ViewModels;

// Upstream 2.9.4 Game settings cog (DetailLayoutHost.GameSettings / .Header): a ⚙ beside the game's title holding settings
// that apply to this game only. Linux holds Block Steam updates (#290, upstream 2.9.4's row). Remove from RHI (#293) is in
// the Management section (w14/layouts-a, #304), as upstream.
public partial class MainWindowViewModel
{
    /// <summary>The ⚙ shows when a row applies: Block Steam updates (a Steam game RHI found the manifest of).</summary>
    public bool ShowGameSettings => SelectedGame is not null && CanBlockSteamUpdates;

    /// <summary>The Block Steam updates row is offered for the selected game.</summary>
    public bool GameSettingsHasSteamRow => CanBlockSteamUpdates;


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
    }
}
