using System.Collections.ObjectModel;
using RHI.Core.Deployment;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Deployment;

public enum LinuxUpdateRunMode
{
    Manual,
    Background
}

public sealed record ManagedUpdateCheck(
    bool Managed,
    bool UpdateAvailable,
    string Message,
    string? InstalledVersion = null,
    string? LatestVersion = null,
    // The check could not tell (remote unreachable, no validators, game files missing): no update.
    bool CouldNotCheck = false,
    // Updates are refused for this game (no confirmed patch target): a failure, never "current".
    bool Blocked = false,
    // w3/parity (#96): an update exists but a layer over the component (NR Cost Scaler) holds it: not a failure, not
    // "current", not attempted. The layer's display name.
    string? HeldBy = null,
    // w12 integration A1 (#243 × #236–#238): CouldNotCheck for a reason on this machine (a game held by its own failed
    // undo, a file removed or changed outside RHI, a record without its file's hash or proxy name, a game whose bitness
    // changed): Update All names the game and the reason, as it names a failure. A source that could not be reached
    // stays a count.
    bool LocalReason = false);

public sealed record LinuxUpdateItemResult(
    string GameName,
    ManagedComponentKind Component,
    bool Success,
    bool Changed,
    bool Deferred,
    string Message,
    bool Unchecked = false,
    // Counted as failed because updates are refused for this game (no confirmed patch target).
    bool Blocked = false,
    // w3/parity (#96): held by a layer (ManagedUpdateCheck.HeldBy); counted on its own.
    string? HeldBy = null,
    // #178: left out by the update inclusion (this game's, or Settings' for every game); never checked, never updated.
    bool Excluded = false,
    // #178: left out because the update-inclusion file cannot be read (Excluded is true too).
    bool ExclusionUnreadable = false,
    // w8 final review 1: the update was written and the save then reported an error (finished as committed; Message says so).
    bool Landed = false,
    // #243: could not be checked for a reason on this machine (a Custom ReShade whose source DLL is gone; the check is
    // local): the summary names the game and the reason. A source that could not be reached (reshade.me) stays a count.
    bool LocalReason = false);

public sealed record LinuxUpdateAllResult(IReadOnlyList<LinuxUpdateItemResult> Items)
{
    public int ChangedCount => Items.Count(item => item.Changed);
    public int FailedCount => Items.Count(item => !item.Success && !item.Deferred);
    public int DeferredCount => Items.Count(item => item.Deferred);
    public int CurrentCount => Items.Count(item => item.Success && !item.Changed && !item.Deferred && !item.Unchecked && item.HeldBy is null && !item.Excluded);
    public int UncheckedCount => Items.Count(item => item.Unchecked);
    public int HeldCount => Items.Count(item => item.HeldBy is not null); // w3/parity (#96)
    public int ExcludedCount => Items.Count(item => item.Excluded); // #178
    /// <summary>#178: of <see cref="ExcludedCount"/>, those left out only because the inclusion file cannot be read.</summary>
    public int ExcludedUnreadableCount => Items.Count(item => item.Excluded && item.ExclusionUnreadable);
    public IReadOnlyList<LinuxUpdateItemResult> FailedItems => Items.Where(item => !item.Success && !item.Deferred).ToArray();
    /// <summary>w8 final review 1: the games with an update whose save reported an error after it was written, once each.</summary>
    public IReadOnlyList<string> LandedGames => Items.Where(item => item.Landed).Select(item => item.GameName).Distinct(StringComparer.Ordinal).ToArray();
    /// <summary>#243: the items that could not be checked for a reason on this machine, each named in the summary.</summary>
    public IReadOnlyList<LinuxUpdateItemResult> LocalUncheckedItems => Items.Where(item => item.Unchecked && item.LocalReason).ToArray();
    /// <summary>#242: the games this pass left alone because they were running (each once): the background loop updates
    /// them once they close.</summary>
    public IReadOnlyList<DetectedGame> RunningGames { get; init; } = [];

    public string Summary => Items.Count == 0
        ? "No RHI-managed Update All components were found."
        : $"Update All: {ChangedCount} updated · {CurrentCount} current · {DeferredCount} deferred · {FailedCount} failed"
          + (UncheckedCount > 0 ? $" · {UncheckedCount} could not be checked" : "")
          + (HeldCount > 0 ? $" · {HeldCount} held by " + string.Join(", ", Items.Select(item => item.HeldBy).OfType<string>().Distinct()) : "")
          + (ExcludedCount > 0 ? $" · {ExcludedCount} left out" : "")
          + (ExcludedUnreadableCount > 0 ? " (the update-inclusion file cannot be read)" : "") + ".";
}

/// <summary>
/// w14/layouts-c (upstream 2.9.0 top bar: "Update All (N)" or "All up to date"): what a library-wide check-only pass found. A
/// game is in <see cref="Games"/> once it was checked (true = a component RHI installed has an update Update All would take);
/// a game whose every finding was "could not check" is left out, so it keeps what was known before.
/// </summary>
/// <param name="NotChecked">The games the pass did not reach because GitHub's request limit was reached (0 = it finished).</param>
public sealed record LinuxUpdateCheckPassResult(IReadOnlyDictionary<DetectedGame, bool> Games, int NotChecked)
{
    public bool CutShort => NotChecked > 0;
}

public interface ILinuxManagedUpdateBackend
{
    Task<ManagedUpdateCheck> CheckManagedUpdateAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default);

    Task<ComponentOperationResult> UpdateManagedAsync(
        DetectedGame game,
        ManagedComponentKind component,
        IReadOnlyList<string> indexedFiles,
        CancellationToken cancellationToken = default);

    /// <summary>#178: whether the update inclusion leaves this component of this game out of Update All.</summary>
    UpdateExclusion UpdateExclusionFor(DetectedGame game, ManagedComponentKind component) => UpdateExclusion.None;

    /// <summary>
    /// #243: whether this component's check is local, so manual Update All checks it first although it refreshes it
    /// without a check (ReShade): its check can find a local reason it cannot be checked (a Custom ReShade whose source
    /// DLL is gone), and the game is then reported as not checked, named with that reason, instead of a refresh that fails.
    /// </summary>
    bool ChecksBeforeManualRefresh(DetectedGame game, ManagedComponentKind component) => false;
}

public interface IGameRunningProbe
{
    bool IsRunning(DetectedGame game);
}

/// <summary>
/// Best-effort Linux process probe. Wine/Proton normally retain the Unix game executable
/// path in /proc/&lt;pid&gt;/cmdline even though /proc/&lt;pid&gt;/exe points to wine/preloader.
/// Failure to inspect one process is ignored; the update backends still retain their own
/// transactional/hash protections.
/// </summary>
public sealed class LinuxGameRunningProbe : IGameRunningProbe
{
    public bool IsRunning(DetectedGame game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath)) return false;
        string expected;
        try { expected = Path.GetFullPath(game.ExecutablePath); }
        catch { return false; }
        if (!Directory.Exists("/proc")) return false;

        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out _)) continue;
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(procDir, "cmdline"));
                if (bytes.Length == 0) continue;
                var args = System.Text.Encoding.UTF8.GetString(bytes)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var arg in args)
                {
                    if (arg.Length == 0) continue;
                    if (string.Equals(arg, expected, StringComparison.Ordinal)) return true;
                    if (!Path.IsPathRooted(arg)) continue;
                    try
                    {
                        if (string.Equals(Path.GetFullPath(arg), expected, StringComparison.Ordinal)) return true;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return false;
    }
}

/// <summary>
/// Linux-native Update All coordinator. Only the component families explicitly accepted for
/// the supported managed-component set participate. Both modes first perform a version/identity
/// check and mutate only components with an actual pending update; manual mode refreshes the families in
/// <see cref="RefreshedInManualMode"/> regardless, as upstream's Update All does for ReShade. An update is
/// reported as "updated" only when the component's installed payload changed (#113). A failure is isolated
/// to one game/component.
/// </summary>
public sealed class LinuxUpdateAllService
{
    private static readonly ManagedComponentKind[] Supported =
    [
        ManagedComponentKind.ReShade,
        ManagedComponentKind.RenoDX,
        ManagedComponentKind.ReLimiter,
        ManagedComponentKind.DisplayCommander,
        ManagedComponentKind.OptiScaler,
        ManagedComponentKind.REFramework,
        ManagedComponentKind.LumaFramework,
        ManagedComponentKind.DofFix,
        ManagedComponentKind.DlssEnablerStandalone,
        ManagedComponentKind.Rtx40MfgStandalone,
        ManagedComponentKind.RtxEncore, // w4/encore (#158)
        // Neural Rendering add-ons follow their per-game pin; Latest auto-updates (upstream 2.7.9).
        // Only the add-on file is replaced in place; DLSS DLLs and configs are left alone.
        ManagedComponentKind.Dlss5Feeder,
        ManagedComponentKind.Dlss5Dx11Bridge,
        ManagedComponentKind.ShortFuse,
        // #194 (upstream AddonPackService.CheckAndUpdateAllAsync): the Addon Manager's add-ons, after ReShade.
        ManagedComponentKind.ReShadeAddonManager,
        // w7 lane rows: Update All families (each row ends with a comma)
        // w7/smalls rows
        ManagedComponentKind.NrCostScaler, // #199 (upstream re-stages it in the background pass); updated after the owners
        // w7/reshade rows
        // w7/shader-sets rows
        // w7/nvapi-launch rows
        // w7/dxvk-hdr rows
        ManagedComponentKind.DxvkHdr, // #200 DX9 HDR: d3d9.dll only
        // w12 lane rows: Update All families (each row ends with a comma)
        // w12/mfg rows
        ManagedComponentKind.MfgAdaUnlock, // #237 (upstream abf18c7 AddonPackService.RedeployToTrackedGames); after the other add-ons
        ManagedComponentKind.Dlssg2030Unlock, // #238 (upstream bb9b2fd: DLSSG 20/30 deployed at start)
        ManagedComponentKind.UltimateAsiLoader, // #238 (upstream a1a4e48: ASI Loader updates reach games); x86 and x64 each from their own asset
        ManagedComponentKind.Hsf, // w14/extras-addons (#277: RHI keeps HSF up to date in the games it installed it in)
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows
        // w12/hdr-wsi rows
        // w14 lane rows: Update All families (each row ends with a comma)
        // w14/nr rows
        ManagedComponentKind.GitcUplift, // #274: Latest follows new releases (add-on, helper and effects together); a pin stays
        // w14/nr-2 rows
        ManagedComponentKind.Dlss5Tool, // #317: Latest follows its build's releases (#276: Foveated follows Foveated); a pin stays
    ];

    private static readonly ReadOnlyCollection<ManagedComponentKind> SupportedReadOnly = Array.AsReadOnly(Supported);

    // Manual mode checks first like background mode, except ReShade, which upstream's Update All re-deploys
    // whenever it is installed (#113). A refresh that leaves the payload as it was counts as current, not updated.
    private static readonly ManagedComponentKind[] RefreshedInManualMode = [ManagedComponentKind.ReShade];

    private readonly LinuxDeploymentRecordStore _records;
    private readonly ILinuxManagedUpdateBackend _backend;
    private int _runningPass;
    private int _checkingPass;

    public LinuxUpdateAllService(
        LinuxDeploymentRecordStore records,
        ILinuxManagedUpdateBackend backend,
        IGameRunningProbe? runningProbe = null)
    {
        _records = records;
        _backend = backend;
        RunningProbe = runningProbe ?? new LinuxGameRunningProbe();
    }

    /// <summary>Tells whether a game is running (a test sets a fake one).</summary>
    internal IGameRunningProbe RunningProbe { get; set; }

    /// <summary>#242: whether the game runs now, by the same probe a pass defers it with.</summary>
    public bool IsRunning(DetectedGame game) => RunningProbe.IsRunning(game);

    public static IReadOnlyList<ManagedComponentKind> SupportedComponents => SupportedReadOnly;

    /// <summary>#178: the item message of a component the update inclusion leaves out.</summary>
    public const string LeftOutMessage = "Left out of Update All by its update inclusion.";

    /// <summary>#178: the item message while the update-inclusion file cannot be read (everything is left out).</summary>
    public const string LeftOutUnreadableMessage = "Left out of Update All: the update-inclusion file cannot be read.";

    public bool HasManagedTargets(IEnumerable<DetectedGame> games)
        => games.Any(game => _records.FindAll(game).Any(record => IsSupported(record.Component)));

    public async Task<LinuxUpdateAllResult> RunAsync(
        IReadOnlyList<DetectedGame> games,
        Func<DetectedGame, IReadOnlyList<string>> indexedFiles,
        LinuxUpdateRunMode mode,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _runningPass, 1, 0) != 0)
            return new LinuxUpdateAllResult([
                new LinuxUpdateItemResult("RHI Linux", ManagedComponentKind.ReShade, true, false, true,
                    "Another Update All/background pass is already running.")
            ]);

        var results = new List<LinuxUpdateItemResult>();
        var running = new List<DetectedGame>(); // #242
        try
        {
            foreach (var game in games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targets = _records.FindAll(game)
                    .Where(record => IsSupported(record.Component))
                    .Select(record => record.Component)
                    .Distinct()
                    .OrderBy(UpdateOrder)
                    .ToArray();
                if (targets.Length == 0) continue;

                // #178: what the update inclusion leaves out is neither checked nor updated, and never "could not check".
                var exclusions = targets.ToDictionary(component => component, component => _backend.UpdateExclusionFor(game, component));
                var excluded = targets.Where(component => exclusions[component] != UpdateExclusion.None).ToArray();
                foreach (var component in excluded)
                {
                    var unreadable = exclusions[component] == UpdateExclusion.Unreadable;
                    results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false, unreadable ? LeftOutUnreadableMessage : LeftOutMessage,
                        Excluded: true, ExclusionUnreadable: unreadable));
                }
                targets = targets.Except(excluded).ToArray();
                if (targets.Length == 0) continue;

                if (RunningProbe.IsRunning(game))
                {
                    running.Add(game);
                    foreach (var component in targets)
                        results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, true,
                            "Game is running; update deferred until a later Update All/background pass."));
                    continue;
                }

                var files = indexedFiles(game) ?? Array.Empty<string>();
                foreach (var component in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // One component of one game is one unit: Cancel stops the pass between units, and once a unit
                    // starts changing files it runs to completion or full rollback.
                    using var unit = new OperationCancellation(cancellationToken);
                    try
                    {
                        // #243: a refreshed family is still checked first where its check can say "could not check" for a
                        // local reason; only that answer stops its refresh.
                        var refreshed = mode == LinuxUpdateRunMode.Manual && RefreshedInManualMode.Contains(component);
                        if (!refreshed || _backend.ChecksBeforeManualRefresh(game, component))
                        {
                            var check = await _backend.CheckManagedUpdateAsync(game, component, files, unit.Token).ConfigureAwait(false);
                            if (check.Blocked)
                            {
                                results.Add(new LinuxUpdateItemResult(game.Name, component, false, false, false, check.Message, Blocked: true));
                                continue;
                            }
                            if (check.HeldBy is not null) // w3/parity (#96): left as it is while a layer wraps it
                            {
                                results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false, check.Message, HeldBy: check.HeldBy));
                                continue;
                            }
                            if (refreshed ? check.Managed && check.CouldNotCheck : !check.Managed || !check.UpdateAvailable)
                            {
                                var couldNotCheck = check.Managed && check.CouldNotCheck;
                                // #243: a local check's "could not check" has its reason here: the summary names it. w12
                                // integration A1: so has a check that says its reason is local (the MFG families' held game,
                                // file removed/changed outside RHI, missing hash or proxy name, changed bitness).
                                results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false,
                                    check.Message, Unchecked: couldNotCheck,
                                    LocalReason: couldNotCheck && (check.LocalReason || _backend.ChecksBeforeManualRefresh(game, component))));
                                continue;
                            }
                        }

                        var before = PayloadOf(game, component);
                        var update = await _backend.UpdateManagedAsync(game, component, files, unit.Token).ConfigureAwait(false);
                        // The component's source was unreachable (e.g. a reshade.me outage) and nothing was changed:
                        // "could not be checked", not a failure and never an update (#132).
                        // A successful update counts as "updated" only when the installed payload changed (#113).
                        results.Add(update.SourceUnavailable
                            ? new LinuxUpdateItemResult(game.Name, component, true, false, false, update.Message, Unchecked: true)
                            : new LinuxUpdateItemResult(game.Name, component, update.Success,
                                update.Success && !string.Equals(before, PayloadOf(game, component), StringComparison.Ordinal), false, update.Message,
                                Landed: update.Landed));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new LinuxUpdateItemResult(game.Name, component, false, false, false,
                            $"{component} update failed: {ex.Message}"));
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _runningPass, 0);
        }
        return new LinuxUpdateAllResult(results) { RunningGames = running };
    }

    /// <summary>
    /// w14/layouts-c: the library-wide check-only pass behind the top bar's Update All count (upstream counts the games whose
    /// cards show an update). Each game's supported components are checked with the same check Update All runs first (the
    /// update inclusion's left-out components are skipped, as Update All skips them); nothing is updated and no file changes.
    /// Release lookups go through the app's one HttpClient, so they share the GitHub API cache with every other lookup.
    /// <paramref name="stop"/> is asked before each game: once it says GitHub's request limit was reached the pass ends, and
    /// the games left are counted as not checked. Null when a check pass is already running.
    /// </summary>
    /// <param name="indexedFiles">The game's file index, asked for only when the game has components to check.</param>
    public async Task<LinuxUpdateCheckPassResult?> CheckOnlyAsync(
        IReadOnlyList<DetectedGame> games,
        Func<DetectedGame, CancellationToken, Task<IReadOnlyList<string>>> indexedFiles,
        Func<bool>? stop,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _checkingPass, 1, 0) != 0) return null;
        try
        {
            var found = new Dictionary<DetectedGame, bool>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < games.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stop?.Invoke() == true) return new LinuxUpdateCheckPassResult(found, games.Count - i);
                var game = games[i];
                var targets = _records.FindAll(game)
                    .Where(record => IsSupported(record.Component))
                    .Select(record => record.Component)
                    .Distinct()
                    .Where(component => _backend.UpdateExclusionFor(game, component) == UpdateExclusion.None)
                    .OrderBy(UpdateOrder)
                    .ToArray();
                if (targets.Length == 0)
                {
                    found[game] = false;
                    continue;
                }
                var files = await indexedFiles(game, cancellationToken).ConfigureAwait(false);
                bool update = false, unknown = false;
                foreach (var component in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var check = await _backend.CheckManagedUpdateAsync(game, component, files, cancellationToken).ConfigureAwait(false);
                        if (check.Managed && check.UpdateAvailable && check.HeldBy is null && !check.Blocked)
                        {
                            update = true;
                            break; // one is enough for the count
                        }
                        unknown |= check.Managed && check.CouldNotCheck;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RHI.Core.Services.AppLog.Warn("update", $"update check of {component} for {game.Name} failed", ex);
                        unknown = true;
                    }
                }
                if (update || !unknown) found[game] = update;
            }
            return new LinuxUpdateCheckPassResult(found, 0);
        }
        finally
        {
            Interlocked.Exchange(ref _checkingPass, 0);
        }
    }

    public static bool IsSupported(ManagedComponentKind component) => Supported.Contains(component);

    private string? PayloadOf(DetectedGame game, ManagedComponentKind component)
        => _records.Find(game, component) is { } record ? LinuxDeploymentRecord.PayloadFingerprint(record) : null;

    private static int UpdateOrder(ManagedComponentKind component) => component switch
    {
        ManagedComponentKind.ReShade => 0,
        ManagedComponentKind.RenoDX => 1,
        ManagedComponentKind.ReLimiter => 2,
        ManagedComponentKind.DisplayCommander => 3,
        ManagedComponentKind.OptiScaler => 4,
        ManagedComponentKind.REFramework => 5,
        ManagedComponentKind.LumaFramework => 6,
        ManagedComponentKind.DofFix => 7,
        ManagedComponentKind.DlssEnablerStandalone => 8,
        ManagedComponentKind.Rtx40MfgStandalone => 9,
        ManagedComponentKind.Dlss5Feeder => 10,
        ManagedComponentKind.Dlss5Dx11Bridge => 11,
        ManagedComponentKind.ShortFuse => 12,
        ManagedComponentKind.RtxEncore => 13,
        ManagedComponentKind.ReShadeAddonManager => 14, // #194
        ManagedComponentKind.MfgAdaUnlock => 15, // #237: after the other add-ons
        ManagedComponentKind.Dlssg2030Unlock => 16, // #238
        ManagedComponentKind.UltimateAsiLoader => 17, // #238
        ManagedComponentKind.GitcUplift => 18, // w14/nr (#274): after the other Neural Rendering methods
        ManagedComponentKind.Dlss5Tool => 11, // w14/nr-2 (#317): with the DX11 Bridge it works with
        _ => 99
    };
}
