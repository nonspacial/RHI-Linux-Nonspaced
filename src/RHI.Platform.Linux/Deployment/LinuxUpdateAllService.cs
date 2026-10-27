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
    string? HeldBy = null);

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
    bool Landed = false);

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

    public string Summary => Items.Count == 0
        ? "No RHI-managed Update All components were found."
        : $"Update All: {ChangedCount} updated · {CurrentCount} current · {DeferredCount} deferred · {FailedCount} failed"
          + (UncheckedCount > 0 ? $" · {UncheckedCount} could not be checked" : "")
          + (HeldCount > 0 ? $" · {HeldCount} held by " + string.Join(", ", Items.Select(item => item.HeldBy).OfType<string>().Distinct()) : "")
          + (ExcludedCount > 0 ? $" · {ExcludedCount} left out" : "")
          + (ExcludedUnreadableCount > 0 ? " (the update-inclusion file cannot be read)" : "") + ".";
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
        // w12/shaders rows
        // w12/update-all rows
        // w12/github-cache rows
        // w12/luma rows
        // w12/epic rows
        // w12/nr-logs rows
        // w12/pins rows
    ];

    private static readonly ReadOnlyCollection<ManagedComponentKind> SupportedReadOnly = Array.AsReadOnly(Supported);

    // Manual mode checks first like background mode, except ReShade, which upstream's Update All re-deploys
    // whenever it is installed (#113). A refresh that leaves the payload as it was counts as current, not updated.
    private static readonly ManagedComponentKind[] RefreshedInManualMode = [ManagedComponentKind.ReShade];

    private readonly LinuxDeploymentRecordStore _records;
    private readonly ILinuxManagedUpdateBackend _backend;
    private readonly IGameRunningProbe _running;
    private int _runningPass;

    public LinuxUpdateAllService(
        LinuxDeploymentRecordStore records,
        ILinuxManagedUpdateBackend backend,
        IGameRunningProbe? runningProbe = null)
    {
        _records = records;
        _backend = backend;
        _running = runningProbe ?? new LinuxGameRunningProbe();
    }

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

                if (_running.IsRunning(game))
                {
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
                        if (mode == LinuxUpdateRunMode.Background || !RefreshedInManualMode.Contains(component))
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
                            if (!check.Managed || !check.UpdateAvailable)
                            {
                                results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false,
                                    check.Message, Unchecked: check.Managed && check.CouldNotCheck));
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
        return new LinuxUpdateAllResult(results);
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
        _ => 99
    };
}
