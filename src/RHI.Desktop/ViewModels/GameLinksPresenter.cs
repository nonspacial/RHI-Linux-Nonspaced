using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Network;

namespace RHI.Desktop.ViewModels;

/// <summary>What the nxm:// confirmation shows before anything is downloaded.</summary>
/// <param name="ForGame">The library game whose Nexus page matches the link's game, when exactly one does.</param>
public sealed record NxmConfirmation(NxmLink Link, NexusModFile? File, string? ForGame);

/// <summary>
/// The web pages a library card links to (#183: PCGamingWiki, Ultra+, an ultrawide fix, the HDR Gaming Database and
/// the RenoDX mod's discussion page, through <see cref="GameLinkCatalogue"/>), and Nexus Mods (#99): the card's Nexus Mods page (manifest <c>nexusUrlOverrides</c>,
/// <see cref="DetectedGame.NexusUrlOverride"/>, else <see cref="NexusGameCatalogue"/>; #174), and a "Mod Manager Download" (nxm://) link arriving at RHI — parsed
/// strictly, confirmed by the user with the mod and file named, downloaded into RHI's cache, then offered for install
/// through the paths a dropped file takes (each confirms again). Nothing is installed from a link without confirmation.
/// </summary>
public sealed class GameLinksPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly NexusAccountPresenter _account;
    private readonly NexusApiClient _api;
    private readonly NexusDownloadService _downloads;

    public GameLinksPresenter(NexusAccountPresenter account, NexusApiClient api, NexusDownloadService downloads, NexusGameCatalogue? games = null)
    {
        _account = account;
        _api = api;
        _downloads = downloads;
        Games = games;
    }

    /// <summary>Which games have a Nexus Mods page beyond the manifest (#174); null = manifest and built-in table only.</summary>
    public NexusGameCatalogue? Games { get; }

    /// <summary>#183: the card links' lists and PCGamingWiki lookups; null = Nexus Mods only (set by <c>AppComposition</c>).</summary>
    public GameLinkCatalogue? Links { get; set; }

    /// <summary>#183: the RenoDX catalogue whose matched mod names the "Discussion / instructions" page; null = no such entry.</summary>
    public RenoDxCatalogService? RenoDx { get; set; }

    /// <summary>Starts (or joins) every list behind the card links in the background (Nexus's game list included).</summary>
    public void Prefetch()
    {
        Games?.Prefetch();
        Links?.Prefetch();
    }

    /// <summary>Full Refresh: the card links' lists are downloaded again in the background (never awaited).</summary>
    public Task RefreshListsAsync() => Links?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>
    /// #183: the card menu's web-page entries for a game, each listed only where the game has that page (hidden
    /// otherwise): "Discussion / instructions" for the RenoDX mod matched to the game, then a "Web pages" submenu with
    /// PCGamingWiki, Ultra+, Ultrawide fix and HDR Gaming Database. Each entry's tooltip is its link. A list still loading
    /// is joined, never awaited; a PCGamingWiki page nobody knows yet is looked up in the background, so it appears the
    /// next time the menu opens.
    /// </summary>
    public IReadOnlyList<CardMenuItem> MenuItemsFor(DetectedGame game, Action<string> open)
    {
        Links?.Prefetch();
        var items = new List<CardMenuItem>();
        if (DiscussionPageFor(game) is { } discussion)
            items.Add(Entry("discussion", T("Cat.DiscussionPage"), discussion, open));
        if (Links is not { } links) return items;
        if (links.PcgwPageFor(game) is null) links.ResolvePcgwInBackground(game);
        var pages = new List<CardMenuItem>(4);
        if (links.PcgwPageFor(game) is { } pcgw) pages.Add(Entry("pcgw-page", T("Cat.PcgwPage"), pcgw, open));
        if (links.UltraPlusPageFor(game) is { } ultraPlus) pages.Add(Entry("ultraplus-page", T("Cat.UltraPlusPage"), ultraPlus, open));
        if (links.UltrawideFixPageFor(game) is { } uwFix) pages.Add(Entry("uwfix-page", T("Cat.UltrawideFixPage"), uwFix, open));
        if (links.HdrDatabasePageFor(game) is { } hdr) pages.Add(Entry("hdr-database-page", T("Cat.HdrDatabasePage"), hdr, open));
        if (pages.Count > 0)
            items.Add(new CardMenuItem("web-pages", T("Cat.WebPages"), CardMenuItem.OpensSubmenu, Children: pages, Tip: T("Cat.WebPagesTip")));
        return items;
    }

    /// <summary>#183: the discussion / instructions page of the RenoDX mod RHI matched to the game (https, on GitHub), else null.</summary>
    public string? DiscussionPageFor(DetectedGame? game)
        => game is not null && RenoDx is { } renoDx && renoDx.TryGetResolvedMatch(game, out var mod)
            ? GameLinkCatalogue.SafePage(mod?.DiscussionUrl, LinkHosts.Discussion)
            : null;

    private static CardMenuItem Entry(string id, string label, string url, Action<string> open)
        => new(id, label, new RelayCommand(() => open(url)), Tip: url);

    /// <summary>Asks the user to confirm a link's download (set by the window; no window = never confirmed).</summary>
    public Func<NxmConfirmation, Task<bool>>? Confirm { get; set; }

    /// <summary>Offers one downloaded file for install, as a dropped file (set by the window).</summary>
    public Func<string, Task>? OfferFile { get; set; }

    /// <summary>
    /// The game's Nexus Mods page (www.nexusmods.com, https, no credentials), or null — the card menu shows "Nexus Mods
    /// page" only then. Opening it needs no account (#174). Order: the manifest's page for the game, RHI's built-in
    /// table of store names Nexus spells differently (Skyrim Special Edition), then Nexus's own game list when
    /// <paramref name="games"/> has loaded it.
    /// </summary>
    public static string? PageFor(DetectedGame? game, NexusGameCatalogue? games = null)
    {
        if (game is null) return null;
        if (ManifestPageFor(game) is { } manifest) return manifest;
        var steamAppId = game.SteamAppId ?? game.ManifestSteamAppIdOverride;
        return games is not null ? games.PageFor(game.Name, steamAppId) : NexusGameCatalogue.KnownPageFor(game.Name, steamAppId);
    }

    /// <summary>The page named by the manifest's <c>nexusUrlOverrides</c>, when it is an https Nexus Mods link.</summary>
    private static string? ManifestPageFor(DetectedGame game)
    {
        if (game.NexusUrlOverride is not { } url || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.Host is "www.nexusmods.com" or "nexusmods.com" ? uri.AbsoluteUri : null;
    }

    /// <summary>The Nexus game domain of a page (<c>/games/&lt;domain&gt;</c> or <c>/&lt;domain&gt;/mods/…</c>), or null.</summary>
    public static string? GameDomainOf(string? pageUrl)
    {
        if (pageUrl is null || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("games", StringComparison.OrdinalIgnoreCase)) return segments[1].ToLowerInvariant();
        if (segments.Length >= 1 && !segments[0].Equals("games", StringComparison.OrdinalIgnoreCase)) return segments[0].ToLowerInvariant();
        return null;
    }

    /// <summary>Handles an nxm:// link (command line or hand-off). Every outcome is a status line. UI thread.</summary>
    public async Task HandleAsync(MainWindowViewModel vm, string raw)
    {
        await vm.Initialization;
        if (NxmLink.TryParse(raw) is not { } link)
        {
            AppLog.Warn("nxm", "Ignored a malformed nxm:// link");
            vm.ReportParityNotice(T("Parity.NxmMalformed"));
            return;
        }
        if (_account.UsableKey() is not { } key)
        {
            vm.ReportParityNotice(T("Parity.NxmNotConnected"));
            return;
        }
        if (link.Key is null && !_account.IsPremium)
        {
            vm.ReportParityNotice(T("Parity.NxmNoKeyFree"));
            return;
        }

        NexusModFile? file = null;
        try
        {
            file = (await _api.GetModFilesAsync(key, link.GameDomain, link.ModId)).FirstOrDefault(f => f.FileId == link.FileId);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UntrustedDownloadException or TaskCanceledException)
        {
            AppLog.Warn("nxm", "Could not read the mod's file list; the confirmation names the file by number", ex);
        }

        var matches = vm.AllLibraryGames.Where(g => GameDomainOf(PageFor(g, Games)) == link.GameDomain).ToList();
        var game = matches.Count == 1 ? matches[0] : null;
        if (Confirm is null || !await Confirm(new NxmConfirmation(link, file, game?.Name)))
        {
            vm.StatusText = T("Parity.NxmDeclined");
            return;
        }

        var label = file?.Name is { Length: > 0 } name ? name : TF("Parity.NxmFileNumber", link.FileId);
        vm.StatusText = TF("Parity.NxmDownloading", label);
        NexusDownload download;
        try
        {
            download = await _downloads.DownloadAsync(key, link, file);
        }
        catch (NexusApiException ex) when (ex.Rejected)
        {
            vm.StatusText = T(link.Key is null ? "Parity.NxmPremiumNeeded" : "Parity.NxmLinkRefused");
            return;
        }
        catch (NexusApiException ex) when (ex.RateLimited)
        {
            vm.StatusText = T("Parity.NxmRateLimited");
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UntrustedDownloadException
                                       or System.Text.Json.JsonException or TaskCanceledException)
        {
            AppLog.Warn("nxm", "The Nexus Mods download failed", ex);
            vm.StatusText = TF("Parity.NxmDownloadFailed", label, UiLocalizationService.Present(ex.Message));
            return;
        }

        if (download.Installable.Count == 0)
        {
            vm.StatusText = TF("Parity.NxmNothingToInstall", label, download.Folder);
            return;
        }
        // Luma packages install into the selected game: the one the link's game matches, when there is exactly one.
        if (game is not null && !ReferenceEquals(vm.SelectedGame, game)) vm.SelectedGame = game;
        vm.StatusText = TF("Parity.NxmDownloaded", label, download.Folder);
        if (OfferFile is null) return;
        foreach (var path in download.Installable) await OfferFile(path);
    }
}
