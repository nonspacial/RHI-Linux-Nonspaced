using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

// #309 small UI parity (upstream 2.8.9–2.9.2): the quieter Favourite button, the store badge's Steam Community link and a
// game added by hand opening straight away.
public partial class MainWindowViewModel
{
    // ---- Favourite (upstream 2.8.9 ApplyFavouriteButtonStyle): "Favourited" in amber, the same quiet border as Hide -----

    /// <summary>The selected game is a favourite: the header's Favourite button reads "Favourited" in amber.</summary>
    public bool SelectedGameIsFavourite => SelectedGame?.IsFavourite == true;

    /// <summary>What the Favourite button does now (upstream's two tooltips).</summary>
    public string FavouriteActionTip => T(SelectedGameIsFavourite ? "Fav.OnTip" : "Fav.OffTip");

    // ---- Store badge (upstream 2.8.9 DetailPanelBuilder.StoreBadge): opens the game's Steam Community page ----------------

    /// <summary>
    /// The game's Steam Community hub, from any store: the manifest's <c>steamAppIdOverrides</c> first, then the Steam
    /// library's app id (upstream's order); null when neither is known (upstream also asks PCGamingWiki's data, which Linux
    /// does not keep).
    /// </summary>
    public static string? SteamCommunityUrl(DetectedGame? game)
        => (game?.ManifestSteamAppIdOverride is > 0 ? game.ManifestSteamAppIdOverride : game?.SteamAppId) is int id and > 0
            ? $"https://steamcommunity.com/app/{id}"
            : null;

    /// <summary>The store badge is a link (hand cursor, tooltip line) for the selected game.</summary>
    public bool StoreBadgeOpensCommunity => SteamCommunityUrl(SelectedGame) is not null;

    /// <summary>The store badge's tooltip: the library source, then (when it is a link) what a click does.</summary>
    public string StoreBadgeToolTip => StoreBadgeOpensCommunity
        ? LibraryProviderToolTip + "\n" + T("Store.OpenCommunityTip")
        : LibraryProviderToolTip;

    /// <summary>Opens the selected game's Steam Community page through the desktop opener (http/https only).</summary>
    [RelayCommand]
    private void OpenSteamCommunity()
    {
        if (SteamCommunityUrl(SelectedGame) is not { } url) return;
        if (!LinuxDesktopOpener.TryOpenUri(url, out var error))
            StatusText = TF("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    // ---- A game added by hand (upstream 2.9.0): opens straight away; a search or filter that would hide it is cleared ----

    /// <summary>
    /// Selects <paramref name="game"/> and, when the search box, the active saved filter or the filter tab would hide it,
    /// clears them in that order until it shows (upstream: "If your search or filter would hide it, they're cleared").
    /// </summary>
    internal void RevealAndSelect(DetectedGame game)
    {
        ApplyFilter();
        if (!_library.IsShown(game) && SearchText.Length > 0)
        {
            SearchText = "";
            ApplyFilter();
        }
        if (!_library.IsShown(game) && CustomFilters.TurnOff()) ApplyFilter();
        if (!_library.IsShown(game) && LibraryFilterMode != "All Games") LibraryFilterMode = "All Games"; // applies and saves it
        SelectedGame = game;
    }

    private void NotifyUiParity()
    {
        OnPropertyChanged(nameof(SelectedGameIsFavourite));
        OnPropertyChanged(nameof(FavouriteActionTip));
        OnPropertyChanged(nameof(StoreBadgeOpensCommunity));
        OnPropertyChanged(nameof(StoreBadgeToolTip));
    }
}
