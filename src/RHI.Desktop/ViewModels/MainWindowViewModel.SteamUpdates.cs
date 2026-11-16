using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection;

namespace RHI.Desktop.ViewModels;

// #290 (upstream 2.9.4 "Block Steam updates", Game settings): the selected Steam game's app manifest read-only or not.
public partial class MainWindowViewModel
{

    private SteamUpdateBlockState SteamUpdateState => SelectedGame is { } game ? SteamUpdateBlock.State(game) : SteamUpdateBlockState.Unavailable;

    /// <summary>The switch shows only for a Steam game whose app manifest RHI found.</summary>
    public bool CanBlockSteamUpdates => SteamUpdateState != SteamUpdateBlockState.Unavailable;

    public bool SteamUpdatesBlocked => SteamUpdateState == SteamUpdateBlockState.Blocked;

    public string BlockSteamUpdatesText => UiLocalizationService.Text(SteamUpdatesBlocked ? "Steam.AllowUpdates" : "Steam.BlockUpdates");

    public string BlockSteamUpdatesTip => UiLocalizationService.Text("Steam.BlockUpdatesTip");

    [RelayCommand]
    private void ToggleSteamUpdates()
    {
        if (SelectedGame is not { } game || IsBusy) return;
        var block = !SteamUpdatesBlocked;
        var result = SteamUpdateBlock.Set(game, block);
        StatusText = result.Success
            ? UiLocalizationService.Format(block ? "Steam.UpdatesBlocked" : "Steam.UpdatesAllowed", game.Name)
            : SteamUpdateBlockFailure(result.Message);
        NotifySteamUpdateBlock();
    }

    internal static string SteamUpdateBlockFailure(string? message) => message switch
    {
        SteamUpdateBlock.UpdatingMessage => UiLocalizationService.Text("Steam.UpdatingNow"),
        SteamUpdateBlock.NotSteamMessage => UiLocalizationService.Text("Steam.NoManifest"),
        { } failed when failed.StartsWith(SteamUpdateBlock.FailedPrefix, StringComparison.Ordinal)
            => UiLocalizationService.Format("Steam.ManifestNotChanged", failed[SteamUpdateBlock.FailedPrefix.Length..]),
        _ => UiLocalizationService.Present(message)
    };

    private void NotifySteamUpdateBlock()
    {
        OnPropertyChanged(nameof(CanBlockSteamUpdates));
        OnPropertyChanged(nameof(SteamUpdatesBlocked));
        OnPropertyChanged(nameof(BlockSteamUpdatesText));
    }
}
