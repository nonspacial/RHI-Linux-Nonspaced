using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Network;

namespace RHI.Desktop.ViewModels;

/// <summary>What the nxm:// confirmation shows before anything is downloaded.</summary>
/// <param name="ForGame">The library game whose Nexus page matches the link's game, when exactly one does.</param>
public sealed record NxmConfirmation(NxmLink Link, NexusModFile? File, string? ForGame);

/// <summary>
/// Nexus Mods links for library games (#99): the card's Nexus Mods page (manifest <c>nexusUrlOverrides</c>,
/// <see cref="DetectedGame.NexusUrlOverride"/>), and a "Mod Manager Download" (nxm://) link arriving at RHI — parsed
/// strictly, confirmed by the user with the mod and file named, downloaded into RHI's cache, then offered for install
/// through the paths a dropped file takes (each confirms again). Nothing is installed from a link without confirmation.
/// </summary>
public sealed class NexusLinkPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly NexusAccountPresenter _account;
    private readonly NexusApiClient _api;
    private readonly NexusDownloadService _downloads;

    public NexusLinkPresenter(NexusAccountPresenter account, NexusApiClient api, NexusDownloadService downloads)
    {
        _account = account;
        _api = api;
        _downloads = downloads;
    }

    /// <summary>Asks the user to confirm a link's download (set by the window; no window = never confirmed).</summary>
    public Func<NxmConfirmation, Task<bool>>? Confirm { get; set; }

    /// <summary>Offers one downloaded file for install, as a dropped file (set by the window).</summary>
    public Func<string, Task>? OfferFile { get; set; }

    /// <summary>
    /// The game's Nexus Mods page (www.nexusmods.com, https, no credentials), or null — the card menu shows "Nexus Mods
    /// page" only then.
    /// </summary>
    public static string? PageFor(DetectedGame? game)
    {
        if (game?.NexusUrlOverride is not { } url || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
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

        var matches = vm.AllLibraryGames.Where(g => GameDomainOf(PageFor(g)) == link.GameDomain).ToList();
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
