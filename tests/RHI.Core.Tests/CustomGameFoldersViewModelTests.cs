using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection.CustomFolders;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #293 (upstream 2.9.0 Settings › Tools &amp; Maintenance › Custom game folders) over the real offline shell: folders are
/// saved in the library file, scanned at start, on Refresh and with Scan now; found games are only offered (status line
/// link, review), the review adds them as manual games with the chosen name, exe and (Linux) prefix, and Ignore selected /
/// removing an added game keep a folder from being offered again until Forget ignored games.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CustomGameFoldersViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-cgfvm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Library()
    {
        var games = Directory.CreateDirectory(Path.Combine(_root, "Games")).FullName;
        CustomGameFolderTests.Put(games, "Hades.v1.38290-GOG/Hades.exe", 2000);
        CustomGameFolderTests.Put(games, "Hades.v1.38290-GOG/HadesLauncher.exe", 3000);
        CustomGameFolderTests.Put(games, "Tunic/Tunic.exe", 2000);
        CustomGameFolderTests.Put(games, "Tunic/Tunic_DX12.exe", 2000);
        return games;
    }

    private static async Task<ViewModelFixture> StartAsync(Action<LinuxGameLibraryStore>? seed = null, string language = "en-US")
    {
        var fx = await ViewModelFixture.StartAsync([], language, seed: seed);
        fx.Vm.CustomFolderProbe = _ => new ExeProbe(false, false);
        return fx;
    }

    [Fact]
    public async Task AddFolder_ScansIt_OffersTheGames_AndTheReviewAddsThePickedOneWithItsNameExeAndPrefix()
    {
        var games = Library();
        var prefix = Directory.CreateDirectory(Path.Combine(_root, "pfx")).FullName;
        using var fx = await StartAsync();

        Assert.Equal($"The folder \"{Path.Combine(_root, "missing")}\" doesn't exist.", await fx.Vm.AddCustomGameFolderAndScanAsync(Path.Combine(_root, "missing")));
        var line = await fx.Vm.AddCustomGameFolderAndScanAsync(games);

        Assert.Equal("Found 2 new games.", line);
        Assert.Equal([games], fx.Library.Load().CustomGameFolders!);
        Assert.Equal([games], fx.Vm.CustomGameFolders);
        Assert.True(fx.Vm.ShowCustomGamesNotice);
        Assert.Equal("2 new games found in your custom folders · Review", fx.Vm.CustomGamesNoticeText);
        Assert.Empty(fx.Vm.AllLibraryGames); // never added on their own

        var review = fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(prefix))!;
        Assert.Equal("2 new games in your custom folders", review.WindowTitle);
        var hades = review.Rows.Single(row => row.Name == "Hades");
        var tunic = review.Rows.Single(row => row.Name == "Tunic");
        Assert.Equal("Hades.exe  (2.0 MB)", hades.ExeItems[0]); // the launcher ranks lower
        Assert.False(hades.CheckTheExe);
        Assert.True(tunic.CheckTheExe); // two exes alike: "Check the exe"
        tunic.IsChecked = false;
        hades.Name = "Hades (GOG)";
        await hades.BrowsePrefixCommand.ExecuteAsync(null);
        Assert.Equal(prefix, hades.Prefix);
        var closed = false;
        review.CloseRequested += () => closed = true;

        await review.AddSelectedCommand.ExecuteAsync(null);

        Assert.True(closed, review.Status);
        Assert.StartsWith("1 game added from your custom folders.", fx.Vm.StatusText, StringComparison.Ordinal);
        var entry = Assert.Single(fx.Library.Load().ManualGames);
        Assert.Equal("Hades (GOG)", entry.Name);
        Assert.Equal(Path.Combine(games, "Hades.v1.38290-GOG"), entry.InstallPath);
        Assert.Equal(Path.Combine(games, "Hades.v1.38290-GOG", "Hades.exe"), entry.ExecutablePath);
        Assert.Equal(prefix, entry.PrefixPath);
        Assert.Equal("Manual Wine", entry.Runner);
        Assert.Contains(fx.Vm.AllLibraryGames, game => game.Name == "Hades (GOG)" && game.IsManuallyAdded);
        Assert.Equal(["Tunic"], fx.Vm.PendingCustomGames.Select(c => c.SuggestedName));

        // The added game is not offered again by the next scan.
        Assert.Equal("Found 1 new game.", await fx.Vm.ScanCustomFoldersNowAsync());
    }

    [Fact]
    public async Task AFolderThatCannotBeAGameFolder_IsRefusedAndNotSaved()
    {
        using var fx = await StartAsync();
        Assert.Equal("Pick the folder that holds your games, not a whole drive or a system folder.", fx.Vm.AddCustomGameFolder("/usr"));
        Assert.Null(fx.Library.Load().CustomGameFolders);
        Assert.Equal("Add a folder first.", await fx.Vm.ScanCustomFoldersNowAsync());
    }

    [Fact]
    public async Task IgnoreSelected_KeepsAGameFromBeingOffered_UntilForgetIgnoredGames()
    {
        var games = Library();
        using var fx = await StartAsync();
        await fx.Vm.AddCustomGameFolderAndScanAsync(games);
        var review = fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(null))!;
        review.Rows.Single(row => row.Name == "Hades").IsChecked = false;

        review.IgnoreSelectedCommand.Execute(null);

        Assert.StartsWith("1 game ignored.", fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(games, "Tunic")], fx.Library.Load().IgnoredCustomGameFolders!);
        Assert.Equal(1, fx.Vm.IgnoredCustomGameCount);
        Assert.Equal("Found 1 new game.", await fx.Vm.ScanCustomFoldersNowAsync());
        Assert.Equal(["Hades"], fx.Vm.PendingCustomGames.Select(c => c.SuggestedName));

        Assert.Equal("Ignored games will be offered again on the next scan.", fx.Vm.ForgetIgnoredCustomGames());
        Assert.Null(fx.Library.Load().IgnoredCustomGameFolders);
        Assert.Equal("Found 2 new games.", await fx.Vm.ScanCustomFoldersNowAsync());
    }

    [Fact]
    public async Task RemovingAGameAddedFromACustomFolder_IgnoresItsFolder()
    {
        var games = Library();
        using var fx = await StartAsync();
        await fx.Vm.AddCustomGameFolderAndScanAsync(games);
        var review = fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(null))!;
        review.Rows.Single(row => row.Name == "Tunic").IsChecked = false;
        await review.AddSelectedCommand.ExecuteAsync(null);
        fx.Vm.SelectedGame = fx.Vm.AllLibraryGames.Single(game => game.Name == "Hades");

        await fx.Vm.ManualGames.RemoveManualGameCommand.ExecuteAsync(null);

        Assert.Empty(fx.Library.Load().ManualGames);
        Assert.Equal([Path.Combine(games, "Hades.v1.38290-GOG")], fx.Library.Load().IgnoredCustomGameFolders!);
        Assert.Equal("Found 1 new game.", await fx.Vm.ScanCustomFoldersNowAsync());
        Assert.Equal(["Tunic"], fx.Vm.PendingCustomGames.Select(c => c.SuggestedName));
    }

    [Fact]
    public async Task TheScanRunsAtStartAndOnRefresh_AndNotNowHidesTheLinkUntilTheNextScan()
    {
        var games = Library();
        using var fx = await StartAsync(seed: store => store.Save(new LinuxGameLibraryState { CustomGameFolders = [games] }));
        await WaitUntil(() => fx.Vm.PendingCustomGames.Count == 2);
        Assert.True(fx.Vm.ShowCustomGamesNotice);

        fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(null))!.NotNowCommand.Execute(null);
        Assert.False(fx.Vm.ShowCustomGamesNotice);
        Assert.Equal(2, fx.Vm.PendingCustomGames.Count); // still offered from Settings

        CustomGameFolderTests.Put(games, "Celeste/Celeste.exe", 2000);
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => fx.Vm.PendingCustomGames.Count == 3);
        Assert.True(fx.Vm.ShowCustomGamesNotice);
    }

    [Fact]
    public async Task AGameWhoseNameIsAlreadyListed_IsNotAdded_AndTheReviewSaysWhy()
    {
        var games = Library();
        using var fx = await StartAsync();
        await fx.Vm.AddCustomGameFolderAndScanAsync(games);
        var review = fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(null))!;
        review.Rows.Single(row => row.Name == "Tunic").IsChecked = false;
        await review.AddSelectedCommand.ExecuteAsync(null);
        await fx.Vm.ScanCustomFoldersNowAsync();
        var second = fx.Vm.CreateCustomGamesReview(_ => Task.FromResult<string?>(null))!;
        second.Rows.Single().Name = "hades";

        await second.AddSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Not added — hades: \"hades\" is already in your library.", second.Status);
        Assert.Single(fx.Library.Load().ManualGames);
        Assert.Single(second.Rows);
    }

    [Fact]
    public async Task PortugueseTexts()
    {
        var games = Library();
        UiLocalizationService.Apply("pt-BR");
        using var fx = await StartAsync(language: "pt-BR");
        Assert.Equal("2 jogos novos encontrados.", await fx.Vm.AddCustomGameFolderAndScanAsync(games));
        Assert.Equal("2 jogos novos encontrados nas suas pastas personalizadas · Revisar", fx.Vm.CustomGamesNoticeText);
        Assert.Equal("Essa pasta já está na lista.", fx.Vm.AddCustomGameFolder(games));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
