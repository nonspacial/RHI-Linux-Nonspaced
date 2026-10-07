using System.Collections.ObjectModel;
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
    bool CouldNotCheck = false);

public sealed record LinuxUpdateItemResult(
    string GameName,
    ManagedComponentKind Component,
    bool Success,
    bool Changed,
    bool Deferred,
    string Message);

public sealed record LinuxUpdateAllResult(IReadOnlyList<LinuxUpdateItemResult> Items)
{
    public int ChangedCount => Items.Count(item => item.Changed);
    public int FailedCount => Items.Count(item => !item.Success && !item.Deferred);
    public int DeferredCount => Items.Count(item => item.Deferred);
    public int CurrentCount => Items.Count(item => item.Success && !item.Changed && !item.Deferred);

    public string Summary => Items.Count == 0
        ? "No RHI-managed Update All components were found."
        : $"Update All: {ChangedCount} updated · {CurrentCount} current · {DeferredCount} deferred · {FailedCount} failed.";
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
/// the supported managed-component set participate. Manual mode refreshes every managed component to the latest
/// compatible payload; background mode first performs a version/identity check and mutates only
/// components with an actual pending update. A failure is isolated to one game/component.
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
        // Neural Rendering add-ons follow their per-game pin; Latest auto-updates (upstream 2.7.9).
        // Only the add-on file is replaced in place; DLSS DLLs and configs are left alone.
        ManagedComponentKind.Dlss5Feeder,
        ManagedComponentKind.Dlss5Dx11Bridge,
        ManagedComponentKind.ShortFuse
    ];

    private static readonly ReadOnlyCollection<ManagedComponentKind> SupportedReadOnly = Array.AsReadOnly(Supported);

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
                    try
                    {
                        if (mode == LinuxUpdateRunMode.Background)
                        {
                            var check = await _backend.CheckManagedUpdateAsync(game, component, files, cancellationToken).ConfigureAwait(false);
                            if (!check.Managed)
                            {
                                results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false,
                                    check.Message));
                                continue;
                            }
                            if (!check.UpdateAvailable)
                            {
                                results.Add(new LinuxUpdateItemResult(game.Name, component, true, false, false,
                                    check.Message));
                                continue;
                            }
                        }

                        var update = await _backend.UpdateManagedAsync(game, component, files, cancellationToken).ConfigureAwait(false);
                        results.Add(new LinuxUpdateItemResult(game.Name, component, update.Success, update.Success, false, update.Message));
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
        _ => 99
    };
}
