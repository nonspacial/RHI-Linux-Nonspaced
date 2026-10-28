using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Network;

/// <summary>What <see cref="NexusModUpdateCheck"/> could tell about a mod RHI cannot download (#245).</summary>
public enum NexusModUpdateOutcome
{
    /// <summary>Nexus has a main or update file uploaded after the install's baseline.</summary>
    Newer,
    /// <summary>Nexus has no main or update file newer than the baseline.</summary>
    Current,
    /// <summary>No Nexus account is connected, so Nexus was not asked.</summary>
    NoKey,
    /// <summary>Nexus refused the saved key (401/403): the user fixes it in Settings → Accounts &amp; updates.</summary>
    KeyRefused,
    /// <summary>Nexus could not be asked or gave no dated file (error, rate limit, not a mod page).</summary>
    Failed
}

/// <summary>The answer for one mod page; <see cref="Message"/> is a fixed platform text (PT-BR presentation pairs).</summary>
public sealed record NexusModUpdateAnswer(NexusModUpdateOutcome Outcome, string PageUrl, string Message, NexusModFile? Newest = null);

/// <summary>
/// #245 (upstream 2.8.8 <c>20d3297</c>, <c>MainViewModel.CheckLumaNexusUpdatesAsync</c>): the one "is there a newer file on
/// Nexus Mods" check, for mods RHI can only point at (Luma mods the wiki lists only on Nexus; #198 reuses it). It asks
/// Nexus's v1 API with the connected account's key only (owner rule: API key, never SSO or anonymous GraphQL) through
/// <see cref="NexusApiClient"/> (bounded answers, no redirects, one 429 retry), and compares the newest MAIN/UPDATE
/// file's upload time with the install's baseline: the installed Nexus file's upload time when the caller knows the
/// file id, else the install time (upstream's baseline, which a new install resets). Upload times are used rather than
/// the mod's "updated" time, which also moves when only the page text changes. One file list per mod is kept for
/// <see cref="Freshness"/> (selection and Update All share it); a failure is never kept as an answer, never reads as
/// current. After Nexus answers 429 (rate limited, after the client's one retry) nothing is asked for
/// <see cref="RateLimitBackoff"/>: every check meanwhile is could-not-check with that answer (the limit is the account's).
/// </summary>
public sealed class NexusModUpdateCheck
{
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(1);
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(5);

    // Fixed messages (PT-BR presentation pairs); values come last.
    public const string NoKeyMessage = "RHI checks this mod for updates on Nexus Mods only with a connected Nexus account: Settings → Accounts & updates → Connect Nexus account.";
    public const string FailedPrefix = "Nexus Mods could not be asked about this mod just now: ";
    public const string KeyRefusedMessage = "Nexus Mods refused the saved Nexus key, so RHI could not check this mod for updates. Connect the account again: Settings → Accounts & updates → Disconnect, then Connect Nexus account.";
    public const string NotAModPageMessage = "The link is not a Nexus Mods mod page.";
    public const string NoDatedFileMessage = "Nexus Mods lists no dated main file for this mod.";

    private static readonly HashSet<string> UpdateCategories = new(StringComparer.OrdinalIgnoreCase) { "MAIN", "UPDATE" };

    private readonly NexusApiClient _api;
    private readonly Func<string?> _apiKey;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<NexusModFile> Files)> _files = new(StringComparer.OrdinalIgnoreCase);
    private (DateTimeOffset Until, string Message)? _rateLimited;

    /// <param name="apiKey">The connected account's key, or null (asked on every check: connecting needs no restart).</param>
    public NexusModUpdateCheck(NexusApiClient api, Func<string?> apiKey, TimeProvider? timeProvider = null)
    {
        _api = api;
        _apiKey = apiKey;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The game domain and mod id of a Nexus mod page (<c>https://www.nexusmods.com/&lt;game&gt;/mods/&lt;id&gt;</c>), else null.</summary>
    public static (string Domain, int ModId)? ParsePage(string? url)
    {
        if (GameLinkCatalogue.SafePage(url, LinkHosts.NexusMod) is not { } safe) return null;
        var segments = new Uri(safe).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 && segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(segments[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
               && segments[0].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? (segments[0], id)
            : null;
    }

    /// <param name="pageUrl">The mod's Nexus page.</param>
    /// <param name="installedUtc">When the install was made (the baseline when the file id is unknown).</param>
    /// <param name="installedFileId">The Nexus file the install came from, when known.</param>
    public async Task<NexusModUpdateAnswer> CheckAsync(string pageUrl, DateTimeOffset installedUtc, int? installedFileId = null,
        CancellationToken cancellationToken = default)
    {
        if (ParsePage(pageUrl) is not { } page) return new(NexusModUpdateOutcome.Failed, pageUrl, FailedPrefix + NotAModPageMessage);
        if (_apiKey() is not { Length: > 0 } key) return new(NexusModUpdateOutcome.NoKey, pageUrl, NoKeyMessage);

        IReadOnlyList<NexusModFile>? files = null;
        var cacheKey = page.Domain + "/" + page.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = _time.GetUtcNow();
        string? backingOff = null;
        lock (_gate)
        {
            if (_files.TryGetValue(cacheKey, out var kept) && now - kept.At < Freshness) files = kept.Files;
            else if (_rateLimited is { } limited && now < limited.Until) backingOff = limited.Message;
        }
        if (backingOff is not null) return new(NexusModUpdateOutcome.Failed, pageUrl, FailedPrefix + backingOff);
        if (files is null)
        {
            try
            {
                files = await _api.GetModFilesAsync(key, page.Domain, page.ModId, cancellationToken).ConfigureAwait(false);
            }
            catch (NexusApiException ex) when (!cancellationToken.IsCancellationRequested && ex.Rejected)
            {
                AppLog.Warn("nexus", "Nexus Mods refused the saved key while asked for a mod's files", ex);
                return new(NexusModUpdateOutcome.KeyRefused, pageUrl, KeyRefusedMessage);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is IOException or JsonException or UntrustedDownloadException or HttpRequestException
                                           or TaskCanceledException or InvalidOperationException)
            {
                AppLog.Warn("nexus", "Could not ask Nexus Mods for a mod's files", ex);
                if (ex is NexusApiException { RateLimited: true })
                    lock (_gate) _rateLimited = (now + RateLimitBackoff, ex.Message);
                return new(NexusModUpdateOutcome.Failed, pageUrl, FailedPrefix + ex.Message);
            }
            lock (_gate) _files[cacheKey] = (now, files);
        }

        var dated = files.Where(file => file.UploadedUtc is not null && UpdateCategories.Contains(file.Category)).ToArray();
        if (dated.Length == 0) return new(NexusModUpdateOutcome.Failed, pageUrl, FailedPrefix + NoDatedFileMessage);
        var baseline = installedFileId is { } id && files.FirstOrDefault(file => file.FileId == id)?.UploadedUtc is { } uploaded
            ? uploaded
            : installedUtc;
        var newest = dated.Where(file => file.FileId != installedFileId).MaxBy(file => file.UploadedUtc);
        return newest is not null && newest.UploadedUtc > baseline
            ? new(NexusModUpdateOutcome.Newer, pageUrl, "", newest)
            : new(NexusModUpdateOutcome.Current, pageUrl, "", dated.MaxBy(file => file.UploadedUtc));
    }
}
