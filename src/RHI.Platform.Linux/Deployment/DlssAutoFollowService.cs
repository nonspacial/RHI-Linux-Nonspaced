using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#177: which families follow the newest manifest version (Settings → DLSS/Streamline, both off by default).</summary>
public sealed record DlssFollowChoice(bool Dlss, bool Streamline)
{
    public bool Any => Dlss || Streamline;
}

public enum DlssFollowOutcome
{
    /// <summary>Both toggles are off: nothing was fetched or changed.</summary>
    Off,
    /// <summary>The manifest could not be fetched (offline, or only the cached copy answered): no baseline moved.</summary>
    CouldNotCheck,
    /// <summary>The baseline file cannot be read (it was copied aside): nothing moved.</summary>
    StateUnreadable,
    /// <summary>First pass of a family: its newest version became the baseline, nothing was swapped.</summary>
    Seeded,
    /// <summary>The newest versions are the ones already followed.</summary>
    Current,
    /// <summary>A newer version was published: files on the previous newest were swapped (see the swaps).</summary>
    Followed
}

/// <summary>One file (or Streamline folder) a pass swapped, or failed to swap.</summary>
public sealed record DlssFollowSwap(string GameName, string Label, string FromVersion, string ToVersion, bool Success, string? Error);

public sealed record DlssFollowResult(DlssFollowOutcome Outcome, IReadOnlyList<DlssFollowSwap> Swaps, int DeferredGames = 0, string? Problem = null)
{
    public int UpdatedCount => Swaps.Count(swap => swap.Success);
    public int FailedCount => Swaps.Count(swap => !swap.Success);
}

/// <summary>What <see cref="DlssAutoFollowService.CheckAsync"/> found: done (<see cref="Result"/>), or plans to apply.</summary>
public sealed class DlssFollowCheck
{
    internal DlssFollowCheck(DlssFollowResult result, IReadOnlyList<DlssAutoFollowService.Plan> plans)
    {
        Result = result;
        Plans = plans;
    }

    internal static DlssFollowCheck Done(DlssFollowResult result) => new(result, []);

    /// <summary>The outcome when nothing needs applying.</summary>
    public DlssFollowResult Result { get; }

    /// <summary>A newer version was published for at least one followed family: apply under the busy gate.</summary>
    public bool NeedsApply => Plans.Count > 0;

    internal IReadOnlyList<DlssAutoFollowService.Plan> Plans { get; }
}

/// <summary>
/// #177 (upstream 2.8.7 <c>RunDlssAutoUpdateAsync</c>): after an answered DLSS-manifest fetch, compares each followed
/// family's newest manifest version with the version the last pass followed to. The first pass of a family only seeds
/// that baseline. When the newest changed, every game file of that family whose own PE version is exactly the PREVIOUS
/// newest (never 1.x) is swapped to the new newest; files on any other version (a version picked by hand, the game's
/// own, DLSS 1.x) are left alone, so a game opts in by being on the latest. Targets are the Quick Apply set minus every
/// RHI-recorded file (<see cref="LinuxDlssDefaultsService.InspectForFollow"/>); <c>nvngx_dlssnr.dll</c> is never followed
/// (one owner per game). A running game is skipped and followed by the next pass; a swap that fails keeps the file and
/// the baseline still advances (upstream). Every payload is downloaded before the first game file changes, Cancel stops
/// the pass between games, and each swapped file gets one automatic update-history entry.
/// </summary>
public sealed class DlssAutoFollowService
{
    /// <summary>The history entry's note (its tooltip): the swap was automatic.</summary>
    public const string AutomaticDlssNote = "Automatic: Follow newest DLSS is on.";
    public const string AutomaticStreamlineNote = "Automatic: Follow newest Streamline is on.";
    /// <summary>A game RHI must not change now (records left at an old folder, no confirmed target); the reason follows.</summary>
    public const string RefusedPrefix = "Not followed: ";
    /// <summary>The newest payload could not be downloaded; the reason follows.</summary>
    public const string DownloadFailedPrefix = "The newest version could not be downloaded: ";

    /// <summary>The newest version vanished from the manifest between the check and the download; what follows names it.</summary>
    public const string NotInManifestPrefix = "No longer in the DLSS manifest: ";
    /// <summary>A swap that failed without a reason of its own.</summary>
    public const string SwapFailedMessage = "The swap failed; the game's file was kept.";

    /// <summary>At most this many older newest versions are kept while games stay running across passes.</summary>
    internal const int MaxHeldVersions = 8;

    internal sealed record Family(string Key, DlssComponentKind? Kind, string Label, bool IsStreamline);

    private static readonly Family[] DlssFamilies =
    [
        new("dlss", DlssComponentKind.SuperResolution, "DLSS SR (nvngx_dlss.dll)", false),
        new("dlssd", DlssComponentKind.RayReconstruction, "DLSS RR (nvngx_dlssd.dll)", false),
        new("dlssg", DlssComponentKind.FrameGeneration, "DLSS FG (nvngx_dlssg.dll)", false),
    ];

    private static readonly Family StreamlineFamily = new("streamline", null, "Streamline (sl.*.dll)", true);

    internal sealed record Plan(Family Family, string Newest, IReadOnlyList<string> FollowFrom);

    private sealed record Work(DetectedGame Game, IReadOnlyList<(Plan Plan, IReadOnlyList<(string Target, string From)> Targets)> Items);

    private readonly DlssManagerService _dlss;
    private readonly LinuxDlssDefaultsService _targets;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DlssAutoFollowStore _store;
    private readonly ComponentUpdateLogStore? _log;
    private readonly IGameRunningProbe _running;
    private readonly DeploymentRelocation _relocation;
    private readonly Func<DateTime> _utcNow;
    // One check or apply at a time (start-up, Full Refresh and the background loop may all ask).
    private readonly SemaphoreSlim _pass = new(1, 1);

    public DlssAutoFollowService(
        DlssManagerService dlss,
        LinuxDlssDefaultsService targets,
        LinuxDeploymentRecordStore records,
        DlssAutoFollowStore store,
        ComponentUpdateLogStore? updateLog = null,
        IGameRunningProbe? runningProbe = null,
        Func<DateTime>? utcNow = null)
    {
        _dlss = dlss;
        _targets = targets;
        _records = records;
        _store = store;
        _log = updateLog;
        _running = runningProbe ?? new LinuxGameRunningProbe();
        _relocation = new DeploymentRelocation(records);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Check and apply in one go (no busy gate in between): <see cref="CheckAsync"/>, then <see cref="ApplyAsync"/> when a
    /// newer version was published.
    /// </summary>
    public async Task<DlssFollowResult> RunAsync(
        IReadOnlyList<DetectedGame> games,
        DlssFollowChoice choice,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        var check = await CheckAsync(choice, cancellationToken).ConfigureAwait(false);
        return check.NeedsApply ? await ApplyAsync(check, games, indexedFiles, cancellationToken).ConfigureAwait(false) : check.Result;
    }

    /// <summary>
    /// The network half, run without the busy gate (no game file changes here): fetches the DLSS manifest and compares
    /// each followed family's newest version with its baseline. Seeds a family seen for the first time and forgets the
    /// baseline of a family whose toggle is off (both saved at once); every other baseline moves only in
    /// <see cref="ApplyAsync"/>, so a pass that never got the gate is simply repeated by the next one.
    /// </summary>
    public async Task<DlssFollowCheck> CheckAsync(DlssFollowChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!await _pass.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return DlssFollowCheck.Done(new(DlssFollowOutcome.Current, []));
        try
        {
            return await CheckCoreAsync(choice, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>
    /// The file half of a check that found a newer version (call it while holding the busy gate): swaps every game file on
    /// the previous newest, writes the history, then moves the baselines.
    /// </summary>
    /// <param name="games">Every game in the library.</param>
    /// <param name="indexedFiles">A game's file index when one is at hand (null: built when the game is looked at).</param>
    /// <param name="cancellationToken">Cancel is honoured between games only.</param>
    public async Task<DlssFollowResult> ApplyAsync(
        DlssFollowCheck check,
        IReadOnlyList<DetectedGame> games,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(games);
        if (!check.NeedsApply) return check.Result;
        if (!await _pass.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(DlssFollowOutcome.Current, []);
        try
        {
            return await ApplyCoreAsync(check.Plans, games, indexedFiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<DlssFollowCheck> CheckCoreAsync(DlssFollowChoice choice, CancellationToken cancellationToken)
    {
        if (_store.Load() is not { } tracks)
        {
            AppLog.Warn("dlss-follow", $"The Follow newest DLSS baseline file cannot be read ({_store.FilePath}); nothing is followed until it reads again.");
            return DlssFollowCheck.Done(new(DlssFollowOutcome.StateUnreadable, []));
        }

        // A family whose toggle is off forgets its baseline: turning it on later seeds it afresh instead of following
        // from a version that was the newest long ago.
        var families = (choice.Dlss ? DlssFamilies : []).Concat(choice.Streamline ? [StreamlineFamily] : []).ToArray();
        var changed = false;
        foreach (var key in tracks.Keys.ToArray())
            if (!families.Any(family => family.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                changed |= tracks.Remove(key);
        if (!choice.Any)
        {
            if (changed) _store.Save(tracks);
            return DlssFollowCheck.Done(new(DlssFollowOutcome.Off, []));
        }

        DlssManifestData manifest;
        try
        {
            manifest = await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("dlss-follow", "Follow newest DLSS: the DLSS manifest could not be fetched; nothing moves until it can", ex);
            if (changed) _store.Save(tracks);
            return DlssFollowCheck.Done(new(DlssFollowOutcome.CouldNotCheck, [], Problem: ex.Message));
        }
        if (!_dlss.LastManifestFetchAnswered)
        {
            // Only the cached copy answered: it may be days old, so it can say nothing about what is newest now.
            if (changed) _store.Save(tracks);
            return DlssFollowCheck.Done(new(DlssFollowOutcome.CouldNotCheck, []));
        }

        var seeded = false;
        var plans = new List<Plan>();
        foreach (var family in families)
        {
            if (NewestOf(manifest, family) is not { } newest) continue;
            if (!tracks.TryGetValue(family.Key, out var track) || string.IsNullOrWhiteSpace(track.Newest))
            {
                tracks[family.Key] = new DlssFollowTrack { Newest = newest };
                seeded = changed = true;
                continue;
            }
            var followFrom = track.HeldFrom.Append(track.Newest)
                .Where(version => !string.IsNullOrWhiteSpace(version) && !SameVersion(version, newest))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (followFrom.Length == 0) continue;
            plans.Add(new Plan(family, newest, followFrom));
        }
        if (changed) _store.Save(tracks);
        return plans.Count == 0
            ? DlssFollowCheck.Done(new(seeded ? DlssFollowOutcome.Seeded : DlssFollowOutcome.Current, []))
            : new DlssFollowCheck(new(DlssFollowOutcome.Followed, []), plans);
    }

    private async Task<DlssFollowResult> ApplyCoreAsync(
        IReadOnlyList<Plan> plans,
        IReadOnlyList<DetectedGame> games,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles,
        CancellationToken cancellationToken)
    {
        var work = FindWork(games, plans, indexedFiles, cancellationToken);
        var swaps = new List<DlssFollowSwap>();

        // Network half: every payload a swap needs is cached before the first game file changes.
        var unavailable = new Dictionary<Family, string>();
        foreach (var plan in work.SelectMany(item => item.Items).Select(item => item.Plan).Distinct())
        {
            try
            {
                if (plan.Family.IsStreamline)
                {
                    if (await _dlss.EnsureStreamlineCachedAsync(plan.Newest, cancellationToken).ConfigureAwait(false) is null)
                        unavailable[plan.Family] = NotInManifestPrefix + "Streamline " + plan.Newest;
                }
                else
                {
                    var cached = await _dlss.EnsureCachedVersionAsync(plan.Family.Kind!.Value, plan.Newest, cancellationToken).ConfigureAwait(false);
                    if (cached is null || !SameVersion(cached.Value.Version, plan.Newest))
                        unavailable[plan.Family] = NotInManifestPrefix + plan.Family.Label + " " + plan.Newest;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn("dlss-follow", $"Follow newest DLSS: {plan.Family.Label} {plan.Newest} could not be downloaded", ex);
                unavailable[plan.Family] = ex.Message;
            }
        }

        // File half: one game is one unit; Cancel stops the pass between games (the baseline then stays put, so the
        // next pass picks up the games not reached; the ones swapped are no longer on the previous newest).
        var deferredFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredGames = 0;
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = item.Game;
            if (_running.IsRunning(game) || XboxWriteGate.Refusal(game) is not null)
            {
                deferredGames++;
                foreach (var (plan, _) in item.Items) deferredFamilies.Add(plan.Family.Key);
                continue;
            }
            if (Refusal(game) is { } refused)
            {
                foreach (var (plan, targets) in item.Items)
                    foreach (var (_, from) in targets)
                        swaps.Add(Record(game, plan, from, false, RefusedPrefix + refused));
                continue;
            }

            using var unit = new OperationCancellation(cancellationToken);
            foreach (var (plan, targets) in item.Items)
            {
                if (unavailable.TryGetValue(plan.Family, out var why))
                {
                    foreach (var (_, from) in targets) swaps.Add(Record(game, plan, from, false, DownloadFailedPrefix + why));
                    continue;
                }
                FileDeploymentBatchResult result;
                try
                {
                    var paths = targets.Select(target => target.Target).ToArray();
                    result = plan.Family.IsStreamline
                        ? await _dlss.SwapStreamlineAsync(paths, plan.Newest, unit.Token).ConfigureAwait(false)
                        : await _dlss.SwapAsync(plan.Family.Kind!.Value, paths, plan.Newest, unit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !unit.WritesStarted)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new FileDeploymentBatchResult(false, [], ex.Message);
                }
                foreach (var (_, from) in targets)
                    swaps.Add(Record(game, plan, from, result.Success, result.Success ? null : result.Error ?? SwapFailedMessage));
                if (!result.Success)
                    AppLog.Warn("dlss-follow", $"Follow newest: {plan.Family.Label} {plan.Newest} could not be applied to {game.Name}; its files were kept: {result.Error}");
            }
        }

        // Upstream advances the baseline even when a swap failed; families with a running game keep what they still
        // follow from, so the next pass swaps that game too.
        var tracks = _store.Load() ?? throw new JsonStoreUnavailableException(_store.FilePath, null);
        foreach (var plan in plans)
            tracks[plan.Family.Key] = new DlssFollowTrack
            {
                Newest = plan.Newest,
                HeldFrom = deferredFamilies.Contains(plan.Family.Key) ? [.. plan.FollowFrom.Take(MaxHeldVersions)] : [],
                Unknown = tracks.TryGetValue(plan.Family.Key, out var old) ? old.Unknown : null
            };
        _store.Save(tracks);
        return new(DlssFollowOutcome.Followed, swaps, deferredGames);
    }

    // Which files of which games are on a version being followed from. Reads PE versions only (no writes, no network).
    private List<Work> FindWork(
        IReadOnlyList<DetectedGame> games,
        IReadOnlyList<Plan> plans,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles,
        CancellationToken cancellationToken)
    {
        var work = new List<Work>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relevant = plans.Where(plan => Follows(game, plan.Family)).ToArray();
            if (relevant.Length == 0 || !game.HasConfirmedPatchTarget) continue;
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath)) continue;
            var candidate = _targets.InspectForFollow(game, indexedFiles?.Invoke(game), cancellationToken);
            if (!candidate.Eligible) continue;
            var items = new List<(Plan, IReadOnlyList<(string, string)>)>();
            foreach (var plan in relevant)
            {
                // The history names the version as the manifest spells it (310.4.0, not the DLL's 310.4.0.0).
                var targets = TargetsOf(candidate.Detection, plan.Family)
                    .Select(target => (Target: target, Installed: InstalledVersion(target, plan.Family)))
                    .Where(target => target.Installed is { } installed && !IsVersion1(installed))
                    .Select(target => (target.Target, From: plan.FollowFrom.FirstOrDefault(version => SameVersion(version, target.Installed))))
                    .Where(target => target.From is not null)
                    .Select(target => (target.Target, target.From!))
                    .ToArray();
                if (targets.Length > 0) items.Add((plan, targets));
            }
            if (items.Count > 0) work.Add(new Work(game, items));
        }
        return work;
    }

    /// <summary>Whether this game follows <paramref name="family"/> (a seam for the per-game update inclusion).</summary>
    private static bool Follows(DetectedGame game, Family family) => true;

    // Records left pointing at the folder the game was moved from (Refs #38): nothing changes until Repair after move.
    private string? Refusal(DetectedGame game) => _relocation.Guard(game) is { } refused ? refused.Message : null;

    private DlssFollowSwap Record(DetectedGame game, Plan plan, string from, bool success, string? error)
    {
        var swap = new DlssFollowSwap(game.Name, plan.Family.Label, from, plan.Newest, success, error);
        // The history store never throws on a write failure: it keeps the entry in memory and says so itself.
        _log?.Record(new ComponentUpdateLogEntry
        {
            TimestampUtc = _utcNow(),
            GameName = game.Name,
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            Component = plan.Family.IsStreamline ? ManagedComponentKind.Streamline : ManagedComponentKind.Dlss,
            ComponentName = plan.Family.Label,
            Action = ComponentUpdateAction.Update,
            FromVersion = from,
            ToVersion = plan.Newest,
            Success = success,
            Message = success ? (plan.Family.IsStreamline ? AutomaticStreamlineNote : AutomaticDlssNote) : error
        });
        return swap;
    }

    private static string? NewestOf(DlssManifestData manifest, Family family)
    {
        var entries = family.Key switch
        {
            "dlss" => manifest.Dlss,
            "dlssd" => manifest.Dlssd,
            "dlssg" => manifest.Dlssg,
            _ => manifest.Streamline
        };
        var newest = entries.FirstOrDefault()?.Version;
        return string.IsNullOrWhiteSpace(newest) ? null : newest.Trim();
    }

    private static IReadOnlyList<string> TargetsOf(DlssDetectionResult detection, Family family) => family.Key switch
    {
        "dlss" => detection.DlssPaths,
        "dlssd" => detection.DlssdPaths,
        "dlssg" => detection.DlssgPaths,
        _ => detection.StreamlineFolders
    };

    // A Streamline folder's version is its interposer's (upstream), else sl.common.dll's.
    private static string? InstalledVersion(string target, Family family)
    {
        if (!family.IsStreamline) return PeFileVersion(target);
        return PeFileVersion(Path.Combine(target, "sl.interposer.dll")) ?? PeFileVersion(Path.Combine(target, "sl.common.dll"));
    }

    /// <summary>The PE version of a DLL as dotted numbers (the fixed version block first, then the FileVersion string).</summary>
    internal static string? PeFileVersion(string path)
    {
        var info = PeVersionReader.Read(path);
        if (info is null) return null;
        if (info.FixedFileVersion is { } fixedVersion && (fixedVersion.Major | fixedVersion.Minor | Math.Max(0, fixedVersion.Build) | Math.Max(0, fixedVersion.Revision)) != 0)
            return fixedVersion.ToString();
        var text = info.FileVersion ?? info.ProductVersion;
        return Numbers(text) is { } numbers ? string.Join('.', numbers) : null;
    }

    /// <summary>
    /// True when two versions name the same build: numbers compared part by part, missing parts read as 0, so the
    /// manifest's <c>310.9.16</c> matches a DLL's <c>310,9,16,0</c>.
    /// </summary>
    internal static bool SameVersion(string? a, string? b)
        => Numbers(a) is { } x && Numbers(b) is { } y && x.SequenceEqual(y);

    private static bool IsVersion1(string version) => Numbers(version) is [1, ..];

    // The leading dotted (or comma-separated) numbers of a version string, padded to four parts; null without any.
    private static int[]? Numbers(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var token = version.Trim().Split([' ', '+', '-', '('], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null) return null;
        var parts = token.Replace(',', '.').Split('.', StringSplitOptions.TrimEntries);
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)) return null;
            numbers.Add(number);
        }
        if (numbers.Count == 0 || numbers.Count > 4) return null;
        while (numbers.Count < 4) numbers.Add(0);
        return [.. numbers];
    }
}
