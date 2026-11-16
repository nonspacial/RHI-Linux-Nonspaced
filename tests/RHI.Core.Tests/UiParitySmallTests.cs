using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #309 small UI parity (upstream 2.8.9–2.9.2): the quieter Favourite button, the store badge's Steam Community link, greyed
/// library names for games without ReShade, the HDR / resolution launch tooltips and a game added by hand opening at once.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class UiParitySmallTests
{
    [Fact]
    public async Task Favourite_ReadsFavouritedInAmber_WithUpstreamsTooltips()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var alpha = fx.Game("Alpha");
        using var _ = await Started(fx, alpha);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(row => row.Name == "Alpha").Game;

        Assert.False(fx.Vm.SelectedGameIsFavourite);
        Assert.Equal("☆ Favourite", fx.Vm.FavouriteActionText);
        Assert.Equal("Add this game to Favourites (find it with the Favourites filter).", fx.Vm.FavouriteActionTip);

        fx.Vm.ToggleFavouriteCommand.Execute(null);

        Assert.True(fx.Vm.SelectedGameIsFavourite);
        Assert.Equal("★ Favourited", fx.Vm.FavouriteActionText);
        Assert.Equal("Favourite. Click to remove it from Favourites.", fx.Vm.FavouriteActionTip);
    }

    // The fixture's games come from the scan; this adds one by hand and waits for it to be listed.
    private static async Task<IDisposable> Started(ViewModelFixture fx, DetectedGame game)
    {
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath });
        Assert.True(added, message);
        return new Nothing();
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1091500, null, "https://steamcommunity.com/app/1091500")] // the Steam library's app id
    [InlineData(null, 2050650, "https://steamcommunity.com/app/2050650")] // a non-Steam game the manifest knows on Steam
    [InlineData(1091500, 2050650, "https://steamcommunity.com/app/2050650")] // the manifest first, as upstream
    [InlineData(0, null, null)]
    public void StoreBadge_OpensTheSteamCommunityPage_WhenAnAppIdIsKnown(int? steamAppId, int? manifestOverride, string? expected)
    {
        var game = new DetectedGame { Name = "Game", SteamAppId = steamAppId, ManifestSteamAppIdOverride = manifestOverride };
        Assert.Equal(expected, MainWindowViewModel.SteamCommunityUrl(game));
    }

    [Fact]
    public async Task StoreBadge_TooltipSaysItOpensTheCommunityPage_OnlyWhenItDoes()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Beta");
        using var _ = await Started(fx, game);
        var listed = fx.Vm.Games.Single(row => row.Name == "Beta").Game;
        fx.Vm.SelectedGame = listed;
        Assert.False(fx.Vm.StoreBadgeOpensCommunity);
        Assert.Equal(fx.Vm.LibraryProviderToolTip, fx.Vm.StoreBadgeToolTip);

        listed.ManifestSteamAppIdOverride = 620;
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = listed;
        Assert.True(fx.Vm.StoreBadgeOpensCommunity);
        Assert.Equal(fx.Vm.LibraryProviderToolTip + "\nClick to open the game's Steam Community page.", fx.Vm.StoreBadgeToolTip);
    }

    [Fact]
    public async Task LibraryNames_AreGreyedWithoutReShade_AndTheTooltipSaysSo()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var with = fx.Game("With ReShade");
        File.WriteAllText(Path.Combine(with.BinaryDirectory!, "ReShade.ini"), "[GENERAL]\n");
        var without = fx.Game("Without ReShade");
        using (await Started(fx, with)) { }
        using (await Started(fx, without)) { }
        await fx.Vm.RefreshLibraryFactsForTestingAsync();

        var withRow = fx.Vm.Games.Single(row => row.Name == "With ReShade");
        var withoutRow = fx.Vm.Games.Single(row => row.Name == "Without ReShade");
        Assert.False(withRow.ShowsNoReShade);
        Assert.Equal("With ReShade", withRow.ToolTip);
        Assert.True(withoutRow.ShowsNoReShade);
        Assert.Equal("Without ReShade\nReShade isn't installed for this game.", withoutRow.ToolTip);
    }

    [Fact]
    public void LibraryNames_AreNotGreyedBeforeTheFactsAreKnown()
    {
        var row = new GameRowViewModel(new DetectedGame { Name = "Gamma" });
        Assert.Null(row.ReShadeInstalled);
        Assert.False(row.ShowsNoReShade);
        Assert.True(row.SetReShadeInstalled(false));
        Assert.True(row.ShowsNoReShade);
        Assert.False(row.SetReShadeInstalled(false)); // unchanged: no notification
    }

    [Fact]
    public void LaunchTooltips_SayWhetherHdrAndResolutionAreOnForTheGame()
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal("HDR on launch: On, Gamescope (set for this game).\nWhen Play starts the game, RHI starts it with this HDR setup.\n"
                     + "Pick Off to turn it off for this game.", LaunchToggleTips.Hdr(true, "Gamescope"));
        Assert.StartsWith("HDR on launch: Off.", LaunchToggleTips.Hdr(false, "Off"));
        var resolutionOn = LaunchToggleTips.Resolution(true);
        Assert.StartsWith("Change resolution: On (set for this game).\n", resolutionOn);
        Assert.Contains(UiLocalizationService.Text("Shell.ResToggleTip"), resolutionOn);
        Assert.EndsWith("Untick to turn it off for this game.", resolutionOn);
        Assert.StartsWith("Change resolution: Off.\n", LaunchToggleTips.Resolution(false));
    }

    [Fact]
    public async Task LaunchTooltips_FollowTheSelectedGamesHdrProfile()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Delta");
        using var _ = await Started(fx, game);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(row => row.Name == "Delta").Game;
        Assert.StartsWith("Change resolution: Off.", fx.Vm.Runtime.ResolutionToggleTip);
        fx.Vm.Runtime.ResolutionToggle = true;
        Assert.StartsWith("Change resolution: On (set for this game).", fx.Vm.Runtime.ResolutionToggleTip);
        Assert.StartsWith("HDR on launch: Off.", fx.Vm.Runtime.HdrModeTip);
    }

    [Fact]
    public async Task AGameAddedByHand_OpensAtOnce_ClearingTheSearchAndFilterThatWouldHideIt()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        using (await Started(fx, fx.Game("Epsilon"))) { }
        fx.Vm.LibraryFilterMode = "Favourites";
        fx.Vm.SearchText = "Epsilon";

        var zeta = fx.Game("Zeta");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Zeta", InstallPath = zeta.InstallPath, ExecutablePath = zeta.ExecutablePath });

        Assert.True(added, message);
        Assert.Equal("Zeta", fx.Vm.SelectedGame?.Name);
        Assert.Equal("", fx.Vm.SearchText);
        Assert.Equal("All Games", fx.Vm.LibraryFilterMode);
        Assert.Contains(fx.Vm.Games, row => row.Name == "Zeta");
    }

    [Fact]
    public async Task AGameAddedByHand_KeepsASearchThatShowsIt()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        using (await Started(fx, fx.Game("Eta One"))) { }
        fx.Vm.SearchText = "Eta";

        var two = fx.Game("Eta Two");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(
            new LinuxManualGameEntry { Name = "Eta Two", InstallPath = two.InstallPath, ExecutablePath = two.ExecutablePath });

        Assert.True(added, message);
        Assert.Equal("Eta Two", fx.Vm.SelectedGame?.Name);
        Assert.Equal("Eta", fx.Vm.SearchText); // it shows under the search: nothing to clear
    }
}
