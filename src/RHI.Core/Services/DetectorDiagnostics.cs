namespace RHI.Core.Services;

/// <summary>
/// Lets a detector report a problem it recovered from (one unreadable Steam library, a Lutris config
/// that failed to parse) without failing the whole scan. The scanner collects the reports per detector
/// into <see cref="DetectorScanStatus.Warnings"/>; every report is also written to <see cref="AppLog"/>.
/// Reports flow through the async context, so detector code needs no extra parameters.
/// </summary>
public static class DetectorDiagnostics
{
    private static readonly AsyncLocal<List<string>?> Collector = new();
    private static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> Listed = new();

    /// <summary>
    /// w12 follow-up (merge review nit 1): <paramref name="games"/> games were listed, but without a launch command (Play
    /// then says to set one in Edit game). Logged and counted for the scan's own plain line; never a "skipped" warning,
    /// since nothing was skipped.
    /// </summary>
    public static void ListedWithoutLaunchCommand(int games, string message, Exception? error = null)
    {
        if (games <= 0) return;
        if (Listed.Value is { } box) Interlocked.Add(ref box.Value, games);
        AppLog.Warn("scan", message, error);
    }

    public static void Warn(string message, Exception? error = null)
    {
        var text = error is null ? message : $"{message}: {error.Message}";
        var collector = Collector.Value;
        if (collector is not null)
        {
            lock (collector) collector.Add(text);
        }
        AppLog.Warn("scan", message, error);
    }

    /// <summary>Runs <paramref name="detect"/> with a fresh collector and returns what it reported.</summary>
    internal static async Task<(T Result, IReadOnlyList<string> Warnings)> CollectAsync<T>(Func<Task<T>> detect)
    {
        var (result, warnings, _) = await CollectScanAsync(detect).ConfigureAwait(false);
        return (result, warnings);
    }

    /// <summary>As <see cref="CollectAsync"/>, plus the games listed without a launch command.</summary>
    internal static async Task<(T Result, IReadOnlyList<string> Warnings, int ListedWithoutLaunchCommand)> CollectScanAsync<T>(Func<Task<T>> detect)
    {
        var collector = new List<string>();
        var listed = new System.Runtime.CompilerServices.StrongBox<int>();
        Collector.Value = collector;
        Listed.Value = listed;
        var result = await detect().ConfigureAwait(false);
        lock (collector) return (result, collector.ToArray(), Volatile.Read(ref listed.Value));
    }
}

/// <summary>Outcome of one detector in a library scan.</summary>
/// <param name="Name">Detector name (Steam, Heroic, Lutris, Wine, Manual).</param>
/// <param name="Count">Entries the detector returned.</param>
/// <param name="Error">Set when the detector failed outright; its games are missing from the library.</param>
/// <param name="Warnings">Problems the detector recovered from (entries or libraries it had to skip).</param>
/// <param name="Reused">True when a partial refresh reused the previous scan's result and status.</param>
/// <param name="ListedWithoutLaunchCommand">Games listed without a launch command (their own line, not a warning).</param>
public sealed record DetectorScanStatus(string Name, int Count, string? Error, IReadOnlyList<string> Warnings, bool Reused = false,
    int ListedWithoutLaunchCommand = 0)
{
    public bool Failed => Error is not null;
}

/// <summary>A library-policy stage (upstream manifest) that could not be applied; the local library stays as detected.</summary>
public sealed record PolicyScanStatus(string Name, string Error);

/// <summary>Library scan output: the merged games plus what each detector and policy stage reported.</summary>
public sealed record GameLibraryScanResult(
    IReadOnlyList<RHI.Core.Models.DetectedGame> Games,
    IReadOnlyList<DetectorScanStatus> Detectors,
    IReadOnlyList<PolicyScanStatus> PolicyFailures)
{
    public bool HasProblems => PolicyFailures.Count > 0 || Detectors.Any(d => d.Failed || d.Warnings.Count > 0 || d.ListedWithoutLaunchCommand > 0);
}
